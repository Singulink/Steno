package steno

import (
	"errors"
	"sort"
	"sync"
)

// This file is a plain codec written from FORMAT.md, in the structure of Tests/Singulink.Steno.Tests/ReferenceSteno.cs. It shares no code with
// the package: it builds the layout again before every code point, so it is slow, but each step can be checked against the specification. The
// tests use it as an oracle for the package.

type refWindow struct{ start, end int }

type refState struct {
	ctx     int // 0 general, 1 CJK, 2 Hangul
	slot    int // 0 letters, 1 hiragana, 2 katakana
	main    refWindow
	second  *refWindow
	conf    int
	pending *refWindow
}

type refRange struct{ lo, hi, size, first, leads int }

func refBlock(c int) *refWindow {
	s := c - c%0x80
	return &refWindow{s, s + 0x7F}
}

func refWindowOf(c int) *refWindow {
	switch {
	case c <= 0x00BF:
		return nil
	case c <= 0x013F:
		return &refWindow{0x00C0, 0x013F}
	case c <= 0x01BF:
		return &refWindow{0x0140, 0x01BF}
	case c <= 0x023F:
		return &refWindow{0x01C0, 0x023F}
	case c <= 0x02BF:
		return &refWindow{0x0240, 0x02BF}
	case c <= 0x02FF:
		return &refWindow{0x02C0, 0x02FF}
	case c <= 0x036F:
		return &refWindow{0x0300, 0x036F}
	case c <= 0x037F:
		return nil
	case c <= 0x04FF:
		return refBlock(c)
	case c <= 0x052F:
		return &refWindow{0x0500, 0x052F}
	case c <= 0x058F:
		return &refWindow{0x0530, 0x058F}
	case c <= 0x05FF:
		return &refWindow{0x0590, 0x05FF}
	case c <= 0x1FFF:
		return refBlock(c)
	case c <= 0x20FF:
		return nil
	case c <= 0x2FFF:
		return refBlock(c)
	case c <= 0x30FF:
		return nil
	case c <= 0x33FF:
		return refBlock(c)
	case c <= 0x9FFF:
		return nil
	case c <= 0xABFF:
		return refBlock(c)
	case c <= 0xDFFF:
		return nil
	case c <= 0x1FFFF:
		return refBlock(c)
	case c <= 0x3FFFF:
		return nil
	default:
		return refBlock(c)
	}
}

// refPow248 is 248^e. Trail bytes are digits in base 248, plus 8 (FORMAT.md §8.2).
func refPow248(e int) int {
	r := 1
	for i := 0; i < e; i++ {
		r *= 248
	}
	return r
}

func refLayout(s *refState) []refRange {
	type seg struct{ lo, hi, size int }
	segs := []seg{{0, 0x10FFFF, 0}}
	piece := func(lo, hi, size int) {
		if lo > hi {
			return
		}
		var next []seg
		for _, g := range segs {
			if g.hi < lo || g.lo > hi {
				next = append(next, g)
				continue
			}
			if g.lo < lo {
				next = append(next, seg{g.lo, lo - 1, g.size})
			}
			if g.hi > hi {
				next = append(next, seg{hi + 1, g.hi, g.size})
			}
		}
		next = append(next, seg{lo, hi, size})
		sort.Slice(next, func(i, j int) bool { return next[i].lo < next[j].lo })
		segs = next
	}

	m0, m1 := s.main.start, s.main.end
	if s.ctx == 0 {
		piece(max(0x80, m0-248), m0-1, 2)
		if m1 < 0x10FFFF {
			piece(m1+1, min(0x10FFFF, m1+248), 2)
		}
	}
	piece(0x00, 0x1F, 2)
	piece(0x09, 0x0A, 1)
	piece(0x0D, 0x0D, 1)
	if s.ctx == 1 && s.slot != 0 {
		piece(0x20, 0x40, 1)
		piece(0x41, 0x7E, 2)
	} else {
		piece(0x20, 0x7E, 1)
	}
	piece(0x7F, 0xBF, 2)
	if s.ctx != 1 {
		piece(0x80, 0xEFF, 2)
	}
	piece(0x2000, 0x20CF, 2)
	if s.second != nil {
		piece(s.second.start, s.second.end, 2)
	}
	switch s.ctx {
	case 0:
		piece(m0, m1, 1)
	case 2:
		piece(m0, m1, 2)
		piece(0x3000, 0x303F, 2)
		piece(0xAC00, 0xD7A3, 2)
		piece(0xFF00, 0xFFFF, 2)
	case 1:
		piece(m0, m1, 2)
		piece(0x3000, 0x300F, 1)
		piece(0x3010, 0x30FF, 2)
		if s.slot == 1 {
			piece(0x3041, 0x3096, 1)
		}
		if s.slot == 2 {
			piece(0x30A1, 0x30FC, 1)
		}
		piece(0x4E00, 0x9FFF, 2)
		piece(0xFF00, 0xFFFF, 2)
		if s.slot == 0 {
			piece(0xFF00, 0xFF1F, 1)
		}
	}

	var sized []seg
	for i := 0; i < len(segs); {
		if segs[i].size != 0 {
			sized = append(sized, segs[i])
			i++
			continue
		}
		lo, hi := segs[i].lo, segs[i].hi
		i++
		for i < len(segs) && segs[i].size == 0 {
			hi = segs[i].hi
			i++
		}
		start := lo
		for _, b := range []int{0x10FC0, 0x20000} { // the runs of §6.3: ..U+10FBF, U+10FC0..U+1FFFF, U+20000..
			if start < b && hi >= b {
				sized = append(sized, seg{start, b - 1, 0})
				start = b
			}
		}
		sized = append(sized, seg{start, hi, 0})
		for k := range sized {
			if sized[k].size == 0 {
				n := sized[k].hi - sized[k].lo + 1
				switch {
				case n <= 248:
					sized[k].size = 2
				case n <= 61504:
					sized[k].size = 3
				default:
					sized[k].size = 4
				}
			}
		}
	}

	var ranges []refRange
	lead := 1
	for j := 0; j < len(sized); {
		lo, hi, size := sized[j].lo, sized[j].hi, sized[j].size
		j++
		for j < len(sized) && sized[j].size == size && sized[j].lo == hi+1 {
			hi = sized[j].hi
			j++
		}
		n := hi - lo + 1
		per := refPow248(size - 1)
		leads := (n + per - 1) / per
		ranges = append(ranges, refRange{lo, hi, size, lead, leads})
		lead += leads
	}
	if lead-1 > 254 {
		panic("layout needs more than 254 lead bytes")
	}
	return ranges
}

