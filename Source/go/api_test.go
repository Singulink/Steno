package steno

// Tests of the API contract: destinations and their spare capacity, streaming, the io adapters, concurrency and memory.

import (
	"bufio"
	"bytes"
	"errors"
	"io"
	"math/rand"
	"runtime"
	"slices"
	"strings"
	"sync"
	"testing"
	"testing/iotest"
)

// --- Spare capacity and failed calls ---------------------------------------------------------------------------------------------------

func spareIntact(buf []byte, from int) int {
	for i := from; i < len(buf); i++ {
		if buf[i] != 0xEE {
			return i
		}
	}
	return -1
}

// TestAppendEncodeKeepsSpareCapacity: AppendEncode writes no byte past the length it returns, into capacity that the caller may own.
func TestAppendEncodeKeepsSpareCapacity(t *testing.T) {
	backing := bytes.Repeat([]byte{0xEE}, 32)
	out, err := AppendEncode(backing[:0], "abcééé") // 9 bytes of UTF-8, 6 Steno bytes
	if err != nil || len(out) != 6 {
		t.Fatal(out, err)
	}
	if i := spareIntact(backing, len(out)); i >= 0 {
		t.Errorf("AppendEncode(dst, \"abcééé\") returned %d bytes but changed dst's backing array at index %d: % X", len(out), i, backing[:12])
	}
}

func TestAppendDecodeKeepsSpareCapacity(t *testing.T) {
	backing := bytes.Repeat([]byte{0xEE}, 32)
	out, err := AppendDecode(backing[:0], []byte{0x48, 0x49, 0x4A, 0x01, 0x08, 0x01, 0x08, 0x48}) // "abc\x00\x00a": 8 Steno bytes, 6 UTF-8 bytes
	if err != nil || len(out) != 6 {
		t.Fatal(out, err)
	}
	if i := spareIntact(backing, len(out)); i >= 0 {
		t.Errorf("AppendDecode returned %d bytes but changed dst's backing array at index %d", len(out), i)
	}
}

// TestFailedAppendKeepsLength: a failed append returns dst with its length and its bytes unchanged. It may have written into the spare
// capacity, as any append-style function may; the destination can be used again.
func TestFailedAppendKeepsLength(t *testing.T) {
	backing := bytes.Repeat([]byte{0xEE}, 32)
	dst := backing[:2]
	out, err := AppendEncode(dst, "abcdefghijklmnop\xff")
	if err == nil || len(out) != 2 || &out[0] != &backing[0] || out[0] != 0xEE || out[1] != 0xEE {
		t.Fatalf("failed AppendEncode: % X, %v", out, err)
	}
	backing = bytes.Repeat([]byte{0xEE}, 32)
	if out, err = AppendEncode(backing[:2], "ok"); err != nil || len(out) != 4 || spareIntact(backing, 4) >= 0 {
		t.Fatalf("AppendEncode: % X, %v", out, err)
	}
	var e Encoder
	got, err := e.AppendString(backing[:2], "abcdefghij\xff")
	if err == nil || len(got) != 2 {
		t.Fatalf("failed Encoder.AppendString: % X, %v", got, err)
	}
	backing = bytes.Repeat([]byte{0xEE}, 32)
	if got, err := e.AppendString(backing[:2], "x"); err != nil || len(got) != 3 || spareIntact(backing, 3) >= 0 {
		t.Fatalf("Encoder.AppendString after a failed one: % X, %v", got, err)
	}
	var d Decoder
	if got, err := d.Append(backing[:2], []byte{0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x50, 0x00}); err == nil || len(got) != 2 {
		t.Fatalf("failed Decoder.Append: % X, %v", got, err)
	}
}

