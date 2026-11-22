package steno

import (
	"math/rand"
	"testing"
	"time"
)

// hopWindows are 104 block windows above U+1000, outside the ranges of the CJK and Hangul contexts.
var hopWindows = func() []int32 {
	var ws []int32
	for _, r := range [][2]int32{{0x1000, 0x2000}, {0x2100, 0x3000}, {0x3100, 0x3400}, {0xA000, 0xAC00}, {0xE000, 0xE600}} {
		for w := r[0]; w < r[1]; w += 0x80 {
			ws = append(ws, w)
		}
	}
	return ws
}()

// hopText returns n code points, each from a window chosen at random among the first windows of hopWindows, spread over the list.
func hopText(windows, n int, seed int64) []rune {
	rng := rand.New(rand.NewSource(seed))
	out := make([]rune, n)
	for i := range out {
		w := hopWindows[rng.Intn(windows)*len(hopWindows)/windows]
		out[i] = w + int32(rng.Intn(0x80))
	}
	return out
}

// russianText returns n code points of Cyrillic words and spaces.
func russianText(n int) []rune {
	rng := rand.New(rand.NewSource(1))
	out := make([]rune, n)
	for i := range out {
		if rng.Intn(7) == 0 {
			out[i] = ' '
		} else {
			out[i] = 0x430 + rune(rng.Intn(32))
		}
	}
	return out
}

// perCodePoint returns the best time per code point, over several rounds, to encode and to decode text. Each round repeats the call for at
// least 20 ms, so that the resolution of the clock does not matter.
func perCodePoint(text []rune, rounds int) (enc, dec float64) {
	b, _ := EncodeRunes(text)
	measure := func(f func()) float64 {
		best := 1e18
		for r := 0; r < rounds; r++ {
			start := time.Now()
			calls := 0
			for time.Since(start) < 20*time.Millisecond {
				f()
				calls++
			}
			best = min(best, float64(time.Since(start).Nanoseconds())/float64(calls*len(text)))
		}
		return best
	}
	enc = measure(func() { EncodeRunes(text) })
	dec = measure(func() { DecodeRunes(b) })
	return enc, dec
}

// TestHopCost checks that text whose code points hop among more windows than the layout cache has slots, so that almost every code point
// reaches a state that no cache holds, costs at most a modest factor more per code point than text hopping among three windows.
func TestHopCost(t *testing.T) {
	n := 50000
	e3, d3 := perCodePoint(hopText(3, n, 1), 5)
	e20, d20 := perCodePoint(hopText(20, n, 2), 5)
	e104, d104 := perCodePoint(hopText(104, n, 3), 5)
	er, dr := perCodePoint(russianText(n), 5)
	t.Logf("ns per code point, encode / decode: Russian %.1f / %.1f; 3 windows %.1f / %.1f; 20 windows %.1f / %.1f; 104 windows %.1f / %.1f",
		er, dr, e3, d3, e20, d20, e104, d104)
	if e104 > 50*e3 || d104 > 50*d3 {
		t.Errorf("text hopping among 104 windows costs %.0f / %.0f ns per code point, among 3 windows %.0f / %.0f", e104, d104, e3, d3)
	}
}

// TestLayoutWithoutTables checks that a layout without its lookup tables finds the same range for every lead byte and for code points all over
// the code space as with them, in the states that random text reaches.
func TestLayoutWithoutTables(t *testing.T) {
	rng := rand.New(rand.NewSource(7))
	for i := 0; i < 300; i++ {
		var e encState
		e.reset()
		for _, c := range randomText(rng, 20, true) {
			e.put(nil, c)
		}
		light := buildLayout(e.s)
		full := buildLayout(e.s)
		full.ensureTables()
		for x := 0; x < 256; x++ {
			a, b := light.findLead(uint8(x)), full.findLead(uint8(x))
			if (a == nil) != (b == nil) || a != nil && *a != *b {
				t.Fatalf("state %+v, lead %02X: %v without tables, %v with", e.s, x, a, b)
			}
		}
		for c := int32(0); c <= 0x10FFFF; c += 1 + int32(rng.Intn(97)) {
			if a, b := light.find(c), full.find(c); *a != *b {
				t.Fatalf("state %+v, U+%04X: %v without tables, %v with", e.s, c, *a, *b)
			}
		}
	}
}
