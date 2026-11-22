package steno

import "unicode/utf8"

// An Encoder encodes one value that is given in pieces. Over any split of the value, also inside a UTF-8 sequence or a surrogate pair, it writes
// exactly the bytes that the one-shot functions write for the whole value.
//
// The zero value is an encoder at the start of a value. Reset starts a new value. Flush writes what the encoder holds back, but it does not start a
// new value: the next call goes on with the same value.
//
// The pieces of one value may be in different forms. What a piece ends in is held back only for a piece of the same form: a held high surrogate
// code unit is written as a surrogate code point of its own before a piece that is not UTF-16, and a held start of a UTF-8 sequence is invalid
// UTF-8 before a piece that is not UTF-8. An empty piece changes nothing.
//
// A call that returns an error leaves the encoder as it was, and returns dst with its length, and its bytes up to that length, unchanged; the
// spare capacity beyond it may have been written. A call that succeeds writes no byte past the bytes it appends. An Encoder must not be used by
// several goroutines at once.
type Encoder struct {
	e        encState
	started  bool
	lastHigh bool    // the last code point encoded was a high surrogate code point
	utf8     [4]byte // the start of a UTF-8 sequence that a piece ended in
	nUTF8    uint8
	high     uint16 // a high surrogate code unit that a UTF-16 piece ended in, or 0
}

// Reset makes e an encoder at the start of a new value, and drops what it holds back.
func (e *Encoder) Reset() {
	*e = Encoder{}
}

func (e *Encoder) init() {
	if !e.started {
		e.e.reset()
		e.started = true
	}
}

// AppendString encodes the UTF-8 text s as the next piece of the value and appends the bytes to dst. A UTF-8 sequence that s ends in the middle of
// is held back until the next call completes it.
//
// If the text is not valid UTF-8, AppendString returns an *EncodeError with the reason InvalidUTF8; it does not wait for more input when the bytes
// can no longer be a valid sequence.
func (e *Encoder) AppendString(dst []byte, s string) ([]byte, error) {
	return appendUTF8Piece(e, dst, s)
}

// Append is AppendString for UTF-8 text in a byte slice. dst and p must not overlap.
func (e *Encoder) Append(dst []byte, p []byte) ([]byte, error) {
	return appendUTF8Piece(e, dst, p)
}

func appendUTF8Piece[T string | []byte](e *Encoder, dst []byte, s T) ([]byte, error) {
	e.init()
	if len(s) == 0 {
		return dst, nil
	}
	saved := *e
	n := len(dst)
	dst, err := e.flushHigh(dst)
	if err != nil {
		return dst[:n], err
	}

	i := 0
	if e.nUTF8 != 0 {
		// Complete the held sequence with the first bytes of s.
		held := int(e.nUTF8)
		buf := e.utf8
		m := held + copy(buf[held:], s)
		c, size, status := decodeUTF8(buf[:m], 0)
		switch status {
		case utf8Invalid:
			*e = saved
			return dst[:n], &EncodeError{Reason: InvalidUTF8, Offset: -held}
		case utf8Short:
			e.utf8 = buf
			e.nUTF8 = uint8(m)
			return dst, nil
		}
		dst = e.e.put(dst, c)
		e.nUTF8 = 0
		e.lastHigh = false
		i = size - held
	}

	if i < len(s) {
		var j, status int
		dst, j, status = encodeUTF8(&e.e, dst, s[i:])
		e.lastHigh = false
		switch status {
		case utf8Invalid:
			*e = saved
			return dst[:n], &EncodeError{Reason: InvalidUTF8, Offset: i + j}
		case utf8Short:
			e.nUTF8 = uint8(copy(e.utf8[:], s[i+j:]))
		}
	}
	return dst, nil
}

// AppendRunes encodes the code points r as the next piece of the value and appends the bytes to dst. Its errors are those of EncodeRunes; a high
// surrogate code point at the end of the piece before and a low one at the start of r are not a value either.
func (e *Encoder) AppendRunes(dst []byte, r []rune) ([]byte, error) {
	e.init()
	if len(r) == 0 {
		return dst, nil
	}
	saved := *e
	n := len(dst)
	dst, err := e.flushAll(dst)
	if err != nil {
		return dst[:n], err
	}
	dst, i, reason := encodeRunes(&e.e, dst, r, e.lastHigh)
	if reason != 0 {
		*e = saved
		return dst[:n], &EncodeError{Reason: reason, Offset: i}
	}
	if len(r) > 0 {
		e.lastHigh = isHighSurrogate(r[len(r)-1])
	}
	return dst, nil
}