// TestAppendSpareCapacityEverywhere: every form keeps the spare capacity of its destination, for runs that end at every offset of a word.
func TestAppendSpareCapacityEverywhere(t *testing.T) {
	for n := 0; n <= 20; n++ {
		for _, tail := range []string{"", "é", "東", "\n", "\x01"} {
			s := strings.Repeat("abcdefghij", 2)[:n] + tail
			want, _ := Encode(s)
			backing := bytes.Repeat([]byte{0xEE}, len(want)+16)
			if got, _ := AppendEncode(backing[:0], s); !bytes.Equal(got, want) || spareIntact(backing, len(got)) >= 0 {
				t.Fatalf("AppendEncode(%q) wrote past its result: % X", s, backing)
			}
			u := toUTF16([]rune(s))
			backing = bytes.Repeat([]byte{0xEE}, len(want)+16)
			if got := AppendEncodeUTF16(backing[:0], u); !bytes.Equal(got, want) || spareIntact(backing, len(got)) >= 0 {
				t.Fatalf("AppendEncodeUTF16(%q) wrote past its result: % X", s, backing)
			}
			backing = bytes.Repeat([]byte{0xEE}, len(s)+16)
			if got, _ := AppendDecode(backing[:0], want); string(got) != s || spareIntact(backing, len(got)) >= 0 {
				t.Fatalf("AppendDecode(%X) wrote past its result: % X", want, backing)
			}
			var e Encoder
			backing = bytes.Repeat([]byte{0xEE}, len(want)+16)
			if got, _ := e.Append(backing[:0], []byte(s)); !bytes.Equal(got, want) || spareIntact(backing, len(got)) >= 0 {
				t.Fatalf("Encoder.Append(%q) wrote past its result: % X", s, backing)
			}
		}
	}
}

// --- Writer ----------------------------------------------------------------------------------------------------------------------------

// TestWriterAfterError: after an invalid-UTF-8 error whose sequence began in an earlier Write, the held bytes are dropped and the Writer
// can be used again.
func TestWriterAfterError(t *testing.T) {
	var buf bytes.Buffer
	w := NewWriter(&buf)
	if _, err := w.Write([]byte("a\xe2")); err != nil { // E2 starts a 3-byte sequence: held
		t.Fatal(err)
	}
	if _, err := w.Write([]byte("(")); err == nil { // E2 28 is invalid
		t.Fatal("no error")
	}
	n, err := w.Write([]byte("valid text"))
	if err != nil || n != 10 {
		t.Fatalf("Write of valid text after an invalid-UTF-8 error: n=%d, %v", n, err)
	}
	// The held E2 was dropped with the error; "(" was not consumed.
	if err := w.Close(); err != nil {
		t.Fatal(err)
	}
	if want, _ := Encode("avalid text"); !bytes.Equal(buf.Bytes(), want) {
		t.Errorf("Writer wrote % X, want % X", buf.Bytes(), want)
	}
}

// TestWriterOffsetAcrossChunks: a bad sequence that starts in one 4,096-byte chunk of a Write and ends in the next is reported at its
// position in p, and n counts exactly the bytes before it.
func TestWriterOffsetAcrossChunks(t *testing.T) {
	p := []byte(strings.Repeat("a", chunk-1) + "\xe2(")
	w := NewWriter(io.Discard)
	n, err := w.Write(p)
	var ee *EncodeError
	if !errors.As(err, &ee) {
		t.Fatal(err)
	}
	if ee.Offset != chunk-1 || n != chunk-1 {
		t.Errorf("Write: n=%d, offset %d; the bad sequence starts at %d", n, ee.Offset, chunk-1)
	}

	// Exactly the n bytes were consumed: writing the rest without the bad byte gives the encoding of the text without it.
	var buf bytes.Buffer
	w = NewWriter(&buf)
	if n, err = w.Write(p); n != chunk-1 || err == nil {
		t.Fatal(n, err)
	}
	if _, err := w.Write(p[n+1:]); err != nil || w.Close() != nil {
		t.Fatal(err)
	}
	if want, _ := Encode(string(p[:n]) + string(p[n+1:])); !bytes.Equal(buf.Bytes(), want) {
		t.Errorf("after the error: the Writer wrote %d bytes, want %d", buf.Len(), len(want))
	}

	// A bad sequence inside a chunk, after valid text: the text before it is written and counted.
	buf.Reset()
	w = NewWriter(&buf)
	q := []byte("Grüße\xffmore")
	if n, err = w.Write(q); n != 7 || !errors.As(err, &ee) || ee.Offset != 7 {
		t.Fatalf("Write(%q): n=%d, %v", q, n, err)
	}
	if want, _ := Encode("Grüße"); !bytes.Equal(buf.Bytes(), want) {
		t.Errorf("Write(%q) wrote % X, want % X", q, buf.Bytes(), want)
	}
}

type shortWriter struct{ w bytes.Buffer }

