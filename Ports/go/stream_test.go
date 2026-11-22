package steno

import (
	"bytes"
	"errors"
	"io"
	"math/rand"
	"slices"
	"strings"
	"testing"
	"testing/iotest"
	"unicode/utf8"
)

// splits cuts n units into random pieces, some of them empty.
func splits(rng *rand.Rand, n int) []int {
	cuts := []int{0}
	for p := 0; p < n; {
		p += rng.Intn(5)
		cuts = append(cuts, min(p, n))
	}
	return append(cuts, n)
}

func TestEncoderSplits(t *testing.T) {
	rng := rand.New(rand.NewSource(10))
	var e Encoder
	for i := 0; i < 2000; i++ {
		cps := randomText(rng, 30, true)
		want, _ := EncodeRunes(cps)
		e.Reset()

		// Runes, in random pieces.
		var got []byte
		var err error
		cuts := splits(rng, len(cps))
		for j := 1; j < len(cuts); j++ {
			if got, err = e.AppendRunes(got, cps[cuts[j-1]:cuts[j]]); err != nil {
				t.Fatalf("AppendRunes: %v", err)
			}
		}
		if got, err = e.Flush(got); err != nil || !bytes.Equal(got, want) {
			t.Fatalf("runes in pieces %v: %X, %v; want %X", cuts, got, err, want)
		}

		// UTF-16, one code unit per call and in random pieces: the splits fall inside surrogate pairs.
		u := toUTF16(cps)
		for _, one := range []bool{true, false} {
			e.Reset()
			got = got[:0]
			cuts = splits(rng, len(u))
			if one {
				cuts = cuts[:0]
				for j := 0; j <= len(u); j++ {
					cuts = append(cuts, j)
				}
			}
			for j := 1; j < len(cuts); j++ {
				if got, err = e.AppendUTF16(got, u[cuts[j-1]:cuts[j]]); err != nil {
					t.Fatalf("AppendUTF16: %v", err)
				}
			}
			if got, err = e.Flush(got); err != nil || !bytes.Equal(got, want) {
				t.Fatalf("UTF-16 in pieces %v: %X, %v; want %X", cuts, got, err, want)
			}
		}

		// UTF-8, one byte per call and in random pieces: the splits fall inside UTF-8 sequences.
		if hasSurrogate(cps) {
			continue
		}
		s := string(cps)
		for _, one := range []bool{true, false} {
			e.Reset()
			got = got[:0]
			cuts = splits(rng, len(s))
			if one {
				cuts = cuts[:0]
				for j := 0; j <= len(s); j++ {
					cuts = append(cuts, j)
				}
			}
			for j := 1; j < len(cuts); j++ {
				piece := s[cuts[j-1]:cuts[j]]
				if j%2 == 0 {
					got, err = e.AppendString(got, piece)
				} else {
					got, err = e.Append(got, []byte(piece))
				}
				if err != nil {
					t.Fatalf("Append: %v", err)
				}
			}
			if got, err = e.Flush(got); err != nil || !bytes.Equal(got, want) {
				t.Fatalf("UTF-8 in pieces %v: %X, %v; want %X", cuts, got, err, want)
			}
		}
	}
}

