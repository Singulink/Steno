//! The vector code against the scalar code: the same text and the same bytes through both, with the vector code turned off for the thread
//! for the second pass. Runs long enough for the vector code, with a code point that breaks the run at every offset around the block
//! boundaries, inputs that end inside a block, and damaged bytes inside long runs.
//!
//! When the vector code does not run in this process (no `simd` feature, a CPU without SSSE3, or `STENO_SIMD=0`), both passes run the
//! scalar code and the tests still check the results against the plain reference codec.

#![cfg(feature = "std")]

mod common;

use common::{reference, to_string, to_utf16, Rng};
use singulink_steno::{code_points, utf16, Decoder, Encoder};

/// A script: code points that make long runs in one layout, a prefix that puts the encoder into that layout, and code points that break a run.
struct Script {
    name: &'static str,
    prefix: &'static [u32],
    run: &'static [(u32, u32)],
    breakers: &'static [u32],
}

const SCRIPTS: &[Script] = &[
    Script {
        name: "ascii",
        prefix: &[],
        run: &[(0x20, 0x7E), (0x0A, 0x0A), (0x09, 0x09), (0x0D, 0x0D)],
        breakers: &[0x01, 0x7F, 0xE9, 0x416, 0x1F600, 0xD800],
    },
    Script {
        name: "cyrillic",
        prefix: &[0x416, 0x416],
        run: &[(0x400, 0x47F), (0x400, 0x47F), (0x20, 0x40), (0x0A, 0x0A)],
        breakers: &[0xAB, 0x2014, 0x531, 0x480, 0x1F600, 0xDC00, 0x41, 0x100],
    },
    Script {
        name: "devanagari",
        prefix: &[0x915, 0x915],
        run: &[(0x900, 0x97F), (0x20, 0x2F), (0x0A, 0x0A)],
        breakers: &[0x980, 0x2014, 0x0D, 0x1F600, 0xE9],
    },
    Script {
        name: "supplementary main window",
        prefix: &[0x1F600, 0x1F601],
        run: &[(0x1F600, 0x1F67F), (0x20, 0x7E)],
        breakers: &[0x1F680, 0x416, 0xD83D],
    },
    Script {
        name: "chinese",
        prefix: &[0x4E00],
        run: &[(0x4E00, 0x9FFF), (0x4E00, 0x9FFF), (0x3000, 0x300F), (0x20, 0x7E), (0x0A, 0x0A)],
        breakers: &[0x3400, 0x20000, 0x141, 0xAC00, 0xFF01, 0x3010, 0xFF21, 0x3041],
    },
    Script {
        name: "japanese hiragana",
        prefix: &[0x4E00, 0x3042],
        run: &[(0x4E00, 0x9FFF), (0x3041, 0x3096), (0x3041, 0x3096), (0x3000, 0x300F), (0x20, 0x40)],
        breakers: &[0x30A2, 0x41, 0x61, 0x30FB, 0x3097, 0x7F, 0x416],
    },
    Script {
        name: "japanese katakana",
        prefix: &[0x30A2],
        run: &[(0x4E00, 0x9FFF), (0x30A1, 0x30FC), (0x3000, 0x300F), (0x20, 0x40)],
        breakers: &[0x3042, 0x41, 0x30FD, 0x20000],
    },
    Script {
        name: "korean",
        prefix: &[0xD55C],
        run: &[(0xAC00, 0xD7A3), (0xAC00, 0xD7A3), (0x20, 0x7E), (0x0A, 0x0A)],
        breakers: &[0x3131, 0x4E00, 0x3000, 0xFF01, 0xD7A4, 0x141],
    },
];

fn run_code_point(rng: &mut Rng, s: &Script) -> u32 {
    let (lo, hi) = s.run[rng.below(s.run.len() as u64) as usize];
    rng.range(lo, hi)
}

/// A value: the prefix, then a run of `len` code points, with `breaker` at offset `at` of the run if given.
fn value(rng: &mut Rng, s: &Script, len: usize, breaker: Option<(usize, u32)>) -> Vec<u32> {
    let mut v = s.prefix.to_vec();

    for i in 0..len {
        match breaker {
            Some((at, c)) if at == i => v.push(c),
            _ => v.push(run_code_point(rng, s)),
        }
    }

    // A lone surrogate breaker must not be followed by a low surrogate; none of the runs has one.
    v
}

