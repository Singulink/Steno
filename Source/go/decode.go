package steno

import "encoding/binary"

// decState is the state of a decoder with the layout of that state.
type decState struct {
	s        state
	l        *layout
	recent   recentLayouts
	lastHigh bool // the last code point was a high surrogate code point
}

func (d *decState) reset() {
	d.s = initialState
	d.l = initialLayout
	d.lastHigh = false
}

// read reads the code point whose encoding starts at b[i] (§9.2) in the current layout, and returns it with the number of bytes it took. It does
// not apply rule 5, which needs the code point before, and it does not update the state. Truncated means that b ends before the code point does.
func (d *decState) read(b []byte, i int) (c int32, n int, reason Reason) {
	l := d.l
	x := b[i]
	if o := uint32(x) - uint32(l.mainLead); o < l.mainLen {
		return l.mainLo + int32(o), 1, 0
	}
	r := l.findLead(x)
	if r == nil {
		if x == 0 {
			return 0, 0, ZeroByte
		}
		return 0, 0, UnusedLead
	}
	o := int(x - r.first)
	k := int(r.size)
	for j := 1; j < k; j++ {
		if i+j >= len(b) {
			return 0, 0, Truncated
		}
		t := b[i+j]
		if t < trailMin {
			if t == 0 {
				return 0, 0, ZeroByte
			}
			return 0, 0, InvalidTrail
		}
		o = o*base + int(t) - trailMin
	}
	if o > int(r.hi-r.lo) {
		return 0, 0, PastRange
	}
	return r.lo + int32(o), k, 0
}

// advance updates the state after the code point c, which took k bytes (§7).
func (d *decState) advance(c int32, k int) {
	if k == 1 {
		d.s.updateOneByte(c)
		return
	}
	old := d.s
	d.s.update(c, k)
	if !d.s.sameLayout(&old) {
		d.l = d.recent.of(&d.s, d.l)
	}
}

// The decode loops below read b from the start and stop at the first code point that is rejected. They return the extended output, the index in
// b where the rejected code point starts (len(b) if none is), and the reason. They keep d.lastHigh up to date for rule 5.
//
// Their hot paths need no state update beyond a counter and never change the layout (FORMAT.md §14.2): runs of printable ASCII (found 8 bytes
// at a time), code points of the main window, and the 2-byte ideographs or Hangul syllables of the wide contexts.

// wide reads a code point of the wide range of the layout at b[i], or returns false.
func (l *layout) wide(b []byte, i int) (int32, bool) {
	x := b[i]
	if x < l.wideFirst || x > l.wideLast || i+1 >= len(b) || b[i+1] < trailMin {
		return 0, false
	}
	c := l.wideSpanLo + int32(x-l.wideFirst)*base + int32(b[i+1]) - trailMin
	return c, uint32(c-l.wideLo) < l.wideLen
}

// appendUTF8 appends the UTF-8 form of c, which is not a surrogate.
func appendUTF8(dst []byte, c int32) []byte {
	switch {
	case c < 0x80:
		return append(dst, byte(c))
	case c < 0x800:
		return append(dst, 0xC0|byte(c>>6), 0x80|byte(c)&0x3F)
	case c < 0x10000:
		return append(dst, 0xE0|byte(c>>12), 0x80|byte(c>>6)&0x3F, 0x80|byte(c)&0x3F)
	default:
		return append(dst, 0xF0|byte(c>>18), 0x80|byte(c>>12)&0x3F, 0x80|byte(c>>6)&0x3F, 0x80|byte(c)&0x3F)
	}
}

// appendUTF16 appends the UTF-16 form of c by FORMAT.md §3.
func appendUTF16(dst []uint16, c int32) []uint16 {
	if c >= 0x10000 {
		c -= 0x10000
		return append(dst, uint16(0xD800+c>>10), uint16(0xDC00+c&0x3FF))
	}
	return append(dst, uint16(c))
}

