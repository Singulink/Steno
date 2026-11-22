package steno

// This file is a second plain codec written from FORMAT.md, independently of the package and of reference_test.go, as a second oracle. It
// shares no code and no structure with either: the layout is built literally as §6.1's pseudocode says, on an array of one size per
// code point, and the ranges are read back from the array.

import (
	"os"
	"slices"
	"sync"
	"testing"
)

type oState struct {
	ctx     int // 0 general, 1 CJK, 2 Hangul
	slot    int // 0 letters, 1 hiragana, 2 katakana
	main    int // first code point of the main window
	second  int // -1 for none
	conf    int
	pending int // -1 for none
}

type oRange struct{ lo, hi, size, first, leads int }

func oInitial() oState { return oState{main: 0xC0, second: -1, pending: -1} }

// oWindow is window(c) of §4 as (start, end), or (-1, -1).
func oWindow(c int) (int, int) {
	type row struct{ lo, hi, ws, we int } // ws == -2: block; ws == -1: none
	rows := [...]row{
		{0x0000, 0x00BF, -1, -1}, {0x00C0, 0x013F, 0xC0, 0x13F}, {0x0140, 0x01BF, 0x140, 0x1BF}, {0x01C0, 0x023F, 0x1C0, 0x23F},
		{0x0240, 0x02BF, 0x240, 0x2BF}, {0x02C0, 0x02FF, 0x2C0, 0x2FF}, {0x0300, 0x036F, 0x300, 0x36F}, {0x0370, 0x037F, -1, -1},
		{0x0380, 0x04FF, -2, 0}, {0x0500, 0x052F, 0x500, 0x52F}, {0x0530, 0x058F, 0x530, 0x58F}, {0x0590, 0x05FF, 0x590, 0x5FF},
		{0x0600, 0x1FFF, -2, 0}, {0x2000, 0x20FF, -1, -1}, {0x2100, 0x2FFF, -2, 0}, {0x3000, 0x30FF, -1, -1}, {0x3100, 0x33FF, -2, 0},
		{0x3400, 0x9FFF, -1, -1}, {0xA000, 0xABFF, -2, 0}, {0xAC00, 0xDFFF, -1, -1}, {0xE000, 0x1FFFF, -2, 0}, {0x20000, 0x3FFFF, -1, -1},
		{0x40000, 0x10FFFF, -2, 0},
	}
	for _, r := range rows {
		if c >= r.lo && c <= r.hi {
			if r.ws == -2 {
				b := c - c%0x80
				return b, b + 0x7F
			}
			return r.ws, r.we
		}
	}
	panic("oWindow: not a code point")
}

var (
	oMu      sync.Mutex
	oSizes   []uint8
	oLayouts = map[[4]int][]oRange{}
)