func TestEncoderErrors(t *testing.T) {
	encodeErr := func(err error) EncodeError {
		var ee *EncodeError
		if !errors.As(err, &ee) {
			t.Fatalf("got %v, want an *EncodeError", err)
		}
		return *ee
	}

	// Invalid UTF-8, in one piece and across pieces.
	for _, s := range []string{"a\xff", "ab\xc3", "\xed\xa0\x80", "\xf4\x90\x80\x80", "\xe0\x80\x80", "x\xc0\xaf"} {
		if _, err := Encode(s); err == nil {
			t.Errorf("Encode(%q) accepted invalid UTF-8", s)
		}
		if b, err := AppendEncode([]byte("keep"), s); err == nil || string(b) != "keep" {
			t.Errorf("AppendEncode(%q) = %q, %v", s, b, err)
		}
	}
	if ee := encodeErr(func() error { _, err := Encode("ab\xffc"); return err }()); ee.Offset != 2 || ee.Reason != InvalidUTF8 {
		t.Errorf("Encode: %+v", ee)
	}

	var e Encoder
	out, err := e.AppendString(nil, "a\xe3\x81")
	if err != nil {
		t.Fatal(err)
	}
	// The held sequence is broken by the next piece: an error, and the encoder is as it was.
	if b, err := e.AppendString(out, "x"); err == nil || len(b) != len(out) || encodeErr(err).Offset != -2 {
		t.Errorf("broken held sequence: %X, %v", b, err)
	}
	if _, err := e.Flush(nil); err == nil || encodeErr(err).Offset != -2 {
		t.Errorf("Flush with a held sequence: %v", err)
	}
	if out, err = e.AppendString(out, "\x82"); err != nil {
		t.Fatal(err)
	}
	if want, _ := Encode("aあ"); !bytes.Equal(out, want) {
		t.Errorf("after an error: %X, want %X", out, want)
	}

	// A high surrogate written by Flush and then a low surrogate is not a value.
	e.Reset()
	out, _ = e.AppendUTF16(nil, []uint16{0x61, 0xD800})
	out, _ = e.Flush(out)
	if _, err := e.AppendUTF16(out, []uint16{0xDC00}); err == nil || encodeErr(err).Reason != NotAValue {
		t.Errorf("low surrogate after a flushed high surrogate: %v", err)
	}
	if _, err := e.AppendRunes(out, []rune{0xDC00}); err == nil || encodeErr(err).Reason != NotAValue {
		t.Errorf("low surrogate rune after a flushed high surrogate: %v", err)
	}
	if out, err = e.AppendRunes(out, []rune{0x62}); err != nil {
		t.Fatal(err)
	}
	if want, _ := EncodeRunes([]rune{0x61, 0xD800, 0x62}); !bytes.Equal(out, want) {
		t.Errorf("%X, want %X", out, want)
	}

	// A held high surrogate before a string is a code point of its own.
	e.Reset()
	out, _ = e.AppendUTF16(nil, []uint16{0xD83D})
	out, _ = e.AppendString(out, "z")
	if want, _ := EncodeRunes([]rune{0xD83D, 'z'}); !bytes.Equal(out, want) {
		t.Errorf("%X, want %X", out, want)
	}

	// Runes that are not code points.
	for _, r := range []rune{-1, 0x110000} {
		if _, err := EncodeRunes([]rune{0x41, r}); err == nil || encodeErr(err).Reason != InvalidRune || encodeErr(err).Offset != 1 {
			t.Errorf("EncodeRunes(%X): %v", r, err)
		}
	}
}

func TestDecoderSplits(t *testing.T) {
	rng := rand.New(rand.NewSource(11))
	var d Decoder
	for i := 0; i < 2000; i++ {
		cps := randomText(rng, 30, true)
		b, _ := EncodeRunes(cps)
		u := toUTF16(cps)

		for _, one := range []bool{true, false} {
			cuts := splits(rng, len(b))
			if one {
				cuts = cuts[:0]
				for j := 0; j <= len(b); j++ {
					cuts = append(cuts, j)
				}
			}
			d.Reset()
			var gotR []rune
			var gotU []uint16
			var gotS []byte
			var errS error
			var d8 Decoder
			for j := 1; j < len(cuts); j++ {
				piece := b[cuts[j-1]:cuts[j]]
				var err error
				if j%2 == 0 {
					gotR, err = d.AppendRunes(gotR, piece)
					gotU = append(gotU, toUTF16(gotR[len(gotR)-countNew(gotR, gotU):])...)
				} else {
					var nu []uint16
					nu, err = d.AppendUTF16(nil, piece)
					gotU = append(gotU, nu...)
					gotR = append(gotR, utf16ToRunes(nu)...)
				}
				if err != nil {
					t.Fatalf("Decoder: %v", err)
				}
				if errS == nil {
					gotS, errS = d8.Append(gotS, piece)
				}
			}
			if err := d.Flush(); err != nil || !slices.Equal(gotU, u) {
				t.Fatalf("pieces %v of %X: %X, %v; want %X", cuts, b, gotU, err, u)
			}
			if hasSurrogate(cps) {
				if reasonOf(errS) != "lone-surrogate" {
					t.Fatalf("Decoder.Append on %X: %v, want lone-surrogate", b, errS)
				}
			} else if errS != nil || d8.Flush() != nil || string(gotS) != string(cps) {
				t.Fatalf("Decoder.Append on %X: %q, %v", b, gotS, errS)
			}
		}
	}
}

