// Command bench runs the workload benchmarks of the Go reference implementation of the Steno Encoding and writes bench/results.csv and
// BENCHMARKS.md from their output.
//
// Run it from the module folder (Source/go):
//
//	go run ./bench
//
// It runs "go test -run ^$ -bench ^BenchmarkWorkload$" in this module (the Steno Encoding and the standard library's UTF-8, on all 14 texts)
// with -cpu 1, -count 5 and -benchtime 500ms, and then "go test -run ^$ -bench ^BenchmarkCompare$" in the module in compare/ (the compressors,
// on 10 of the texts) with -cpu 1, -count 5 and -benchtime 300ms. It saves the raw output of both in bench/raw.txt, and then writes
// bench/results.csv, bench/sizes.csv, bench/libraries.csv, BENCHMARKS.md and the summary block of README.md. Each run replaces these files, so
// it is safe to run again. With -parse it only reads an earlier raw output and writes the other files. For a smoke run, use a short
// -benchtime and -compare-benchtime, -count 1 and -compare-count 1, a -texts filter, an -out folder outside the module, and -readme=false.
package main

import (
	"bufio"
	"bytes"
	"flag"
	"fmt"
	"io"
	"math"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"time"
)

var texts = []string{"english", "french", "polish", "vietnamese", "russian", "greek", "arabic", "urdu", "hindi", "thai", "amharic",
	"chinese", "japanese", "korean"}

func main() {
	count := flag.Int("count", 5, "number of runs of each benchmark")
	benchtime := flag.String("benchtime", "500ms", "time of each run of each benchmark")
	filter := flag.String("texts", "", "regular expression of the texts to run (all if empty)")
	out := flag.String("out", ".", "folder for bench/raw.txt, bench/results.csv and BENCHMARKS.md")
	parse := flag.String("parse", "", "read this raw output instead of running the benchmarks")
	readme := flag.Bool("readme", true, "update the summary block of README.md")
	compare := flag.Bool("compare", true, "also run the compressors of the module in compare/")
	compareCount := flag.Int("compare-count", 5, "number of runs of each compressor benchmark")
	compareTime := flag.String("compare-benchtime", "300ms", "time of each run of each compressor benchmark")
	flag.Parse()

	cmp := compareSettings{run: *compare, count: *compareCount, benchtime: *compareTime}
	if err := run(*count, *benchtime, *filter, *out, *parse, *readme, cmp); err != nil {
		fmt.Fprintln(os.Stderr, "bench:", err)
		os.Exit(1)
	}
}

type compareSettings struct {
	run       bool
	count     int
	benchtime string
}

// goTest runs go test with the given benchmark pattern in dir, shows its output and appends it to raw.
func goTest(dir, pattern, benchtime string, count int, raw *bytes.Buffer) error {
	args := []string{"test", "-run", "^$", "-bench", pattern, "-benchtime", benchtime, "-count", strconv.Itoa(count), "-cpu", "1",
		"-timeout", "120m", "."}
	fmt.Fprintln(os.Stderr, "in", dir+":", "go", strings.Join(args, " "))
	cmd := exec.Command("go", args...)
	cmd.Dir = dir
	cmd.Stdout = io.MultiWriter(raw, os.Stdout)
	cmd.Stderr = os.Stderr
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("go test in %s: %w", dir, err)
	}
	return nil
}

