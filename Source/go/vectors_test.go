package steno

import (
	"bufio"
	"bytes"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"testing"
)

// vectorFile is the published vector file, by relative path from this folder.
var vectorFile = filepath.Join("..", "..", "TestVectors", "steno-v1-vectors.txt")

type vector struct {
	kind   string // valid, invalid or not-a-value
	name   string
	cps    []rune
	bytes  []byte
	reason string
}

func readVectors(t testing.TB, path string) []vector {
	t.Helper()
	f, err := os.Open(path)
	if err != nil {
		t.Fatalf("open vectors: %v", err)
	}
	defer f.Close()

	var vs []vector
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 1<<20), 1<<26)
	for line := 1; sc.Scan(); line++ {
		text := sc.Text()
		if text == "" || text[0] == '#' {
			continue
		}
		fields := strings.Split(text, "\t")
		v := vector{kind: fields[0], name: fields[1]}
		var err error
		switch {
		case v.kind == "valid" && len(fields) == 4:
			v.cps, err = parseCodePoints(fields[2])
			if err == nil {
				v.bytes, err = parseBytes(fields[3])
			}
		case v.kind == "invalid" && len(fields) == 4:
			v.bytes, err = parseBytes(fields[2])
			v.reason = fields[3]
		case v.kind == "not-a-value" && len(fields) == 3:
			v.cps, err = parseCodePoints(fields[2])
		default:
			err = errors.New("unknown line")
		}
		if err != nil {
			t.Fatalf("%s:%d: %v", path, line, err)
		}
		vs = append(vs, v)
	}
	if err := sc.Err(); err != nil {
		t.Fatal(err)
	}
	return vs
}

func parseCodePoints(s string) ([]rune, error) {
	if s == "-" {
		return []rune{}, nil
	}
	var cps []rune
	for _, f := range strings.Split(s, " ") {
		c, err := strconv.ParseUint(f, 16, 32)
		if err != nil {
			return nil, err
		}
		cps = append(cps, rune(c))
	}
	return cps, nil
}

func parseBytes(s string) ([]byte, error) {
	if s == "-" {
		return []byte{}, nil
	}
	return hex.DecodeString(s)
}

// toUTF16 writes code points as UTF-16 by FORMAT.md §3: a surrogate code point is one code unit of its own.
func toUTF16(cps []rune) []uint16 {
	u := make([]uint16, 0, len(cps))
	for _, c := range cps {
		if c >= 0x10000 {
			c -= 0x10000
			u = append(u, uint16(0xD800+c>>10), uint16(0xDC00+c&0x3FF))
		} else {
			u = append(u, uint16(c))
		}
	}
	return u
}

func hasSurrogate(cps []rune) bool {
	return slices.ContainsFunc(cps, func(c rune) bool { return c >= 0xD800 && c <= 0xDFFF })
}

func reasonOf(err error) string {
	var de *DecodeError
	if errors.As(err, &de) {
		return de.Reason.String()
	}
	if err == nil {
		return "<nil>"
	}
	return "unexpected error " + err.Error()
}

// streamRunes decodes b one byte per call with a Decoder.
func streamRunes(b []byte) ([]rune, error) {
	var d Decoder
	out := []rune{}
	for i := range b {
		var err error
		if out, err = d.AppendRunes(out, b[i:i+1]); err != nil {
			return nil, err
		}
	}
	return out, d.Flush()
}

// checkVectors checks every vector against every form of the API and returns the counts by kind.
func checkVectors(t *testing.T, vs []vector) map[string]int {
	counts := map[string]int{}
	for _, v := range vs {
		counts[v.kind]++
		switch v.kind {
		case "valid":
			checkValid(t, v)
		case "invalid":
			checkInvalid(t, v)
		case "not-a-value":
			if _, err := EncodeRunes(v.cps); err == nil {
				t.Errorf("%s: EncodeRunes accepted a sequence that is not a value", v.name)
			} else if ee := (*EncodeError)(nil); !errors.As(err, &ee) || ee.Reason != NotAValue {
				t.Errorf("%s: EncodeRunes: %v, want not-a-value", v.name, err)
			}
			var e Encoder
			if _, err := e.AppendRunes(nil, v.cps); err == nil {
				t.Errorf("%s: Encoder.AppendRunes accepted a sequence that is not a value", v.name)
			}
		}
	}
	return counts
}