// countNew returns the number of runes at the end of r that are not yet in u.
func countNew(r []rune, u []uint16) int {
	return len(r) - len(utf16ToRunes(u))
}

// utf16ToRunes reads code units by FORMAT.md §3.
func utf16ToRunes(u []uint16) []rune {
	var r []rune
	for i := 0; i < len(u); i++ {
		c := rune(u[i])
		if isHighSurrogate(c) && i+1 < len(u) && isLowSurrogate(rune(u[i+1])) {
			c = 0x10000 + (c-0xD800)<<10 + rune(u[i+1]) - 0xDC00
			i++
		}
		r = append(r, c)
	}
	return r
}

func TestDecoderErrors(t *testing.T) {
	b, _ := Encode("Москва 東京")

	// Finishing early: every cut inside a code point is truncated at Flush, and the decoder keeps going after more bytes.
	var d Decoder
	for cut := 0; cut <= len(b); cut++ {
		d.Reset()
		out, err := d.Append(nil, b[:cut])
		if err != nil {
			t.Fatal(err)
		}
		_, oneShot := Decode(b[:cut])
		if ferr := d.Flush(); reasonOf(ferr) != reasonOf(oneShot) {
			t.Errorf("cut %d: Flush %v, one-shot %v", cut, ferr, oneShot)
		}
		out, err = d.Append(out, b[cut:])
		if err != nil || d.Flush() != nil || string(out) != "Москва 東京" {
			t.Errorf("cut %d: %q, %v", cut, out, err)
		}
	}

	// An error leaves the decoder as it was, with offsets from the start of the value.
	d.Reset()
	out, _ := d.Append(nil, b[:3]) // "М" and the lead byte of "о"... or "о" itself: either way the offsets count from the value
	if got, err := d.Append(out, []byte{b[3], 0x00}); err == nil || len(got) != len(out) {
		t.Errorf("zero byte: %q, %v", got, err)
	} else if de := err.(*DecodeError); de.Reason != ZeroByte || de.Offset != 4 {
		t.Errorf("zero byte: %+v", de)
	}
	out, err := d.Append(out, b[3:])
	if err != nil || string(out) != "Москва 東京" {
		t.Errorf("after an error: %q, %v", out, err)
	}

	// Surrogate pair across calls.
	high, _ := EncodeRunes([]rune{0xD800})
	pair := append(slices.Clone(high), 0xF8, 0xC9, 0x40) // the bytes of U+DC00 in the same layout (see FORMAT.md §11.12)
	d.Reset()
	if _, err := d.AppendRunes(nil, pair[:len(high)]); err != nil {
		t.Fatal(err)
	}
	if _, err := d.AppendRunes(nil, pair[len(high):]); reasonOf(err) != "surrogate-pair" {
		t.Errorf("pair across calls: %v", err)
	}
	if _, err := DecodeRunes(pair); reasonOf(err) != "surrogate-pair" {
		t.Errorf("pair: %v", err)
	}
}

