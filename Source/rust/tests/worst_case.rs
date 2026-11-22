//! Worst-case time: text that reaches a new state at almost every code point, which hops among many windows (each code point from a random
//! window of a set), against ordinary text in one window. The time per code point must stay within a modest factor of ordinary text.
//!
//! `STENO_WORST_CASE=1 cargo test --release --test worst_case -- --nocapture` prints the table (nanoseconds per code point, encode and
//! decode) for hopping among 3, 20, 104 and 400 windows. The default run checks one generous bound in a few milliseconds of work.

mod common;

use std::time::Instant;

use common::Rng;

/// The windows from U+1000 on that are outside the CJK and Hangul ranges (so the state stays in the general context), in order.
fn windows(count: usize) -> Vec<u32> {
    let mut out = Vec::new();
    let mut c = 0x1000;

    while out.len() < count {
        let cjk = (0x3000..0xA000).contains(&c) && !(0x3100..0x3400).contains(&c)
            || (0xAC00..0xE000).contains(&c)
            || (0x2_0000..0x4_0000).contains(&c);

        if !cjk && !(0x2000..0x2100).contains(&c) {
            out.push(c);
        }

        c += 0x80;
    }

    out
}

/// `len` code points, each from a random one of the windows.
fn hopping(rng: &mut Rng, windows: &[u32], len: usize) -> String {
    (0..len)
        .map(|_| {
            let w = windows[rng.below(windows.len() as u64) as usize];
            char::from_u32(w + rng.below(0x80) as u32).unwrap_or(' ')
        })
        .collect()
}

/// Ordinary text: Cyrillic words and spaces.
fn ordinary(rng: &mut Rng, len: usize) -> String {
    (0..len).map(|_| if rng.chance(15) { ' ' } else { char::from_u32(0x430 + rng.below(32) as u32).unwrap() }).collect()
}

/// The best time per code point, in nanoseconds, of encoding and of decoding `text` (a few repetitions of each).
fn time(text: &str, reps: usize) -> (f64, f64) {
    let n = text.chars().count() as f64;
    let bytes = singulink_steno::encode(text);
    let (mut enc, mut dec) = (f64::MAX, f64::MAX);

    for _ in 0..reps {
        let t = Instant::now();
        std::hint::black_box(singulink_steno::encode(std::hint::black_box(text)));
        enc = enc.min(t.elapsed().as_nanos() as f64 / n);

        let t = Instant::now();
        std::hint::black_box(singulink_steno::decode(std::hint::black_box(&bytes)).unwrap());
        dec = dec.min(t.elapsed().as_nanos() as f64 / n);
    }

    (enc, dec)
}

#[test]
fn hopping_among_windows_stays_within_a_bound_of_ordinary_text() {
    let mut rng = Rng::new(60);
    let long = std::env::var_os("STENO_WORST_CASE").is_some();
    let len = if long { 200_000 } else { 20_000 };
    let (base_enc, base_dec) = time(&ordinary(&mut rng, len), 5);

    if long {
        println!("ordinary text: encode {base_enc:.1} ns, decode {base_dec:.1} ns per code point");
    }

    let mut worst: f64 = 0.0;

    for count in if long { vec![3, 20, 104, 400] } else { vec![400] } {
        let text = hopping(&mut rng, &windows(count), len);
        let bytes = singulink_steno::encode(&text);
        assert_eq!(singulink_steno::decode(&bytes).unwrap(), text);
        let (enc, dec) = time(&text, 3);
        worst = worst.max(enc).max(dec);

        if long {
            println!("{count} windows: encode {enc:.1} ns, decode {dec:.1} ns per code point");
        }
    }

    // Generous, so that a debug build and a busy machine pass: the point is that no layout is built per code point.
    let bound = if cfg!(debug_assertions) { 20_000.0 } else { 2_000.0 };
    assert!(worst < bound, "hopping text takes {worst:.0} ns per code point (bound {bound})");
}
