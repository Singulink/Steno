//! Speed of the Rust reference implementation of the Steno Encoding, with Criterion, on the shared workloads of the repository.
//!
//! For each text and kind (`document`, `values`) there is one benchmark group, `<text>.<kind>`, with these benchmarks, named
//! `<operation>.<form>.<codec>`:
//!
//! - `encode.native.steno`: `&str` to Steno bytes (`encode_to_slice`).
//! - `encode.utf16.steno`: UTF-16 code units to Steno bytes (`utf16::encode_to_slice`).
//! - `encode.utf16.utf8`: the baseline, UTF-16 code units to UTF-8 with the standard library (`char::decode_utf16` into a `String`).
//! - `decode.native.steno`: Steno bytes to a `String` (`decode_to_string`).
//! - `decode.utf16.steno`: Steno bytes to UTF-16 code units (`utf16::decode_to_slice`).
//! - `decode.utf16.utf8`: the baseline, UTF-8 bytes to UTF-16 code units with the standard library (`str::from_utf8`, which validates, then
//!   `str::encode_utf16` into a `Vec`).
//!
//! One iteration processes every value of the workload once. The inputs and the output buffers are prepared before the measurement, and
//! each output buffer is allocated once. `src/report.rs` turns the results into `bench/results.csv` and `BENCHMARKS.md`.
//!
//! The default settings (1 s of warm-up, 2 s of measurement, 50 samples per benchmark) can be overridden on the command line, for example
//! `cargo bench -p singulink-steno-bench --bench codec -- --warm-up-time 0.2 --measurement-time 0.3 --sample-size 10 english`.

use std::hint::black_box;
use std::time::Duration;

use criterion::{criterion_group, criterion_main, Criterion, Throughput};
use singulink_steno::{self as steno, utf16};
use singulink_steno_bench::{load, utf16_units, KINDS, TEXTS};

fn benches(c: &mut Criterion) {
    for &(text, _) in TEXTS {
        for &kind in KINDS {
            let values = load(text, kind);
            let units: Vec<Vec<u16>> = values.iter().map(|v| v.encode_utf16().collect()).collect();
            let steno_bytes: Vec<Vec<u8>> = values.iter().map(|v| steno::encode(v)).collect();
            let utf8_bytes: Vec<&[u8]> = values.iter().map(|v| v.as_bytes()).collect();

            let max_utf8 = values.iter().map(String::len).max().unwrap_or(0);
            let max_units = units.iter().map(Vec::len).max().unwrap_or(0);
            let max_steno = steno_bytes.iter().map(Vec::len).max().unwrap_or(0);

            let mut byte_buf = vec![0u8; steno::max_encoded_len(max_utf8).max(utf16::max_encoded_len(max_units))];
            let mut unit_buf = vec![0u16; utf16::max_decoded_len(max_steno).max(max_units)];
            let mut string_buf = String::with_capacity(steno::max_decoded_len(max_steno).max(max_utf8));
            let mut unit_vec: Vec<u16> = Vec::with_capacity(unit_buf.len());

            let mut group = c.benchmark_group(format!("{text}.{kind}"));
            group.throughput(Throughput::Elements(utf16_units(&values) as u64));

            group.bench_function("encode.native.steno", |b| {
                b.iter(|| {
                    for v in &values {
                        black_box(steno::encode_to_slice(black_box(v), &mut byte_buf).unwrap());
                    }
                })
            });

            group.bench_function("encode.utf16.steno", |b| {
                b.iter(|| {
                    for u in &units {
                        black_box(utf16::encode_to_slice(black_box(u), &mut byte_buf).unwrap());
                    }
                })
            });

            group.bench_function("encode.utf16.utf8", |b| {
                b.iter(|| {
                    for u in &units {
                        string_buf.clear();
                        string_buf
                            .extend(char::decode_utf16(black_box(u).iter().copied()).map(|r| r.unwrap_or(char::REPLACEMENT_CHARACTER)));
                        black_box(string_buf.as_bytes());
                    }
                })
            });

            group.bench_function("decode.native.steno", |b| {
                b.iter(|| {
                    for s in &steno_bytes {
                        string_buf.clear();
                        steno::decode_to_string(black_box(s), &mut string_buf).unwrap();
                        black_box(string_buf.as_str());
                    }
                })
            });

            group.bench_function("decode.utf16.steno", |b| {
                b.iter(|| {
                    for s in &steno_bytes {
                        black_box(utf16::decode_to_slice(black_box(s), &mut unit_buf).unwrap());
                    }
                })
            });

            group.bench_function("decode.utf16.utf8", |b| {
                b.iter(|| {
                    for &s in &utf8_bytes {
                        unit_vec.clear();
                        unit_vec.extend(std::str::from_utf8(black_box(s)).unwrap().encode_utf16());
                        black_box(unit_vec.as_slice());
                    }
                })
            });

            group.finish();
        }
    }
}

fn config() -> Criterion {
    Criterion::default().warm_up_time(Duration::from_secs(1)).measurement_time(Duration::from_secs(2)).sample_size(50)
}

criterion_group! {
    name = codec;
    config = config();
    targets = benches
}

criterion_main!(codec);
