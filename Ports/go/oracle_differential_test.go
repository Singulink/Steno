package steno

// Differential tests against the second oracle of oracle_test.go. The budget is small by default; set STENO_ORACLE_N to a larger number of
// iterations for a long run.

import (
	"bytes"
	"errors"
	"fmt"
	"math/rand"
	"os"
	"slices"
	"strconv"
	"testing"
	"unicode/utf8"
)

// oracleN returns how many iterations a test against the second oracle runs. The oracle builds each layout on an array of 1.1 million
// entries, so it is slow: by default the tests run a twentieth of their full count, so that go test stays fast. STENO_ORACLE=full runs the
// full count (about 3 minutes in all), and STENO_ORACLE_N sets a count of its own.
func oracleN(full int) int {
	if s := os.Getenv("STENO_ORACLE_N"); s != "" {
		if n, err := strconv.Atoi(s); err == nil {
			return n
		}
	}
	if os.Getenv("STENO_ORACLE") == "full" {
		return full
	}
	return max(1, full/20)
}

var oracleWindows = func() []int {
	var ws []int
	last := -1
	for c := 0; c <= 0x10FFFF; c += 0x10 {
		if w, _ := oWindow(c); w >= 0 && w != last {
			ws = append(ws, w)
			last = w
		}
	}
	return ws
}()

var edgeCodePoints = []rune{
	0, 1, 8, 9, 0xA, 0xB, 0xD, 0x1F, 0x20, 0x40, 0x41, 0x5A, 0x5B, 0x60, 0x61, 0x7A, 0x7B, 0x7E, 0x7F, 0x80, 0xBF, 0xC0, 0x13F, 0x140,
	0x2FF, 0x300, 0x36F, 0x370, 0x37F, 0x380, 0x52F, 0x530, 0x5FF, 0x600, 0x7FF, 0x800, 0xEFF, 0xF00, 0x1FFF, 0x2000, 0x20CF, 0x20D0, 0x20FF,
	0x2100, 0x2FFF, 0x3000, 0x300F, 0x3010, 0x303F, 0x3040, 0x3041, 0x3096, 0x3097, 0x30A0, 0x30A1, 0x30FA, 0x30FB, 0x30FC, 0x30FF, 0x3100,
	0x33FF, 0x3400, 0x4DFF, 0x4E00, 0x9FFF, 0xA000, 0xABFF, 0xAC00, 0xD7A3, 0xD7A4, 0xD7FF, 0xD800, 0xDBFF, 0xDC00, 0xDFFF, 0xE000, 0xFEFF,
	0xFF00, 0xFF1F, 0xFF20, 0xFFFE, 0xFFFF, 0x10000, 0x10FBF, 0x10FC0, 0x1FFFF, 0x20000, 0x3FFFF, 0x40000, 0x10FF80, 0x10FFFF,
}

