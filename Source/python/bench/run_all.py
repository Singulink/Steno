"""Runs the speed benchmarks of the Python reference implementation and writes the results.

    python bench/run_all.py            the full run: every text, one after another, then the report
    python bench/run_all.py --smoke    a smoke run of a few seconds (english and chinese, the smallest settings), into bench/smoke.*

The full run keeps the results of each text in bench/raw/<text>.json. If it is stopped, the next run skips the texts that are done, so it is
safe to run again; delete bench/raw to start afresh. It needs pyperf and the alternatives' packages: pip install --group bench (pip 25.1 or
later), or pip install pyperf zstandard brotli lz4.
"""

from __future__ import annotations

import csv
import io
import os
import subprocess
import sys
from pathlib import Path

from codecs_table import TEXTS, all_codecs, codecs_for, load, write_lf

HERE = Path(__file__).resolve().parent


def write_sizes(path: Path, texts: list[str], smoke: bool) -> None:
    """Writes the total encoded size of each workload in each codec."""
    codecs = all_codecs()
    rows = ["implementation,text,kind,codec,bytes"]
    for text in texts:
        for kind in ("document", "values"):
            values = load(text, kind)
            for name in codecs_for(text, smoke):
                if name in codecs:
                    rows.append(f"python,{text},{kind},{name},{sum(len(codecs[name].encode_one(v)) for v in values)}")
    write_lf(path, "\n".join(rows) + "\n")


def write_libraries(path: Path) -> None:
    out = io.StringIO()
    writer = csv.writer(out, lineterminator="\n")
    writer.writerow(["codec", "library", "version"])
    for c in all_codecs().values():
        writer.writerow([c.name, c.library, c.version])
    write_lf(path, out.getvalue())


def main() -> None:
    smoke = "--smoke" in sys.argv
    texts = ["english", "chinese"] if smoke else TEXTS
    raw = HERE / ("smoke-raw" if smoke else "raw")
    raw.mkdir(exist_ok=True)
    env = dict(os.environ, PYTHONHASHSEED="0")
    for text in texts:
        out = raw / f"{text}.json"
        if out.exists() and not smoke:
            print(f"{text}: done before, skipped")
            continue
        partial = raw / f"{text}.json.part"
        partial.unlink(missing_ok=True)
        cmd = [sys.executable, str(HERE / "run.py"), "--text", text, "-o", str(partial), "--quiet"]
        if smoke:
            cmd.append("--smoke")
        print(f"{text}: running", flush=True)
        subprocess.run(cmd, check=True, cwd=HERE, env=env)
        partial.replace(out)
    prefix = "smoke." if smoke else ""
    write_sizes(HERE / f"{prefix}sizes.csv", texts, smoke)
    write_libraries(HERE / f"{prefix}libraries.csv")
    subprocess.run([sys.executable, str(HERE / "report.py"), *(["--smoke"] if smoke else [])], check=True, cwd=HERE)


if __name__ == "__main__":
    main()
