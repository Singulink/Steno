#!/bin/sh
# Runs the full speed benchmark of the Rust reference implementation, writes bench/results.csv and BENCHMARKS.md, and fills in the
# benchmark summary block of README.md.
#
# 168 benchmarks (14 texts, 2 kinds, 6 operations), each with 1 s of warm-up and 2 s of measurement: about 10 minutes on a quiet machine.
# Extra arguments go to Criterion, for example a filter (`sh bench/run.sh russian`) or shorter settings
# (`--warm-up-time 0.2 --measurement-time 0.3 --sample-size 10`). Safe to run again: each run starts from no results.
#
# On the windows-gnu toolchain, Criterion's dependencies need a C compiler and `dlltool` (MinGW-w64) to build; elsewhere nothing more.
set -eu
cd "$(dirname "$0")/.."
rm -rf "${CARGO_TARGET_DIR:-target}/criterion"
cargo bench -p singulink-steno-bench --bench codec -- "$@"
cargo run --release -p singulink-steno-bench --bin report