// oracleText makes a value designed to stress the fast paths: ASCII runs of every length with a breaking code point at every offset, runs in
// any window, wide contexts entered and left, kana slot changes, and the edges of the format.
func oracleText(rng *rand.Rand, segments int, surrogates bool) []rune {
	var out []rune
	add := func(c rune) {
		if c >= 0xD800 && c <= 0xDFFF && !surrogates {
			c = 'x'
		}
		if len(out) > 0 && isHighSurrogate(out[len(out)-1]) && isLowSurrogate(c) {
			c = 'y'
		}
		out = append(out, c)
	}
	for s := 0; s < segments; s++ {
		switch rng.Intn(11) {
		case 0, 1: // ASCII run, sometimes long, sometimes with letters only
			n := rng.Intn(48)
			if rng.Intn(8) == 0 {
				n = rng.Intn(300)
			}
			for j := 0; j < n; j++ {
				switch rng.Intn(20) {
				case 0:
					add(rune(rng.Intn(0x20)))
				case 1:
					add(0x7F)
				default:
					add(rune(0x20 + rng.Intn(0x5F)))
				}
			}
		case 2: // a run in a random window
			w := oracleWindows[rng.Intn(len(oracleWindows))]
			_, e := oWindow(w)
			for j := rng.Intn(12); j >= 0; j-- {
				add(rune(w + rng.Intn(e-w+1)))
			}
		case 3: // ideographs and kana
			for j := rng.Intn(20); j >= 0; j-- {
				switch rng.Intn(6) {
				case 0:
					add(rune(0x4E00 + rng.Intn(0x5200)))
				case 1:
					add(rune(0x3041 + rng.Intn(0x56)))
				case 2:
					add(rune(0x30A1 + rng.Intn(0x5C)))
				case 3:
					add(rune(0x3000 + rng.Intn(0x40)))
				case 4:
					add(rune(0xFF00 + rng.Intn(0x100)))
				default:
					add(rune(0x20000 + rng.Intn(0x20000)))
				}
			}
		case 4: // Hangul
			for j := rng.Intn(20); j >= 0; j-- {
				if rng.Intn(5) == 0 {
					add(rune(0x3000 + rng.Intn(0x40)))
				} else {
					add(rune(0xAC00 + rng.Intn(0xD7A4-0xAC00)))
				}
			}
		case 5: // edges
			for j := rng.Intn(4); j >= 0; j-- {
				add(edgeCodePoints[rng.Intn(len(edgeCodePoints))])
			}
		case 6: // any code point
			add(rune(rng.Intn(0x110000)))
		case 7: // supplementary windows near the end of the code space and plane 1
			b := []int{0x10000, 0x1F000, 0x1FF80, 0x40000, 0x10FF00, 0xE0000}[rng.Intn(6)]
			for j := rng.Intn(10); j >= 0; j-- {
				add(rune(b + rng.Intn(0x100)))
			}
		case 8: // two windows alternating (second window use)
			w1 := oracleWindows[rng.Intn(len(oracleWindows))]
			w2 := oracleWindows[rng.Intn(len(oracleWindows))]
			for j := rng.Intn(10); j >= 0; j-- {
				w := w1
				if rng.Intn(2) == 0 {
					w = w2
				}
				add(rune(w + rng.Intn(0x30)))
			}
		case 9: // surrogates
			if surrogates {
				add(rune(0xD800 + rng.Intn(0x800)))
			}
		default: // a single Latin letter or ASCII letter between wide text
			add(rune(0x41 + rng.Intn(26)))
		}
	}
	return out
}

func describe(cps []rune) string {
	if len(cps) > 60 {
		return fmt.Sprintf("%d code points %U...", len(cps), cps[:60])
	}
	return fmt.Sprintf("%U", cps)
}

func runesHaveSurrogate(cps []rune) int {
	for i, c := range cps {
		if c >= 0xD800 && c <= 0xDFFF {
			return i
		}
	}
	return -1
}