// refLayouts keeps the layouts that refLayout built, by the fields of the state they depend on, only to make the tests faster. The layout is
// still built by refLayout from the state.
var (
	refLayoutsMu sync.Mutex
	refLayouts   = map[[5]int][]refRange{}
)

func refLayoutCached(s *refState) []refRange {
	k := [5]int{s.ctx, s.slot, s.main.start, -1, 0}
	if s.second != nil {
		k[3] = s.second.start
	}
	refLayoutsMu.Lock()
	defer refLayoutsMu.Unlock()
	l, ok := refLayouts[k]
	if !ok {
		l = refLayout(s)
		refLayouts[k] = l
	}
	return l
}

func refIn(c, lo, hi int) bool { return lo <= c && c <= hi }

func refUpdate(s *refState, c, n int) {
	if c <= 0x7F {
		if s.ctx == 1 && (refIn(c, 0x41, 0x5A) || refIn(c, 0x61, 0x7A)) {
			s.slot = 0
		}
		return
	}
	if refIn(c, 0xAC00, 0xD7A3) {
		s.pending = nil
		s.ctx = 2
		return
	}
	if refIn(c, 0x3040, 0x30FF) || refIn(c, 0x3400, 0x9FFF) || refIn(c, 0x20000, 0x3FFFF) || (refIn(c, 0x3000, 0x303F) && s.ctx != 2) {
		if s.ctx != 1 {
			s.ctx = 1
			s.slot = 0
		}
		s.pending = nil
		if refIn(c, 0x3041, 0x3096) {
			s.slot = 1
		} else if refIn(c, 0x30A1, 0x30FA) {
			s.slot = 2
		}
		return
	}
	if s.ctx != 0 && refIn(c, 0xFF00, 0xFFFF) {
		return
	}
	w := refWindowOf(c)
	if w == nil {
		return
	}
	if s.ctx == 0 && w.start == s.main.start {
		s.conf = min(s.conf+1, 2)
		return
	}
	if n > 2 {
		s.second = w
	}
	if s.ctx == 0 {
		if s.conf > 0 {
			s.conf--
			return
		}
	} else if s.pending == nil || s.pending.start != w.start {
		s.pending = w
		return
	}
	if s.main.start != w.start {
		m := s.main
		s.second = &m
	} else if s.second != nil && s.second.start == w.start {
		s.second = nil
	}
	s.main = *w
	s.ctx = 0
	s.pending = nil
	s.conf = 0
}

func refInitial() *refState { return &refState{main: refWindow{0xC0, 0x13F}} }

var errRefNotAValue = errors.New("not a value")

func refEncode(cps []int) ([]byte, error) {
	for i, c := range cps {
		if c < 0 || c > 0x10FFFF {
			return nil, errors.New("not a code point")
		}
		if i > 0 && refIn(cps[i-1], 0xD800, 0xDBFF) && refIn(c, 0xDC00, 0xDFFF) {
			return nil, errRefNotAValue
		}
	}
	s := refInitial()
	var out []byte
	for _, c := range cps {
		layout := refLayoutCached(s)
		var r refRange
		for _, x := range layout {
			if x.lo <= c && c <= x.hi {
				r = x
			}
		}
		o := c - r.lo
		k := r.size
		out = append(out, byte(r.first+o/refPow248(k-1)))
		rest := o % refPow248(k-1)
		for i := k - 2; i >= 0; i-- {
			p := refPow248(i)
			out = append(out, byte(rest/p+8))
			rest %= p
		}
		refUpdate(s, c, k)
	}
	return out, nil
}

// refDecode returns the code points, or the reason and offset of the rejection.
func refDecode(b []byte) ([]int, string, int) {
	s := refInitial()
	var out []int
	pos := 0
	for pos < len(b) {
		layout := refLayoutCached(s)
		x := int(b[pos])
		if x == 0 {
			return nil, "zero-byte", pos
		}
		var rg *refRange
		for i := range layout {
			if layout[i].first <= x && x < layout[i].first+layout[i].leads {
				rg = &layout[i]
				break
			}
		}
		if rg == nil {
			return nil, "unused-lead", pos
		}
		start := pos
		pos++
		o := x - rg.first
		for i := 0; i < rg.size-1; i++ {
			if pos >= len(b) {
				return nil, "truncated", start
			}
			u := int(b[pos])
			if u == 0 {
				return nil, "zero-byte", start
			}
			if u < 8 {
				return nil, "invalid-trail", start
			}
			o = o*248 + u - 8
			pos++
		}
		c := rg.lo + o
		if c > rg.hi {
			return nil, "past-range", start
		}
		if len(out) > 0 && refIn(out[len(out)-1], 0xD800, 0xDBFF) && refIn(c, 0xDC00, 0xDFFF) {
			return nil, "surrogate-pair", start
		}
		out = append(out, c)
		refUpdate(s, c, pos-start)
	}
	return out, "", 0
}
