//! Tests sized for Miri (`cargo +nightly miri test --test miri`, with `RUSTFLAGS="-C target-feature=+ssse3"` on x86-64, or
//! `--target aarch64-unknown-linux-gnu` for the NEON code): short values that still reach every kernel (runs of 4..80 code units, UTF-8
//! runs of 64 bytes or more, wide runs of 32 or more), through every form, with exact output slices.

mod oracle;

use oracle::*;
use singulink_steno::{self as steno, utf16};

#[test]
fn miri_kernels() {
    let mut rng = Rng(seed() ^ 0x313);
    let mut o = Oracle::new();
    let prefixes: &[&[u32]] = &[&[], &[0x416, 0x416], &[0x4E00], &[0x4E00, 0x3042], &[0x4E00, 0x30A2], &[0xAC00], &[0xFF80, 0xFF81]];
    let kinds: &[u32] = &[0, 3, 3, 4, 5, 6, 7, 1];
    let n = if cfg!(miri) { 24 * budget(300) / 2 } else { 2000 };

    for i in 0..n {
        let p = prefixes[i % prefixes.len()];
        let mut cps = p.to_vec();
        let kind = match p.first() {
            None => rng.pick(&[0u32, 1]),
            Some(0x416) | Some(0xFF80) => 3,
            Some(0x4E00) => *rng.pick(&[&4u32, &5, &6]),
            Some(0xAC00) => 7,
            _ => rng.pick(kinds),
        };
        let len = rng.pick(&[4usize, 9, 15, 16, 17, 33, 41, 70, 80]);
        if kind == 3 {
            let w = p[0] & !0x7F;
            for _ in 0..len {
                cps.push(if rng.chance(80) { w + rng.below(0x80) as u32 } else { 0x20 });
            }
        } else {
            push_run(&mut rng, &mut cps, kind, len);
        }
        if rng.chance(50) {
            let at = rng.below(cps.len() as u64) as usize;
            cps.insert(at, rng.pick(&[0x01, 0xD800, 0x1F600, 0x3041, 0xFF01, 0xE9]));
        }
        fix_value(&mut cps, true);

        let want = o.encode(&cps).unwrap();
        let units = to_utf16(&cps);

        let mut buf = vec![0u8; want.len()];
        assert_eq!(utf16::encode_to_slice(&units, &mut buf), Ok(want.len()));
        assert_eq!(buf, want, "{}", cps_hex(&cps));
        let mut out = vec![0u16; units.len()];
        assert_eq!(utf16::decode_to_slice(&want, &mut out), Ok(units.len()));
        assert_eq!(out, units);
        assert_eq!(utf16::encode(&units), want);
        assert_eq!(utf16::decode(&want).unwrap(), units);

        if let Some(s) = to_string(&cps) {
            let mut buf = vec![0u8; want.len()];
            assert_eq!(steno::encode_to_slice(&s, &mut buf), Ok(want.len()));
            assert_eq!(buf, want);
            assert_eq!(steno::encode(&s), want);
            let d = steno::decode(&want).unwrap();
            assert!(std::str::from_utf8(d.as_bytes()).is_ok());
            assert_eq!(d, s);
            let mut out = vec![0u8; s.len()];
            assert_eq!(steno::decode_to_slice(&want, &mut out), Ok(s.len()));
            assert_eq!(out, s.as_bytes());
            assert_eq!(steno::decoded_len(&want), Ok(s.len()));
            assert_eq!(steno::encoded_len(&s), want.len());
        }

        assert!(steno::is_ascii_text(&want) == cps.iter().all(|&c| matches!(c, 9 | 10 | 13 | 0x20..=0x7E)));
    }
}

