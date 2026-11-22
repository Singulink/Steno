package steno

import (
	"hash/maphash"
	"sync/atomic"
)

// This file builds the layout of a state (FORMAT.md §6) and caches layouts.

// The digits of a trail byte (FORMAT.md §8.2): a trail byte is a digit 0..247 in base 248, plus 8, so it is 08..FF.
const (
	base     = 248
	trailMin = 8
	base2    = base * base // 61,504, the code points of one lead byte at size 3
)

// span is a range of the layout (§6.4) with its lead bytes (§6.5).
type span struct {
	lo, hi int32
	first  uint8 // first lead byte
	size   uint8 // bytes per code point, 1 to 4
}

// maxSpans bounds the number of runs while the pieces are laid. The 18 pieces of §6.2 cut the code points into at most 37 runs, and the two
// cuts of §6.3 add at most 2 ranges.
const maxSpans = 48

// layout is the layout of a state, with what the hot paths need. A layout is immutable once built, except for its lookup tables, which are
// added once, atomically, when the layout is used again; it holds no reference to another layout.
//
// A layout is built in two steps, so that text that reaches a new state at almost every code point (and so cannot be helped by any cache)
// pays only for the first: the ranges and the few values of the fast paths (cheap to build, a few hundred nanoseconds), and the tables that
// find a range in one step (tables), which are built only when the layout is found again tablesAfter times. Until then, a range is found by
// a binary search over the ranges.
type layout struct {
	key    uint64
	spans  []span // spanArr[:n]
	tables atomic.Pointer[layoutTables]
	uses   atomic.Uint32 // how many times the layout was found again, until it has its tables

	// leadEnd is the lead byte after the last one that a range uses: lead bytes from leadEnd are unused.
	leadEnd uint8

	// asciiEnc maps a code point below 0x80 to its byte if it takes 1 byte, or 0. It is shared by all layouts with the same asciiHi.
	asciiEnc *[128]uint8

	// asciiDec maps a lead byte to the code point below 0x80 that it encodes on its own, or 0. It is shared like asciiEnc.
	asciiDec *[256]uint8

	// The main window when its code points take 1 byte (the general context); mainLen is 0 otherwise.
	mainLo   int32
	mainLen  uint32
	mainLead uint8

	// The run of printable ASCII that takes 1 byte as c - 0x19, from U+0020 to asciiHi (U+007E, or U+0040 in the CJK context with a kana slot
	// other than letters), as the per-byte addends of the word-at-a-time checks (see swar.go).
	asciiHi     uint8
	encRunAdd   uint64 // 0x7F - asciiHi in every byte
	decRunAdd   uint64 // 0x7F - (asciiHi - 0x19) in every byte
	encRunAdd16 uint64 // 0x7FFF - asciiHi in every 16-bit lane

	// The 2-byte range of the wide context whose code points keep the layout: the ideographs U+4E00..U+9FFF in the CJK context, the Hangul
	// syllables U+AC00..U+D7A3 in the Hangul context. wideLen is 0 in the general context. The code points wideLo..wideLo+wideLen-1 lie in a
	// span of size 2 that starts at wideSpanLo, with the lead bytes wideFirst..wideLast.
	wideLo     int32
	wideLen    uint32
	wideSpanLo int32
	wideFirst  uint8
	wideLast   uint8

	// spanArr holds the ranges of most layouts; a layout with more ranges keeps them in a slice of their own.
	spanArr [24]span
}

// layoutTables find the range of a code point or a lead byte in one step.
type layoutTables struct {
	// block maps each block of 128 code points below U+10000 to the index of the span that holds its first code point.
	block [512]uint8

	// lead maps a lead byte to its span index plus one, or 0 if the lead byte is unused.
	lead [256]uint8
}

// tablesAfter is how many times a layout must be found again before it gets its lookup tables.
const tablesAfter = 2

// reused records that l was found again (in the shared cache or as the previous layout of an encoder or decoder), and builds its lookup
// tables once it has been found tablesAfter times.
func (l *layout) reused() {
	if l.tables.Load() == nil && l.uses.Add(1) >= tablesAfter {
		l.ensureTables()
	}
}

