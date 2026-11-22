//! The native comparison: the Steno Encoding next to the other encodings of text and next to general compressors, on the shared
//! workloads, each value encoded and decoded on its own into buffers allocated once; and the comparison of two encoded values without
//! decoding them.
//!
//! Groups `native.<text>.<kind>`, benchmarks `<operation>.<form>.<codec>`:
//!
//! - `steno`, form `native`: `&str` to Steno bytes (`encode_to_slice`) and Steno bytes to UTF-8 bytes (`decode_to_slice`); form `utf16`: the
//!   same with UTF-16 code units.
//! - `steno-c` and `steno-c-scalar`, forms `native` (UTF-8) and `utf16`: the plain C implementation of the repository (`Ports/c/steno.c`),
//!   compiled by `build.rs` with the `cc` crate at `-O2`, as it is and with `STENO_NO_SIMD`; one call of its C interface per value.
//! - `utf8-copy`, form `native`: UTF-8 stored as it is: `str::from_utf8` (validation) and a copy, both ways.
//! - `utf8`, form `utf16`: the standard library's conversion of UTF-16 to UTF-8 and back.
//! - `scsu-icu` and `bocu1-icu`, form `utf16`: ICU4C's converters, called through ICU's C API, one converter of each opened once.
//! - `zstd-1`, `zstd-19`, `brotli-1`, `brotli-11`, `lz4` and `deflate-6`, form `native`: the C reference libraries on the UTF-8 bytes of each
//!   value by itself.
//!
//! Groups `compare.<text>` (16-character values), benchmarks `compare.<pairs>.<method>`, for each pair of the sorted values that are next to
//! each other (`neighbours`) and for as many random pairs (`random`): `steno` compares the Steno bytes, `steno-decode` decodes both values
//! and compares the text, `utf8` compares the two `str`.
//!
//! Settings: flat sampling, 0.5 s of warm-up, 1 s of measurement and 20 samples per benchmark; the command line can override them.

use std::hint::black_box;
use std::time::Duration;

use criterion::{criterion_group, criterion_main, Criterion, SamplingMode, Throughput};
use singulink_steno::{self as steno, utf16};
use singulink_steno_bench::icu::Icu;
use singulink_steno_bench::{load, utf16_units, KINDS, TEXTS};

/// The functions of the plain C implementation that the benchmark calls, in its two builds (see `build.rs`).
mod plain_c {
    type Status = i32;

    extern "C" {
        pub fn steno_c_encode_utf8(src: *const u8, src_len: usize, dst: *mut u8, dst_len: usize, out_len: *mut usize) -> Status;
        pub fn steno_c_decode_utf8(src: *const u8, src_len: usize, dst: *mut u8, dst_len: usize, out_len: *mut usize) -> Status;
        pub fn steno_c_encode_utf16(src: *const u16, src_len: usize, dst: *mut u8, dst_len: usize, out_len: *mut usize) -> Status;
        pub fn steno_c_decode_utf16(src: *const u8, src_len: usize, dst: *mut u16, dst_len: usize, out_len: *mut usize) -> Status;
        pub fn steno_cs_encode_utf8(src: *const u8, src_len: usize, dst: *mut u8, dst_len: usize, out_len: *mut usize) -> Status;
        pub fn steno_cs_decode_utf8(src: *const u8, src_len: usize, dst: *mut u8, dst_len: usize, out_len: *mut usize) -> Status;
        pub fn steno_cs_encode_utf16(src: *const u16, src_len: usize, dst: *mut u8, dst_len: usize, out_len: *mut usize) -> Status;
        pub fn steno_cs_decode_utf16(src: *const u8, src_len: usize, dst: *mut u16, dst_len: usize, out_len: *mut usize) -> Status;
    }

    /// One build of the plain C implementation: its four functions.
    #[derive(Clone, Copy)]
    pub struct Build {
        pub name: &'static str,
        enc8: unsafe extern "C" fn(*const u8, usize, *mut u8, usize, *mut usize) -> Status,
        dec8: unsafe extern "C" fn(*const u8, usize, *mut u8, usize, *mut usize) -> Status,
        enc16: unsafe extern "C" fn(*const u16, usize, *mut u8, usize, *mut usize) -> Status,
        dec16: unsafe extern "C" fn(*const u8, usize, *mut u16, usize, *mut usize) -> Status,
    }

    pub const BUILDS: [Build; 2] = [
        Build {
            name: "steno-c",
            enc8: steno_c_encode_utf8,
            dec8: steno_c_decode_utf8,
            enc16: steno_c_encode_utf16,
            dec16: steno_c_decode_utf16,
        },
        Build {
            name: "steno-c-scalar",
            enc8: steno_cs_encode_utf8,
            dec8: steno_cs_decode_utf8,
            enc16: steno_cs_encode_utf16,
            dec16: steno_cs_decode_utf16,
        },
    ];