func run(count int, benchtime, filter, out, parse string, readme bool, cmp compareSettings) error {
	if err := os.MkdirAll(filepath.Join(out, "bench"), 0o755); err != nil {
		return err
	}
	rawPath := filepath.Join(out, "bench", "raw.txt")

	var raw []byte
	if parse != "" {
		var err error
		if raw, err = os.ReadFile(parse); err != nil {
			return err
		}
	} else {
		suffix := ""
		if filter != "" {
			suffix = "/(" + filter + ")/"
		}
		var buf bytes.Buffer
		err := goTest(".", "^BenchmarkWorkload$"+suffix, benchtime, count, &buf)
		if err == nil && cmp.run {
			err = goTest("compare", "^BenchmarkCompare$"+suffix, cmp.benchtime, cmp.count, &buf)
		}
		raw = buf.Bytes()
		// Keep the raw output also when the run failed, for -parse.
		if werr := os.WriteFile(rawPath, raw, 0o644); werr != nil {
			return werr
		}
		if err != nil {
			return err
		}
	}

	res, info, err := parseOutput(raw)
	if err != nil {
		return err
	}
	if len(res) == 0 {
		return fmt.Errorf("no benchmark results in the output")
	}
	info.count, info.benchtime = count, benchtime
	info.compareCount, info.compareBenchtime = cmp.count, cmp.benchtime
	if parse != "" {
		info.count, info.benchtime = res[0].runs, "see the raw output"
		info.compareCount, info.compareBenchtime = 0, "see the raw output"
	}
	info.libs = libraries(info.goVersion)

	if err := writeFile(filepath.Join(out, "bench", "results.csv"), csvText(res)); err != nil {
		return err
	}
	if err := writeFile(filepath.Join(out, "bench", "sizes.csv"), sizesText(res)); err != nil {
		return err
	}
	if err := writeFile(filepath.Join(out, "bench", "libraries.csv"), librariesText(info.libs)); err != nil {
		return err
	}
	if err := writeFile(filepath.Join(out, "BENCHMARKS.md"), markdown(res, info)); err != nil {
		return err
	}
	if readme {
		return updateReadme(filepath.Join(out, "README.md"), summary(res, info))
	}
	return nil
}

func writeFile(path, text string) error {
	fmt.Fprintln(os.Stderr, "writing", path)
	return os.WriteFile(path, []byte(text), 0o644)
}

// result is one benchmark over all its runs.
type result struct {
	text, kind, operation, form, codec string
	ns, errNs                          float64 // median and sample standard deviation of ns per operation
	values, units                      int
	size                               int // total encoded bytes, for encode benchmarks; -1 if not reported
	runs                               int
}

type runInfo struct {
	goVersion, goos, goarch, cpu string
	date                         string
	count                        int
	benchtime                    string
	compareCount                 int
	compareBenchtime             string
	libs                         []library
}

// lineRE matches a result line of either benchmark. The runs use -cpu 1, so the names have no -N suffix (codec names such as zstd-19 end in
// digits of their own).
var lineRE = regexp.MustCompile(`^Benchmark(?:Workload|Compare)/(\w+)/(document|values)/(encode|decode)/(native|utf16)/([\w-]+)\s+\d+\s+(.*)$`)

func parseOutput(raw []byte) ([]result, runInfo, error) {
	info := runInfo{goVersion: runtime.Version(), date: time.Now().Format("2006-01-02")}
	type key struct{ text, kind, operation, form, codec string }
	samples := map[key][]float64{}
	meta := map[key][3]int{}
	var order []key

	sc := bufio.NewScanner(bytes.NewReader(raw))
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		switch {
		case strings.HasPrefix(line, "goos:"):
			info.goos = strings.TrimSpace(strings.TrimPrefix(line, "goos:"))
		case strings.HasPrefix(line, "goarch:"):
			info.goarch = strings.TrimSpace(strings.TrimPrefix(line, "goarch:"))
		case strings.HasPrefix(line, "cpu:"):
			info.cpu = strings.TrimSpace(strings.TrimPrefix(line, "cpu:"))
		}
		m := lineRE.FindStringSubmatch(line)
		if m == nil {
			continue
		}
		k := key{m[1], m[2], m[3], m[4], m[5]}
		// The metrics come in pairs: value, unit.
		f := strings.Fields(m[6])
		var ns float64 = -1
		var values, units int
		size := -1
		for i := 0; i+1 < len(f); i += 2 {
			v, err := strconv.ParseFloat(f[i], 64)
			if err != nil {
				return nil, info, fmt.Errorf("bad metric in %q", line)
			}
			switch f[i+1] {
			case "ns/op":
				ns = v
			case "values":
				values = int(v)
			case "u16units":
				units = int(v)
			case "bytes":
				size = int(v)
			}
		}
		if ns < 0 {
			return nil, info, fmt.Errorf("no ns/op in %q", line)
		}
		if _, ok := samples[k]; !ok {
			order = append(order, k)
		}
		samples[k] = append(samples[k], ns)
		meta[k] = [3]int{values, units, size}
	}

	var res []result
	for _, k := range order {
		s := samples[k]
		res = append(res, result{text: k.text, kind: k.kind, operation: k.operation, form: k.form, codec: k.codec,
			ns: median(s), errNs: stddev(s), values: meta[k][0], units: meta[k][1], size: meta[k][2], runs: len(s)})
	}
	return res, info, sc.Err()
}