// ensureTables builds the lookup tables of l if it has none yet. Two goroutines may both build them; both tables are the same.
func (l *layout) ensureTables() {
	if l.tables.Load() != nil {
		return
	}
	t := new(layoutTables)
	for i, r := range l.spans {
		end := int(l.leadEnd)
		if i+1 < len(l.spans) {
			end = int(l.spans[i+1].first)
		}
		for x := int(r.first); x < end; x++ {
			t.lead[x] = uint8(i + 1)
		}
	}
	for i, j := 0, 0; i < len(t.block); i++ {
		for l.spans[j].hi < int32(i)<<7 {
			j++
		}
		t.block[i] = uint8(j)
	}
	l.tables.Store(t)
}

// find returns the span that holds the code point c.
func (l *layout) find(c int32) *span {
	s := l.spans
	if t := l.tables.Load(); t != nil && c < 0x10000 {
		i := int(t.block[c>>7&0x1FF])
		for s[i].hi < c {
			i++
		}
		return &s[i]
	}
	lo, hi := 0, len(s)-1
	for lo < hi {
		m := int(uint(lo+hi) >> 1)
		if s[m].hi < c {
			lo = m + 1
		} else {
			hi = m
		}
	}
	return &s[lo]
}

// findLead returns the span that the lead byte x is assigned to, or nil if x is unused.
func (l *layout) findLead(x uint8) *span {
	if t := l.tables.Load(); t != nil {
		if ri := t.lead[x]; ri != 0 {
			return &l.spans[ri-1]
		}
		return nil
	}
	if x == 0 || x >= l.leadEnd {
		return nil
	}
	s := l.spans
	lo, hi := 0, len(s)-1
	for lo < hi {
		m := int(uint(lo+hi+1) >> 1)
		if s[m].first <= x {
			lo = m
		} else {
			hi = m - 1
		}
	}
	return &s[lo]
}

type segment struct {
	lo, hi int32
	size   uint8 // 0: no piece covers it
}

// overlay is the list of runs of §6.2 while the pieces are laid: runs of code points in order, each with the size of the last piece that
// covers it, or 0. It lives on the stack.
type overlay struct {
	segs [maxSpans]segment
	n    int
}

// piece lays the piece lo..hi with the given size over the pieces laid before it (§6.2): it replaces the runs that it overlaps with at most
// three runs, in place.
func (o *overlay) piece(lo, hi int32, size uint8) {
	segs := o.segs[:o.n]
	i := 0
	for segs[i].hi < lo {
		i++
	}
	j := i
	for j < len(segs) && segs[j].lo <= hi {
		j++
	}
	var rep [3]segment
	k := 0
	if segs[i].lo < lo {
		rep[k] = segment{segs[i].lo, lo - 1, segs[i].size}
		k++
	}
	rep[k] = segment{lo, hi, size}
	k++
	if last := segs[j-1]; last.hi > hi {
		rep[k] = segment{hi + 1, last.hi, last.size}
		k++
	}
	n := o.n - (j - i) + k
	if n > maxSpans {
		panic("steno: internal error: too many runs in a layout")
	}
	copy(o.segs[i+k:n], o.segs[j:o.n])
	copy(o.segs[i:i+k], rep[:k])
	o.n = n
}

// The ASCII tables of a layout depend only on whether the CJK context has a kana slot other than letters (FORMAT.md §6.2, piece 3).
var asciiLetters, asciiKana = asciiTablesFor(0x7E), asciiTablesFor(0x40)

type asciiTables struct {
	enc [128]uint8
	dec [256]uint8
}

// asciiTablesFor returns the 1-byte codes of tab, line feed, carriage return and U+0020..hi, which are the same in every layout with that hi:
// 02, 03, 05 and c - 0x19 (§13.5).
func asciiTablesFor(hi int32) *asciiTables {
	t := new(asciiTables)
	set := func(c int32, b uint8) {
		t.enc[c] = b
		t.dec[b] = uint8(c)
	}
	set(0x09, 0x02)
	set(0x0A, 0x03)
	set(0x0D, 0x05)
	for c := int32(0x20); c <= hi; c++ {
		set(c, uint8(c-0x19))
	}
	return t
}

