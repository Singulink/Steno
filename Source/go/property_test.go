package steno

import (
	"bytes"
	"math/rand"
	"slices"
	"sync"
	"testing"
)

// The ranges that the random text is drawn from: scripts, controls, kana, kanji, Hangul, emoji, supplementary planes and surrogates.
var textRanges = [][2]rune{
	{0x00, 0x1F}, {0x20, 0x7E}, {0x20, 0x7E}, {0x7F, 0xBF}, {0xC0, 0x24F}, {0x300, 0x36F}, {0x370, 0x3FF}, {0x400, 0x4FF}, {0x500, 0x5FF},
	{0x600, 0x6FF}, {0x900, 0x97F}, {0xE00, 0xE7F}, {0x10A0, 0x10FF}, {0x1200, 0x137F}, {0x2000, 0x20FF}, {0x2100, 0x2BFF}, {0x3000, 0x303F},
	{0x3040, 0x30FF}, {0x3100, 0x33FF}, {0x4E00, 0x9FFF}, {0xA000, 0xABFF}, {0xAC00, 0xD7A3}, {0xD800, 0xDFFF}, {0xE000, 0xF8FF},
	{0xFE00, 0xFFFF}, {0xFF00, 0xFF1F}, {0x10000, 0x1FFFF}, {0x1F300, 0x1F6FF}, {0x1FE00, 0x1FE80}, {0x20000, 0x3FFFF}, {0x40000, 0x10FFFF},
	{0xFFF80, 0xFFFFF},
}

// randomText returns a value of up to n code points: runs of code points from a few ranges, so that windows repeat and the state moves through
// switches, the wide contexts and back. With surrogates set, it may hold surrogate code points, but never a high one followed by a low one.
func randomText(rng *rand.Rand, n int, surrogates bool) []rune {
	length := rng.Intn(n + 1)
	out := make([]rune, 0, length)
	for len(out) < length {
		r := textRanges[rng.Intn(len(textRanges))]
		if !surrogates && r[0] == 0xD800 {
			continue
		}
		// Most runs stay in one window of the range.
		base := r[0] + rune(rng.Intn(int(r[1]-r[0]+1)))
		run := 1 + rng.Intn(6)
		for j := 0; j < run && len(out) < length; j++ {
			c := base
			if rng.Intn(3) > 0 {
				c = base&^0x7F + rune(rng.Intn(0x80))
			}
			if rng.Intn(4) == 0 {
				c = rune(0x20 + rng.Intn(0x5F))
			}
			if c < r[0] || c > r[1] {
				c = base
			}
			if len(out) > 0 && isHighSurrogate(out[len(out)-1]) && isLowSurrogate(c) {
				continue
			}
			out = append(out, c)
		}
	}
	return out
}

func compareRunes(a, b []rune) int {
	for i := 0; i < len(a) && i < len(b); i++ {
		if a[i] != b[i] {
			if a[i] < b[i] {
				return -1
			}
			return 1
		}
	}
	return len(a) - len(b)
}

func sign(x int) int {
	switch {
	case x < 0:
		return -1
	case x > 0:
		return 1
	}
	return 0
}

// TestProperties checks the package against the plain codec and the properties of FORMAT.md §13 on seeded random text.
func TestProperties(t *testing.T) {
	rng := rand.New(rand.NewSource(1))
	var prev []rune
	var prevBytes []byte
	for i := 0; i < 3000; i++ {
		cps := randomText(rng, 40, true)
		b, err := EncodeRunes(cps)
		if err != nil {
			t.Fatalf("EncodeRunes(%X): %v", cps, err)
		}

		// Agrees with the plain codec.
		ints := make([]int, len(cps))
		for j, c := range cps {
			ints[j] = int(c)
		}
		if want, _ := refEncode(ints); !bytes.Equal(b, want) {
			t.Fatalf("EncodeRunes(%X) = %X, plain codec %X", cps, b, want)
		}

		// Round trip, in every form.
		if got, err := DecodeRunes(b); err != nil || !slices.Equal(got, cps) {
			t.Fatalf("DecodeRunes(%X) = %X, %v; want %X", b, got, err, cps)
		}
		u := toUTF16(cps)
		if got := EncodeUTF16(u); !bytes.Equal(got, b) {
			t.Fatalf("EncodeUTF16(%X) = %X, want %X", u, got, b)
		}
		if got, err := DecodeUTF16(b); err != nil || !slices.Equal(got, u) {
			t.Fatalf("DecodeUTF16(%X) = %X, %v", b, got, err)
		}
		if !hasSurrogate(cps) {
			s := string(cps)
			if got, err := Encode(s); err != nil || !bytes.Equal(got, b) {
				t.Fatalf("Encode(%q) = %X, %v; want %X", s, got, err, b)
			}
			if got, err := Decode(b); err != nil || got != s {
				t.Fatalf("Decode(%X) = %q, %v; want %q", b, got, err, s)
			}
		}

		// No zero byte.
		if bytes.IndexByte(b, 0) >= 0 {
			t.Fatalf("EncodeRunes(%X) = %X has a zero byte", cps, b)
		}

		// The encoding of a prefix is a prefix.
		k := rng.Intn(len(cps) + 1)
		if pb, _ := EncodeRunes(cps[:k]); !bytes.HasPrefix(b, pb) {
			t.Fatalf("encoding of prefix %X of %X is %X, not a prefix of %X", cps[:k], cps, pb, b)
		}

		// Byte order is code point order, also against a value with a common prefix.
		if got, want := sign(bytes.Compare(prevBytes, b)), sign(compareRunes(prev, cps)); got != want {
			t.Fatalf("order of %X and %X is %d, bytes %d", prev, cps, want, got)
		}
		other := append(slices.Clone(cps[:k]), randomText(rng, 5, true)...)
		if k > 0 && isHighSurrogate(cps[k-1]) {
			other = append(slices.Clone(cps[:k]), 0x41)
		}
		if ob, err := EncodeRunes(other); err == nil {
			if got, want := sign(bytes.Compare(ob, b)), sign(compareRunes(other, cps)); got != want {
				t.Fatalf("order of %X and %X is %d, bytes %d", other, cps, want, got)
			}
		}
		prev, prevBytes = cps, b
	}
}

