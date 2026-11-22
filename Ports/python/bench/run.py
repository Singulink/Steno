"""The pyperf benchmarks of one text: python bench/run.py --text english -o bench/raw/english.json [--smoke].

bench/run_all.py runs it for every text. pyperf runs each benchmark in worker processes of its own, calibrates the number of loops, and keeps
the time of each run; the report uses its mean and standard deviation. One operation is "encode (or decode) every value of the workload once",
in a plain loop that calls the library directly.
"""

from __future__ import annotations

import sys
from typing import Any, Callable, Dict, List

import pyperf
from codecs_table import TEXTS, all_codecs, codecs_for, load

SMOKE = "--smoke" in sys.argv


def add_cmdline_args(cmd: List[str], args: Any) -> None:
    cmd.extend(["--text", args.text])
    if args.smoke:
        cmd.append("--smoke")


if SMOKE:
    runner = pyperf.Runner(processes=1, values=2, warmups=1, min_time=0.005, add_cmdline_args=add_cmdline_args)
else:
    runner = pyperf.Runner(processes=3, values=3, warmups=1, min_time=0.1, add_cmdline_args=add_cmdline_args)
runner.argparser.add_argument("--text", required=True, choices=TEXTS)
runner.argparser.add_argument("--smoke", action="store_true")
args = runner.parse_args()

codecs = all_codecs()
_inputs: Dict[Any, List[Any]] = {}


def inputs(kind: str, codec: str, operation: str) -> List[Any]:
    """The inputs of one benchmark, prepared once per worker process, outside the timed loop."""
    key = (kind, codec, operation)
    if key not in _inputs:
        values = load(args.text, kind)
        if operation == "encode":
            _inputs[key] = values
        else:
            c = codecs[codec]
            encoded = [c.encode_one(v) for v in values]
            if [c.decode_one(b) for b in encoded] != values:
                raise AssertionError(f"{codec} does not round-trip {args.text}")
            _inputs[key] = encoded
    return _inputs[key]


def make(kind: str, codec: str, operation: str) -> Callable[[int], float]:
    loop = codecs[codec].encode_loop if operation == "encode" else codecs[codec].decode_loop

    def timed(loops: int) -> float:
        return loop(loops, inputs(kind, codec, operation))

    return timed


for kind in ("document", "values"):
    for codec in codecs_for(args.text, SMOKE):
        if codec not in codecs:
            continue
        for operation in ("encode", "decode"):
            runner.bench_time_func(f"{args.text}/{kind}/{operation}/{codec}", make(kind, codec, operation))
