package steno

// This file holds the windows (FORMAT.md §4), the state (§5) and the state update (§7).

const (
	ctxGeneral uint8 = iota
	ctxCJK
	ctxHangul
)

const (
	slotLetters uint8 = iota
	slotHiragana
	slotKatakana
)

// noWindow marks a window field that is none.
const noWindow int32 = -1

// windowOf returns the first and last code point of window(c), or (noWindow, noWindow) if c has no window (§4).
func windowOf(c int32) (start, end int32) {
	switch {
	case c <= 0x00BF:
		return noWindow, noWindow
	case c <= 0x013F:
		return 0x00C0, 0x013F
	case c <= 0x01BF:
		return 0x0140, 0x01BF
	case c <= 0x023F:
		return 0x01C0, 0x023F
	case c <= 0x02BF:
		return 0x0240, 0x02BF
	case c <= 0x02FF:
		return 0x02C0, 0x02FF
	case c <= 0x036F:
		return 0x0300, 0x036F
	case c <= 0x037F:
		return noWindow, noWindow
	case c <= 0x04FF:
		return block(c)
	case c <= 0x052F:
		return 0x0500, 0x052F
	case c <= 0x058F:
		return 0x0530, 0x058F
	case c <= 0x05FF:
		return 0x0590, 0x05FF
	case c <= 0x1FFF:
		return block(c)
	case c <= 0x20FF:
		return noWindow, noWindow
	case c <= 0x2FFF:
		return block(c)
	case c <= 0x30FF:
		return noWindow, noWindow
	case c <= 0x33FF:
		return block(c)
	case c <= 0x9FFF:
		return noWindow, noWindow
	case c <= 0xABFF:
		return block(c)
	case c <= 0xDFFF:
		return noWindow, noWindow
	case c <= 0x1FFFF:
		return block(c)
	case c <= 0x3FFFF:
		return noWindow, noWindow
	default:
		return block(c)
	}
}

func block(c int32) (start, end int32) {
	s := c &^ 0x7F
	return s, s + 0x7F
}

// windowEnd returns the last code point of the window that starts at start.
func windowEnd(start int32) int32 {
	_, end := windowOf(start)
	return end
}

// state is the state of §5. Windows are given by their first code point.
type state struct {
	main    int32
	second  int32 // noWindow for none
	pending int32 // noWindow for none
	ctx     uint8
	slot    uint8
	conf    uint8
}

var initialState = state{main: 0x00C0, second: noWindow, pending: noWindow, ctx: ctxGeneral, slot: slotLetters}

// key returns the fields that the layout depends on (§5), packed: the context, the kana slot in the CJK context only, the main window and the
// second window.
func (s *state) key() uint64 {
	var slot uint64
	if s.ctx == ctxCJK {
		slot = uint64(s.slot)
	}

	return uint64(s.ctx) | slot<<2 | uint64(uint32(s.main))<<4 | uint64(uint32(s.second+1))<<26
}

// sameLayout reports whether s and t have the same layout.
func (s *state) sameLayout(t *state) bool {
	return s.ctx == t.ctx && s.main == t.main && s.second == t.second && (s.ctx != ctxCJK || s.slot == t.slot)
}

func isHighSurrogate(c int32) bool { return c >= 0xD800 && c <= 0xDBFF }
func isLowSurrogate(c int32) bool  { return c >= 0xDC00 && c <= 0xDFFF }

// update is the state update of §7.1 after the code point c, which took k bytes.
func (s *state) update(c int32, k int) {
	// Step 1.
	if c <= 0x7F {
		if s.ctx == ctxCJK && ((c >= 0x41 && c <= 0x5A) || (c >= 0x61 && c <= 0x7A)) {
			s.slot = slotLetters
		}
		return
	}

	// Step 2.
	if c >= 0xAC00 && c <= 0xD7A3 {
		s.pending = noWindow
		s.ctx = ctxHangul
		return
	}

	// Step 3.
	if (c >= 0x3040 && c <= 0x30FF) || (c >= 0x3400 && c <= 0x9FFF) || (c >= 0x20000 && c <= 0x3FFFF) ||
		(c >= 0x3000 && c <= 0x303F && s.ctx != ctxHangul) {
		if s.ctx != ctxCJK {
			s.ctx = ctxCJK
			s.slot = slotLetters
		}
		s.pending = noWindow
		if c >= 0x3041 && c <= 0x3096 {
			s.slot = slotHiragana
		} else if c >= 0x30A1 && c <= 0x30FA {
			s.slot = slotKatakana
		}
		return
	}

	// Step 4.
	if s.ctx != ctxGeneral && c >= 0xFF00 && c <= 0xFFFF {
		return
	}

	// Step 5.
	w, _ := windowOf(c)
	if w == noWindow {
		return
	}

	// Step 6.
	if s.ctx == ctxGeneral && w == s.main {
		if s.conf < 2 {
			s.conf++
		}
		return
	}

	// Step 7.
	if k >= 3 {
		s.second = w
	}

	// Step 8.
	if s.ctx == ctxGeneral {
		if s.conf > 0 {
			s.conf--
			return
		}
	} else if s.pending != w {
		s.pending = w
		return
	}

	// Step 9.
	if s.main != w {
		s.second = s.main
	} else if s.second == w {
		s.second = noWindow
	}
	s.main = w
	s.ctx = ctxGeneral
	s.pending = noWindow
	s.conf = 0
}

// updateOneByte is update(c, 1) for a code point c that took 1 byte (FORMAT.md §14.2). Such a code point never changes the layout: in the general
// context it is ASCII or in the main window, and in the CJK context it is ASCII, U+3000..U+300F, U+FF00..U+FF1F or a kana of the kana slot.
func (s *state) updateOneByte(c int32) {
	if c < 0x80 {
		return
	}
	if s.ctx == ctxGeneral {
		if s.conf < 2 {
			s.conf++
		}
	} else if c >= 0x3000 && c <= 0x30FF {
		s.pending = noWindow
	}
}
