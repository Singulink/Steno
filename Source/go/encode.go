package steno

// encState is the state of an encoder with the layout of that state.
type encState struct {
	s      state
	l      *layout
	recent recentLayouts
}

func (e *encState) reset() {
	e.s = initialState
	e.l = initialLayout
}

// put appends the bytes of the code point c (§8.2) and updates the state (§7).
func (e *encState) put(dst []byte, c int32) []byte {
	l := e.l
	if o := uint32(c - l.mainLo); o < l.mainLen {
		if e.s.conf < 2 {
			e.s.conf++
		}
		return append(dst, l.mainLead+uint8(o))
	}
	if uint32(c-l.wideLo) < l.wideLen {
		e.s.pending = noWindow
		o := uint32(c - l.wideSpanLo)
		return append(dst, l.wideFirst+uint8(o/base), uint8(o%base+trailMin))
	}
	return e.putSlow(dst, c)
}

// putSlow is put for a code point that is not in the main window or the wide range of the layout.
func (e *encState) putSlow(dst []byte, c int32) []byte {
	r := e.l.find(c)
	dst = appendCode(dst, r, c)
	if r.size == 1 {
		e.s.updateOneByte(c)
		return dst
	}

	old := e.s
	e.s.update(c, int(r.size))
	if !e.s.sameLayout(&old) {
		e.l = e.recent.of(&e.s, e.l)
	}
	return dst
}

// Results of decodeUTF8.
const (
	utf8OK = iota
	utf8Invalid
	utf8Short // valid so far, but the input ends before the sequence does
)

// decodeUTF8 reads the UTF-8 sequence that starts at s[i]. It accepts exactly what unicode/utf8 accepts as one rune: no overlong forms, no
// surrogates and nothing above U+10FFFF.
func decodeUTF8[T string | []byte](s T, i int) (c int32, n int, status int) {
	b := s[i]
	var lo, hi byte = 0x80, 0xBF
	switch {
	case b < 0x80:
		return int32(b), 1, utf8OK
	case b < 0xC2:
		return 0, 0, utf8Invalid
	case b < 0xE0:
		n, c = 2, int32(b&0x1F)
	case b < 0xF0:
		n, c = 3, int32(b&0x0F)
		if b == 0xE0 {
			lo = 0xA0
		} else if b == 0xED {
			hi = 0x9F
		}
	case b < 0xF5:
		n, c = 4, int32(b&0x07)
		if b == 0xF0 {
			lo = 0x90
		} else if b == 0xF4 {
			hi = 0x8F
		}
	default:
		return 0, 0, utf8Invalid
	}

	for j := 1; j < n; j++ {
		if i+j >= len(s) {
			return 0, 0, utf8Short
		}
		t := s[i+j]
		if t < lo || t > hi {
			return 0, 0, utf8Invalid
		}
		lo, hi = 0x80, 0xBF
		c = c<<6 | int32(t&0x3F)
	}
	return c, n, utf8OK
}