/// Counts the disagreements with the oracle per function instead of stopping at the first (for targets where many fail at once, such as
/// aarch64_be under Miri).
#[test]
fn miri_disagreements_per_function() {
    let mut o = Oracle::new();
    let mut bad: std::collections::BTreeMap<&str, usize> = Default::default();
    let mut first: std::collections::BTreeMap<&str, String> = Default::default();
    let mut note = |name: &'static str, ok: bool, what: String| {
        if !ok {
            *bad.entry(name).or_default() += 1;
            first.entry(name).or_insert(what);
        }
    };
    let texts: Vec<Vec<u32>> = vec![
        "Hello, world. This is a line of ASCII text.\r\n".chars().map(u32::from).collect(),
        "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ!".chars().map(u32::from).collect(),
        "Москва — столица России, крупнейший по численности населения город страны".chars().map(u32::from).collect(),
        "東京都は日本の首都であり、人口が最も多い都市です。ひらがなとカタカナも使います。".chars().map(u32::from).collect(),
        "カタカナカタカナカタカナカタカナカタカナカタカナカタカナカタカナ".chars().map(u32::from).collect(),
        "大韓民国의 수도는 서울특별시이며 가장 큰 도시입니다 한국어 문장입니다 한국어 문장입니다".chars().map(u32::from).collect(),
        "中文文本中文文本中文文本中文文本中文文本中文文本中文文本中文文本中文文本".chars().map(u32::from).collect(),
    ];

    for cps in &texts {
        let want = o.encode(cps).unwrap();
        let s = to_string(cps).unwrap();
        let units = to_utf16(cps);
        let short = s.chars().take(12).collect::<String>();
        note("encode", steno::encode(&s) == want, short.clone());
        note("utf16::encode", utf16::encode(&units) == want, short.clone());
        note("decode", steno::decode(&want).as_deref() == Ok(s.as_str()), short.clone());
        note("utf16::decode", utf16::decode(&want).as_deref() == Ok(&units[..]), short.clone());
        note("is_ascii_text", steno::is_ascii_text(&want) == cps.iter().all(|&c| matches!(c, 9 | 10 | 13 | 0x20..=0x7E)), short.clone());
    }

    eprintln!("disagreements: {bad:?}\nfirst: {first:?}");
    assert!(bad.is_empty(), "disagreements: {bad:?}");
}

/// The kernels of 1-byte codes only (no byte shuffles, which Miri cannot run on aarch64): UTF-16 in and out, code points out, and
/// `is_ascii_text`, on runs with one code point that breaks them at every offset.
#[test]
fn miri_singles_kernels_only() {
    let mut o = Oracle::new();
    let mut bad: std::collections::BTreeMap<&str, usize> = Default::default();
    let mut first: std::collections::BTreeMap<&str, String> = Default::default();
    let mut total = 0;

    for (prefix, lo, hi) in [(&[][..], 0x20u32, 0x7Eu32), (&[0x416u32, 0x416][..], 0x400, 0x47F)] {
        for len in [5usize, 12, 20, 33] {
            for at in 0..len {
                let mut cps = prefix.to_vec();
                for j in 0..len {
                    cps.push(if j == at { 0x1F600 } else { lo + (j as u32 * 7) % (hi - lo + 1) });
                }
                let want = o.encode(&cps).unwrap();
                let units = to_utf16(&cps);
                let tag = cps_hex(&cps);
                total += 1;
                let mut check = |name: &'static str, ok: bool| {
                    if !ok {
                        *bad.entry(name).or_default() += 1;
                        first.entry(name).or_insert(tag.clone());
                    }
                };
                check("utf16::encode", utf16::encode(&units) == want);
                check("utf16::decode", utf16::decode(&want).as_deref() == Ok(&units[..]));
                check("utf16::decoded_len", utf16::decoded_len(&want) == Ok(units.len()));
                check("is_ascii_text", !steno::is_ascii_text(&want));
            }
        }
    }

    eprintln!("{total} values; disagreements: {bad:?}\nfirst: {first:?}");
    assert!(bad.is_empty(), "disagreements: {bad:?}");
}
