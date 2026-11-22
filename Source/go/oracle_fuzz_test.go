package steno

// FuzzOracle checks the package against the second oracle of oracle_test.go, which FuzzDecode and FuzzRoundTrip do not use. It checks decode reasons and offsets in every form and in the streaming Decoder with mixed output forms, and
// encodes code points built from the input (3 bytes each, so any code point and any surrogate can appear) in every form.

import (
	"math/rand"
	"testing"
)

func FuzzOracle(f *testing.F) {
	f.Add([]byte{0x2F, 0x50, 0x08}, int64(1))
	f.Add([]byte{0xE9, 0xF4, 0xA2, 0x01, 0x08}, int64(2))
	f.Add([]byte{0x00, 0x30, 0x42, 0x00, 0x4E, 0x00, 0x00, 0x00, 0x41}, int64(3))
	f.Fuzz(func(t *testing.T, data []byte, seed int64) {
		if len(data) > 600 {
			return
		}
		rng := rand.New(rand.NewSource(seed))
		checkGarbageAgainstOracle(t, data, rng)

		var cps []rune
		for i := 0; i+3 <= len(data); i += 3 {
			c := rune(data[i])<<16 | rune(data[i+1])<<8 | rune(data[i+2])
			switch data[i] >> 5 {
			case 0, 1, 2: // any code point
				c %= 0x110000
			case 3: // ASCII
				c = c % 0x80
			case 4: // printable ASCII
				c = 0x20 + c%0x5F
			case 5: // near the kana, ideographs and Hangul
				c = 0x3000 + c%0xA800
			default: // a window from the list
				w := oracleWindows[int(c)%len(oracleWindows)]
				c = rune(w + int(data[i+2])%0x40)
			}
			if len(cps) > 0 && isHighSurrogate(cps[len(cps)-1]) && isLowSurrogate(c) {
				c = 'z'
			}
			cps = append(cps, c)
		}
		checkValueAgainstOracle(t, cps)
	})
}