// AppendUTF16 encodes the UTF-16 code units u as the next piece of the value and appends the bytes to dst. A high surrogate at the end of u is held
// back until the next call, which may start with its low surrogate.
//
// It returns an error only when the encoder holds an unfinished UTF-8 sequence (InvalidUTF8), or when u starts with a low surrogate after a high
// surrogate code point that was already written, by Flush or AppendRunes (NotAValue).
func (e *Encoder) AppendUTF16(dst []byte, u []uint16) ([]byte, error) {
	e.init()
	if len(u) == 0 {
		return dst, nil
	}
	saved := *e
	n := len(dst)
	dst, err := e.flushUTF8(dst)
	if err != nil {
		return dst[:n], err
	}

	if e.high != 0 {
		if d := u[0]; d >= 0xDC00 && d <= 0xDFFF {
			dst = e.e.put(dst, 0x10000+(int32(e.high)-0xD800)<<10+(int32(d)-0xDC00))
			u = u[1:]
			e.lastHigh = false
		} else {
			dst = e.e.put(dst, int32(e.high))
			e.lastHigh = true
		}
		e.high = 0
	}
	if len(u) == 0 {
		return dst, nil
	}

	if e.lastHigh && u[0] >= 0xDC00 && u[0] <= 0xDFFF {
		*e = saved
		return dst[:n], &EncodeError{Reason: NotAValue, Offset: 0}
	}

	// The last code point written here is never a lone high surrogate: a high surrogate at the end of u is held back.
	dst, done := encodeUTF16(&e.e, dst, u, true)
	if done < len(u) {
		e.high = u[done]
	}
	e.lastHigh = false
	return dst, nil
}

// Flush writes what the encoder holds back: a high surrogate code unit that the last UTF-16 piece ended in is written as a surrogate code point of
// its own. If the encoder holds the start of a UTF-8 sequence, Flush returns an *EncodeError with the reason InvalidUTF8 and a negative offset.
//
// Flush does not start a new value. Call it at the end of the value; call Reset to start the next one.
func (e *Encoder) Flush(dst []byte) ([]byte, error) {
	e.init()
	saved := *e
	n := len(dst)
	dst, err := e.flushAll(dst)
	if err != nil {
		*e = saved
		return dst[:n], err
	}
	return dst, nil
}

func (e *Encoder) flushAll(dst []byte) ([]byte, error) {
	dst, err := e.flushUTF8(dst)
	if err != nil {
		return dst, err
	}
	return e.flushHigh(dst)
}

// dropUTF8 drops the start of a UTF-8 sequence that the encoder holds.
func (e *Encoder) dropUTF8() {
	e.nUTF8 = 0
}

func (e *Encoder) flushUTF8(dst []byte) ([]byte, error) {
	if e.nUTF8 != 0 {
		return dst, &EncodeError{Reason: InvalidUTF8, Offset: -int(e.nUTF8)}
	}
	return dst, nil
}

func (e *Encoder) flushHigh(dst []byte) ([]byte, error) {
	if e.high != 0 {
		dst = e.e.put(dst, int32(e.high))
		e.high = 0
		e.lastHigh = true
	}
	return dst, nil
}

// A Decoder decodes one value whose bytes are given in pieces. Over any split of the bytes, it writes exactly what the one-shot functions write for
// the whole value, or fails with the same error. The bytes of a code point that a piece ends in the middle of are held back until the next call
// completes them.
//
// The zero value is a decoder at the start of a value. Reset starts a new value. Flush checks that the value is complete, but it does not start a
// new value.
//
// A call that returns an error leaves the decoder as it was, and returns dst with its length, and its elements up to that length, unchanged;
// the spare capacity beyond it may have been written. A call that succeeds writes nothing past what it appends. The offset of a DecodeError
// counts from the first byte given since the last Reset. A Decoder must not be used by several goroutines at once.
type Decoder struct {
	d       decState
	started bool
	pos     int     // the number of bytes given so far
	held    [4]byte // the bytes of an unfinished code point
	nHeld   uint8
}

// Reset makes d a decoder at the start of a new value, and drops the bytes it holds.
func (d *Decoder) Reset() {
	*d = Decoder{}
}

func (d *Decoder) init() {
	if !d.started {
		d.d.reset()
		d.started = true
	}
}

// head completes a held code point with the first bytes of b. It returns the code point, the number of bytes of b it took, and whether there
// is a code point; with ok false and no error, all of b was held.
func (d *Decoder) head(b []byte) (c int32, used int, ok bool, err error) {
	held := int(d.nHeld)
	buf := d.held
	m := held + copy(buf[held:], b)
	c, n, reason := d.d.read(buf[:m], 0)
	switch reason {
	case 0:
		d.nHeld = 0
		return c, n - held, true, nil
	case Truncated:
		d.held = buf
		d.nHeld = uint8(m)
		return 0, len(b), false, nil
	default:
		return 0, 0, false, &DecodeError{Reason: reason, Offset: d.pos - held}
	}
}

// tail holds the bytes of the unfinished code point that b ends in.
func (d *Decoder) tail(b []byte, i int) {
	d.nHeld = uint8(copy(d.held[:], b[i:]))
}