func (s *shortWriter) Write(p []byte) (int, error) {
	if len(p) > 3 {
		s.w.Write(p[:3])
		return 3, io.ErrShortWrite
	}
	return s.w.Write(p)
}

func TestWriterUnderlyingError(t *testing.T) {
	w := NewWriter(&shortWriter{})
	if _, err := w.Write([]byte("abcdefgh")); err != io.ErrShortWrite {
		t.Fatalf("got %v", err)
	}
	if _, err := w.Write([]byte("x")); err != io.ErrShortWrite {
		t.Fatalf("not sticky: %v", err)
	}
	if err := w.Close(); err != io.ErrShortWrite {
		t.Fatalf("Close: %v", err)
	}
	w.Reset(io.Discard)
	if _, err := w.Write([]byte("ok")); err != nil || w.Close() != nil || w.Close() != nil {
		t.Fatalf("after Reset: %v", err)
	}
}

func TestWriterCopy(t *testing.T) {
	rng := rand.New(rand.NewSource(7))
	for i := 0; i < oracleN(300); i++ {
		cps := oracleText(rng, 1+rng.Intn(30), false)
		text := string(cps)
		want, _ := oEncode(cps)
		var buf bytes.Buffer
		bw := bufio.NewWriterSize(&buf, 16)
		w := NewWriter(bw)
		var src io.Reader = strings.NewReader(text)
		switch i % 3 {
		case 1:
			src = iotest.OneByteReader(src)
		case 2:
			src = iotest.HalfReader(src)
		}
		if _, err := io.Copy(w, src); err != nil {
			t.Fatal(err)
		}
		if err := w.Close(); err != nil {
			t.Fatal(err)
		}
		bw.Flush()
		if !bytes.Equal(buf.Bytes(), want) {
			t.Fatalf("io.Copy through Writer: %X want %X", buf.Bytes(), want)
		}
	}
}

// --- Reader ----------------------------------------------------------------------------------------------------------------------------

type zeroThenReader struct {
	r     io.Reader
	zeros int
}

func (z *zeroThenReader) Read(p []byte) (int, error) {
	if z.zeros > 0 {
		z.zeros--
		return 0, nil
	}
	return z.r.Read(p)
}

func TestReaderConformance(t *testing.T) {
	rng := rand.New(rand.NewSource(11))
	for i := 0; i < oracleN(300); i++ {
		cps := oracleText(rng, 1+rng.Intn(40), false)
		text := string(cps)
		b, _ := oEncode(cps)
		readers := []io.Reader{
			bytes.NewReader(b), iotest.OneByteReader(bytes.NewReader(b)), iotest.HalfReader(bytes.NewReader(b)),
			iotest.DataErrReader(bytes.NewReader(b)), &zeroThenReader{iotest.OneByteReader(bytes.NewReader(b)), 5},
		}
		for j, r := range readers {
			if err := iotest.TestReader(NewReader(r), []byte(text)); err != nil {
				t.Fatalf("reader %d: %v", j, err)
			}
		}
		// Read with an empty p must not consume or block.
		zr := NewReader(bytes.NewReader(b))
		if n, err := zr.Read(nil); n != 0 || (err != nil && err != io.EOF) {
			t.Fatalf("Read(nil): %d %v", n, err)
		}
		if got, err := io.ReadAll(zr); err != nil || string(got) != text {
			t.Fatalf("after Read(nil): %q %v", got, err)
		}
	}
}

// TestReaderErrors: text before the bad code point, then the error, by the oracle, with one byte per read.
func TestReaderErrors(t *testing.T) {
	rng := rand.New(rand.NewSource(12))
	for i := 0; i < oracleN(3000); i++ {
		cps := oracleText(rng, 1+rng.Intn(10), true)
		b, _ := oEncode(cps)
		if len(b) == 0 {
			continue
		}
		b = slices.Clone(b)
		b[rng.Intn(len(b))] = byte(rng.Intn(256))
		if rng.Intn(3) == 0 {
			b = b[:rng.Intn(len(b)+1)]
		}
		got, offs, reason, at := oDecode(b)
		wantText := []rune{}
		wantReason, wantAt := reason, at
		for k, c := range got {
			if c >= 0xD800 && c <= 0xDFFF {
				wantReason, wantAt = "lone-surrogate", offs[k]
				break
			}
			wantText = append(wantText, c)
		}
		var r io.Reader = bytes.NewReader(b)
		if i%2 == 0 {
			r = iotest.OneByteReader(r)
		}
		out, err := io.ReadAll(NewReader(r))
		if wantReason == "" {
			if err != nil || string(out) != string(wantText) {
				t.Fatalf("% X: %q %v", b, out, err)
			}
			continue
		}
		var de *DecodeError
		if !errors.As(err, &de) || de.Reason.String() != wantReason || de.Offset != wantAt || string(out) != string(wantText) {
			t.Fatalf("Reader(% X) = %q, %v; want %q, %s at %d", b, out, err, string(wantText), wantReason, wantAt)
		}
	}
}