    // SAFETY (all four): the pointers and lengths describe live slices that do not overlap, and `n` is a live `usize`, as the C interface
    // requires; the functions write at most `dst.len()` elements.
    impl Build {
        #[inline]
        pub fn encode_utf8(&self, src: &[u8], dst: &mut [u8]) -> usize {
            let mut n = 0;
            let st = unsafe { (self.enc8)(src.as_ptr(), src.len(), dst.as_mut_ptr(), dst.len(), &mut n) };
            assert_eq!(st, 0, "{} encode_utf8", self.name);
            n
        }

        #[inline]
        pub fn decode_utf8(&self, src: &[u8], dst: &mut [u8]) -> usize {
            let mut n = 0;
            let st = unsafe { (self.dec8)(src.as_ptr(), src.len(), dst.as_mut_ptr(), dst.len(), &mut n) };
            assert_eq!(st, 0, "{} decode_utf8", self.name);
            n
        }

        #[inline]
        pub fn encode_utf16(&self, src: &[u16], dst: &mut [u8]) -> usize {
            let mut n = 0;
            let st = unsafe { (self.enc16)(src.as_ptr(), src.len(), dst.as_mut_ptr(), dst.len(), &mut n) };
            assert_eq!(st, 0, "{} encode_utf16", self.name);
            n
        }

        #[inline]
        pub fn decode_utf16(&self, src: &[u8], dst: &mut [u16]) -> usize {
            let mut n = 0;
            let st = unsafe { (self.dec16)(src.as_ptr(), src.len(), dst.as_mut_ptr(), dst.len(), &mut n) };
            assert_eq!(st, 0, "{} decode_utf16", self.name);
            n
        }
    }
}

/// A general compressor at one setting: compresses or decompresses one value into a buffer and returns the length.
enum Compressor {
    Zstd(zstd::bulk::Compressor<'static>, zstd::bulk::Decompressor<'static>),
    Brotli(brotlic::Quality),
    Lz4,
    Deflate(flate2::Compress, flate2::Decompress),
}

impl Compressor {
    fn all() -> Vec<(&'static str, Compressor)> {
        let zstd = |level| Compressor::Zstd(zstd::bulk::Compressor::new(level).unwrap(), zstd::bulk::Decompressor::new().unwrap());
        let brotli = |q| Compressor::Brotli(brotlic::Quality::new(q).unwrap());
        let deflate =
            |level| Compressor::Deflate(flate2::Compress::new(flate2::Compression::new(level), false), flate2::Decompress::new(false));
        vec![
            ("zstd-1", zstd(1)),
            ("zstd-19", zstd(19)),
            ("brotli-1", brotli(1)),
            ("brotli-11", brotli(11)),
            ("lz4", Compressor::Lz4),
            ("deflate-6", deflate(6)),
        ]
    }

    #[inline]
    fn compress(&mut self, src: &[u8], dst: &mut [u8]) -> usize {
        match self {
            Compressor::Zstd(c, _) => c.compress_to_buffer(src, dst).unwrap(),
            Compressor::Brotli(q) => {
                brotlic::compress(src, dst, *q, brotlic::WindowSize::default(), brotlic::CompressionMode::Generic).unwrap()
            }
            Compressor::Lz4 => lz4::block::compress_to_buffer(src, None, false, dst).unwrap(),
            Compressor::Deflate(c, _) => {
                c.reset();
                c.compress(src, dst, flate2::FlushCompress::Finish).unwrap();
                c.total_out() as usize
            }
        }
    }

    /// `len` is the length of the decompressed value (LZ4 blocks need it).
    #[inline]
    fn decompress(&mut self, src: &[u8], dst: &mut [u8], len: usize) -> usize {
        match self {
            Compressor::Zstd(_, d) => d.decompress_to_buffer(src, dst).unwrap(),
            Compressor::Brotli(_) => brotlic::decompress(src, dst).unwrap(),
            Compressor::Lz4 => lz4::block::decompress_to_buffer(src, Some(len as i32), dst).unwrap(),
            Compressor::Deflate(_, d) => {
                d.reset(false);
                d.decompress(src, dst, flate2::FlushDecompress::Finish).unwrap();
                d.total_out() as usize
            }
        }
    }
}