func decodeToUTF8(d *decState, dst []byte, b []byte) ([]byte, int, Reason) {
	l := d.l
	ad := l.asciiDec
	high := d.lastHigh
	i := 0
	for i < len(b) {
		x := b[i]
		if a := ad[x]; a != 0 {
			high = false
			if i+8 <= len(b) {
				// Printable ASCII, 8 bytes at a time.
				w := binary.LittleEndian.Uint64(b[i:])
				if n := decRunLen(w, l.decRunAdd); n > 0 {
					dst = appendPart64(dst, w+0x1919191919191919, n)
					i += n
					continue
				}
			}
			dst = append(dst, a)
			i++
			continue
		}
		if o := uint32(x) - uint32(l.mainLead); o < l.mainLen {
			c := l.mainLo + int32(o)
			switch {
			case c < 0x800:
				dst = append(dst, 0xC0|byte(c>>6), 0x80|byte(c)&0x3F)
			case c < 0x10000:
				dst = append(dst, 0xE0|byte(c>>12), 0x80|byte(c>>6)&0x3F, 0x80|byte(c)&0x3F)
			default:
				dst = appendUTF8(dst, c)
			}
			if d.s.conf < 2 {
				d.s.conf++
			}
			high = false
			i++
			continue
		}
		if x >= l.wideFirst && x <= l.wideLast && i+1 < len(b) && b[i+1] >= trailMin {
			c := l.wideSpanLo + int32(x-l.wideFirst)*base + int32(b[i+1]) - trailMin
			if uint32(c-l.wideLo) < l.wideLen {
				dst = append(dst, 0xE0|byte(c>>12), 0x80|byte(c>>6)&0x3F, 0x80|byte(c)&0x3F)
				d.s.pending = noWindow
				high = false
				i += 2
				continue
			}
		}

		c, n, reason := d.read(b, i)
		if reason == 0 && c >= 0xD800 && c <= 0xDFFF {
			reason = LoneSurrogate
			if high && isLowSurrogate(c) {
				reason = SurrogatePair
			}
		}
		if reason != 0 {
			d.lastHigh = high
			return dst, i, reason
		}
		d.advance(c, n)
		high = false
		dst = appendUTF8(dst, c)
		l = d.l
		ad = l.asciiDec
		i += n
	}
	d.lastHigh = high
	return dst, i, 0
}

func decodeToRunes(d *decState, dst []rune, b []byte) ([]rune, int, Reason) {
	l := d.l
	ad := l.asciiDec
	high := d.lastHigh
	i := 0
	for i < len(b) {
		x := b[i]
		if a := ad[x]; a != 0 {
			// A run of ASCII, a byte at a time: widening the bytes costs more than finding the run 8 bytes at a time saves.
			high = false
			dst = append(dst, rune(a))
			for i++; i < len(b); i++ {
				if a = ad[b[i]]; a == 0 {
					break
				}
				dst = append(dst, rune(a))
			}
			continue
		}
		if o := uint32(x) - uint32(l.mainLead); o < l.mainLen {
			dst = append(dst, l.mainLo+int32(o))
			if d.s.conf < 2 {
				d.s.conf++
			}
			high = false
			i++
			continue
		}
		if c, ok := l.wide(b, i); ok {
			dst = append(dst, c)
			d.s.pending = noWindow
			high = false
			i += 2
			continue
		}

		c, n, reason := d.read(b, i)
		if reason == 0 && high && isLowSurrogate(c) {
			reason = SurrogatePair
		}
		if reason != 0 {
			d.lastHigh = high
			return dst, i, reason
		}
		d.advance(c, n)
		high = isHighSurrogate(c)
		dst = append(dst, c)
		l = d.l
		ad = l.asciiDec
		i += n
	}
	d.lastHigh = high
	return dst, i, 0
}

func decodeToUTF16(d *decState, dst []uint16, b []byte) ([]uint16, int, Reason) {
	l := d.l
	ad := l.asciiDec
	high := d.lastHigh
	i := 0
	for i < len(b) {
		x := b[i]
		if a := ad[x]; a != 0 {
			// A run of ASCII, a byte at a time: widening the bytes costs more than finding the run 8 bytes at a time saves.
			high = false
			dst = append(dst, uint16(a))
			for i++; i < len(b); i++ {
				if a = ad[b[i]]; a == 0 {
					break
				}
				dst = append(dst, uint16(a))
			}
			continue
		}
		if o := uint32(x) - uint32(l.mainLead); o < l.mainLen {
			dst = appendUTF16(dst, l.mainLo+int32(o))
			if d.s.conf < 2 {
				d.s.conf++
			}
			high = false
			i++
			continue
		}
		if c, ok := l.wide(b, i); ok {
			dst = append(dst, uint16(c))
			d.s.pending = noWindow
			high = false
			i += 2
			continue
		}

		c, n, reason := d.read(b, i)
		if reason == 0 && high && isLowSurrogate(c) {
			reason = SurrogatePair
		}
		if reason != 0 {
			d.lastHigh = high
			return dst, i, reason
		}
		d.advance(c, n)
		high = isHighSurrogate(c)
		dst = appendUTF16(dst, c)
		l = d.l
		ad = l.asciiDec
		i += n
	}
	d.lastHigh = high
	return dst, i, 0
}