/// Everything, once with the vector code and once without.
fn both<T: PartialEq + std::fmt::Debug>(what: &str, f: impl Fn() -> T) -> T {
    singulink_steno::__set_simd_for_thread(true);
    let fast = f();
    singulink_steno::__set_simd_for_thread(false);
    let slow = f();
    singulink_steno::__set_simd_for_thread(true);
    assert_eq!(fast, slow, "{what}: the vector code and the scalar code disagree");
    fast
}

fn check_value(v: &[u32], what: &str) {
    let units = to_utf16(v);
    let expected = reference::encode(v);

    let bytes = both(what, || utf16::encode(&units));
    assert_eq!(bytes, expected, "{what}: encode UTF-16 {v:X?}");
    assert_eq!(both(what, || code_points::encode(v).unwrap()), expected, "{what}");
    assert_eq!(both(what, || utf16::decode(&bytes).unwrap()), units, "{what}: decode UTF-16");
    assert_eq!(both(what, || code_points::decode(&bytes).unwrap()), v, "{what}");

    // Exact-size slices, so that the blocks run up to the end of the output.
    assert_eq!(
        both(what, || {
            let mut out = vec![0u8; bytes.len()];
            utf16::encode_to_slice(&units, &mut out).map(|n| out[..n].to_vec())
        }),
        Ok(bytes.clone())
    );
    assert_eq!(
        both(what, || {
            let mut out = vec![0u16; units.len()];
            utf16::decode_to_slice(&bytes, &mut out).map(|n| out[..n].to_vec())
        }),
        Ok(units.clone())
    );

    if let Some(text) = to_string(v) {
        assert_eq!(both(what, || singulink_steno::encode(&text)), expected, "{what}: encode str");
        assert_eq!(both(what, || singulink_steno::decode(&bytes).unwrap()), text, "{what}: decode str");
        assert_eq!(both(what, || singulink_steno::encoded_len(&text)), expected.len());
        assert_eq!(
            both(what, || {
                let mut out = vec![0u8; text.len()];
                singulink_steno::decode_to_slice(&bytes, &mut out).map(|n| out[..n].to_vec())
            }),
            Ok(text.as_bytes().to_vec())
        );
    }

    // Inputs that end inside a block: every cut of the bytes decodes the same way, and every prefix of the text encodes to a prefix.
    for n in (0..bytes.len()).step_by(3) {
        let cut = &bytes[..n];
        // `both` compares the vector code with the scalar code, which the vector files test.
        let _ = both(what, || utf16::decode(cut).map_err(|e| (e.reason(), e.position())));
    }

    for n in (0..units.len()).step_by(5) {
        if n > 0 && (0xD800..=0xDBFF).contains(&units[n - 1]) {
            continue; // not a prefix in code points
        }

        let part = both(what, || utf16::encode(&units[..n]));
        assert!(bytes.starts_with(&part), "{what}: prefix {n}");
    }

    assert_eq!(both(what, || singulink_steno::is_ascii_text(&bytes)), v.iter().all(|&c| matches!(c, 0x09 | 0x0A | 0x0D | 0x20..=0x7E)));
}

#[test]
fn a_breaker_at_every_offset_around_the_blocks() {
    let mut rng = Rng::new(50);

    for s in SCRIPTS {
        for &breaker in s.breakers {
            // Offsets 0 to 40 from the start of the run, and 0 to 40 before its end, cover every position in and around the blocks of 8, 16
            // and 32.
            for at in (0..=40).chain(40..=80) {
                let v = value(&mut rng, s, 81, Some((at, breaker)));
                check_value(&v, &format!("{} with U+{breaker:04X} at {at}", s.name));
            }
        }

        // Long runs with no breaker, of every length up to 80.
        for len in 0..=80 {
            let v = value(&mut rng, s, len, None);
            check_value(&v, &format!("{} of {len}", s.name));
        }
    }
}

#[test]
fn random_mixes_of_the_scripts() {
    let mut rng = Rng::new(51);

    for _ in 0..400 {
        let mut v = Vec::new();

        while v.len() < 300 {
            let s = &SCRIPTS[rng.below(SCRIPTS.len() as u64) as usize];
            let len = rng.below(70) as usize;
            v.extend(value(&mut rng, s, len, None));

            if rng.chance(50) {
                v.push(s.breakers[rng.below(s.breakers.len() as u64) as usize]);
            }
        }

        // Drop a high surrogate followed by a low one: not a value.
        let mut clean: Vec<u32> = Vec::with_capacity(v.len());

        for c in v {
            if clean.last().is_some_and(|&p| (0xD800..=0xDBFF).contains(&p)) && (0xDC00..=0xDFFF).contains(&c) {
                continue;
            }

            clean.push(c);
        }

        check_value(&clean, "a random mix");
    }
}

