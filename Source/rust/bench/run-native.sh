#!/bin/sh
# Runs the native comparison of the Rust reference implementation of the Steno Encoding with the other encodings (UTF-8, SCSU and BOCU-1
# through ICU) and with general compressors, and the comparison of encoded values; then writes bench/native-results.csv,
# bench/native-libraries.csv and the native section of BENCHMARKS.md.
#
# About 30 minutes on a quiet machine: 14 texts, documents and 16-character values, 32 benchmarks each (the plain C implementation of
# Source/c in two builds among them), and 6 comparisons per text, with flat sampling (0.5 s of warm-up, 1 s of measurement, 20 samples;
# Brotli 11 on the values takes about 4 s). `--measurement-time 0.7` brings it to about 25 minutes.
#
# Restartable by group: pass a filter to run only some groups, for example `sh bench/run-native.sh '^native\.(russian|greek)\.'` or
# `sh bench/run-native.sh '^compare\.'`; the results of the other groups stay, and the report takes all that are there. Without a filter,
# the earlier results of this benchmark are removed first.
#
# ICU is loaded at run time (the system icu.dll on Windows); where it is missing, SCSU and BOCU-1 are left out. On the windows-gnu
# toolchain, the compressors and Criterion need a C compiler and `dlltool` to build.
set -eu
cd "$(dirname "$0")/.."
target="${CARGO_TARGET_DIR:-target}"

if [ "$#" -eq 0 ]; then
    rm -rf "$target"/criterion/native.* "$target"/criterion/compare.*
fi

cargo bench -p singulink-steno-bench --bench native -- "$@"
cargo run --release -p singulink-steno-bench --bin report
