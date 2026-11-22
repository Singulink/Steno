// Package compare measures the general-purpose compressors that a Go developer would use, on the shared workloads of the repository, next to
// the benchmarks of the Go reference implementation of the Steno Encoding. It is a separate module, so that the steno module keeps no
// dependencies. Run it through "go run ./bench" in the parent folder.
package compare

import (
	"bytes"
	"compress/flate"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"unicode/utf16"

	"github.com/andybalholm/brotli"
	"github.com/klauspost/compress/zstd"
	"github.com/pierrec/lz4/v4"
)

// Texts is the representative subset of the workloads that the compressors are measured on.
var texts = []string{"english", "french", "russian", "arabic", "hindi", "thai", "amharic", "chinese", "japanese", "korean"}

// codecs are the compressors with one fast and one strong setting each, by their names in the results.
var codecs = []string{"deflate-fast", "deflate-max", "brotli-1", "brotli-11", "zstd-1", "zstd-19", "lz4", "lz4-hc"}

type workload struct {
	values [][]byte // the UTF-8 bytes of each value
	units  int      // UTF-16 code units of all values
}

func load(tb testing.TB, text, kind string) *workload {
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "Benchmarks", "Workloads", text+"."+kind+".txt"))
	if err != nil {
		tb.Skipf("workload %s.%s: %v", text, kind, err)
	}
	content := strings.TrimSuffix(string(data), "\n")
	strs := []string{content}
	if kind == "values" {
		strs = strings.Split(content, "\n")
	}
	w := &workload{}
	for _, s := range strs {
		w.values = append(w.values, []byte(s))
		w.units += len(utf16.Encode([]rune(s)))
	}
	return w
}

// Sinks that keep the compiler from dropping the work.
var (
	sinkBytes []byte
	sinkInt   int
)

// compressor is one codec, set up once and reused for every value, as the documentation of each library says to do for many small inputs.
type compressor struct {
	name  string
	fw    *flate.Writer
	fr    readResetter
	bw    *brotli.Writer
	brd   *brotli.Reader
	zenc  *zstd.Encoder
	zdec  *zstd.Decoder
	lz    *lz4.Compressor
	lzhc  *lz4.CompressorHC
	out   bytes.Buffer
	src   bytes.Reader
	block []byte
}

// readResetter is the reader of compress/flate with its Reset method.
type readResetter interface {
	Read([]byte) (int, error)
	flate.Resetter
}

func newCompressor(name string, maxLen int) *compressor {
	c := &compressor{name: name, block: make([]byte, lz4.CompressBlockBound(maxLen))}
	switch name {
	case "deflate-fast", "deflate-max":
		level := flate.BestSpeed
		if name == "deflate-max" {
			level = flate.BestCompression
		}
		c.fw, _ = flate.NewWriter(nil, level)
		c.fr = flate.NewReader(bytes.NewReader(nil)).(readResetter)
	case "brotli-1", "brotli-11":
		level := 1
		if name == "brotli-11" {
			level = 11
		}
		c.bw = brotli.NewWriterLevel(nil, level)
		c.brd = brotli.NewReader(nil)
	case "zstd-1", "zstd-19":
		level := 1
		if name == "zstd-19" {
			level = 19
		}
		c.zenc, _ = zstd.NewWriter(nil, zstd.WithEncoderLevel(zstd.EncoderLevelFromZstd(level)), zstd.WithEncoderConcurrency(1))
		c.zdec, _ = zstd.NewReader(nil, zstd.WithDecoderConcurrency(1))
	case "lz4":
		c.lz = &lz4.Compressor{}
	case "lz4-hc":
		c.lzhc = &lz4.CompressorHC{Level: lz4.Level9}
	default:
		panic("unknown codec " + name)
	}
	return c
}