// TestReencode checks that every byte string that is accepted is the encoding of what it decodes to (uniqueness), on random and mutated bytes.
func TestReencode(t *testing.T) {
	rng := rand.New(rand.NewSource(2))
	accepted := 0
	for i := 0; i < 20000; i++ {
		var b []byte
		if i%2 == 0 {
			b = make([]byte, rng.Intn(12))
			rng.Read(b)
		} else {
			b, _ = EncodeRunes(randomText(rng, 12, true))
			if len(b) > 0 {
				switch rng.Intn(3) {
				case 0:
					b[rng.Intn(len(b))] = byte(rng.Intn(256))
				case 1:
					b = b[:rng.Intn(len(b))]
				default:
					b = append(b, byte(rng.Intn(256)))
				}
			}
		}
		checkGarbage(t, b)
		if _, err := DecodeRunes(b); err == nil {
			accepted++
		}
	}
	t.Logf("%d of 20000 random and mutated byte strings were accepted", accepted)
}

// checkGarbage checks that b is rejected, or decodes to a value whose encoding is b, in every form, and that the forms agree.
func checkGarbage(t testing.TB, b []byte) {
	cps, err := DecodeRunes(b)
	u, err16 := DecodeUTF16(b)
	s, err8 := Decode(b)
	if reasonOf(err) != reasonOf(err16) {
		t.Fatalf("%X: DecodeRunes %v, DecodeUTF16 %v", b, err, err16)
	}
	sr, errS := streamRunes(b)
	if reasonOf(err) != reasonOf(errS) || !slices.Equal(cps, sr) && err == nil {
		t.Fatalf("%X: DecodeRunes %v, Decoder %v", b, err, errS)
	}
	if err != nil {
		if err8 == nil {
			t.Fatalf("%X: Decode accepted bytes that DecodeRunes rejects: %v", b, err)
		}
		return
	}
	if re, err := EncodeRunes(cps); err != nil || !bytes.Equal(re, b) {
		t.Fatalf("%X decodes to %X, which encodes to %X, %v", b, cps, re, err)
	}
	if re := EncodeUTF16(u); !bytes.Equal(re, b) {
		t.Fatalf("%X decodes to UTF-16 %X, which encodes to %X", b, u, re)
	}
	if hasSurrogate(cps) {
		if reasonOf(err8) != "lone-surrogate" {
			t.Fatalf("%X: Decode %v, want lone-surrogate", b, err8)
		}
	} else if err8 != nil || s != string(cps) {
		t.Fatalf("%X: Decode = %q, %v; want %q", b, s, err8, string(cps))
	}
	if IsASCIIText(b) && !isASCIIText(cps) {
		t.Fatalf("%X: IsASCIIText is true for %X", b, cps)
	}
}

// TestOneByteUpdate checks that the short update for code points that take 1 byte (FORMAT.md §14.2) is the full update of §7, in every layout
// that random text reaches.
func TestOneByteUpdate(t *testing.T) {
	rng := rand.New(rand.NewSource(3))
	checked := 0
	for i := 0; i < 300; i++ {
		var e encState
		e.reset()
		for _, c := range randomText(rng, 30, true) {
			l := e.l
			for _, r := range l.spans {
				if r.size != 1 {
					continue
				}
				for x := r.lo; x <= r.hi; x++ {
					full, short := e.s, e.s
					full.update(x, 1)
					short.updateOneByte(x)
					if full != short || !full.sameLayout(&e.s) {
						t.Fatalf("state %+v, U+%04X: full update %+v, short update %+v", e.s, x, full, short)
					}
					checked++
				}
			}
			e.put(nil, c)
		}
	}
	t.Logf("%d code points checked", checked)
}

