#!/bin/sh
# Builds and runs the JMH benchmarks of the Java reference implementation, then writes bench/results.csv,
# bench/sizes.csv, bench/libraries.csv and BENCHMARKS.md and fills the figures block of README.md.
#
#   bench/run.sh                       the full run, about 25 minutes: StenoBenchmark (Steno, UTF-8 and UTF-16 on
#                                      14 texts x 2 kinds, 196 benchmarks) and AlternativesBenchmark (SCSU, BOCU-1 and
#                                      8 compressor settings on 10 texts x 2 kinds, 400 benchmarks)
#   bench/run.sh -p text=english -wi 1 -i 1 -w 200ms -r 200ms     a smoke run; any JMH options are passed on
#
# Run it from the folder of the Java reference implementation (the parent of this folder), on a quiet machine. It is safe
# to run again: each run replaces these files and the figures block.
set -eu

cd "$(dirname "$0")/.."

./mvnw -q -B -f bench/pom.xml package

# The forks take their heap options from StenoBenchmark (JMH passes the options of this JVM on to them, so none here).
java -jar bench/target/benchmarks.jar -rf json -rff bench/results.json "$@"
java -Xmx256m -cp bench/target/benchmarks.jar com.singulink.steno.bench.Report \
    bench/results.json bench/results.csv BENCHMARKS.md README.md