func oLayoutOf(s oState) []oRange {
	slot := 0
	if s.ctx == 1 {
		slot = s.slot
	}
	key := [4]int{s.ctx, slot, s.main, s.second}
	oMu.Lock()
	defer oMu.Unlock()
	if l, ok := oLayouts[key]; ok {
		return l
	}
	if len(oLayouts) > 20000 {
		oLayouts = map[[4]int][]oRange{}
	}
	if oSizes == nil {
		oSizes = make([]uint8, 0x110000)
	}
	size := oSizes
	for i := range size {
		size[i] = 0
	}
	set := func(lo, hi int, z uint8) {
		for c := lo; c <= hi; c++ {
			size[c] = z
		}
	}
	_, m1 := oWindow(s.main)
	m0 := s.main
	if s.ctx == 0 {
		set(max(0x80, m0-248), m0-1, 2)
		if m1 < 0x10FFFF {
			set(m1+1, min(0x10FFFF, m1+248), 2)
		}
	}
	set(0, 0x1F, 2)
	set(9, 0xA, 1)
	set(0xD, 0xD, 1)
	if s.ctx == 1 && s.slot != 0 {
		set(0x20, 0x40, 1)
		set(0x41, 0x7E, 2)
	} else {
		set(0x20, 0x7E, 1)
	}
	set(0x7F, 0xBF, 2)
	if s.ctx != 1 {
		set(0x80, 0xEFF, 2)
	}
	set(0x2000, 0x20CF, 2)
	if s.second >= 0 {
		_, se := oWindow(s.second)
		set(s.second, se, 2)
	}
	switch s.ctx {
	case 0:
		set(m0, m1, 1)
	case 2:
		set(m0, m1, 2)
		set(0x3000, 0x303F, 2)
		set(0xAC00, 0xD7A3, 2)
		set(0xFF00, 0xFFFF, 2)
	case 1:
		set(m0, m1, 2)
		set(0x3000, 0x300F, 1)
		set(0x3010, 0x30FF, 2)
		if s.slot == 1 {
			set(0x3041, 0x3096, 1)
		} else if s.slot == 2 {
			set(0x30A1, 0x30FC, 1)
		}
		set(0x4E00, 0x9FFF, 2)
		set(0xFF00, 0xFFFF, 2)
		if s.slot == 0 {
			set(0xFF00, 0xFF1F, 1)
		}
	}
	// Stretches: maximal runs of 0, cut at U+10FC0 and U+20000.
	for c := 0; c <= 0x10FFFF; {
		if size[c] != 0 {
			c++
			continue
		}
		e := c
		for e+1 <= 0x10FFFF && size[e+1] == 0 && e+1 != 0x10FC0 && e+1 != 0x20000 {
			e++
		}
		n := e - c + 1
		z := uint8(4)
		if n <= 248 {
			z = 2
		} else if n <= 61504 {
			z = 3
		}
		set(c, e, z)
		c = e + 1
	}
	var l []oRange
	lead := 1
	for c := 0; c <= 0x10FFFF; {
		e := c
		for e+1 <= 0x10FFFF && size[e+1] == size[c] {
			e++
		}
		per := 1
		for j := 1; j < int(size[c]); j++ {
			per *= 248
		}
		n := e - c + 1
		leads := (n + per - 1) / per
		l = append(l, oRange{c, e, int(size[c]), lead, leads})
		lead += leads
		c = e + 1
	}
	if lead > 0xFF {
		panic("oracle: more than 254 lead bytes")
	}
	oLayouts[key] = l
	return l
}

func oUpdate(s *oState, c, k int) {
	in := func(lo, hi int) bool { return c >= lo && c <= hi }
	if in(0, 0x7F) {
		if s.ctx == 1 && (in(0x41, 0x5A) || in(0x61, 0x7A)) {
			s.slot = 0
		}
		return
	}
	if in(0xAC00, 0xD7A3) {
		s.pending = -1
		s.ctx = 2
		return
	}
	if in(0x3040, 0x30FF) || in(0x3400, 0x9FFF) || in(0x20000, 0x3FFFF) || (s.ctx != 2 && in(0x3000, 0x303F)) {
		if s.ctx != 1 {
			s.ctx = 1
			s.slot = 0
		}
		s.pending = -1
		if in(0x3041, 0x3096) {
			s.slot = 1
		}
		if in(0x30A1, 0x30FA) {
			s.slot = 2
		}
		return
	}
	if s.ctx != 0 && in(0xFF00, 0xFFFF) {
		return
	}
	w, _ := oWindow(c)
	if w < 0 {
		return
	}
	if s.ctx == 0 && w == s.main {
		s.conf = min(s.conf+1, 2)
		return
	}
	if k >= 3 {
		s.second = w
	}
	if s.ctx == 0 {
		if s.conf > 0 {
			s.conf--
			return
		}
	} else if s.pending != w {
		s.pending = w
		return
	}
	if s.main != w {
		s.second = s.main
	} else if s.second == w {
		s.second = -1
	}
	s.main = w
	s.ctx = 0
	s.pending = -1
	s.conf = 0
}