// TestLayouts checks the layouts of FORMAT.md §6.6 and §11.1, and that the package builds the same layout as the plain codec in random states.
func TestLayouts(t *testing.T) {
	type row struct {
		lo, hi int32
		size   uint8
		first  uint8
	}
	head := []row{{0, 8, 2, 0x01}, {9, 0xA, 1, 0x02}, {0xB, 0xC, 2, 0x04}, {0xD, 0xD, 1, 0x05}, {0xE, 0x1F, 2, 0x06}}
	cases := []struct {
		text []rune
		want []row
	}{
		{nil, append(slices.Clone(head), row{0x20, 0x7E, 1, 0x07}, row{0x7F, 0xBF, 2, 0x66}, row{0xC0, 0x13F, 1, 0x67},
			row{0x140, 0xEFF, 2, 0xE7}, row{0xF00, 0x1FFF, 3, 0xF6}, row{0x2000, 0x20CF, 2, 0xF7}, row{0x20D0, 0x1FFFF, 3, 0xF8},
			row{0x20000, 0x10FFFF, 4, 0xFA})},
		{[]rune{0x416, 0x416, 0x416, 0x1200}, append(slices.Clone(head), row{0x20, 0x7E, 1, 0x07}, row{0x7F, 0x3FF, 2, 0x66},
			row{0x400, 0x47F, 1, 0x6A}, row{0x480, 0xEFF, 2, 0xEA}, row{0xF00, 0x11FF, 3, 0xF5}, row{0x1200, 0x127F, 2, 0xF6},
			row{0x1280, 0x1FFF, 3, 0xF7}, row{0x2000, 0x20CF, 2, 0xF8}, row{0x20D0, 0x1FFFF, 3, 0xF9}, row{0x20000, 0x10FFFF, 4, 0xFB})},
		{[]rune{0x3042}, append(slices.Clone(head), row{0x20, 0x40, 1, 0x07}, row{0x41, 0x13F, 2, 0x28}, row{0x140, 0x1FFF, 3, 0x2A},
			row{0x2000, 0x20CF, 2, 0x2B}, row{0x20D0, 0x2FFF, 3, 0x2C}, row{0x3000, 0x300F, 1, 0x2D}, row{0x3010, 0x3040, 2, 0x3D},
			row{0x3041, 0x3096, 1, 0x3E}, row{0x3097, 0x30FF, 2, 0x94}, row{0x3100, 0x4DFF, 3, 0x95}, row{0x4E00, 0x9FFF, 2, 0x96},
			row{0xA000, 0xFEFF, 3, 0xEB}, row{0xFF00, 0xFFFF, 2, 0xEC}, row{0x10000, 0x1FFFF, 3, 0xEE}, row{0x20000, 0x10FFFF, 4, 0xF0})},
		{[]rune{0xD55C}, append(slices.Clone(head), row{0x20, 0x7E, 1, 0x07}, row{0x7F, 0xEFF, 2, 0x66}, row{0xF00, 0x1FFF, 3, 0x75},
			row{0x2000, 0x20CF, 2, 0x76}, row{0x20D0, 0x2FFF, 3, 0x77}, row{0x3000, 0x303F, 2, 0x78}, row{0x3040, 0xABFF, 3, 0x79},
			row{0xAC00, 0xD7A3, 2, 0x7A}, row{0xD7A4, 0xFEFF, 3, 0xA8}, row{0xFF00, 0xFFFF, 2, 0xA9}, row{0x10000, 0x1FFFF, 3, 0xAB},
			row{0x20000, 0x10FFFF, 4, 0xAD})},
		{[]rune{0x1000, 0x1001, 0x1200}, append(slices.Clone(head), row{0x20, 0x7E, 1, 0x07}, row{0x7F, 0xFFF, 2, 0x66},
			row{0x1000, 0x107F, 1, 0x77}, row{0x1080, 0x127F, 2, 0xF7}, row{0x1280, 0x1FFF, 3, 0xFA}, row{0x2000, 0x20CF, 2, 0xFB},
			row{0x20D0, 0x1FFFF, 3, 0xFC}, row{0x20000, 0x10FFFF, 4, 0xFE})},
	}
	for _, tc := range cases {
		var e encState
		e.reset()
		for _, c := range tc.text {
			e.put(nil, c)
		}
		var got []row
		for _, r := range e.l.spans {
			got = append(got, row{r.lo, r.hi, r.size, r.first})
		}
		if !slices.Equal(got, tc.want) {
			t.Errorf("layout after %X:\n got %X\nwant %X", tc.text, got, tc.want)
		}
	}

	// Random states against the plain codec.
	rng := rand.New(rand.NewSource(4))
	for i := 0; i < 200; i++ {
		var e encState
		e.reset()
		rs := refInitial()
		for _, c := range randomText(rng, 20, true) {
			ref := refLayout(rs)
			if len(ref) != len(e.l.spans) {
				t.Fatalf("state %+v: %d ranges, plain codec %d", e.s, len(e.l.spans), len(ref))
			}
			for j, r := range ref {
				g := e.l.spans[j]
				if int(g.lo) != r.lo || int(g.hi) != r.hi || int(g.size) != r.size || int(g.first) != r.first {
					t.Fatalf("state %+v: range %d is %+v, plain codec %+v", e.s, j, g, r)
				}
			}
			b := e.put(nil, c)
			refUpdate(rs, int(c), len(b))
		}
	}
}