func TestWriterReader(t *testing.T) {
	text := strings.Repeat("Grüße, Москва! 東京タワー 서울 😀\n", 400)
	want, _ := Encode(text)

	var buf bytes.Buffer
	w := NewWriter(&buf)
	for i := 0; i < len(text); {
		n := min(len(text)-i, 1+i%7)
		if _, err := w.Write([]byte(text[i : i+n])); err != nil {
			t.Fatal(err)
		}
		i += n
	}
	if err := w.Close(); err != nil || !bytes.Equal(buf.Bytes(), want) {
		t.Fatalf("Writer: %d bytes, %v; want %d", buf.Len(), err, len(want))
	}

	// Large writes are cut into chunks; the result is the same.
	buf.Reset()
	w.Reset(&buf)
	if _, err := w.WriteString(text); err != nil || w.Close() != nil || !bytes.Equal(buf.Bytes(), want) {
		t.Fatalf("Writer, one write: %v", err)
	}

	// A write that ends inside a UTF-8 sequence, then Close.
	buf.Reset()
	w.Reset(&buf)
	if _, err := w.WriteString("ab\xe3\x81"); err != nil {
		t.Fatal(err)
	}
	if err := w.Close(); reasonOfEncode(err) != InvalidUTF8 {
		t.Errorf("Close inside a sequence: %v", err)
	}
	if n, err := w.WriteString("\xff"); n != 0 || reasonOfEncode(err) != InvalidUTF8 {
		t.Errorf("Write of invalid UTF-8: %d, %v", n, err)
	}

	// Reader, with one byte per read from the underlying reader and small reads.
	for _, r := range []io.Reader{bytes.NewReader(want), iotest.OneByteReader(bytes.NewReader(want)), iotest.HalfReader(bytes.NewReader(want))} {
		got, err := io.ReadAll(NewReader(r))
		if err != nil || string(got) != text {
			t.Fatalf("Reader: %d bytes, %v", len(got), err)
		}
	}
	if err := iotest.TestReader(NewReader(bytes.NewReader(want)), []byte(text)); err != nil {
		t.Fatal(err)
	}

	// Reset reads a new value with the same Reader, also after an error.
	zr := NewReader(bytes.NewReader([]byte{0x00}))
	if _, err := io.ReadAll(zr); reasonOf(err) != "zero-byte" {
		t.Errorf("Reader on 00: %v", err)
	}
	zr.Reset(bytes.NewReader(want))
	if got, err := io.ReadAll(zr); err != nil || string(got) != text {
		t.Errorf("Reader after Reset: %v", err)
	}

	// A truncated stream: the text before the cut, then truncated.
	long, _ := Encode(text + "東")
	cut := long[:len(long)-1]
	got, err := io.ReadAll(NewReader(bytes.NewReader(cut)))
	if reasonOf(err) != "truncated" || !strings.HasPrefix(text, string(got)) || !utf8.Valid(got) {
		t.Errorf("Reader on cut bytes: %v", err)
	}

	// Bad bytes in the middle: the text before them, then the error.
	bad := slices.Clone(want)
	bad[len(bad)/2] = 0
	got, err = io.ReadAll(NewReader(bytes.NewReader(bad)))
	var de *DecodeError
	if !errors.As(err, &de) || de.Reason != ZeroByte {
		t.Errorf("Reader on bad bytes: %v", err)
	} else if want, _ := DecodeRunes(bad[:de.Offset]); string(got) != string(want) {
		t.Errorf("Reader on bad bytes: %d bytes before the error, want %d", len(got), len(string(want)))
	}
}

func reasonOfEncode(err error) EncodeReason {
	var ee *EncodeError
	if errors.As(err, &ee) {
		return ee.Reason
	}
	return 0
}