// --- Garbage: one-shot and streaming decoders against the oracle ----------------------------------------------------------------------

func checkGarbageAgainstOracle(t testing.TB, b []byte, rng *rand.Rand) {
	cps, offs, reason, at := oDecode(b)
	check := func(name string, err error, wantReason string, wantAt int) {
		if wantReason == "" {
			if err != nil {
				t.Fatalf("%s(% X): %v; want success", name, b, err)
			}
			return
		}
		var de *DecodeError
		if !errors.As(err, &de) || de.Reason.String() != wantReason || de.Offset != wantAt {
			t.Fatalf("%s(% X): %v; want %s at %d", name, b, err, wantReason, wantAt)
		}
	}
	r, err := DecodeRunes(b)
	check("DecodeRunes", err, reason, at)
	if reason == "" {
		if !slices.Equal(r, cps) {
			t.Fatalf("DecodeRunes(% X) = %U want %U", b, r, cps)
		}
		if re, _ := oEncode(cps); !bytes.Equal(re, b) {
			t.Fatalf("oracle: accepted bytes re-encode differently")
		}
	}
	u, err := DecodeUTF16(b)
	check("DecodeUTF16", err, reason, at)
	if reason == "" && !slices.Equal(u, toUTF16(cps)) {
		t.Fatalf("DecodeUTF16 % X", b)
	}
	r8, r8at := reason, at
	for k, c := range cps {
		if c >= 0xD800 && c <= 0xDFFF {
			r8, r8at = "lone-surrogate", offs[k]
			break
		}
	}
	s, err := Decode(b)
	check("Decode", err, r8, r8at)
	if r8 == "" && s != string(cps) {
		t.Fatalf("Decode % X", b)
	}
	if IsASCIIText(b) && (reason != "" || !isASCIIText(cps)) {
		t.Fatalf("IsASCIIText(% X) true", b)
	}

	// Streaming, random splits, one output form per decoder, and a mixed-form decoder.
	for form := 0; form < 4; form++ {
		var d Decoder
		var gotErr error
		var outR []rune
		i := 0
		for i < len(b) && gotErr == nil {
			n := rng.Intn(min(6, len(b)-i) + 1)
			p := b[i : i+n]
			f := form
			if form == 3 {
				f = rng.Intn(3)
				if r8 != "" && f == 0 {
					f = 1 // a mixed decoder with UTF-8 output would hit lone-surrogate; keep it to runes and UTF-16
				}
			}
			switch f {
			case 0:
				var o []byte
				o, gotErr = d.Append(nil, p)
				outR = append(outR, []rune(string(o))...)
			case 1:
				outR, gotErr = d.AppendRunes(outR, p)
			case 2:
				var o []uint16
				o, gotErr = d.AppendUTF16(nil, p)
				outR = append(outR, utf16ToRunes(o)...)
			}
			if rng.Intn(4) == 0 && gotErr == nil {
				_ = d.Flush() // must not change anything
			}
			i += n
		}
		if gotErr == nil {
			gotErr = d.Flush()
		}
		wr, wa := reason, at
		if form == 0 {
			wr, wa = r8, r8at
		}
		check("Decoder form "+string(rune('0'+form)), gotErr, wr, wa)
		if wr == "" && !slices.Equal(outR, cps) {
			t.Fatalf("Decoder form %d (% X) = %U want %U", form, b, outR, cps)
		}
	}
}