func median(s []float64) float64 {
	t := append([]float64(nil), s...)
	sort.Float64s(t)
	n := len(t)
	if n%2 == 1 {
		return t[n/2]
	}
	return (t[n/2-1] + t[n/2]) / 2
}

func stddev(s []float64) float64 {
	if len(s) < 2 {
		return 0
	}
	var mean float64
	for _, x := range s {
		mean += x
	}
	mean /= float64(len(s))
	var sum float64
	for _, x := range s {
		sum += (x - mean) * (x - mean)
	}
	return math.Sqrt(sum / float64(len(s)-1))
}

func csvText(res []result) string {
	var b strings.Builder
	b.WriteString("implementation,text,kind,operation,form,codec,ns_per_operation,error_ns,values,utf16_units\n")
	for _, r := range res {
		fmt.Fprintf(&b, "go,%s,%s,%s,%s,%s,%.1f,%.1f,%d,%d\n", r.text, r.kind, r.operation, r.form, r.codec, r.ns, r.errNs, r.values, r.units)
	}
	return b.String()
}

// library names the library and version behind a codec.
type library struct{ codec, name, version, use string }

// libraries lists the library of each codec. The versions of the packages come from compare/go.mod.
func libraries(goVersion string) []library {
	mod := map[string]string{}
	if data, err := os.ReadFile(filepath.Join("compare", "go.mod")); err == nil {
		for _, line := range strings.Split(string(data), "\n") {
			if f := strings.Fields(line); len(f) >= 2 && strings.HasPrefix(f[1], "v") && strings.Contains(f[0], ".") {
				mod[f[0]] = f[1]
			}
		}
	}
	v := func(path string) string {
		if s, ok := mod[path]; ok {
			return s
		}
		return "unknown"
	}
	const (
		flateUse  = "`flate.NewWriter` once; per value `Reset` to a reused `bytes.Buffer`, `Write`, `Close`. Decode: one `flate.NewReader`, per value `Reset` and `ReadFrom` into a reused buffer."
		brotliUse = "`brotli.NewWriterLevel` once; per value `Reset` to a reused `bytes.Buffer`, `Write`, `Close`. Decode: one `brotli.NewReader`, per value `Reset` and `ReadFrom` into a reused buffer."
		zstdUse   = "One `zstd.Encoder` (`WithEncoderLevel(EncoderLevelFromZstd(n))`, concurrency 1) and one `zstd.Decoder` (concurrency 1), with `EncodeAll` and `DecodeAll` into reused buffers, as the package documents for many small inputs. Defaults otherwise: frames with checksums."
		lz4Use    = "The block API (`lz4.Compressor` or `lz4.CompressorHC` with `Level9`, reused; `UncompressBlock` with the stored length). A value that does not get smaller is stored as it is; one flag byte per value tells which."
	)
	return []library{
		{"steno", "github.com/Singulink/Steno/Source/go", "this folder", "`AppendEncode` and `AppendDecode` into reused buffers."},
		{"utf8", "unicode/utf8 and unicode/utf16", goVersion, "Per rune into reused buffers (see above)."},
		{"deflate-fast", "compress/flate, BestSpeed", goVersion, flateUse},
		{"deflate-max", "compress/flate, BestCompression", goVersion, flateUse},
		{"brotli-1", "github.com/andybalholm/brotli, level 1", v("github.com/andybalholm/brotli"), brotliUse},
		{"brotli-11", "github.com/andybalholm/brotli, level 11", v("github.com/andybalholm/brotli"), brotliUse},
		{"zstd-1", "github.com/klauspost/compress/zstd, level 1 (SpeedFastest)", v("github.com/klauspost/compress"), zstdUse},
		{"zstd-19", "github.com/klauspost/compress/zstd, level 19 (SpeedBestCompression)", v("github.com/klauspost/compress"), zstdUse},
		{"lz4", "github.com/pierrec/lz4/v4, block, fast", v("github.com/pierrec/lz4/v4"), lz4Use},
		{"lz4-hc", "github.com/pierrec/lz4/v4, block, HC level 9", v("github.com/pierrec/lz4/v4"), lz4Use},
	}
}