// TestBounds checks the maximum-size helpers on worst-case inputs, and appending to destinations of exact and too small capacity.
func TestBounds(t *testing.T) {
	type input struct {
		name string
		cps  []rune
	}
	var inputs []input
	controls := []rune{}
	for c := rune(0); c < 0x20; c++ {
		if c != 9 && c != 10 && c != 13 {
			controls = append(controls, c)
		}
	}
	inputs = append(inputs, input{"controls", controls})
	// Code points that take 3 bytes below U+10000 and 4 above, switching each time.
	inputs = append(inputs, input{"switching BMP", []rune{0x1230, 0x3100, 0xA000, 0xE000, 0x1230, 0x3100}})
	inputs = append(inputs, input{"switching supplementary", []rune{0x40000, 0x50000, 0x60000, 0x70000, 0x80000}})
	inputs = append(inputs, input{"two UTF-8 bytes", []rune{0x7FF, 0x5FF, 0x7FF, 0x5FF}})
	inputs = append(inputs, input{"surrogates", []rune{0xD800, 0xDBFF, 0x41, 0xDC00, 0xDFFF}})
	inputs = append(inputs, input{"main window supplementary", []rune{0x1F600, 0x1F601, 0x1F602, 0x1F603, 0x1F604, 0x1F605}})
	rng := rand.New(rand.NewSource(12))
	for i := 0; i < 500; i++ {
		inputs = append(inputs, input{"random", randomText(rng, 30, true)})
	}

	for _, in := range inputs {
		b, err := EncodeRunes(in.cps)
		if err != nil {
			t.Fatal(err)
		}
		u := toUTF16(in.cps)
		if len(b) > MaxEncodedLenRunes(len(in.cps)) || len(b) > MaxEncodedLenUTF16(len(u)) {
			t.Errorf("%s: %d bytes for %d code points, %d code units", in.name, len(b), len(in.cps), len(u))
		}
		if len(in.cps) > MaxDecodedLenRunes(len(b)) || len(u) > MaxDecodedLenUTF16(len(b)) {
			t.Errorf("%s: %d bytes decode to %d code points", in.name, len(b), len(in.cps))
		}
		if !hasSurrogate(in.cps) {
			s := string(in.cps)
			if len(b) > MaxEncodedLen(len(s)) || len(s) > MaxDecodedLen(len(b)) {
				t.Errorf("%s: %d bytes for %d UTF-8 bytes", in.name, len(b), len(s))
			}

			// An exact destination is filled without a new array; a short one grows, and keeps what it held.
			exact := make([]byte, 2, 2+len(b))
			got, _ := AppendEncode(exact, s)
			if &got[0] != &exact[0] || !bytes.Equal(got[2:], b) {
				t.Errorf("%s: AppendEncode into an exact destination", in.name)
			}
			short := []byte{0xAA, 0xBB}
			got, _ = AppendEncode(short[:2:2], s)
			if !bytes.Equal(got, append([]byte{0xAA, 0xBB}, b...)) {
				t.Errorf("%s: AppendEncode into a short destination", in.name)
			}
			dexact := make([]byte, 0, len(s))
			if gotS, _ := AppendDecode(dexact, b); len(s) > 0 && &gotS[0] != &dexact[:1][0] {
				t.Errorf("%s: AppendDecode into an exact destination", in.name)
			}
		}
	}
	if got := MaxEncodedLen(len(string(controls))); got != len(mustEncode(t, string(controls))) {
		t.Errorf("controls: MaxEncodedLen is not reached: %d", got)
	}

	for _, f := range []func(int) int{MaxEncodedLen, MaxEncodedLenRunes, MaxEncodedLenUTF16, MaxDecodedLen, MaxDecodedLenRunes, MaxDecodedLenUTF16} {
		for _, n := range []int{-1, int(^uint(0) >> 1)} {
			func() {
				defer func() { _ = recover() }()
				if n == int(^uint(0)>>1) && f(1) == 1 {
					return // n×1 does not overflow
				}
				f(n)
				t.Errorf("no panic for %d", n)
			}()
		}
	}
}

func mustEncode(t *testing.T, s string) []byte {
	b, err := Encode(s)
	if err != nil {
		t.Fatal(err)
	}
	return b
}