func TestOracleGarbage(t *testing.T) {
	rng := rand.New(rand.NewSource(99))
	n := oracleN(3000)
	for i := 0; i < n; i++ {
		cps := oracleText(rng, 1+rng.Intn(8), true)
		b, _ := oEncode(cps)
		// Truncate at every position.
		for k := 0; k <= len(b); k++ {
			checkGarbageAgainstOracle(t, b[:k], rng)
		}
		if len(b) == 0 {
			continue
		}
		// Mutations.
		for m := 0; m < 8; m++ {
			x := slices.Clone(b)
			switch rng.Intn(4) {
			case 0:
				x[rng.Intn(len(x))] = byte(rng.Intn(256))
			case 1:
				p := rng.Intn(len(x) + 1)
				x = slices.Insert(x, p, byte(rng.Intn(256)))
			case 2:
				p := rng.Intn(len(x))
				x = slices.Delete(x, p, p+1)
			case 3:
				x[rng.Intn(len(x))] = []byte{0, 1, 7, 8, 0xFE, 0xFF}[rng.Intn(6)]
			}
			checkGarbageAgainstOracle(t, x, rng)
		}
	}
}

// TestEveryByteEverywhere puts every byte value at every position (and after it a trail byte of every class) in short encodings from
// many states.
func TestEveryByteEverywhere(t *testing.T) {
	rng := rand.New(rand.NewSource(5))
	prefixes := [][]rune{{}, {'a'}, {0x416, 0x416}, {0x1230}, {0x3042}, {0x30A2}, {0x4E00}, {0xD55C}, {0x4E00, 'a'}, {0x10400, 0x10401},
		{0xD800}, {0xDBFF}, {0x1000, 0x1001, 0x1200}, {0x10FF80, 0x10FF81}, {0x20000}, {0x1FF80, 0x1FF81}}
	tails := [][]byte{nil, {0x00}, {0x01}, {0x07}, {0x08}, {0xFF}, {0x08, 0x08}, {0xFF, 0xFF}, {0x08, 0x07}, {0xFF, 0xFF, 0xFF}, {0x80, 0x80, 0x80}}
	for _, p := range prefixes {
		pb, _ := oEncode(p)
		for x := 0; x < 256; x++ {
			for _, tl := range tails {
				b := append(append(slices.Clone(pb), byte(x)), tl...)
				checkGarbageAgainstOracle(t, b, rng)
				// And at every earlier position.
				for k := range pb {
					y := slices.Clone(pb)
					y[k] = byte(x)
					checkGarbageAgainstOracle(t, append(y, tl...), rng)
				}
			}
		}
	}
	// Every 1- and 2-byte string.
	for x := 0; x < 65536; x++ {
		if x < 256 {
			checkGarbageAgainstOracle(t, []byte{byte(x)}, rng)
		}
		checkGarbageAgainstOracle(t, []byte{byte(x >> 8), byte(x)}, rng)
	}
}

// --- Encoder: mixed forms, splits, failed calls ----------------------------------------------------------------------------------------

func TestEncoderMixedForms(t *testing.T) {
	rng := rand.New(rand.NewSource(3))
	n := oracleN(3000)
	for i := 0; i < n; i++ {
		cps := oracleText(rng, 1+rng.Intn(10), true)
		want, _ := oEncode(cps)
		var e Encoder
		if i%5 == 0 {
			e.Reset()
		}
		var out []byte
		// Cut the code points into chunks; each chunk in one form, cut again inside that form.
		k := 0
		for k < len(cps) {
			m := 1 + rng.Intn(min(8, len(cps)-k))
			chunk := cps[k : k+m]
			form := rng.Intn(3)
			if runesHaveSurrogate(chunk) >= 0 && form == 0 {
				form = 2
			}
			var err error
			switch form {
			case 0:
				s := string(chunk)
				for j := 0; j < len(s); {
					q := rng.Intn(len(s)-j) + 1
					if rng.Intn(2) == 0 {
						out, err = e.AppendString(out, s[j:j+q])
					} else {
						out, err = e.Append(out, []byte(s[j:j+q]))
					}
					if err != nil {
						t.Fatalf("UTF-8 piece: %v", err)
					}
					if rng.Intn(3) == 0 {
						out, _ = e.AppendString(out, "") // empty pieces change nothing
					}
					j += q
				}
			case 1:
				if out, err = e.AppendRunes(out, chunk); err != nil {
					t.Fatalf("runes: %v", err)
				}
			case 2:
				u := toUTF16(chunk)
				for j := 0; j < len(u); {
					q := rng.Intn(len(u)-j) + 1
					if out, err = e.AppendUTF16(out, u[j:j+q]); err != nil {
						t.Fatalf("UTF-16 piece: %v", err)
					}
					j += q
				}
			}
			// A failed call in the middle must change nothing.
			if rng.Intn(3) == 0 {
				before := len(out)
				var err error
				switch rng.Intn(3) {
				case 0:
					out, err = e.AppendString(out, "\xff")
				case 1:
					out, err = e.AppendRunes(out, []rune{-5})
				case 2:
					out, err = e.AppendRunes(out, []rune{0x110000})
				}
				if err == nil || len(out) != before {
					t.Fatalf("failed call: %v, %d -> %d", err, before, len(out))
				}
			}
			k += m
		}
		out, err := e.Flush(out)
		if err != nil || !bytes.Equal(out, want) {
			t.Fatalf("mixed Encoder for %s = %X, %v; want %X", describe(cps), out, err, want)
		}
	}
}

