"""Turns the pyperf results in bench/raw into bench/results.csv and BENCHMARKS.md, and fills the figures of README.md, so that nobody types
numbers. With --smoke it reads bench/smoke-raw and writes bench/smoke.csv and bench/smoke.md, and does not touch README.md.
"""

from __future__ import annotations

import csv
import datetime
import os
import platform
import re
import sys
from pathlib import Path
from typing import Dict, List, Optional, Tuple

import pyperf
from codecs_table import SCSU_BOCU_NOTE, SUBSET, TEXTS, load, utf16_units, write_lf

HERE = Path(__file__).resolve().parent
smoke = "--smoke" in sys.argv
raw = HERE / ("smoke-raw" if smoke else "raw")
csv_path = HERE / ("smoke.csv" if smoke else "results.csv")
md_path = HERE / "smoke.md" if smoke else HERE.parent / "BENCHMARKS.md"
sizes_path = HERE / ("smoke.sizes.csv" if smoke else "sizes.csv")
libraries_path = HERE / ("smoke.libraries.csv" if smoke else "libraries.csv")

Key = Tuple[str, str, str, str]  # text, kind, operation, codec
results: Dict[Key, Tuple[float, float]] = {}
texts: List[str] = []
python_version = ""
for text in TEXTS:
    path = raw / f"{text}.json"
    if not path.exists():
        continue
    texts.append(text)
    suite = pyperf.BenchmarkSuite.load(str(path))
    for bench in suite.get_benchmarks():
        _, kind, operation, codec = bench.get_name().split("/")
        stdev = bench.stdev() if bench.get_nvalue() > 1 else 0.0
        results[(text, kind, operation, codec)] = (bench.mean() * 1e9, stdev * 1e9)
        python_version = bench.get_metadata().get("python_version", python_version)

counts: Dict[Tuple[str, str], Tuple[int, int]] = {}
for text in texts:
    for kind in ("document", "values"):
        values = load(text, kind)
        counts[(text, kind)] = (len(values), utf16_units(values))

with open(csv_path, "w", encoding="utf-8", newline="\n") as f:
    f.write("implementation,text,kind,operation,form,codec,ns_per_operation,error_ns,values,utf16_units\n")
    for (text, kind, operation, codec), (ns, err) in results.items():
        n, units = counts[(text, kind)]
        f.write(f"python,{text},{kind},{operation},native,{codec},{ns:.1f},{err:.1f},{n},{units}\n")

sizes: Dict[Tuple[str, str, str], int] = {}
if sizes_path.exists():
    with open(sizes_path, encoding="utf-8") as f:
        for row in csv.DictReader(f):
            sizes[(row["text"], row["kind"], row["codec"])] = int(row["bytes"])
libraries: List[Dict[str, str]] = []
if libraries_path.exists():
    with open(libraries_path, encoding="utf-8") as f:
        libraries = list(csv.DictReader(f))


def get(text: str, kind: str, operation: str, codec: str) -> Optional[Tuple[float, float]]:
    return results.get((text, kind, operation, codec))


def per_value(r: Optional[Tuple[float, float]], text: str) -> str:
    if r is None:
        return ""
    n = counts[(text, "values")][0]
    return f"{r[0] / n:,.0f} ± {r[1] / n:,.0f}"


def rate(r: Optional[Tuple[float, float]], text: str) -> str:
    if r is None:
        return ""
    units = counts[(text, "document")][1]
    m = units / r[0] * 1000
    return f"{m:,.2f} ± {m * r[1] / r[0]:,.2f}"


def ratio(a: Optional[Tuple[float, float]], b: Optional[Tuple[float, float]]) -> str:
    return "" if a is None or b is None else f"{a[0] / b[0]:,.1f}"


def title(t: str) -> str:
    return t[0].upper() + t[1:]


def speed_table(kind: str, cell: str) -> List[str]:
    fmt = per_value if kind == "values" else rate
    rows = [
        "| Text | Steno encode | UTF-8 encode | Steno ÷ UTF-8 | Steno decode | UTF-8 decode | Steno ÷ UTF-8 |",
        "|---|--:|--:|--:|--:|--:|--:|",
    ]
    for t in texts:
        se, ue = get(t, kind, "encode", "steno"), get(t, kind, "encode", "utf8")
        sd, ud = get(t, kind, "decode", "steno"), get(t, kind, "decode", "utf8")
        rows.append(f"| {title(t)} | {fmt(se, t)} | {fmt(ue, t)} | {ratio(se, ue)} | {fmt(sd, t)} | {fmt(ud, t)} | {ratio(sd, ud)} |")
    return rows


alternatives = [
    c
    for c in ("utf16", "deflate-fast", "deflate-max", "zstd-1", "zstd-19", "brotli-1", "brotli-11", "lz4")
    if any(k[3] == c for k in results)
]