// buildLayout builds the layout of s by the four steps of §6.1, without its lookup tables.
func buildLayout(s state) *layout {
	o := overlay{n: 1}
	o.segs[0] = segment{0, 0x10FFFF, 0}

	m0 := s.main
	m1 := windowEnd(m0)

	// Piece 1.
	if s.ctx == ctxGeneral {
		o.piece(max(0x80, m0-base), m0-1, 2)
		if m1 < 0x10FFFF {
			o.piece(m1+1, min(0x10FFFF, m1+base), 2)
		}
	}

	// Piece 2.
	o.piece(0x00, 0x1F, 2)
	o.piece(0x09, 0x0A, 1)
	o.piece(0x0D, 0x0D, 1)

	// Piece 3.
	kana := s.ctx == ctxCJK && s.slot != slotLetters
	if kana {
		o.piece(0x20, 0x40, 1)
		o.piece(0x41, 0x7E, 2)
	} else {
		o.piece(0x20, 0x7E, 1)
	}

	// Pieces 4, 5 and 6.
	o.piece(0x7F, 0xBF, 2)
	if s.ctx != ctxCJK {
		o.piece(0x80, 0xEFF, 2)
	}
	o.piece(0x2000, 0x20CF, 2)

	// Piece 7.
	if s.second != noWindow {
		o.piece(s.second, windowEnd(s.second), 2)
	}

	// Piece 8.
	switch s.ctx {
	case ctxGeneral:
		o.piece(m0, m1, 1)
	case ctxHangul:
		o.piece(m0, m1, 2)
		o.piece(0x3000, 0x303F, 2)
		o.piece(0xAC00, 0xD7A3, 2)
		o.piece(0xFF00, 0xFFFF, 2)
	case ctxCJK:
		o.piece(m0, m1, 2)
		o.piece(0x3000, 0x300F, 1)
		o.piece(0x3010, 0x30FF, 2)
		if s.slot == slotHiragana {
			o.piece(0x3041, 0x3096, 1)
		} else if s.slot == slotKatakana {
			o.piece(0x30A1, 0x30FC, 1)
		}
		o.piece(0x4E00, 0x9FFF, 2)
		o.piece(0xFF00, 0xFFFF, 2)
		if s.slot == slotLetters {
			o.piece(0xFF00, 0xFF1F, 1)
		}
	}

	// §6.3: join each stretch, split it into parts, and size each part. §6.4 and §6.5: join runs of the same size into ranges and assign
	// lead bytes. Both in one pass.
	var spans [maxSpans + 2]span
	n := 0
	lead := 1
	add := func(lo, hi int32, size uint8) {
		if n > 0 && spans[n-1].size == size {
			spans[n-1].hi = hi
			return
		}
		spans[n] = span{lo: lo, hi: hi, size: size}
		n++
	}
	segs := o.segs[:o.n]
	for i := 0; i < len(segs); {
		if segs[i].size != 0 {
			add(segs[i].lo, segs[i].hi, segs[i].size)
			i++
			continue
		}
		lo, hi := segs[i].lo, segs[i].hi
		for i++; i < len(segs) && segs[i].size == 0; i++ {
			hi = segs[i].hi
		}
		for _, cut := range [...]int32{0x20000 - base2, 0x20000} {
			if lo < cut && hi >= cut {
				add(lo, cut-1, partSize(cut-lo))
				lo = cut
			}
		}
		add(lo, hi, partSize(hi-lo+1))
	}
	for i := range spans[:n] {
		r := &spans[i]
		per := int32(1)
		for j := uint8(1); j < r.size; j++ {
			per *= base
		}
		leads := int((r.hi-r.lo)/per) + 1
		if lead+leads > 0xFF {
			panic("steno: internal error: a layout needs more than 254 lead bytes")
		}
		r.first = uint8(lead)
		lead += leads
	}
	l := &layout{key: s.key(), leadEnd: uint8(lead)}
	if n <= len(l.spanArr) {
		l.spans = l.spanArr[:n]
	} else {
		l.spans = make([]span, n)
	}
	copy(l.spans, spans[:n])

	ascii, hi := asciiLetters, uint8(0x7E)
	if kana {
		ascii, hi = asciiKana, 0x40
	}
	l.asciiEnc = &ascii.enc
	l.asciiDec = &ascii.dec
	l.asciiHi = hi
	l.encRunAdd = repeat(0x7F - hi)
	l.decRunAdd = repeat(0x7F - (hi - 0x19))
	l.encRunAdd16 = lo16 * uint64(0x7FFF-uint16(hi))

	l.wideFirst = 0xFF // no lead byte is in wideFirst..wideLast unless set below
	var wideLo, wideHi int32
	switch s.ctx {
	case ctxCJK:
		wideLo, wideHi = 0x4E00, 0x9FFF
	case ctxHangul:
		wideLo, wideHi = 0xAC00, 0xD7A3
	}
	if wideHi != 0 {
		r := l.find(wideLo)
		if r.size != 2 || r.hi < wideHi {
			panic("steno: internal error: the wide range is not in one span of size 2")
		}
		l.wideLo = wideLo
		l.wideLen = uint32(wideHi - wideLo + 1)
		l.wideSpanLo = r.lo
		l.wideFirst = r.first
		l.wideLast = r.first + uint8((r.hi-r.lo)/base)
	}

	if s.ctx == ctxGeneral {
		r := l.find(m0)
		l.mainLo = m0
		l.mainLen = uint32(m1 - m0 + 1)
		l.mainLead = r.first + uint8(m0-r.lo)
	}

	return l
}