fn native(c: &mut Criterion) {
    let icu = Icu::load();

    match &icu {
        Some(icu) => record_environment(&format!("icu={}", icu.version())),
        None => record_environment("icu=not available"),
    }

    let mut converters: Vec<(&str, singulink_steno_bench::icu::Converter)> = Vec::new();

    if let Some(icu) = &icu {
        for (codec, name) in [("scsu-icu", "SCSU"), ("bocu1-icu", "BOCU-1")] {
            if let Some(conv) = icu.converter(name) {
                converters.push((codec, conv));
            }
        }
    }

    let mut compressors = Compressor::all();
    record_environment(&format!("c-compiler={}", env!("STENO_C_COMPILER")));

    for &(text, _) in TEXTS {
        for &kind in KINDS {
            let values = load(text, kind);
            let units: Vec<Vec<u16>> = values.iter().map(|v| v.encode_utf16().collect()).collect();
            let utf8: Vec<&[u8]> = values.iter().map(|v| v.as_bytes()).collect();
            let steno_bytes: Vec<Vec<u8>> = values.iter().map(|v| steno::encode(v)).collect();

            let max_utf8 = values.iter().map(String::len).max().unwrap_or(0);
            let max_units = units.iter().map(Vec::len).max().unwrap_or(0);
            let mut bytes = vec![0u8; 4 * max_utf8 + 1024];
            let mut text_buf = vec![0u8; 4 * max_utf8 + 1024];
            let mut unit_buf = vec![0u16; 2 * max_units + 1024];
            let mut string_buf = String::with_capacity(4 * max_utf8);
            let mut unit_vec: Vec<u16> = Vec::with_capacity(2 * max_units);

            let mut group = c.benchmark_group(format!("native.{text}.{kind}"));
            group.sampling_mode(SamplingMode::Flat);
            group.throughput(Throughput::Elements(utf16_units(&values) as u64));

            // Steno, UTF-8 form and UTF-16 form.
            group.bench_function("encode.native.steno", |b| {
                b.iter(|| {
                    for v in &values {
                        black_box(steno::encode_to_slice(black_box(v), &mut bytes).unwrap());
                    }
                })
            });
            group.bench_function("decode.native.steno", |b| {
                b.iter(|| {
                    for s in &steno_bytes {
                        black_box(steno::decode_to_slice(black_box(s), &mut text_buf).unwrap());
                    }
                })
            });
            group.bench_function("encode.utf16.steno", |b| {
                b.iter(|| {
                    for u in &units {
                        black_box(utf16::encode_to_slice(black_box(u), &mut bytes).unwrap());
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

            // The plain C implementation, in both builds: the same bytes as this crate, checked once before the measurement.
            for c in plain_c::BUILDS {
                for (v, s) in values.iter().zip(&steno_bytes) {
                    let n = c.encode_utf8(v.as_bytes(), &mut bytes);
                    assert_eq!(&bytes[..n], &s[..], "{} encodes differently", c.name);
                }

                group.bench_function(format!("encode.native.{}", c.name), |b| {
                    b.iter(|| {
                        for v in &values {
                            black_box(c.encode_utf8(black_box(v.as_bytes()), &mut bytes));
                        }
                    })
                });
                group.bench_function(format!("decode.native.{}", c.name), |b| {
                    b.iter(|| {
                        for s in &steno_bytes {
                            black_box(c.decode_utf8(black_box(s), &mut text_buf));
                        }
                    })
                });
                group.bench_function(format!("encode.utf16.{}", c.name), |b| {
                    b.iter(|| {
                        for u in &units {
                            black_box(c.encode_utf16(black_box(u), &mut bytes));
                        }
                    })
                });
                group.bench_function(format!("decode.utf16.{}", c.name), |b| {
                    b.iter(|| {
                        for s in &steno_bytes {
                            black_box(c.decode_utf16(black_box(s), &mut unit_buf));
                        }
                    })
                });
            }

            // UTF-8 as stored text: a validated copy, both ways.
            for op in ["encode", "decode"] {
                group.bench_function(format!("{op}.native.utf8-copy"), |b| {
                    b.iter(|| {
                        for &s in &utf8 {
                            let s = std::str::from_utf8(black_box(s)).unwrap().as_bytes();
                            text_buf[..s.len()].copy_from_slice(s);
                            black_box(&text_buf[..s.len()]);
                        }
                    })
                });
            }

            // UTF-16 to UTF-8 and back with the standard library.
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
            group.bench_function("decode.utf16.utf8", |b| {
                b.iter(|| {
                    for &s in &utf8 {
                        unit_vec.clear();
                        unit_vec.extend(std::str::from_utf8(black_box(s)).unwrap().encode_utf16());
                        black_box(unit_vec.as_slice());
                    }
                })
            });

            // SCSU and BOCU-1 through ICU.
            for (codec, conv) in &mut converters {
                let encoded: Vec<Vec<u8>> = units
                    .iter()
                    .map(|u| {
                        let n = conv.encode(u, &mut bytes);
                        bytes[..n].to_vec()
                    })
                    .collect();

                group.bench_function(format!("encode.utf16.{codec}"), |b| {
                    b.iter(|| {
                        for u in &units {
                            black_box(conv.encode(black_box(u), &mut bytes));
                        }
                    })
                });
                group.bench_function(format!("decode.utf16.{codec}"), |b| {
                    b.iter(|| {
                        for e in &encoded {
                            black_box(conv.decode(black_box(e), &mut unit_buf));
                        }
                    })
                });
            }

            // General compressors on the UTF-8 bytes of each value by itself.
            for (codec, comp) in &mut compressors {
                let compressed: Vec<Vec<u8>> = utf8
                    .iter()
                    .map(|s| {
                        let n = comp.compress(s, &mut bytes);
                        bytes[..n].to_vec()
                    })
                    .collect();

                group.bench_function(format!("encode.native.{codec}"), |b| {
                    b.iter(|| {
                        for &s in &utf8 {
                            black_box(comp.compress(black_box(s), &mut bytes));
                        }
                    })
                });
                group.bench_function(format!("decode.native.{codec}"), |b| {
                    b.iter(|| {
                        for (e, s) in compressed.iter().zip(&utf8) {
                            black_box(comp.decompress(black_box(e), &mut text_buf, s.len()));
                        }
                    })
                });
            }

            group.finish();
        }
    }
}

fn compare(c: &mut Criterion) {
    for &(text, _) in TEXTS {
        let mut values = load(text, "values");
        values.sort();
        values.dedup();
        let steno_bytes: Vec<Vec<u8>> = values.iter().map(|v| steno::encode(v)).collect();
        let n = values.len();

        // Neighbours in code point order, then as many seeded random pairs.
        let neighbours: Vec<(usize, usize)> = (1..n).map(|i| (i - 1, i)).collect();
        let mut seed: u64 = 7;
        let mut next = || {
            seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            (seed >> 33) as usize % n
        };
        let random: Vec<(usize, usize)> = (1..n).map(|_| (next(), next())).collect();

        let max = values.iter().map(String::len).max().unwrap_or(0);
        let (mut a_buf, mut b_buf) = (vec![0u8; 4 * max + 64], vec![0u8; 4 * max + 64]);
        let mut group = c.benchmark_group(format!("compare.{text}"));
        group.sampling_mode(SamplingMode::Flat);

        for (name, pairs) in [("neighbours", &neighbours), ("random", &random)] {
            group.throughput(Throughput::Elements(pairs.len() as u64));

            group.bench_function(format!("compare.{name}.steno"), |b| {
                b.iter(|| {
                    for &(i, j) in pairs {
                        black_box(black_box(&steno_bytes[i]).cmp(black_box(&steno_bytes[j])));
                    }
                })
            });
            group.bench_function(format!("compare.{name}.steno-decode"), |b| {
                b.iter(|| {
                    for &(i, j) in pairs {
                        let na = steno::decode_to_slice(black_box(&steno_bytes[i]), &mut a_buf).unwrap();
                        let nb = steno::decode_to_slice(black_box(&steno_bytes[j]), &mut b_buf).unwrap();
                        black_box(a_buf[..na].cmp(&b_buf[..nb]));
                    }
                })
            });
            group.bench_function(format!("compare.{name}.utf8"), |b| {
                b.iter(|| {
                    for &(i, j) in pairs {
                        black_box(black_box(values[i].as_str()).cmp(black_box(values[j].as_str())));
                    }
                })
            });
        }

        group.finish();
    }
}

/// Writes facts about the run that Criterion does not keep (the version of ICU) next to its results, for the report.
fn record_environment(line: &str) {
    let dir = std::env::var_os("CARGO_TARGET_DIR")
        .map_or_else(|| std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../target"), std::path::PathBuf::from)
        .join("criterion");
    let _ = std::fs::create_dir_all(&dir);
    let path = dir.join("native-environment.txt");
    let mut text = std::fs::read_to_string(&path).unwrap_or_default();
    let key = line.split('=').next().unwrap_or_default();
    text = text.lines().filter(|l| !l.starts_with(&format!("{key}="))).map(|l| format!("{l}\n")).collect();
    text.push_str(&format!("{line}\n"));
    let _ = std::fs::write(path, text);
}

fn config() -> Criterion {
    Criterion::default().warm_up_time(Duration::from_millis(500)).measurement_time(Duration::from_secs(1)).sample_size(20)
}

criterion_group! {
    name = native_benches;
    config = config();
    targets = native, compare
}

criterion_main!(native_benches);