// checkValueAgainstOracle checks every one-shot form for the value cps against the oracle.
func checkValueAgainstOracle(t *testing.T, cps []rune) {
	t.Helper()
	want, ok := oEncode(cps)
	if !ok {
		t.Fatalf("generator made a non-value")
	}
	if got, err := EncodeRunes(cps); err != nil || string(got) != string(want) {
		t.Fatalf("EncodeRunes(%s) = %X, %v; want %X", describe(cps), got, err, want)
	}
	u := toUTF16(cps)
	if got := EncodeUTF16(u); string(got) != string(want) {
		t.Fatalf("EncodeUTF16(%s) = %X; want %X", describe(cps), got, want)
	}
	// Exact capacity, after a prefix: must not grow.
	dst := make([]byte, 3, 3+len(want))
	if got := AppendEncodeUTF16(dst, u); string(got[3:]) != string(want) || &got[0] != &dst[0] {
		t.Fatalf("AppendEncodeUTF16 exact cap (%s) wrong or reallocated", describe(cps))
	}
	if got, err := AppendEncodeRunes(dst, cps); err != nil || string(got[3:]) != string(want) || &got[0] != &dst[0] {
		t.Fatalf("AppendEncodeRunes exact cap (%s) wrong or reallocated", describe(cps))
	}
	if got, err := DecodeRunes(want); err != nil || !slices.Equal(got, cps) {
		t.Fatalf("DecodeRunes(%X) = %U, %v; want %s", want, got, err, describe(cps))
	}
	if got, err := DecodeUTF16(want); err != nil || !slices.Equal(got, u) {
		t.Fatalf("DecodeUTF16(%X) = %X, %v", want, got, err)
	}
	rd := make([]rune, 2, 2+len(cps))
	if got, err := AppendDecodeRunes(rd, want); err != nil || !slices.Equal(got[2:], cps) || &got[0] != &rd[0] {
		t.Fatalf("AppendDecodeRunes exact cap wrong or reallocated")
	}
	ud := make([]uint16, 2, 2+len(u))
	if got, err := AppendDecodeUTF16(ud, want); err != nil || !slices.Equal(got[2:], u) || &got[0] != &ud[0] {
		t.Fatalf("AppendDecodeUTF16 exact cap wrong or reallocated")
	}
	if si := runesHaveSurrogate(cps); si < 0 {
		s := string(cps)
		if got, err := Encode(s); err != nil || string(got) != string(want) {
			t.Fatalf("Encode(%s) = %X, %v; want %X", describe(cps), got, err, want)
		}
		if got, err := AppendEncode(dst, s); err != nil || string(got[3:]) != string(want) || &got[0] != &dst[0] {
			t.Fatalf("AppendEncode exact cap (%s) wrong or reallocated", describe(cps))
		}
		if got, err := encodeInOnePiece(s, nil); err != nil || string(got) != string(want) {
			t.Fatalf("Encoder.Append (%s) = %X, %v", describe(cps), got, err)
		}
		if got, err := Decode(want); err != nil || got != s {
			t.Fatalf("Decode(%X) = %q, %v; want %q", want, got, err, s)
		}
		db := make([]byte, 1, 1+len(s))
		if got, err := AppendDecode(db, want); err != nil || string(got[1:]) != s || &got[0] != &db[0] {
			t.Fatalf("AppendDecode exact cap wrong or reallocated")
		}
		if IsASCIIText(want) != isASCIIText(cps) {
			t.Fatalf("IsASCIIText(%X)", want)
		}
	} else {
		_, offs, _, _ := oDecode(want)
		var de *DecodeError
		if _, err := Decode(want); !errors.As(err, &de) || de.Reason != LoneSurrogate || de.Offset != offs[si] {
			t.Fatalf("Decode(%X) = %v; want lone-surrogate at %d", want, err, offs[si])
		}
	}
}

// encodeInOnePiece encodes s with an Encoder in one UTF-8 piece.
func encodeInOnePiece(s string, dst []byte) ([]byte, error) {
	var e Encoder
	dst, err := e.AppendString(dst, s)
	if err != nil {
		return nil, err
	}
	return e.Flush(dst)
}

func TestOracleDifferential(t *testing.T) {
	rng := rand.New(rand.NewSource(20261121))
	n := oracleN(4000)
	for i := 0; i < n; i++ {
		checkValueAgainstOracle(t, oracleText(rng, 1+rng.Intn(12), i%2 == 0))
	}
}

// TestOracleWordBoundaries puts each kind of run-ending code point at every offset of ASCII runs of every length 0..40, after prefixes that
// put the state in each kind of context, and checks every form.
func TestOracleWordBoundaries(t *testing.T) {
	prefixes := [][]rune{
		{}, {0x416, 0x416}, {0x3042}, {0x30A2}, {0x4E00}, {0xD55C}, {0x4E00, 'a'}, {0x10400, 0x10401}, {0x1F600}, {0x3042, 0xFF21},
	}
	breakers := []rune{0x1F, 0x7F, 0x80, 0xE9, 0x416, 0x3042, 0x4E00, 0xD55C, 0x1F600, 0x10FFFF, 'A', 'z', '@', 0x09, 0x0A, 0x0D, 0xFFFF, 0xD800}
	for _, p := range prefixes {
		for n := 0; n <= 40; n++ {
			for at := 0; at <= n; at++ {
				for _, b := range breakers {
					cps := slices.Clone(p)
					for j := 0; j < n; j++ {
						if j == at {
							cps = append(cps, b)
						}
						cps = append(cps, rune(0x20+(j*7)%0x5F))
					}
					checkValueAgainstOracle(t, cps)
				}
			}
		}
	}
}