func partSize(n int32) uint8 {
	switch {
	case n <= base:
		return 2
	case n <= base2:
		return 3
	default:
		return 4
	}
}

// The layout cache. It has a fixed number of slots, each holding at most one layout, so its memory is bounded: a layout takes about 0.4 KB, and
// 0.8 KB more once it has its lookup tables, so the cache holds about 1.2 MB at most. A slot is read and written atomically: the hot path takes no lock, and a layout is immutable, so a reader
// sees either a whole layout or another one. Two goroutines that miss on the same slot may both build the layout; the later store wins, and
// both layouts are correct.
//
// The slot of a layout depends on a seed that is random in each process, so whoever chooses the text cannot choose layouts that share a slot.
// Text that alternates between two layouts that share a slot by chance is cheap too: each encoder and decoder keeps its layout before the
// current one (recentLayouts).
const cacheBits = 10

var (
	layoutCache   [1 << cacheBits]atomic.Pointer[layout]
	initialLayout = func() *layout {
		l := buildLayout(initialState)
		l.ensureTables()
		return l
	}()
	cacheSeed = func() uint64 {
		var h maphash.Hash // a new Hash has a random seed
		h.WriteByte(0)
		return h.Sum64()
	}()
)

// cacheSlot returns the slot of the layout cache for the layout key k.
func cacheSlot(k uint64) uint64 {
	x := (k ^ cacheSeed) * 0x9E3779B97F4A7C15
	x ^= x >> 31
	x *= cacheSeed | 1
	return x >> (64 - cacheBits)
}

// recentLayouts keeps the layout that one encoder or decoder used before its current one, found before the shared cache. Text that alternates
// between two layouts (two scripts, or a window and its neighbour) then needs no lookup in the shared cache, also when the two layouts share
// a slot there, which would otherwise rebuild a layout at every code point. It holds one layout, for as long as the encoder or decoder lives.
type recentLayouts struct {
	prev *layout
}

// of returns the layout of s, which follows the layout cur.
func (r *recentLayouts) of(s *state, cur *layout) *layout {
	k := s.key()
	if p := r.prev; p != nil && p.key == k {
		r.prev = cur
		p.reused()
		return p
	}
	r.prev = cur
	return layoutOfKey(s, k)
}

// layoutOf returns the layout of s.
func layoutOf(s *state) *layout {
	return layoutOfKey(s, s.key())
}

// layoutOfKey returns the layout of s, whose key is k.
func layoutOfKey(s *state, k uint64) *layout {
	if k == initialLayout.key {
		return initialLayout
	}

	slot := &layoutCache[cacheSlot(k)]
	if l := slot.Load(); l != nil && l.key == k {
		l.reused()
		return l
	}

	l := buildLayout(*s)
	slot.Store(l)
	return l
}

// appendCode appends the bytes of the code point c in the span r (§8.2).
func appendCode(dst []byte, r *span, c int32) []byte {
	o := int(c - r.lo)
	switch r.size {
	case 1:
		return append(dst, r.first+uint8(o))
	case 2:
		return append(dst, r.first+uint8(o/base), uint8(o%base+trailMin))
	case 3:
		t := o % base2
		return append(dst, r.first+uint8(o/base2), uint8(t/base+trailMin), uint8(t%base+trailMin))
	default:
		// A range of size 4 has one lead byte: o is below 248^3.
		t := o % base2
		return append(dst, r.first, uint8(o/base2+trailMin), uint8(t/base+trailMin), uint8(t%base+trailMin))
	}
}