// TestConcurrency encodes and decodes different texts on several goroutines at once and compares with the results of one goroutine. The texts
// reach many more states than the layout cache has slots, so layouts are evicted and rebuilt while other goroutines read them.
func TestConcurrency(t *testing.T) {
	rng := rand.New(rand.NewSource(5))
	texts := make([][]rune, 400)
	want := make([][]byte, len(texts))
	for i := range texts {
		texts[i] = randomText(rng, 60, true)
		want[i], _ = EncodeRunes(texts[i])
	}
	states := map[uint64]bool{}
	for _, text := range texts {
		var e encState
		e.reset()
		for _, c := range text {
			e.put(nil, c)
			states[e.s.key()] = true
		}
	}
	if len(states) <= len(layoutCache) {
		t.Errorf("the texts reach %d layouts, not more than the %d slots of the cache", len(states), len(layoutCache))
	}
	var wg sync.WaitGroup
	errs := make(chan string, 8)
	for g := 0; g < 8; g++ {
		wg.Add(1)
		go func(g int) {
			defer wg.Done()
			for round := 0; round < 5; round++ {
				for i := range texts {
					j := (i*7 + g*31 + round) % len(texts)
					b, err := EncodeRunes(texts[j])
					if err != nil || !bytes.Equal(b, want[j]) {
						errs <- "encode differs"
						return
					}
					cps, err := DecodeRunes(b)
					if err != nil || !slices.Equal(cps, texts[j]) {
						errs <- "decode differs"
						return
					}
				}
			}
		}(g)
	}
	wg.Wait()
	close(errs)
	for e := range errs {
		t.Error(e)
	}
}

// TestLowBytesAreLeads checks the guarantee of FORMAT.md §13.5: a byte below 08 is always the first byte of a code point, of a C0 control or
// the space, and so splitting the bytes on 03 splits the text on its line feeds.
func TestLowBytesAreLeads(t *testing.T) {
	// The first byte of each C0 control and of the space, by §13.5.
	lead := func(c rune) byte {
		switch {
		case c <= 0x08:
			return 0x01
		case c == 0x09:
			return 0x02
		case c == 0x0A:
			return 0x03
		case c <= 0x0C:
			return 0x04
		case c == 0x0D:
			return 0x05
		case c <= 0x1F:
			return 0x06
		default:
			return 0x07
		}
	}
	rng := rand.New(rand.NewSource(6))
	for i := 0; i < 2000; i++ {
		cps := randomText(rng, 40, true)
		// Put line feeds into the text at random places.
		for j := range cps {
			if rng.Intn(8) == 0 && (j == 0 || !isHighSurrogate(cps[j-1])) {
				cps[j] = '\n'
			}
		}
		b, _ := EncodeRunes(cps)

		// The start of each code point in the bytes, from the prefix property.
		starts := map[int]rune{}
		for j := range cps {
			p, _ := EncodeRunes(cps[:j])
			starts[len(p)] = cps[j]
		}
		for k, x := range b {
			if x >= 0x08 {
				continue
			}
			c, ok := starts[k]
			if !ok || c > 0x20 || lead(c) != x {
				t.Fatalf("%X: byte %02X at %d is not the first byte of a C0 control or the space (code point %X, start %v)", cps, x, k, c, ok)
			}
		}

		// Splitting the bytes on 03 gives the line feeds of the text.
		var lf []int
		for k, x := range b {
			if x == 0x03 {
				lf = append(lf, k)
			}
		}
		var want []int
		for j, c := range cps {
			if c == '\n' {
				p, _ := EncodeRunes(cps[:j])
				want = append(want, len(p))
			}
		}
		if !slices.Equal(lf, want) {
			t.Fatalf("%X: the bytes 03 are at %v, the line feeds at %v", cps, lf, want)
		}
	}
}