func librariesText(libs []library) string {
	var b strings.Builder
	b.WriteString("codec,library,version\n")
	for _, l := range libs {
		fmt.Fprintf(&b, "%s,\"%s\",%s\n", l.codec, l.name, l.version)
	}
	return b.String()
}

// sizesText lists the total encoded size of each codec on each workload: Steno and the compressors from their native encode benchmarks,
// UTF-8 from the baseline.
func sizesText(res []result) string {
	var b strings.Builder
	b.WriteString("implementation,text,kind,codec,bytes\n")
	for _, r := range res {
		if r.operation != "encode" || r.size < 0 || (r.codec == "steno" && r.form != "native") {
			continue
		}
		fmt.Fprintf(&b, "go,%s,%s,%s,%d\n", r.text, r.kind, r.codec, r.size)
	}
	return b.String()
}

// encodeResult returns the encode result that has the size of a codec.
func encodeResult(res []result, text, kind, codec string) *result {
	if codec == "utf8" {
		return find(res, text, kind, "encode", "utf16", codec)
	}
	return find(res, text, kind, "encode", "native", codec)
}

// perChar formats bytes per UTF-16 code unit.
func perChar(r *result) string {
	if r == nil || r.size < 0 || r.units == 0 {
		return "-"
	}
	return fmt.Sprintf("%.2f", float64(r.size)/float64(r.units))
}

// compressorTable writes one table of a group of codecs next to Steno: size, encode and decode.
func compressorTable(b *strings.Builder, res []result, kind string, codecs []string, cell func(*result) string) {
	all := append([]string{"steno"}, codecs...)
	b.WriteString("| Text |")
	for _, h := range []string{"bytes/char", "encode", "decode"} {
		for _, c := range all {
			fmt.Fprintf(b, " %s %s |", c, h)
		}
	}
	b.WriteString("\n|---|" + strings.Repeat("--:|", 3*len(all)) + "\n")
	for _, t := range presentTexts(res) {
		if find(res, t, kind, "encode", "native", codecs[0]) == nil {
			continue
		}
		fmt.Fprintf(b, "| %s |", strings.ToUpper(t[:1])+t[1:])
		for _, c := range all {
			fmt.Fprintf(b, " %s |", perChar(encodeResult(res, t, kind, c)))
		}
		for _, op := range []string{"encode", "decode"} {
			for _, c := range all {
				fmt.Fprintf(b, " %s |", cell(find(res, t, kind, op, "native", c)))
			}
		}
		b.WriteString("\n")
	}
}

// find returns the result for the given fields, or nil.
func find(res []result, text, kind, operation, form, codec string) *result {
	for i := range res {
		r := &res[i]
		if r.text == text && r.kind == kind && r.operation == operation && r.form == form && r.codec == codec {
			return r
		}
	}
	return nil
}

// perValue formats ns per value with its spread.
func perValue(r *result) string {
	if r == nil {
		return "-"
	}
	v := float64(r.values)
	return fmt.Sprintf("%.1f ± %.1f", r.ns/v, r.errNs/v)
}

// throughput formats millions of UTF-16 code units per second with its spread (the spread of the time, carried over to the rate).
func throughput(r *result) string {
	if r == nil {
		return "-"
	}
	rate := float64(r.units) / r.ns * 1e3
	return fmt.Sprintf("%.0f ± %.0f", rate, rate*r.errNs/r.ns)
}

// ratio is the time of the UTF-8 baseline over the time of Steno: above 1, Steno is faster.
func ratio(steno, base *result) string {
	if steno == nil || base == nil {
		return "-"
	}
	return fmt.Sprintf("%.2f", base.ns/steno.ns)
}

func presentTexts(res []result) []string {
	var out []string
	for _, t := range texts {
		for _, r := range res {
			if r.text == t {
				out = append(out, t)
				break
			}
		}
	}
	return out
}