// oEncode encodes code points; ok is false when cps is not a value.
func oEncode(cps []rune) ([]byte, bool) {
	for i, c := range cps {
		if c < 0 || c > 0x10FFFF {
			return nil, false
		}
		if i > 0 && cps[i-1] >= 0xD800 && cps[i-1] <= 0xDBFF && c >= 0xDC00 && c <= 0xDFFF {
			return nil, false
		}
	}
	s := oInitial()
	out := []byte{}
	for _, r := range cps {
		c := int(r)
		var g oRange
		for _, x := range oLayoutOf(s) {
			if c >= x.lo && c <= x.hi {
				g = x
				break
			}
		}
		o := c - g.lo
		k := g.size
		digits := make([]int, k-1)
		for j := k - 2; j >= 0; j-- {
			digits[j] = o % 248
			o /= 248
		}
		out = append(out, byte(g.first+o))
		for _, d := range digits {
			out = append(out, byte(d+8))
		}
		oUpdate(&s, c, k)
	}
	return out, true
}

// oDecode decodes b. It returns the code points with the offset of each, and the reason and offset of a rejection ("" if none).
func oDecode(b []byte) (cps []rune, offs []int, reason string, at int) {
	s := oInitial()
	i := 0
	for i < len(b) {
		x := int(b[i])
		if x == 0 {
			return cps, offs, "zero-byte", i
		}
		var g *oRange
		l := oLayoutOf(s)
		for j := range l {
			if x >= l[j].first && x < l[j].first+l[j].leads {
				g = &l[j]
			}
		}
		if g == nil {
			return cps, offs, "unused-lead", i
		}
		o := x - g.first
		for j := 1; j < g.size; j++ {
			if i+j >= len(b) {
				return cps, offs, "truncated", i
			}
			u := int(b[i+j])
			if u == 0 {
				return cps, offs, "zero-byte", i
			}
			if u < 8 {
				return cps, offs, "invalid-trail", i
			}
			o = o*248 + u - 8
		}
		c := g.lo + o
		if c > g.hi {
			return cps, offs, "past-range", i
		}
		if len(cps) > 0 && cps[len(cps)-1] >= 0xD800 && cps[len(cps)-1] <= 0xDBFF && c >= 0xDC00 && c <= 0xDFFF {
			return cps, offs, "surrogate-pair", i
		}
		cps = append(cps, rune(c))
		offs = append(offs, i)
		oUpdate(&s, c, g.size)
		i += g.size
	}
	return cps, offs, "", 0
}

// TestOracleVectors checks the second oracle against the vectors (and the large file when STENO_LARGE_VECTORS is set).
func TestOracleVectors(t *testing.T) {
	// The oracle is slow: by default it checks every fourth published vector; with STENO_ORACLE=full it checks all of them, and the large
	// file when STENO_LARGE_VECTORS names it.
	full := os.Getenv("STENO_ORACLE") == "full"
	files := []string{vectorFile}
	if p := os.Getenv("STENO_LARGE_VECTORS"); p != "" && full {
		files = append(files, p)
	}
	for _, f := range files {
		n := 0
		for i, v := range readVectors(t, f) {
			if !full && i%4 != 0 {
				continue
			}
			n++
			switch v.kind {
			case "valid":
				if b, ok := oEncode(v.cps); !ok || string(b) != string(v.bytes) {
					t.Fatalf("%s: oracle encode %X, want %X", v.name, b, v.bytes)
				}
				if c, _, r, _ := oDecode(v.bytes); r != "" || !slices.Equal(c, v.cps) {
					t.Fatalf("%s: oracle decode %X %s", v.name, c, r)
				}
			case "invalid":
				if _, _, r, _ := oDecode(v.bytes); r != v.reason {
					t.Fatalf("%s: oracle decode reason %s, want %s", v.name, r, v.reason)
				}
			case "not-a-value":
				if _, ok := oEncode(v.cps); ok {
					t.Fatalf("%s: oracle accepted", v.name)
				}
			}
		}
		t.Logf("%s: %d vectors", f, n)
	}
}
