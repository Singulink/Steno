package steno

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"unicode/utf16"
	"unicode/utf8"
)

// BenchmarkWorkload measures the shared workloads of Benchmarks/Workloads in the repository (read by relative path; skipped if absent), by
// the common rules of the reference implementations. The names are
//
//	BenchmarkWorkload/<text>/<kind>/<operation>/<form>/<codec>
//
// with kind document or values, operation encode or decode, form native (Go string and UTF-8 bytes) or utf16 ([]uint16), and codec steno or
// utf8. For the values kind, one operation processes every value of the text once. Every benchmark writes into a buffer that is allocated
// once, so that it measures the codec and not the allocator.
//
// The utf8 codec is the UTF-8 baseline of the standard library between []uint16 and UTF-8 bytes: utf16.DecodeRune and utf8.AppendRune to
// encode, a range over the UTF-8 bytes and utf16.AppendRune to decode. There is no baseline for the native form: a Go string is already UTF-8.
//
// Each benchmark also reports the number of values and UTF-16 code units of one operation as the metrics "values" and "u16units", and each
// encode benchmark the total encoded size as "bytes", which bench/main.go reads. The compressors are measured in the separate module in
// compare/, so that this module keeps no dependencies.
//
// Run the full set with "go run ./bench" from this folder; see bench/main.go.
func BenchmarkWorkload(b *testing.B) {
	dir := filepath.Join("..", "..", "Benchmarks", "Workloads")
	for _, text := range workloadTexts {
		for _, kind := range []string{"document", "values"} {
			data, err := os.ReadFile(filepath.Join(dir, text+"."+kind+".txt"))
			if err != nil {
				b.Skipf("workload %s.%s: %v", text, kind, err)
			}
			w := newWorkload(string(data), kind)
			b.Run(text+"/"+kind, w.run)
		}
	}
}

var workloadTexts = []string{"english", "french", "polish", "vietnamese", "russian", "greek", "arabic", "urdu", "hindi", "thai", "amharic",
	"chinese", "japanese", "korean"}

// workload holds the values of one text in every form, prepared before the measurement.
type workload struct {
	strs  []string
	u16s  [][]uint16
	steno [][]byte
	utf8s [][]byte
	units int

	// The total encoded sizes, reported by the encode benchmarks as the metric "bytes".
	stenoSize, utf8Size int

	// Buffers that are allocated once, large enough for any value of the workload.
	byteBuf []byte
	u16Buf  []uint16
}

// newWorkload cuts the file content into values: a document is the content without its final LF, and a values file has one value per line.
func newWorkload(content, kind string) *workload {
	content = strings.TrimSuffix(content, "\n")
	strs := []string{content}
	if kind == "values" {
		strs = strings.Split(content, "\n")
	}
	w := &workload{strs: strs}
	longest := 0
	for _, s := range strs {
		u := utf16.Encode([]rune(s))
		e, err := Encode(s)
		if err != nil {
			panic(err)
		}
		w.u16s = append(w.u16s, u)
		w.steno = append(w.steno, e)
		w.utf8s = append(w.utf8s, []byte(s))
		w.units += len(u)
		w.stenoSize += len(e)
		w.utf8Size += len(s)
		longest = max(longest, len(s))
	}
	w.byteBuf = make([]byte, 0, MaxEncodedLen(longest)+16)
	w.u16Buf = make([]uint16, 0, 2*longest+16)
	return w
}

// Sinks that keep the compiler from dropping the work.
var (
	sinkBytes []byte
	sinkU16   []uint16
)

func (w *workload) report(b *testing.B) {
	b.ReportMetric(float64(len(w.strs)), "values")
	b.ReportMetric(float64(w.units), "u16units")
}

func (w *workload) run(b *testing.B) {
	b.Run("encode/native/steno", func(b *testing.B) {
		buf := w.byteBuf
		for i := 0; i < b.N; i++ {
			for _, s := range w.strs {
				buf, _ = AppendEncode(buf[:0], s)
			}
		}
		sinkBytes = buf
		w.report(b)
		b.ReportMetric(float64(w.stenoSize), "bytes")
	})
	b.Run("decode/native/steno", func(b *testing.B) {
		buf := w.byteBuf
		for i := 0; i < b.N; i++ {
			for _, e := range w.steno {
				buf, _ = AppendDecode(buf[:0], e)
			}
		}
		sinkBytes = buf
		w.report(b)
	})
	b.Run("encode/utf16/steno", func(b *testing.B) {
		buf := w.byteBuf
		for i := 0; i < b.N; i++ {
			for _, u := range w.u16s {
				buf = AppendEncodeUTF16(buf[:0], u)
			}
		}
		sinkBytes = buf
		w.report(b)
		b.ReportMetric(float64(w.stenoSize), "bytes")
	})
	b.Run("decode/utf16/steno", func(b *testing.B) {
		buf := w.u16Buf
		for i := 0; i < b.N; i++ {
			for _, e := range w.steno {
				buf, _ = AppendDecodeUTF16(buf[:0], e)
			}
		}
		sinkU16 = buf
		w.report(b)
	})
	b.Run("encode/utf16/utf8", func(b *testing.B) {
		buf := w.byteBuf
		for i := 0; i < b.N; i++ {
			for _, u := range w.u16s {
				buf = utf16ToUTF8(buf[:0], u)
			}
		}
		sinkBytes = buf
		w.report(b)
		b.ReportMetric(float64(w.utf8Size), "bytes")
	})
	b.Run("decode/utf16/utf8", func(b *testing.B) {
		buf := w.u16Buf
		for i := 0; i < b.N; i++ {
			for _, p := range w.utf8s {
				buf = utf8ToUTF16(buf[:0], p)
			}
		}
		sinkU16 = buf
		w.report(b)
	})
}

// utf16ToUTF8 is the standard library's conversion of UTF-16 code units to UTF-8, into dst.
func utf16ToUTF8(dst []byte, u []uint16) []byte {
	for i := 0; i < len(u); i++ {
		r := rune(u[i])
		if utf16.IsSurrogate(r) && i+1 < len(u) {
			if c := utf16.DecodeRune(r, rune(u[i+1])); c != utf8.RuneError {
				r = c
				i++
			}
		}
		dst = utf8.AppendRune(dst, r)
	}
	return dst
}

// utf8ToUTF16 is the standard library's conversion of UTF-8 to UTF-16 code units, into dst. A range over string(p) does not copy p.
func utf8ToUTF16(dst []uint16, p []byte) []uint16 {
	for _, r := range string(p) {
		dst = utf16.AppendRune(dst, r)
	}
	return dst
}