// TestEncoderHeldStates checks the documented cross-form rules and the error offsets.
func TestEncoderHeldStates(t *testing.T) {
	var ee *EncodeError
	// A held high surrogate code unit, then a UTF-8 piece: the high is a code point of its own.
	var e Encoder
	out, _ := e.AppendUTF16(nil, []uint16{'a', 0xD83D})
	out, err := e.AppendString(out, "b")
	want, _ := oEncode([]rune{'a', 0xD83D, 'b'})
	if err != nil || !bytes.Equal(out, want) {
		t.Errorf("UTF-16 high then UTF-8: %X %v want %X", out, err, want)
	}
	// A held high, then runes starting with a low: not a value; the encoder must be unchanged, so a UTF-16 low then still pairs.
	e.Reset()
	out, _ = e.AppendUTF16(nil, []uint16{'a', 0xD83D})
	if _, err := e.AppendRunes(out, []rune{0xDE00}); !errors.As(err, &ee) || ee.Reason != NotAValue || ee.Offset != 0 {
		t.Errorf("high then low rune: %v", err)
	}
	out, err = e.AppendUTF16(out, []uint16{0xDE00})
	out, _ = e.Flush(out)
	want, _ = oEncode([]rune{'a', 0x1F600})
	if err != nil || !bytes.Equal(out, want) {
		t.Errorf("after failed AppendRunes: %X %v want %X", out, err, want)
	}
	// A held UTF-8 start, then a UTF-16 piece: InvalidUTF8 at a negative offset; then completing it works.
	e.Reset()
	out, _ = e.AppendString(nil, "x\xf0\x9f")
	if _, err := e.AppendUTF16(out, []uint16{'a'}); !errors.As(err, &ee) || ee.Reason != InvalidUTF8 || ee.Offset != -2 {
		t.Errorf("held UTF-8 then UTF-16: %v", err)
	}
	if _, err := e.Flush(out); !errors.As(err, &ee) || ee.Offset != -2 {
		t.Errorf("Flush with held UTF-8: %v", err)
	}
	out, err = e.Append(out, []byte("\x98\x80"))
	out, _ = e.Flush(out)
	want, _ = oEncode([]rune{'x', 0x1F600})
	if err != nil || !bytes.Equal(out, want) {
		t.Errorf("completed after errors: %X %v", out, err)
	}
	// Zero value: Flush, then every form.
	var z Encoder
	if out, err := z.Flush(nil); err != nil || len(out) != 0 {
		t.Errorf("zero Flush: %X %v", out, err)
	}
	var z2 Encoder
	if out, err := z2.AppendUTF16(nil, []uint16{0xD55C}); err != nil || len(out) == 0 {
		t.Errorf("zero AppendUTF16: %X %v", out, err)
	}
	// Flush of a held high, then a low unit: the docs say NotAValue.
	e.Reset()
	out, _ = e.AppendUTF16(nil, []uint16{0xD83D})
	out, _ = e.Flush(out)
	if _, err := e.AppendUTF16(out, []uint16{0xDE00}); !errors.As(err, &ee) || ee.Reason != NotAValue {
		t.Errorf("flushed high then low unit: %v", err)
	}
	// Held UTF-8 that becomes invalid: offset -held.
	e.Reset()
	_, _ = e.AppendString(nil, "\xe2\x82")
	if _, err := e.AppendString(nil, "("); !errors.As(err, &ee) || ee.Offset != -2 {
		t.Errorf("held then invalid: %v", err)
	}
}