// TestInvalidUTF8Offsets puts bad UTF-8 at every position near the word boundaries.
func TestInvalidUTF8Offsets(t *testing.T) {
	bads := []string{"\xff", "\x80", "\xc0\x80", "\xc1\xbf", "\xc3", "\xe0\x80\x80", "\xe0\x9f\xbf", "\xed\xa0\x80", "\xed\xbf\xbf", "\xef\xbf",
		"\xf0\x8f\xbf\xbf", "\xf4\x90\x80\x80", "\xf5\x80\x80\x80", "\xf0\x9f\x98", "\xe4\xb8"}
	for _, prefix := range []string{"", "Ж", "あ", "中", "한"} {
		for n := 0; n <= 24; n++ {
			for at := 0; at <= n; at++ {
				for _, bad := range bads {
					s := prefix + "abcdefghijklmnopqrstuvwxyz"[:at] + bad + "abcdefghijklmnopqrstuvwxyz"[at:n]
					want := len(prefix) + at
					_, err := Encode(s)
					var ee *EncodeError
					if !errors.As(err, &ee) || ee.Reason != InvalidUTF8 || ee.Offset != want {
						t.Fatalf("Encode(%q) = %v; want invalid-utf8 at %d", s, err, want)
					}
					if utf8.ValidString(s) {
						t.Fatalf("generator: %q is valid", s)
					}
					// The streaming encoder, byte by byte.
					var e Encoder
					var gotErr error
					for i := 0; i < len(s) && gotErr == nil; i++ {
						_, gotErr = e.AppendString(nil, s[i:i+1])
					}
					if gotErr == nil {
						_, gotErr = e.Flush(nil)
					}
					if !errors.As(gotErr, &ee) || ee.Reason != InvalidUTF8 {
						t.Fatalf("Encoder byte by byte (%q): %v", s, gotErr)
					}
				}
			}
		}
	}
}

// TestOracleLongValues encodes and decodes values of a few megabytes, starting with U+FEFF or U+FFFE, against the oracle.
func TestOracleLongValues(t *testing.T) {
	if os.Getenv("STENO_LONG_VALUES") == "" {
		t.Skip("set STENO_LONG_VALUES=1 to run (about 3 minutes, the oracle is slow)")
	}
	rng := rand.New(rand.NewSource(42))
	for k, start := range []rune{0xFEFF, 0xFFFE, 'a'} {
		cps := []rune{start}
		for len(cps) < 300000 {
			for _, c := range oracleText(rng, 20, k == 2) {
				if isHighSurrogate(cps[len(cps)-1]) && isLowSurrogate(c) {
					c = 'q'
				}
				cps = append(cps, c)
			}
		}
		if k == 0 {
			// A long pure ASCII stretch too.
			for j := 0; j < 1<<20; j++ {
				cps = append(cps, rune(0x20+j%0x5F))
			}
		}
		want, ok := oEncode(cps)
		if !ok {
			t.Fatal("generator made a non-value")
		}
		if got, err := EncodeRunes(cps); err != nil || !bytes.Equal(got, want) {
			t.Fatalf("EncodeRunes long value %d differs", k)
		}
		if got := EncodeUTF16(toUTF16(cps)); !bytes.Equal(got, want) {
			t.Fatalf("EncodeUTF16 long value %d differs", k)
		}
		if got, err := DecodeRunes(want); err != nil || !slices.Equal(got, cps) {
			t.Fatalf("DecodeRunes long value %d differs: %v", k, err)
		}
		if runesHaveSurrogate(cps) < 0 {
			if got, err := Encode(string(cps)); err != nil || !bytes.Equal(got, want) {
				t.Fatalf("Encode long value %d differs", k)
			}
			if got, err := Decode(want); err != nil || got != string(cps) {
				t.Fatalf("Decode long value %d differs", k)
			}
		}
		t.Logf("value %d: %d code points, %d Steno bytes", k, len(cps), len(want))
	}
}