// Append decodes the Steno bytes b as the next piece of the value and appends the UTF-8 text to dst. Its errors are those of Decode, with offsets
// from the start of the value. A Truncated error is not returned here, only by Flush, because the next piece may complete the code point. dst
// and b must not overlap.
func (d *Decoder) Append(dst []byte, b []byte) ([]byte, error) {
	d.init()
	saved := *d
	n := len(dst)
	fail := func(err error) ([]byte, error) {
		*d = saved
		return dst[:n], err
	}

	i := 0
	if d.nHeld != 0 {
		c, used, ok, err := d.head(b)
		if err != nil {
			return fail(err)
		}
		if !ok {
			d.pos += len(b)
			return dst, nil
		}
		if c >= 0xD800 && c <= 0xDFFF {
			reason := LoneSurrogate
			if d.d.lastHigh && isLowSurrogate(c) {
				reason = SurrogatePair
			}
			return fail(&DecodeError{Reason: reason, Offset: d.pos - int(saved.nHeld)})
		}
		d.d.advance(c, int(saved.nHeld)+used)
		d.d.lastHigh = false
		dst = utf8.AppendRune(dst, c)
		i = used
	}

	var j int
	var reason Reason
	dst, j, reason = decodeToUTF8(&d.d, dst, b[i:])
	switch reason {
	case 0:
	case Truncated:
		d.tail(b, i+j)
	default:
		return fail(&DecodeError{Reason: reason, Offset: d.pos + i + j})
	}
	d.pos += len(b)
	return dst, nil
}

// AppendRunes decodes the Steno bytes b as the next piece of the value and appends its code points to dst. Its errors are those of DecodeRunes,
// with offsets from the start of the value; Truncated is returned only by Flush.
func (d *Decoder) AppendRunes(dst []rune, b []byte) ([]rune, error) {
	d.init()
	saved := *d
	n := len(dst)
	fail := func(err error) ([]rune, error) {
		*d = saved
		return dst[:n], err
	}

	i := 0
	if d.nHeld != 0 {
		c, used, ok, err := d.head(b)
		if err != nil {
			return fail(err)
		}
		if !ok {
			d.pos += len(b)
			return dst, nil
		}
		if d.d.lastHigh && isLowSurrogate(c) {
			return fail(&DecodeError{Reason: SurrogatePair, Offset: d.pos - int(saved.nHeld)})
		}
		d.d.advance(c, int(saved.nHeld)+used)
		d.d.lastHigh = isHighSurrogate(c)
		dst = append(dst, c)
		i = used
	}

	var j int
	var reason Reason
	dst, j, reason = decodeToRunes(&d.d, dst, b[i:])
	switch reason {
	case 0:
	case Truncated:
		d.tail(b, i+j)
	default:
		return fail(&DecodeError{Reason: reason, Offset: d.pos + i + j})
	}
	d.pos += len(b)
	return dst, nil
}

// AppendUTF16 decodes the Steno bytes b as the next piece of the value and appends it to dst as UTF-16 code units. Its errors are those of
// DecodeUTF16, with offsets from the start of the value; Truncated is returned only by Flush.
func (d *Decoder) AppendUTF16(dst []uint16, b []byte) ([]uint16, error) {
	d.init()
	saved := *d
	n := len(dst)
	fail := func(err error) ([]uint16, error) {
		*d = saved
		return dst[:n], err
	}

	i := 0
	if d.nHeld != 0 {
		c, used, ok, err := d.head(b)
		if err != nil {
			return fail(err)
		}
		if !ok {
			d.pos += len(b)
			return dst, nil
		}
		if d.d.lastHigh && isLowSurrogate(c) {
			return fail(&DecodeError{Reason: SurrogatePair, Offset: d.pos - int(saved.nHeld)})
		}
		d.d.advance(c, int(saved.nHeld)+used)
		d.d.lastHigh = isHighSurrogate(c)
		if c >= 0x10000 {
			c -= 0x10000
			dst = append(dst, uint16(0xD800+c>>10), uint16(0xDC00+c&0x3FF))
		} else {
			dst = append(dst, uint16(c))
		}
		i = used
	}

	var j int
	var reason Reason
	dst, j, reason = decodeToUTF16(&d.d, dst, b[i:])
	switch reason {
	case 0:
	case Truncated:
		d.tail(b, i+j)
	default:
		return fail(&DecodeError{Reason: reason, Offset: d.pos + i + j})
	}
	d.pos += len(b)
	return dst, nil
}

// Flush reports whether the bytes given so far end at the end of a code point. If the decoder holds the bytes of an unfinished code point, Flush
// returns a *DecodeError with the reason Truncated, and keeps the bytes. Flush does not start a new value: call Reset for that.
func (d *Decoder) Flush() error {
	if d.nHeld != 0 {
		return &DecodeError{Reason: Truncated, Offset: d.pos - int(d.nHeld)}
	}
	return nil
}