func table(b *strings.Builder, res []result, kind string, cell func(*result) string) {
	b.WriteString("| Text | Encode string | Decode string | Encode UTF-16 | UTF-8 baseline | Ratio | Decode UTF-16 | UTF-8 baseline | Ratio |\n")
	b.WriteString("|---|--:|--:|--:|--:|--:|--:|--:|--:|\n")
	for _, t := range presentTexts(res) {
		es := find(res, t, kind, "encode", "native", "steno")
		ds := find(res, t, kind, "decode", "native", "steno")
		eu := find(res, t, kind, "encode", "utf16", "steno")
		eb := find(res, t, kind, "encode", "utf16", "utf8")
		du := find(res, t, kind, "decode", "utf16", "steno")
		db := find(res, t, kind, "decode", "utf16", "utf8")
		fmt.Fprintf(b, "| %s | %s | %s | %s | %s | %s | %s | %s | %s |\n", strings.ToUpper(t[:1])+t[1:], cell(es), cell(ds), cell(eu), cell(eb),
			ratio(eu, eb), cell(du), cell(db), ratio(du, db))
	}
}

func markdown(res []result, info runInfo) string {
	var b strings.Builder
	b.WriteString("# Benchmarks of the Go Reference Implementation\n\n")
	b.WriteString("This file is generated by `go run ./bench` from `bench/results.csv`. Do not edit it by hand.\n\n")
	fmt.Fprintf(&b, "- **Tool:** the benchmarks of Go's `testing` package (`go test -bench`), %d runs of %s per benchmark, on one core (`-cpu 1`).\n",
		info.count, info.benchtime)
	fmt.Fprintf(&b, "- **Compiler:** %s, %s/%s.\n", info.goVersion, info.goos, info.goarch)
	fmt.Fprintf(&b, "- **Machine:** %s. **Date:** %s.\n", info.cpu, info.date)
	b.WriteString("- **Workloads:** the shared files of `Benchmarks/Workloads`: each text as one document, and cut into 16-character values (at most 16 " +
		"UTF-16 code units each), one operation processing every value once.\n")
	b.WriteString("- **Forms:** string is a Go string to Steno bytes and Steno bytes to UTF-8 bytes in a reused buffer (`AppendEncode`, " +
		"`AppendDecode`). UTF-16 is `[]uint16` (`AppendEncodeUTF16`, `AppendDecodeUTF16`), for comparison with the .NET implementation.\n")
	b.WriteString("- **UTF-8 baseline:** the standard library between `[]uint16` and UTF-8 bytes, into a reused buffer: `utf16.DecodeRune` and " +
		"`utf8.AppendRune` to encode, a range over the UTF-8 bytes and `utf16.AppendRune` to decode. A Go string is already UTF-8, so the string " +
		"form has no baseline.\n")
	b.WriteString("- **Figures:** the median of the runs, ± the sample standard deviation of the runs. Ratio is the time of the UTF-8 baseline " +
		"divided by the time of the Steno Encoding: above 1, the Steno Encoding is faster.\n\n")

	b.WriteString("## 16-Character Values\n\nNanoseconds per value; lower is better.\n\n")
	table(&b, res, "values", perValue)
	b.WriteString("\n## Documents\n\nMillions of UTF-16 code units per second; higher is better.\n\n")
	table(&b, res, "document", throughput)

	b.WriteString("\n## SCSU and BOCU-1\n\n")
	b.WriteString("Go has no implementation of SCSU or BOCU-1 in common use: `golang.org/x/text` knows their IANA names but does not implement " +
		"them, and Go programs do not commonly use ICU. A Go developer's real choices are UTF-8, a compressor, or the Steno Encoding.\n")

	groups := []struct {
		title  string
		codecs []string
	}{
		{"Deflate", []string{"deflate-fast", "deflate-max"}},
		{"Brotli", []string{"brotli-1", "brotli-11"}},
		{"Zstandard", []string{"zstd-1", "zstd-19"}},
		{"LZ4", []string{"lz4", "lz4-hc"}},
	}
	if hasCodec(res, "deflate-fast") {
		b.WriteString("\n## Compressors\n\n")
		b.WriteString("Each compressor works on the UTF-8 bytes of each value by itself, as a database compresses a field, on 10 of the 14 texts " +
			"(English, French, Russian, Arabic, Hindi, Thai, Amharic, Chinese, Japanese, Korean). They are measured by the module in `compare/`, " +
			"which has its own `go.mod`, so that the `steno` module keeps no dependencies. ")
		fmt.Fprintf(&b, "Runs per benchmark: %d, of %s each. ", info.compareCount, info.compareBenchtime)
		b.WriteString("Steno is the string form of the tables above. Size is in bytes per UTF-16 code unit; " +
			"`bench/sizes.csv` has the totals. The decode time of a compressor includes copying the value into the reused output buffer.\n\n")
		b.WriteString("| Codec | Library | Version | How it is driven |\n|---|---|---|---|\n")
		for _, l := range info.libs {
			fmt.Fprintf(&b, "| %s | %s | %s | %s |\n", l.codec, l.name, l.version, l.use)
		}
		for _, g := range groups {
			fmt.Fprintf(&b, "\n### %s, 16-Character Values\n\nBytes per character, and nanoseconds per value (lower is better).\n\n", g.title)
			compressorTable(&b, res, "values", g.codecs, perValue)
			fmt.Fprintf(&b, "\n### %s, Documents\n\nBytes per character, and millions of UTF-16 code units per second (higher is better).\n\n", g.title)
			compressorTable(&b, res, "document", g.codecs, throughput)
		}
	}

	b.WriteString("\n## Caveats\n\n")
	b.WriteString("- The machine was in use while the benchmarks ran. The spread is part of the result: compare figures only where their spreads " +
		"do not overlap.\n")
	b.WriteString("- Speed is a property of this implementation, not of the encoding. This is portable scalar Go with word-at-a-time fast " +
		"paths: no assembly, no vector instructions and no `unsafe`.\n")
	b.WriteString("- The baseline converts between UTF-16 and UTF-8 with the standard library's per-rune functions, not with a vectorised " +
		"converter.\n")
	return b.String()
}

