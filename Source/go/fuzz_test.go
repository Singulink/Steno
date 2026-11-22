package steno

import (
	"bytes"
	"slices"
	"testing"
	"unicode/utf8"
)

// The fuzz targets start from the published vectors. Without -fuzz, go test runs each seed once.

func addVectorSeeds(f *testing.F, add func(v vector)) {
	for _, v := range readVectors(f, vectorFile) {
		add(v)
	}
}

// FuzzDecode checks that any byte string is rejected, or decodes to a value whose encoding is that byte string, in every form of the API, and
// that the forms agree.
func FuzzDecode(f *testing.F) {
	addVectorSeeds(f, func(v vector) {
		if v.kind != "not-a-value" {
			f.Add(v.bytes)
		}
	})
	f.Fuzz(func(t *testing.T, b []byte) {
		checkGarbage(t, b)
	})
}

// FuzzRoundTrip checks that text survives encoding and decoding, in one piece and split in two, as UTF-8 and as UTF-16 code units.
func FuzzRoundTrip(f *testing.F) {
	addVectorSeeds(f, func(v vector) {
		if v.kind == "valid" {
			u := toUTF16(v.cps)
			b := make([]byte, 0, 2*len(u))
			for _, x := range u {
				b = append(b, byte(x>>8), byte(x))
			}
			f.Add(b, uint(len(v.cps)/2))
		}
	})
	f.Fuzz(func(t *testing.T, data []byte, cut uint) {
		if utf8.Valid(data) {
			s := string(data)
			b, err := Encode(s)
			if err != nil {
				t.Fatalf("Encode(%q): %v", s, err)
			}
			if bytes.IndexByte(b, 0) >= 0 {
				t.Fatalf("Encode(%q) = %X has a zero byte", s, b)
			}
			if got, err := Decode(b); err != nil || got != s {
				t.Fatalf("Decode(Encode(%q)) = %q, %v", s, got, err)
			}
			k := int(cut % uint(len(data)+1))
			var e Encoder
			sb, err1 := e.AppendString(nil, s[:k])
			sb, err2 := e.Append(sb, data[k:])
			sb, err3 := e.Flush(sb)
			if err1 != nil || err2 != nil || err3 != nil || !bytes.Equal(sb, b) {
				t.Fatalf("%q split at %d: %X, want %X", s, k, sb, b)
			}
			var d Decoder
			j := int(cut % uint(len(b)+1))
			out, err1 := d.Append(nil, b[:j])
			out, err2 = d.Append(out, b[j:])
			if err1 != nil || err2 != nil || d.Flush() != nil || string(out) != s {
				t.Fatalf("%X split at %d: %q", b, j, out)
			}
		} else if _, err := Encode(string(data)); reasonOfEncode(err) != InvalidUTF8 {
			t.Fatalf("Encode(%q): %v, want invalid-utf8", data, err)
		}

		u := make([]uint16, len(data)/2)
		for i := range u {
			u[i] = uint16(data[2*i])<<8 | uint16(data[2*i+1])
		}
		b := EncodeUTF16(u)
		if got, err := DecodeUTF16(b); err != nil || !slices.Equal(got, u) {
			t.Fatalf("DecodeUTF16(EncodeUTF16(%X)) = %X, %v", u, got, err)
		}
		k := int(cut % uint(len(u)+1))
		var e Encoder
		sb, _ := e.AppendUTF16(nil, u[:k])
		sb, _ = e.AppendUTF16(sb, u[k:])
		sb, _ = e.Flush(sb)
		if !bytes.Equal(sb, b) {
			t.Fatalf("%X split at %d: %X, want %X", u, k, sb, b)
		}
		if r, err := DecodeRunes(b); err != nil {
			t.Fatalf("DecodeRunes: %v", err)
		} else if rb, err := EncodeRunes(r); err != nil || !bytes.Equal(rb, b) {
			t.Fatalf("EncodeRunes(%X) = %X, %v; want %X", r, rb, err, b)
		}
	})
}