func checkValid(t *testing.T, v vector) {
	t.Helper()
	got, err := EncodeRunes(v.cps)
	if err != nil || !bytes.Equal(got, v.bytes) {
		t.Errorf("%s: EncodeRunes = %X, %v; want %X", v.name, got, err, v.bytes)
	}
	cps, err := DecodeRunes(v.bytes)
	if err != nil || !slices.Equal(cps, v.cps) {
		t.Errorf("%s: DecodeRunes = %X, %v; want %X", v.name, cps, err, v.cps)
	}

	u := toUTF16(v.cps)
	if got := EncodeUTF16(u); !bytes.Equal(got, v.bytes) {
		t.Errorf("%s: EncodeUTF16 = %X; want %X", v.name, got, v.bytes)
	}
	if got, err := DecodeUTF16(v.bytes); err != nil || !slices.Equal(got, u) {
		t.Errorf("%s: DecodeUTF16 = %X, %v; want %X", v.name, got, err, u)
	}

	if got, err := streamRunes(v.bytes); err != nil || !slices.Equal(got, v.cps) {
		t.Errorf("%s: Decoder byte by byte = %X, %v", v.name, got, err)
	}

	if !hasSurrogate(v.cps) {
		s := string(v.cps)
		if got, err := Encode(s); err != nil || !bytes.Equal(got, v.bytes) {
			t.Errorf("%s: Encode = %X, %v; want %X", v.name, got, err, v.bytes)
		}
		if got, err := Decode(v.bytes); err != nil || got != s {
			t.Errorf("%s: Decode = %q, %v", v.name, got, err)
		}
		if IsASCIIText(v.bytes) != isASCIIText(v.cps) {
			t.Errorf("%s: IsASCIIText = %v", v.name, !isASCIIText(v.cps))
		}
	} else if _, err := Decode(v.bytes); reasonOf(err) != "lone-surrogate" {
		t.Errorf("%s: Decode: %v, want lone-surrogate", v.name, err)
	}
}

func isASCIIText(cps []rune) bool {
	for _, c := range cps {
		if c != 9 && c != 10 && c != 13 && (c < 0x20 || c > 0x7E) {
			return false
		}
	}
	return true
}

func checkInvalid(t *testing.T, v vector) {
	t.Helper()
	if _, err := DecodeRunes(v.bytes); reasonOf(err) != v.reason {
		t.Errorf("%s: DecodeRunes: %s, want %s", v.name, reasonOf(err), v.reason)
	}
	if _, err := DecodeUTF16(v.bytes); reasonOf(err) != v.reason {
		t.Errorf("%s: DecodeUTF16: %s, want %s", v.name, reasonOf(err), v.reason)
	}
	if _, err := streamRunes(v.bytes); reasonOf(err) != v.reason {
		t.Errorf("%s: Decoder byte by byte: %s, want %s", v.name, reasonOf(err), v.reason)
	}
	if IsASCIIText(v.bytes) {
		t.Errorf("%s: IsASCIIText accepted invalid bytes", v.name)
	}

	// Decode to a string fails with the same reason, unless the value has a surrogate code point before the bad one.
	_, err := Decode(v.bytes)
	var de *DecodeError
	if !errors.As(err, &de) {
		t.Errorf("%s: Decode: %v", v.name, err)
		return
	}
	if de.Reason == LoneSurrogate {
		if _, err := DecodeRunes(v.bytes[:de.Offset]); err != nil {
			t.Errorf("%s: Decode: lone-surrogate at %d after bad bytes", v.name, de.Offset)
		}
	} else if de.Reason.String() != v.reason {
		t.Errorf("%s: Decode: %s, want %s", v.name, de.Reason, v.reason)
	}
}

func TestVectors(t *testing.T) {
	vs := readVectors(t, vectorFile)
	counts := checkVectors(t, vs)
	t.Logf("published vectors: %d valid, %d invalid, %d not-a-value", counts["valid"], counts["invalid"], counts["not-a-value"])
	if counts["valid"] != 6798 || counts["invalid"] != 829 || counts["not-a-value"] != 13 {
		t.Errorf("unexpected counts %v", counts)
	}
}

// TestLargeVectors runs the large vector file that STENO_LARGE_VECTORS names. It is skipped when the variable is not set.
func TestLargeVectors(t *testing.T) {
	path := os.Getenv("STENO_LARGE_VECTORS")
	if path == "" {
		t.Skip("STENO_LARGE_VECTORS is not set")
	}
	vs := readVectors(t, path)
	counts := checkVectors(t, vs)
	t.Logf("large vectors: %d valid, %d invalid, %d not-a-value", counts["valid"], counts["invalid"], counts["not-a-value"])
}

// TestReferenceVectors checks the plain codec of reference_test.go against the published vectors, so that it can serve as an oracle.
func TestReferenceVectors(t *testing.T) {
	for _, v := range readVectors(t, vectorFile) {
		ints := make([]int, len(v.cps))
		for i, c := range v.cps {
			ints[i] = int(c)
		}
		switch v.kind {
		case "valid":
			if got, err := refEncode(ints); err != nil || !bytes.Equal(got, v.bytes) {
				t.Errorf("%s: refEncode = %X, %v", v.name, got, err)
			}
			if got, reason, _ := refDecode(v.bytes); reason != "" || !slices.Equal(got, ints) {
				t.Errorf("%s: refDecode = %X, %s", v.name, got, reason)
			}
		case "invalid":
			_, reason, offset := refDecode(v.bytes)
			if reason != v.reason {
				t.Errorf("%s: refDecode: %s, want %s", v.name, reason, v.reason)
			}
			// The offset of the package's error is the start of the same code point.
			var de *DecodeError
			if _, err := DecodeRunes(v.bytes); !errors.As(err, &de) || de.Offset != offset {
				t.Errorf("%s: DecodeRunes: %v, want offset %d", v.name, err, offset)
			}
		case "not-a-value":
			if _, err := refEncode(ints); err != errRefNotAValue {
				t.Errorf("%s: refEncode: %v", v.name, err)
			}
		}
	}
}