// encodeUTF8 encodes the UTF-8 text s. It stops at the first sequence that is not valid or not complete, and returns where it starts and why.
//
// The hot paths work on the UTF-8 bytes directly: printable ASCII 8 bytes at a time, and sequences of 2 and 3 bytes decoded in place and, when
// they are in the main window or the wide range, encoded in place. These never change the layout.
func encodeUTF8[T string | []byte](e *encState, dst []byte, s T) ([]byte, int, int) {
	l := e.l
	ae := l.asciiEnc
	i := 0
	for i < len(s) {
		b := s[i]
		if b < 0x80 {
			if i+8 <= len(s) {
				// Printable ASCII, 8 bytes at a time.
				w := load64(s, i)
				if n := encRunLen(w, l.encRunAdd); n > 0 {
					dst = appendPart64(dst, w-0x1919191919191919, n)
					i += n
					continue
				}
			}
			if x := ae[b]; x != 0 {
				dst = append(dst, x)
			} else {
				dst = e.putSlow(dst, int32(b))
				l = e.l
				ae = l.asciiEnc
			}
			i++
			continue
		}

		var c int32
		n := 0
		if b < 0xE0 {
			if b >= 0xC2 && i+1 < len(s) && s[i+1]&0xC0 == 0x80 {
				c, n = int32(b&0x1F)<<6|int32(s[i+1]&0x3F), 2
			}
		} else if b < 0xF0 && i+2 < len(s) {
			b1, b2 := s[i+1], s[i+2]
			if b1&0xC0 == 0x80 && b2&0xC0 == 0x80 {
				c = int32(b&0x0F)<<12 | int32(b1&0x3F)<<6 | int32(b2&0x3F)
				if c >= 0x800 && (c < 0xD800 || c > 0xDFFF) {
					n = 3
				}
			}
		}
		if n == 0 {
			var status int
			if c, n, status = decodeUTF8(s, i); status != utf8OK {
				return dst, i, status
			}
		}
		i += n

		if o := uint32(c - l.mainLo); o < l.mainLen {
			dst = append(dst, l.mainLead+uint8(o))
			if e.s.conf < 2 {
				e.s.conf++
			}
		} else if uint32(c-l.wideLo) < l.wideLen {
			o := uint32(c - l.wideSpanLo)
			dst = append(dst, l.wideFirst+uint8(o/base), uint8(o%base+trailMin))
			e.s.pending = noWindow
		} else {
			dst = e.putSlow(dst, c)
			l = e.l
			ae = l.asciiEnc
		}
	}
	return dst, i, utf8OK
}

// encodeRunes encodes code points. prevHigh says whether the code point before r[0] was a high surrogate. It returns the index of the first rune
// that is rejected, with the reason, or len(r) and 0.
func encodeRunes(e *encState, dst []byte, r []rune, prevHigh bool) ([]byte, int, EncodeReason) {
	l := e.l
	ae := l.asciiEnc
	for i, c := range r {
		if uint32(c) < 0x80 {
			if x := ae[c]; x != 0 {
				dst = append(dst, x)
				prevHigh = false
				continue
			}
		} else if o := uint32(c - l.mainLo); o < l.mainLen {
			dst = append(dst, l.mainLead+uint8(o))
			if e.s.conf < 2 {
				e.s.conf++
			}
			prevHigh = false
			continue
		} else if uint32(c-l.wideLo) < l.wideLen {
			o := uint32(c - l.wideSpanLo)
			dst = append(dst, l.wideFirst+uint8(o/base), uint8(o%base+trailMin))
			e.s.pending = noWindow
			prevHigh = false
			continue
		} else if uint32(c) > 0x10FFFF {
			return dst, i, InvalidRune
		} else if prevHigh && isLowSurrogate(c) {
			return dst, i, NotAValue
		}
		prevHigh = isHighSurrogate(c)
		dst = e.putSlow(dst, c)
		l = e.l
		ae = l.asciiEnc
	}
	return dst, len(r), 0
}

// encodeUTF16 encodes UTF-16 code units as §3 reads them. If hold is set and u ends in a high surrogate, that unit is not encoded, and the
// returned count is len(u) - 1.
func encodeUTF16(e *encState, dst []byte, u []uint16, hold bool) ([]byte, int) {
	l := e.l
	ae := l.asciiEnc
	for i := 0; i < len(u); i++ {
		c := int32(u[i])
		if c < 0x80 {
			if c >= 0x20 {
				var n int
				dst, n = appendASCIIRun16(dst, u[i:], l.encRunAdd16)
				if n > 0 {
					i += n - 1
					continue
				}
			}
			if x := ae[c]; x != 0 {
				dst = append(dst, x)
				continue
			}
		} else if o := uint32(c - l.mainLo); o < l.mainLen {
			dst = append(dst, l.mainLead+uint8(o))
			if e.s.conf < 2 {
				e.s.conf++
			}
			continue
		} else if uint32(c-l.wideLo) < l.wideLen {
			o := uint32(c - l.wideSpanLo)
			dst = append(dst, l.wideFirst+uint8(o/base), uint8(o%base+trailMin))
			e.s.pending = noWindow
			continue
		} else if isHighSurrogate(c) {
			if i+1 < len(u) {
				if d := int32(u[i+1]); isLowSurrogate(d) {
					c = 0x10000 + (c-0xD800)<<10 + (d - 0xDC00)
					i++
				}
			} else if hold {
				return dst, i
			}
		}
		dst = e.put(dst, c)
		l = e.l
		ae = l.asciiEnc
	}
	return dst, len(u)
}

