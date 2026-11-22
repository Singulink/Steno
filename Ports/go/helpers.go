package steno

import (
	"encoding/binary"
	"math"
)

// Name is the name of the Steno Encoding, version 1, to store next to Steno data or in its schema: the bytes carry no header or version marker.
const Name = "x-steno"

// asciiText marks the Steno bytes that encode tab, line feed, carriage return and U+0020..U+007E in the initial layout (FORMAT.md §14.1).
var asciiText = func() (t [256]bool) {
	t[0x02], t[0x03], t[0x05] = true, true, true
	for b := 0x07; b <= 0x65; b++ {
		t[b] = true
	}
	return t
}()

// IsASCIIText reports whether the Steno bytes b encode a value that has only tab, line feed, carriage return and the printable ASCII characters
// U+0020..U+007E. The empty value is ASCII text. The answer comes from the bytes alone, without decoding (FORMAT.md §14.1): such a value has only
// the bytes 02, 03, 05 and 07..65, and bytes that are all of these values are valid. For bytes that are not valid Steno data it returns false.
//
// When it returns true, the UTF-8 form of the value has as many bytes as b: byte x stands for x + 0x19, and 02, 03 and 05 for tab, line feed and
// carriage return.
func IsASCIIText(b []byte) bool {
	// 8 bytes at a time while they are all in 07..65; the rest, and words with 02, 03 or 05, byte by byte.
	for len(b) >= 8 {
		if decRunLen(binary.LittleEndian.Uint64(b), 0x1A1A1A1A1A1A1A1A) == 8 {
			b = b[8:]
			continue
		}
		for _, x := range b[:8] {
			if !asciiText[x] {
				return false
			}
		}
		b = b[8:]
	}
	for _, x := range b {
		if !asciiText[x] {
			return false
		}
	}
	return true
}

func maxLen(n, factor int, name string) int {
	if n < 0 || n > math.MaxInt/factor {
		panic("steno: " + name + ": length out of range")
	}
	return n * factor
}

// MaxEncodedLen returns the maximum number of Steno bytes of a value whose UTF-8 form has n bytes: 2n. An ASCII character takes at most 2 Steno
// bytes, a character of 2 or 3 UTF-8 bytes at most 3, and one of 4 UTF-8 bytes at most 4. The ASCII control characters other than tab, line feed
// and carriage return take exactly 2. It panics if n is negative or the result overflows an int.
func MaxEncodedLen(n int) int { return maxLen(n, 2, "MaxEncodedLen") }

// MaxEncodedLenRunes returns the maximum number of Steno bytes of a value of n code points: 4n. No code point takes more than 4 bytes. It panics if
// n is negative or the result overflows an int.
func MaxEncodedLenRunes(n int) int { return maxLen(n, 4, "MaxEncodedLenRunes") }

// MaxEncodedLenUTF16 returns the maximum number of Steno bytes of a value of n UTF-16 code units: 3n. A code point below U+10000 (one code unit)
// takes at most 3 bytes, and a surrogate pair at most 4. It panics if n is negative or the result overflows an int.
func MaxEncodedLenUTF16(n int) int { return maxLen(n, 3, "MaxEncodedLenUTF16") }

// MaxDecodedLen returns the maximum number of UTF-8 bytes of the value in n Steno bytes: 4n. A code point takes at least 1 Steno byte and at most
// 4 UTF-8 bytes; a supplementary code point of the main window takes 1 Steno byte. It panics if n is negative or the result overflows an int.
func MaxDecodedLen(n int) int { return maxLen(n, 4, "MaxDecodedLen") }

// MaxDecodedLenRunes returns the maximum number of code points of the value in n Steno bytes: n. It panics if n is negative.
func MaxDecodedLenRunes(n int) int { return maxLen(n, 1, "MaxDecodedLenRunes") }

// MaxDecodedLenUTF16 returns the maximum number of UTF-16 code units of the value in n Steno bytes: 2n. It panics if n is negative or the result
// overflows an int.
func MaxDecodedLenUTF16(n int) int { return maxLen(n, 2, "MaxDecodedLenUTF16") }