func hasCodec(res []result, codec string) bool {
	for _, r := range res {
		if r.codec == codec {
			return true
		}
	}
	return false
}

// summary is the block of README.md between the markers: the ratios and speeds of three texts.
func summary(res []result, info runInfo) string {
	var b strings.Builder
	b.WriteString("Measured on " + info.cpu + ", " + info.date + ". [BENCHMARKS.md](BENCHMARKS.md) has every figure with its spread and the " +
		"ratios to the standard library's UTF-8.\n\n")
	b.WriteString("| Text | 16-character values, encode / decode string (ns per value) | Documents, encode / decode string (M UTF-16 units/s) |\n")
	b.WriteString("|---|--:|--:|\n")
	for _, t := range []string{"english", "russian", "hindi", "chinese", "japanese", "korean"} {
		ev := find(res, t, "values", "encode", "native", "steno")
		dv := find(res, t, "values", "decode", "native", "steno")
		ed := find(res, t, "document", "encode", "native", "steno")
		dd := find(res, t, "document", "decode", "native", "steno")
		if ev == nil || dv == nil || ed == nil || dd == nil {
			continue
		}
		fmt.Fprintf(&b, "| %s | %.1f / %.1f | %.0f / %.0f |\n", strings.ToUpper(t[:1])+t[1:], ev.ns/float64(ev.values), dv.ns/float64(dv.values),
			float64(ed.units)/ed.ns*1e3, float64(dd.units)/dd.ns*1e3)
	}
	return b.String()
}

const (
	beginMarker = "<!-- benchmark summary: generated by go run ./bench -->"
	endMarker   = "<!-- end of benchmark summary -->"
)

func updateReadme(path, block string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	text := string(data)
	i := strings.Index(text, beginMarker)
	j := strings.Index(text, endMarker)
	if i < 0 || j < i {
		return fmt.Errorf("%s has no benchmark summary markers", path)
	}
	text = text[:i+len(beginMarker)] + "\n\n" + block + "\n" + text[j:]
	return writeFile(path, text)
}