// Encode returns the Steno bytes of the value in the UTF-8 string s.
//
// If s is not valid UTF-8, Encode returns nil and an *EncodeError with the reason InvalidUTF8. It never replaces a bad sequence with U+FFFD. A
// Go string cannot hold a surrogate code point; use EncodeRunes or EncodeUTF16 for a value that has one.
func Encode(s string) ([]byte, error) {
	if len(s) <= 256 {
		var buf [512]byte
		b, err := AppendEncode(buf[:0], s)
		if err != nil {
			return nil, err
		}
		return append(make([]byte, 0, len(b)), b...), nil
	}
	return AppendEncode(make([]byte, 0, len(s)+len(s)/4+16), s)
}

// AppendEncode appends the Steno bytes of the value in the UTF-8 string s to dst and returns the extended slice. It needs at most
// MaxEncodedLen(len(s)) bytes. It writes no byte past the bytes it appends, so data in the spare capacity of dst stays as it is.
//
// If s is not valid UTF-8, AppendEncode returns an *EncodeError with the reason InvalidUTF8, and dst with its length, and its bytes up to that
// length, unchanged; the spare capacity beyond it may have been written.
func AppendEncode(dst []byte, s string) ([]byte, error) {
	var e encState
	e.reset()
	n := len(dst)
	out, i, status := encodeUTF8(&e, dst, s)
	if status != utf8OK {
		return dst[:n], &EncodeError{Reason: InvalidUTF8, Offset: i}
	}
	return out, nil
}

// EncodeRunes returns the Steno bytes of the value whose code points are r. A rune in U+D800..U+DFFF is a surrogate code point of its own.
//
// If a high surrogate code point is directly followed by a low one, r is not a value: EncodeRunes returns an *EncodeError with the reason
// NotAValue. (Combine such a pair into one rune first, or use EncodeUTF16, which does.) A rune that is negative or above U+10FFFF gives the
// reason InvalidRune.
func EncodeRunes(r []rune) ([]byte, error) {
	return AppendEncodeRunes(make([]byte, 0, len(r)+len(r)/2+16), r)
}

// AppendEncodeRunes is EncodeRunes that appends to dst. It needs at most MaxEncodedLenRunes(len(r)) bytes. On an
// error it returns dst with its length, and its bytes up to that length, unchanged; the spare capacity beyond it may have been written.
func AppendEncodeRunes(dst []byte, r []rune) ([]byte, error) {
	var e encState
	e.reset()
	n := len(dst)
	out, i, reason := encodeRunes(&e, dst, r, false)
	if reason != 0 {
		return dst[:n], &EncodeError{Reason: reason, Offset: i}
	}
	return out, nil
}

// EncodeUTF16 returns the Steno bytes of the value in the UTF-16 code units u. Every sequence of code units is a value: a surrogate that is not
// part of a pair is a code point of its own (FORMAT.md §3). EncodeUTF16 never fails.
func EncodeUTF16(u []uint16) []byte {
	return AppendEncodeUTF16(make([]byte, 0, len(u)+len(u)/2+16), u)
}

// AppendEncodeUTF16 is EncodeUTF16 that appends to dst. It needs at most MaxEncodedLenUTF16(len(u)) bytes.
func AppendEncodeUTF16(dst []byte, u []uint16) []byte {
	var e encState
	e.reset()
	dst, _ = encodeUTF16(&e, dst, u, false)
	return dst
}