// --- Concurrency and memory ------------------------------------------------------------------------------------------------------------

func TestConcurrentEvictions(t *testing.T) {
	rng := rand.New(rand.NewSource(8))
	type job struct {
		cps  []rune
		want []byte
	}
	var jobs []job
	for i := 0; i < oracleN(1500); i++ {
		// Distinct layouts: random main and second windows.
		w1 := oracleWindows[rng.Intn(len(oracleWindows))]
		w2 := oracleWindows[rng.Intn(len(oracleWindows))]
		cps := []rune{rune(w2), rune(w2 + 1), rune(w1), rune(w1 + 2), 'a', rune(w2 + 3), rune(w1 + 4), rune(w1 + 5)}
		cps = append(cps, oracleText(rng, 3, true)...)
		want, _ := oEncode(cps)
		jobs = append(jobs, job{cps, want})
	}
	var wg sync.WaitGroup
	errs := make(chan string, 8)
	for g := 0; g < 8; g++ {
		wg.Add(1)
		go func(g int) {
			defer wg.Done()
			for rep := 0; rep < 20; rep++ {
				for i := g; i < len(jobs); i += 3 {
					j := jobs[(i+rep*7)%len(jobs)]
					if got, err := EncodeRunes(j.cps); err != nil || !bytes.Equal(got, j.want) {
						errs <- "encode mismatch"
						return
					}
					if got, err := DecodeRunes(j.want); err != nil || !slices.Equal(got, j.cps) {
						errs <- "decode mismatch"
						return
					}
				}
			}
		}(g)
	}
	wg.Wait()
	close(errs)
	for e := range errs {
		t.Fatal(e)
	}
}

func TestCacheMemory(t *testing.T) {
	var ms runtime.MemStats
	runtime.GC()
	runtime.ReadMemStats(&ms)
	base := ms.HeapAlloc
	n := 0
	for _, w1 := range oracleWindows {
		for k := 0; k < oracleN(30); k++ {
			w2 := oracleWindows[(k*977+w1)%len(oracleWindows)]
			_, _ = EncodeRunes([]rune{rune(w2), rune(w1), rune(w1 + 1), 0x3042, rune(w2), rune(w2 + 1), 0xD55C, rune(w1), rune(w1 + 1)})
			n++
		}
	}
	runtime.GC()
	runtime.ReadMemStats(&ms)
	grow := int64(ms.HeapAlloc) - int64(base)
	t.Logf("%d values over many layouts: heap grew by %d KB", n, grow/1024)
	if grow > 8<<20 {
		t.Errorf("heap grew by %d bytes", grow)
	}
}

// TestWriterChunkBoundary: one Write whose 4,096-byte chunks split UTF-8 sequences of every length at every position.
func TestWriterChunkBoundary(t *testing.T) {
	for _, ch := range []string{"é", "中", "😀"} {
		for pad := chunk - 4; pad <= chunk+1; pad++ {
			text := strings.Repeat("a", pad) + strings.Repeat(ch, 3) + "z"
			want, _ := Encode(text)
			var buf bytes.Buffer
			w := NewWriter(&buf)
			if n, err := w.Write([]byte(text)); err != nil || n != len(text) {
				t.Fatalf("Write: %d %v", n, err)
			}
			if err := w.Close(); err != nil || !bytes.Equal(buf.Bytes(), want) {
				t.Fatalf("pad %d %q: %v", pad, ch, err)
			}
		}
	}
}

// TestReaderNoProgress: a Reader gives up with io.ErrNoProgress when the underlying reader keeps returning no bytes and no error.
func TestReaderNoProgress(t *testing.T) {
	r := NewReader(&zeroThenReader{r: bytes.NewReader([]byte{0x2F}), zeros: 1 << 30})
	if n, err := r.Read(make([]byte, 8)); n != 0 || err != io.ErrNoProgress {
		t.Fatalf("Read: %d, %v; want io.ErrNoProgress", n, err)
	}
	// An empty p returns at once, without reading.
	z := &zeroThenReader{r: bytes.NewReader(nil), zeros: 1}
	if n, err := NewReader(z).Read(nil); n != 0 || err != nil || z.zeros != 1 {
		t.Fatalf("Read(nil): %d, %v; the underlying reader was read", n, err)
	}
}