#[test]
fn damaged_bytes_inside_long_runs() {
    let mut rng = Rng::new(52);

    for s in SCRIPTS {
        for _ in 0..150 {
            let v = value(&mut rng, s, 120, None);
            let mut bytes = code_points::encode(&v).unwrap();
            let at = rng.below(bytes.len() as u64) as usize;

            match rng.below(4) {
                0 => bytes[at] = 0,
                1 => bytes[at] = rng.next_u64() as u8,
                2 => {
                    bytes.remove(at);
                }
                _ => bytes.insert(at, rng.next_u64() as u8),
            }

            let expected = reference::decode(&bytes);
            let got = both(s.name, || code_points::decode(&bytes).map_err(|e| (e.reason().as_str(), e.position())));
            assert_eq!(got, expected, "{}: damaged at {at}", s.name);
            let got16 = both(s.name, || utf16::decode(&bytes).map_err(|e| (e.reason().as_str(), e.position())));
            assert_eq!(got16.map(|u| u.len()), expected.map(|c| to_utf16(&c).len()));
        }
    }
}

#[test]
fn streaming_over_long_runs() {
    let mut rng = Rng::new(53);

    for s in SCRIPTS {
        let v = value(&mut rng, s, 400, None);
        let units = to_utf16(&v);
        let bytes = reference::encode(&v);

        for piece in [1, 7, 15, 16, 17, 31, 33, 100] {
            let out = both(s.name, || {
                let mut enc = Encoder::new();
                let mut out = Vec::new();

                for chunk in units.chunks(piece) {
                    enc.encode_utf16(chunk, &mut out).unwrap();
                }

                enc.finish(&mut out);
                out
            });
            assert_eq!(out, bytes, "{}: encode in pieces of {piece}", s.name);

            let out = both(s.name, || {
                let mut dec = Decoder::new();
                let mut out = Vec::new();

                for chunk in bytes.chunks(piece) {
                    dec.decode_utf16(chunk, &mut out).unwrap();
                }

                dec.finish().unwrap();
                out
            });
            assert_eq!(out, units, "{}: decode in pieces of {piece}", s.name);
        }
    }
}

#[test]
fn the_vector_code_runs_where_it_should() {
    let active = singulink_steno::__set_simd_for_thread(true);
    println!("vector code active: {active}");

    let wanted = std::env::var_os("STENO_SIMD").is_none_or(|v| v != "0");

    #[cfg(all(feature = "simd", target_arch = "x86_64"))]
    assert_eq!(active, wanted && std::arch::is_x86_feature_detected!("ssse3"));

    #[cfg(all(feature = "simd", all(target_arch = "aarch64", target_endian = "little")))]
    assert_eq!(active, wanted);

    #[cfg(not(feature = "simd"))]
    {
        let _ = wanted;
        assert!(!active);
    }
}

#[test]
fn is_ascii_text_on_long_random_bytes() {
    let mut rng = Rng::new(54);

    for _ in 0..3000 {
        let len = rng.below(100) as usize;
        let mut bytes: Vec<u8> = (0..len).map(|_| [0x02, 0x03, 0x05, 0x07, 0x40, 0x65][rng.below(6) as usize]).collect();

        if rng.chance(70) && len > 0 {
            let at = rng.below(len as u64) as usize;
            bytes[at] = rng.range(0x00, 0x70) as u8;
        }

        let expected = bytes.iter().all(|&b| matches!(b, 0x02 | 0x03 | 0x05 | 0x07..=0x65));
        assert_eq!(both("is_ascii_text", || singulink_steno::is_ascii_text(&bytes)), expected, "{bytes:02X?}");
    }
}

#[test]
fn short_values_of_every_length() {
    let mut rng = Rng::new(55);

    for s in SCRIPTS {
        for len in 0..=40 {
            for k in 0..4 {
                let breaker = if k % 2 == 1 && len > 0 {
                    Some((rng.below(len as u64) as usize, s.breakers[rng.below(s.breakers.len() as u64) as usize]))
                } else {
                    None
                };

                let mut v = value(&mut rng, s, len, breaker);

                // Half of them start from the initial state, as most values do.
                if k >= 2 {
                    v.drain(..s.prefix.len());
                }

                check_value(&v, &format!("{} of {len}", s.name));
            }
        }
    }
}
