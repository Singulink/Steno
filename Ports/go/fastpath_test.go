package steno

import (
	"bytes"
	"slices"
	"strings"
	"testing"
)

// TestFastPathBoundaries checks the fast paths (8-byte ASCII words, 4-unit UTF-16 words, the main window and the wide ranges) against the plain
// codec of reference_test.go, with a state change at every offset around the 8-byte boundaries, in every context, and with the encoding cut at
// every byte, so that inputs end inside a word and inside a code point.
func TestFastPathBoundaries(t *testing.T) {
	// The text before the ASCII sets the context: general with another main window, CJK with each kana slot, Hangul.
	contexts := []string{"", "\n\r", "Жж", "東", "東あ", "東ア", "한", "ሀሁ", "😀😁"}
	// The code point after the ASCII changes the state, or ends the run of 1-byte codes in another way.
	changes := []string{"Ж", "東", "あ", "ア", "한", "\t", "\x01", "\x7f", "é", "😀", "ሰ", "Ａ", "、", "Zz"}
	ascii := "The quick brown fox jumps over 1 lazy dog."
	cases := 0
	for ci, ctx := range contexts {
		for p := 0; p <= 17; p++ {
			for chi, ch := range changes {
				for q := 0; q <= 17; q++ {
					s := ctx + ascii[:p] + ch + ascii[len(ascii)-q:] + ch + ascii[:q]
					checkAgainstReference(t, s, (ci+p+chi+q)%3 == 0)
					cases++
				}
			}
		}
	}
	// Long runs of each fast path, cut at every length.
	long := []string{strings.Repeat("abcdefgh", 5), strings.Repeat("Жизнь", 6), strings.Repeat("東京都", 8), strings.Repeat("한국어", 8),
		strings.Repeat("ひらがな", 6), strings.Repeat("カタカナ", 6), strings.Repeat("😀", 20)}
	for _, l := range long {
		r := []rune(l)
		for n := 0; n <= len(r); n++ {
			checkAgainstReference(t, string(r[:n]), true)
			checkAgainstReference(t, "東"+string(r[:n])+"x", false)
			cases += 2
		}
	}
	t.Logf("%d texts checked", cases)
}

func checkAgainstReference(t *testing.T, s string, cuts bool) {
	t.Helper()
	cps := []rune(s)
	ints := make([]int, len(cps))
	for i, c := range cps {
		ints[i] = int(c)
	}
	want, _ := refEncode(ints)

	got, err := Encode(s)
	if err != nil || !bytes.Equal(got, want) {
		t.Fatalf("Encode(%q) = %X, %v; plain codec %X", s, got, err, want)
	}
	if got, _ := EncodeRunes(cps); !bytes.Equal(got, want) {
		t.Fatalf("EncodeRunes(%q) = %X; plain codec %X", s, got, want)
	}
	u := toUTF16(cps)
	if got := EncodeUTF16(u); !bytes.Equal(got, want) {
		t.Fatalf("EncodeUTF16(%q) = %X; plain codec %X", s, got, want)
	}
	if IsASCIIText(want) != isASCIIText(cps) {
		t.Fatalf("IsASCIIText(%X) = %v for %q", want, !isASCIIText(cps), s)
	}
	// Exact and too small destinations.
	exact := make([]byte, 0, len(want))
	if got, _ := AppendEncode(exact, s); !bytes.Equal(got, want) || len(want) > 0 && &got[0] != &exact[:1][0] {
		t.Fatalf("AppendEncode(%q) into an exact destination", s)
	}
	if got := AppendEncodeUTF16(make([]byte, 0, len(want)), u); !bytes.Equal(got, want) {
		t.Fatalf("AppendEncodeUTF16(%q) into an exact destination", s)
	}
	if got, _ := AppendEncode(make([]byte, 1, 3), s); !bytes.Equal(got[1:], want) {
		t.Fatalf("AppendEncode(%q) into a short destination", s)
	}

	if got, err := Decode(want); err != nil || got != s {
		t.Fatalf("Decode(%X) = %q, %v; want %q", want, got, err, s)
	}
	if got, err := AppendDecode(make([]byte, 0, len(s)), want); err != nil || string(got) != s {
		t.Fatalf("AppendDecode(%X) into an exact destination = %q, %v", want, got, err)
	}
	if got, err := DecodeRunes(want); err != nil || !slices.Equal(got, cps) {
		t.Fatalf("DecodeRunes(%X) = %X, %v", want, got, err)
	}
	if got, err := DecodeUTF16(want); err != nil || !slices.Equal(got, u) {
		t.Fatalf("DecodeUTF16(%X) = %X, %v", want, got, err)
	}

	if !cuts {
		return
	}
	// Every cut of the bytes, and the bytes 00, FF and 05 at every position, against the plain codec.
	for n := 0; n <= len(want); n++ {
		checkDecodeAgainstReference(t, want[:n])
		if n < len(want) {
			bad := slices.Clone(want)
			bad[n] = 0
			checkDecodeAgainstReference(t, bad)
			bad[n] = 0xFF
			checkDecodeAgainstReference(t, bad)
			bad[n] = 0x05
			checkDecodeAgainstReference(t, bad)
		}
	}
}

func checkDecodeAgainstReference(t *testing.T, b []byte) {
	t.Helper()
	wantCps, wantReason, wantOffset := refDecode(b)
	cps, err := DecodeRunes(b)
	var gotReason string
	var gotOffset int
	if err != nil {
		de := err.(*DecodeError)
		gotReason, gotOffset = de.Reason.String(), de.Offset
	}
	if gotReason != wantReason || gotOffset != wantOffset {
		t.Fatalf("DecodeRunes(%X): %q at %d; plain codec %q at %d", b, gotReason, gotOffset, wantReason, wantOffset)
	}
	if err != nil {
		if _, err := DecodeUTF16(b); reasonOf(err) != wantReason {
			t.Fatalf("DecodeUTF16(%X): %v; plain codec %s", b, err, wantReason)
		}
		_, err8 := Decode(b)
		if de, ok := err8.(*DecodeError); !ok || de.Reason.String() != wantReason || de.Offset != wantOffset {
			t.Fatalf("Decode(%X): %v; plain codec %s at %d", b, err8, wantReason, wantOffset)
		}
		return
	}
	for i, c := range wantCps {
		if rune(c) != cps[i] {
			t.Fatalf("DecodeRunes(%X) = %X; plain codec %X", b, cps, wantCps)
		}
	}
	if s, err := Decode(b); err != nil || s != string(cps) {
		t.Fatalf("Decode(%X) = %q, %v", b, s, err)
	}
}