def alternatives_table(kind: str) -> List[str]:
    fmt = per_value if kind == "values" else rate
    unit = "ns per value" if kind == "values" else "M UTF-16 units/s"
    rows = [f"| Text | Codec | Encode ({unit}) | Decode ({unit}) | Bytes |", "|---|---|--:|--:|--:|"]
    for t in texts:
        if t not in SUBSET and len(texts) > 2:
            continue
        for c in ("steno", "utf8", *alternatives):
            e, d = get(t, kind, "encode", c), get(t, kind, "decode", c)
            if e is None and d is None:
                continue
            size = sizes.get((t, kind, c))
            rows.append(f"| {title(t)} | {c} | {fmt(e, t)} | {fmt(d, t)} | {'' if size is None else f'{size:,}'} |")
    return rows


now = datetime.datetime.now().strftime("%Y-%m-%d")
cpu = platform.processor() or platform.machine()
lines = [
    "# Benchmarks of the Python Reference Implementation" if not smoke else "# Smoke run (not a measurement)",
    "",
    "Generated by `bench/report.py` from the results of `bench/run_all.py`. Do not edit by hand.",
    "",
    f"- Tool: pyperf {pyperf.__version__} (each benchmark in worker processes of its own; mean ± standard deviation of the runs).",
    f"- Runtime: CPython {python_version or platform.python_version()} on {platform.system()} {platform.release()}, {cpu}, "
    f"{os.cpu_count()} logical processors.",
    f"- Date: {now}. The machine was in use while the benchmarks ran, so the spread is part of the result.",
    "- Workloads: `Benchmarks/Workloads` of the repository. One operation encodes or decodes every value of a workload once, in a plain loop "
    "that calls the library directly. Encoding takes a `str` and returns `bytes`; decoding takes `bytes` and returns a `str` (Python has no "
    "API that writes text into a buffer that is allocated once).",
    "- The ratio is Steno's time divided by UTF-8's (`str.encode` and `bytes.decode`, which are C code): how many times slower the pure "
    "Python codec is.",
    "",
    "## 16-Character Values (ns per value)",
    "",
    *speed_table("values", ""),
    "",
    "## Documents (millions of UTF-16 code units per second)",
    "",
    *speed_table("document", ""),
    "",
    "## Alternatives",
    "",
    "What a Python developer would use instead, on "
    + ("a subset of the texts (" + ", ".join(SUBSET) + ")" if len(texts) > 2 else "the texts of this run")
    + ". Compressors work on UTF-8, each value on its own: encoding is `str.encode('utf-8')` and compression, decoding is decompression and "
    "`bytes.decode('utf-8')`. Bytes is the total encoded size of the workload.",
    "",
    "### 16-Character Values",
    "",
    *alternatives_table("values"),
    "",
    "### Documents",
    "",
    *alternatives_table("document"),
    "",
    "## Libraries",
    "",
    "| Codec | Library and how it is driven | Version |",
    "|---|---|---|",
    *[f"| {r['codec']} | {r['library']} | {r['version']} |" for r in libraries],
    "",
    SCSU_BOCU_NOTE,
    "",
    "Zstandard's compressor and decompressor objects are made once and reused for every value, as its documentation advises for many small "
    "inputs. zlib, Brotli and LZ4 are called through their one-shot functions, which is how their documentation shows them; they have no "
    "reusable object for one-shot calls, so each call sets up its own state.",
    "",
    "## Caveats",
    "",
    "- This is pure Python. The compiled codecs it is compared with (UTF-8 and the compressors) are C code called from Python, so the ratios "
    "show the cost of a codec written in Python, not of the Steno Encoding.",
    "- Short values pay the cost of the Python function calls for each value; documents in one alphabet are converted mostly by C code of the "
    "standard library and go much faster than documents that change script often.",
    "- The figures depend on the machine and its load; compare figures from the same run.",
    "",
]
write_lf(md_path, "\n".join(lines))

if not smoke:
    readme = HERE.parent / "README.md"
    text = readme.read_text(encoding="utf-8")
    summary = ["<!-- benchmarks:start -->"]
    summary.append(
        "Measured with pyperf on " + now + "; all figures, the alternatives and the caveats are in [BENCHMARKS.md](BENCHMARKS.md)."
    )
    summary.append("")
    summary.append("| Text | Encode, 16-character values (ns/value) | Decode | Encode, document (M units/s) | Decode |")
    summary.append("|---|--:|--:|--:|--:|")
    for t in texts:
        summary.append(
            f"| {title(t)} | {per_value(get(t, 'values', 'encode', 'steno'), t)} | {per_value(get(t, 'values', 'decode', 'steno'), t)} | "
            f"{rate(get(t, 'document', 'encode', 'steno'), t)} | {rate(get(t, 'document', 'decode', 'steno'), t)} |"
        )
    summary.append("<!-- benchmarks:end -->")
    text = re.sub(r"<!-- benchmarks:start -->.*?<!-- benchmarks:end -->", lambda _: "\n".join(summary), text, flags=re.S)
    write_lf(readme, text)

print(f"wrote {csv_path.name} and {md_path.name}")
