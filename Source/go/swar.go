package steno

import (
	"encoding/binary"
	"math/bits"
)

// Word-at-a-time (SWAR) helpers for runs of printable ASCII, which take 1 byte each and never change the layout (FORMAT.md §14.2). Printable
// ASCII from U+0020 is the Steno byte c - 0x19 in every layout, up to U+007E (or U+0040 in the CJK context with a kana slot other than letters).
//
// The range checks work on 8 bytes at once. For a byte x below 0x80, x + a has its top bit set exactly when x >= 0x80 - a, and the sum does not
// carry into the next byte. A byte of 0x80 or more is flagged by its own top bit, and the carries it may cause only reach the bytes above it,
// so the lowest flagged byte is always found correctly.

const (
	lo8  = 0x0101010101010101
	hi8  = 0x8080808080808080
	lo16 = 0x0001000100010001
	hi16 = 0x8000800080008000
)

// repeat returns b in every byte of a word.
func repeat(b uint8) uint64 { return lo8 * uint64(b) }

// load64 reads 8 bytes of s from i, little-endian. The compiler turns the byte loads into one load, as it does for binary.LittleEndian.
func load64[T string | []byte](s T, i int) uint64 {
	s = s[i : i+8]
	return uint64(s[0]) | uint64(s[1])<<8 | uint64(s[2])<<16 | uint64(s[3])<<24 |
		uint64(s[4])<<32 | uint64(s[5])<<40 | uint64(s[6])<<48 | uint64(s[7])<<56
}

// encRunLen returns how many of the bytes of w, from the lowest, are in 0x20..hi, where add is 0x7F - hi in every byte. It returns 8 for all.
func encRunLen(w, add uint64) int {
	bad := (w | ^(w + 0x6060606060606060) | (w + add)) & hi8
	return bits.TrailingZeros64(bad) >> 3
}

// decRunLen returns how many of the bytes of w, from the lowest, are in 0x07..hi, where add is 0x7F - hi in every byte. It returns 8 for all.
func decRunLen(w, add uint64) int {
	bad := (w | ^(w + 0x7979797979797979) | (w + add)) & hi8
	return bits.TrailingZeros64(bad) >> 3
}

// encRunLen16 returns how many of the four 16-bit lanes of w, from the lowest, are in 0x20..hi, where add is 0x7FFF - hi in every lane.
func encRunLen16(w, add uint64) int {
	bad := (w | ^(w + 0x7FE07FE07FE07FE0) | (w + add)) & hi16
	return bits.TrailingZeros64(bad) >> 4
}

// appendASCIIRun16 encodes the printable ASCII at the start of u, 4 code units at a time, and returns the number of units it encoded.
func appendASCIIRun16(dst []byte, u []uint16, add uint64) ([]byte, int) {
	i := 0
	for i+4 <= len(u) {
		q := u[i : i+4]
		w := uint64(q[0]) | uint64(q[1])<<16 | uint64(q[2])<<32 | uint64(q[3])<<48
		n := encRunLen16(w, add)
		if n == 0 {
			break
		}
		v := w - 0x0019001900190019
		v = v&0xFF | v>>8&0xFF00 | v>>16&0xFF0000 | v>>24&0xFF000000
		dst = appendPart32(dst, uint32(v), n)
		if n < 4 {
			return dst, i + n
		}
		i += 4
	}
	return dst, i
}

// appendPart64 appends the n lowest bytes of v. It stores the whole word only when all 8 bytes belong to the result, so that it never writes
// past the bytes it appends (a caller may keep data in the spare capacity of dst).
func appendPart64(dst []byte, v uint64, n int) []byte {
	if n == 8 {
		return binary.LittleEndian.AppendUint64(dst, v)
	}
	for j := 0; j < n; j++ {
		dst = append(dst, byte(v>>(8*j)))
	}
	return dst
}

// appendPart32 appends the n lowest bytes of v, n at most 4, in the same way.
func appendPart32(dst []byte, v uint32, n int) []byte {
	if n == 4 {
		return binary.LittleEndian.AppendUint32(dst, v)
	}
	for j := 0; j < n; j++ {
		dst = append(dst, byte(v>>(8*j)))
	}
	return dst
}
