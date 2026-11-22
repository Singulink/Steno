package steno

import (
	"testing"
	"time"
)

// collidingWindows returns two block windows A and B such that the layouts of the general context with the main
// window A and the second window B, and with the main window B and the second window A, are kept in the same slot of the shared layout cache.
// With collide false, it returns two windows whose layouts are in different slots.
func collidingWindows(t *testing.T, collide bool) (int32, int32) {
	key := func(m, s int32) uint64 {
		st := state{main: m, second: s, pending: noWindow, ctx: ctxGeneral}
		return st.key()
	}
	// Block windows of U+0600..U+1FFF and U+E000..U+FFFF: about 6,000 pairs, so a pair that shares a slot exists for almost any seed.
	var windows []int32
	for w := int32(0x600); w < 0x2000; w += 0x80 {
		windows = append(windows, w)
	}
	for w := int32(0xE000); w < 0x10000; w += 0x80 {
		windows = append(windows, w)
	}
	for i, a := range windows {
		for _, b := range windows[i+1:] {
			same := cacheSlot(key(a, b)) == cacheSlot(key(b, a))
			if same == collide {
				return a, b
			}
		}
	}
	t.Skip("no such pair of windows for this hash")
	return 0, 0
}

// alternatingTime returns the time per code point to encode and decode text that alternates between code points of the windows a and b, so
// that the state switches between the two layouts at every code point.
func alternatingTime(a, b int32) float64 {
	text := make([]rune, 200000)
	for i := range text {
		if i%2 == 0 {
			text[i] = a + 5
		} else {
			text[i] = b + 7
		}
	}
	enc, _ := EncodeRunes(text)
	best := time.Duration(1 << 62)
	for r := 0; r < 5; r++ {
		start := time.Now()
		EncodeRunes(text)
		DecodeRunes(enc)
		best = min(best, time.Since(start))
	}
	return float64(best.Nanoseconds()) / float64(2*len(text))
}

// TestCacheCollision checks that text which alternates between two layouts that share a slot of the layout cache is not much slower than text
// that alternates between two layouts in different slots: whoever chooses the text must not be able to make every code point rebuild a layout.
func TestCacheCollision(t *testing.T) {
	ca, cb := collidingWindows(t, true)
	na, nb := collidingWindows(t, false)
	collide := alternatingTime(ca, cb)
	normal := alternatingTime(na, nb)
	t.Logf("windows U+%04X and U+%04X share a slot: %.1f ns per code point; U+%04X and U+%04X do not: %.1f ns per code point (%.1fx)",
		ca, cb, collide, na, nb, normal, collide/normal)
	if collide > 4*normal {
		t.Errorf("colliding layouts take %.1f ns per code point, other layouts %.1f ns", collide, normal)
	}
}