// Decode returns the text of the value in the Steno bytes b as a string.
//
// If b is not valid Steno data, Decode returns "" and a *DecodeError with the reason of FORMAT.md §10.1 and the offset of the code point that
// broke the rule. If the value has a surrogate code point, which a Go string cannot hold, the error has the reason LoneSurrogate; decoding stops
// at the first such code point, so bytes after it are not checked. Decode never replaces anything with U+FFFD.
func Decode(b []byte) (string, error) {
	if len(b) <= 256 {
		var buf [1024]byte
		out, err := AppendDecode(buf[:0], b)
		if err != nil {
			return "", err
		}
		return string(out), nil
	}
	out, err := AppendDecode(make([]byte, 0, len(b)+len(b)/2), b)
	if err != nil {
		return "", err
	}
	return string(out), nil
}

// AppendDecode appends the UTF-8 text of the value in the Steno bytes b to dst and returns the extended slice. It needs at most
// MaxDecodedLen(len(b)) bytes. It writes no byte past the bytes it appends. dst and b must not overlap.
//
// Errors are those of Decode. On an error it returns dst with its length, and its bytes up to that length, unchanged; the spare capacity beyond it may have been written.
func AppendDecode(dst []byte, b []byte) ([]byte, error) {
	var d decState
	d.reset()
	n := len(dst)
	out, i, reason := decodeToUTF8(&d, dst, b)
	if reason != 0 {
		return dst[:n], &DecodeError{Reason: reason, Offset: i}
	}
	return out, nil
}

// DecodeRunes returns the code points of the value in the Steno bytes b. Every valid byte string decodes, also one whose value has surrogate code
// points: each is a rune in U+D800..U+DFFF. If b is not valid Steno data, DecodeRunes returns nil and a *DecodeError.
func DecodeRunes(b []byte) ([]rune, error) {
	return AppendDecodeRunes(make([]rune, 0, len(b)), b)
}

// AppendDecodeRunes is DecodeRunes that appends to dst. It needs at most MaxDecodedLenRunes(len(b)) runes. On an error it returns dst with its
// length, and its runes up to that length, unchanged; the spare capacity beyond it may have been written.
func AppendDecodeRunes(dst []rune, b []byte) ([]rune, error) {
	var d decState
	d.reset()
	n := len(dst)
	out, i, reason := decodeToRunes(&d, dst, b)
	if reason != 0 {
		return dst[:n], &DecodeError{Reason: reason, Offset: i}
	}
	return out, nil
}

// DecodeUTF16 returns the value in the Steno bytes b as UTF-16 code units, with a code point from U+10000 as its surrogate pair and a surrogate
// code point as one code unit (FORMAT.md §3). Every valid byte string decodes. If b is not valid Steno data, DecodeUTF16 returns nil and a
// *DecodeError.
func DecodeUTF16(b []byte) ([]uint16, error) {
	return AppendDecodeUTF16(make([]uint16, 0, len(b)+len(b)/4), b)
}

// AppendDecodeUTF16 is DecodeUTF16 that appends to dst. It needs at most MaxDecodedLenUTF16(len(b)) code units. On an error it returns dst
// with its length, and its code units up to that length, unchanged; the spare capacity beyond it may have been written.
func AppendDecodeUTF16(dst []uint16, b []byte) ([]uint16, error) {
	var d decState
	d.reset()
	n := len(dst)
	out, i, reason := decodeToUTF16(&d, dst, b)
	if reason != 0 {
		return dst[:n], &DecodeError{Reason: reason, Offset: i}
	}
	return out, nil
}