// compress appends the compressed form of v to dst. An LZ4 block that does not get smaller is stored as it is, with a zero byte in front as
// the flag that a store would keep (a compressed block gets a one byte).
func (c *compressor) compress(dst, v []byte) []byte {
	switch {
	case c.fw != nil:
		c.out.Reset()
		c.fw.Reset(&c.out)
		c.fw.Write(v)
		c.fw.Close()
		return append(dst, c.out.Bytes()...)
	case c.bw != nil:
		c.out.Reset()
		c.bw.Reset(&c.out)
		c.bw.Write(v)
		c.bw.Close()
		return append(dst, c.out.Bytes()...)
	case c.zenc != nil:
		return c.zenc.EncodeAll(v, dst)
	default:
		var n int
		if c.lz != nil {
			n, _ = c.lz.CompressBlock(v, c.block)
		} else {
			n, _ = c.lzhc.CompressBlock(v, c.block)
		}
		if n == 0 || n >= len(v) {
			return append(append(dst, 0), v...)
		}
		return append(append(dst, 1), c.block[:n]...)
	}
}

// decompress appends the value of the compressed form z to dst. n is the length of the value, which a store keeps (LZ4 blocks need it).
func (c *compressor) decompress(dst, z []byte, n int) []byte {
	switch {
	case c.fr != nil:
		c.src.Reset(z)
		c.fr.Reset(&c.src, nil)
		c.out.Reset()
		c.out.ReadFrom(c.fr)
		return append(dst, c.out.Bytes()...)
	case c.brd != nil:
		c.src.Reset(z)
		c.brd.Reset(&c.src)
		c.out.Reset()
		c.out.ReadFrom(c.brd)
		return append(dst, c.out.Bytes()...)
	case c.zdec != nil:
		out, _ := c.zdec.DecodeAll(z, dst)
		return out
	default:
		if z[0] == 0 {
			return append(dst, z[1:]...)
		}
		m := len(dst)
		dst = append(dst, make([]byte, n)...)
		k, _ := lz4.UncompressBlock(z[1:], dst[m:])
		return dst[:m+k]
	}
}

func maxValueLen(w *workload) int {
	m := 0
	for _, v := range w.values {
		m = max(m, len(v))
	}
	return m
}

// BenchmarkCompare measures each compressor on the UTF-8 bytes of each value by itself. The names follow BenchmarkWorkload of the parent
// module: BenchmarkCompare/<text>/<kind>/<operation>/native/<codec>. The encode benchmarks report the total compressed size as "bytes".
func BenchmarkCompare(b *testing.B) {
	for _, text := range texts {
		for _, kind := range []string{"document", "values"} {
			w := load(b, text, kind)
			for _, name := range codecs {
				c := newCompressor(name, maxValueLen(w))
				var packed [][]byte
				size := 0
				for _, v := range w.values {
					z := c.compress(nil, v)
					packed = append(packed, z)
					size += len(z)
				}
				buf := make([]byte, 0, 2*maxValueLen(w)+64)
				b.Run(text+"/"+kind+"/encode/native/"+name, func(b *testing.B) {
					out := buf
					for i := 0; i < b.N; i++ {
						for _, v := range w.values {
							out = c.compress(out[:0], v)
						}
					}
					sinkBytes = out
					report(b, w)
					b.ReportMetric(float64(size), "bytes")
				})
				b.Run(text+"/"+kind+"/decode/native/"+name, func(b *testing.B) {
					out := buf
					for i := 0; i < b.N; i++ {
						for j, z := range packed {
							out = c.decompress(out[:0], z, len(w.values[j]))
						}
					}
					sinkBytes = out
					report(b, w)
				})
			}
		}
	}
}

func report(b *testing.B, w *workload) {
	b.ReportMetric(float64(len(w.values)), "values")
	b.ReportMetric(float64(w.units), "u16units")
}

// TestRoundTrip checks that every compressor gives back every value, so that the benchmarks measure working code.
func TestRoundTrip(t *testing.T) {
	for _, text := range []string{"english", "chinese"} {
		for _, kind := range []string{"document", "values"} {
			w := load(t, text, kind)
			for _, name := range codecs {
				c := newCompressor(name, maxValueLen(w))
				for _, v := range w.values {
					z := c.compress(nil, v)
					if got := c.decompress(nil, z, len(v)); !bytes.Equal(got, v) {
						t.Fatalf("%s on %s.%s: round trip failed", name, text, kind)
					}
				}
			}
		}
	}
	sinkInt = len(codecs)
}
