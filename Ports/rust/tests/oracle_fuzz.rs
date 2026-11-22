//! Differential fuzzing against a second, independent oracle: a port of the clean-room C# codec of the repository
//! (`Tests/Singulink.Steno.Tests/ReferenceSteno.cs`), checked against the vectors first. By default a short run; `STENO_FUZZ=n` multiplies
//! the budgets and runs the exhaustive tests in full, `STENO_FUZZ_SEED=s` changes the seed.

mod oracle;

use oracle::*;
use singulink_steno::{self as steno, code_points, utf16, DecodeReason, DecodeToSliceError, Decoder, Encoder};

fn reason_name(r: DecodeReason) -> &'static str {
    r.as_str()
}

#[test]
fn oracle_matches_vectors() {
    let mut paths = vec![repo_root().join("TestVectors/steno-v1-vectors.txt")];

    if let Ok(p) = std::env::var("STENO_LARGE_VECTORS") {
        paths.push(p.into());
    }

    let mut o = Oracle::new();
    let mut n = 0;

    for path in paths {
        for v in read_vectors(&path) {
            n += 1;
            match v {
                Vector::Valid { name, cps, bytes } => {
                    assert_eq!(o.encode(&cps).as_deref(), Some(&bytes[..]), "{name}");
                    let d: Vec<u32> = o.decode(&bytes).unwrap().into_iter().map(|x| x.0).collect();
                    assert_eq!(d, cps, "{name}");
                }
                Vector::Invalid { name, bytes, reason } => {
                    let e = o.decode(&bytes).unwrap_err();
                    assert_eq!(e.reason, reason, "{name}");
                }
                Vector::NotAValue { name, cps } => assert!(o.encode(&cps).is_none(), "{name}"),
            }
        }
    }

    eprintln!("oracle checked on {n} vectors");
}

/// A buffer of `len` elements followed by guard elements; returns whether the guard is intact.
fn guarded<T: Copy + PartialEq>(len: usize, fill: T, guard: T, f: impl FnOnce(&mut [T])) -> bool {
    let mut buf = vec![fill; len + 64];
    for g in &mut buf[len..] {
        *g = guard;
    }
    f(&mut buf[..len]);
    buf[len..].iter().all(|&g| g == guard)
}

/// Checks one value through every form of the API against the oracle's bytes.
fn check_value(o: &mut Oracle, rng: &mut Rng, cps: &[u32]) {
    let want = o.encode(cps).expect("a value");
    let units = to_utf16(cps);
    let ctx = || format!("cps {}", cps_hex(&cps[..cps.len().min(200)]));

    // Encoding.
    assert_eq!(code_points::encode(cps).unwrap(), want, "code_points::encode {}", ctx());
    assert_eq!(utf16::encode(&units), want, "utf16::encode {}", ctx());
    assert_eq!(utf16::encoded_len(&units), want.len(), "utf16::encoded_len {}", ctx());
    assert!(utf16::max_encoded_len(units.len()) >= want.len());

    for len in [want.len(), want.len().saturating_sub(1), want.len() + 1, want.len().saturating_sub(17)] {
        let mut got = None;
        let ok = guarded(len, 0xAAu8, 0x5Au8, |buf| {
            got = Some(utf16::encode_to_slice(&units, buf).map(|n| (n, buf[..n.min(buf.len())].to_vec())))
        });
        assert!(ok, "utf16::encode_to_slice wrote past the end of a slice of {len} {}", ctx());
        match got.unwrap() {
            Ok((n, b)) => {
                assert!(len >= want.len());
                assert_eq!(n, want.len());
                assert_eq!(b, want, "utf16::encode_to_slice {}", ctx());
            }
            Err(_) => assert!(len < want.len(), "utf16::encode_to_slice failed with room {}", ctx()),
        }
    }

    let text = to_string(cps);

    if let Some(text) = &text {
        assert_eq!(steno::encode(text), want, "encode {}", ctx());
        assert_eq!(steno::encoded_len(text), want.len());
        assert!(steno::max_encoded_len(text.len()) >= want.len(), "max_encoded_len {}", ctx());

        for len in [want.len(), want.len().saturating_sub(1), want.len().saturating_sub(16), want.len() + 3] {
            let mut got = None;
            let ok = guarded(len, 0xAAu8, 0x5Au8, |buf| got = Some(steno::encode_to_slice(text, buf).map(|n| (n, buf[..n].to_vec()))));
            assert!(ok, "encode_to_slice wrote past the end of a slice of {len} {}", ctx());
            match got.unwrap() {
                Ok((n, b)) => {
                    assert_eq!(n, want.len());
                    assert_eq!(b, want, "encode_to_slice {}", ctx());
                }
                Err(_) => assert!(len < want.len(), "encode_to_slice failed with room {}", ctx()),
            }
        }
    }

    // Decoding.
    assert_eq!(code_points::decode(&want).unwrap(), cps, "code_points::decode {}", ctx());
    assert_eq!(utf16::decode(&want).unwrap(), units, "utf16::decode {}", ctx());
    assert_eq!(utf16::decoded_len(&want).unwrap(), units.len());
    assert!(utf16::max_decoded_len(want.len()) >= units.len());

    for len in [units.len(), units.len().saturating_sub(1), units.len().saturating_sub(16), units.len() + 5] {
        let mut got = None;
        let ok = guarded(len, 0xAAAAu16, 0x5A5Au16, |buf| got = Some(utf16::decode_to_slice(&want, buf).map(|n| buf[..n].to_vec())));
        assert!(ok, "utf16::decode_to_slice wrote past the end of a slice of {len} {}", ctx());
        match got.unwrap() {
            Ok(u) => assert_eq!(u, units, "utf16::decode_to_slice {}", ctx()),
            Err(DecodeToSliceError::BufferTooSmall) => assert!(len < units.len(), "too small with room {}", ctx()),
            Err(e) => panic!("{e:?} {}", ctx()),
        }
    }

    let first_surrogate = o.decode_str(&want);

    match (&text, first_surrogate) {
        (Some(text), Ok(t2)) => {
            assert_eq!(&t2, text);
            let s = steno::decode(&want).unwrap();
            assert!(std::str::from_utf8(s.as_bytes()).is_ok(), "decode wrote invalid UTF-8 {}", ctx());
            assert_eq!(&s, text, "decode {}", ctx());
            assert_eq!(steno::decoded_len(&want).unwrap(), text.len());
            assert!(steno::max_decoded_len(want.len()) >= text.len(), "max_decoded_len {}", ctx());

            for len in [text.len(), text.len().saturating_sub(1), text.len().saturating_sub(3), text.len() + 7] {
                let mut got = None;
                let ok = guarded(len, 0xAAu8, 0x5Au8, |buf| got = Some(steno::decode_to_slice(&want, buf).map(|n| buf[..n].to_vec())));
                assert!(ok, "decode_to_slice wrote past the end of a slice of {len} {}", ctx());
                match got.unwrap() {
                    Ok(u) => assert_eq!(u, text.as_bytes(), "decode_to_slice {}", ctx()),
                    Err(DecodeToSliceError::BufferTooSmall) => assert!(len < text.len(), "too small with room {}", ctx()),
                    Err(e) => panic!("{e:?} {}", ctx()),
                }
            }
        }
        (None, Err((r, at))) => {
            assert_eq!(r, "surrogate");
            let mut s = String::from("keep");
            let e = steno::decode_to_string(&want, &mut s).unwrap_err();
            assert_eq!((e.reason(), e.position()), (DecodeReason::Surrogate, at), "decode {}", ctx());
            assert_eq!(s, "keep");
            let e = steno::decoded_len(&want).unwrap_err();
            assert_eq!((e.reason(), e.position()), (DecodeReason::Surrogate, at));
            let mut buf = vec![0u8; want.len() * 4];
            match steno::decode_to_slice(&want, &mut buf) {
                Err(DecodeToSliceError::Decode(e)) => assert_eq!((e.reason(), e.position()), (DecodeReason::Surrogate, at)),
                other => panic!("{other:?}"),
            }
        }
        (a, b) => panic!("oracle disagrees with itself {a:?} {b:?}"),
    }

    // Streaming: encoder over random splits of UTF-16 units.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    let mut i = 0;
    while i < units.len() {
        let n = (rng.below(40) as usize).min(units.len() - i);
        enc.encode_utf16(&units[i..i + n], &mut out).unwrap();
        i += n;
    }
    enc.finish(&mut out);
    assert_eq!(out, want, "Encoder utf16 pieces {}", ctx());

    // Mixed pieces by code points: str pieces where possible, else UTF-16 or code points.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    let mut i = 0;
    while i < cps.len() {
        let n = (rng.below(50) as usize).min(cps.len() - i);
        let piece = &cps[i..i + n];
        match (rng.below(3), to_string(piece)) {
            (0, Some(s)) => enc.encode(&s, &mut out),
            (1, _) => enc.encode_utf16(&to_utf16(piece), &mut out).unwrap(),
            _ => enc.encode_code_points(piece, &mut out).unwrap(),
        }
        i += n;
    }
    enc.finish(&mut out);
    assert_eq!(out, want, "Encoder mixed pieces {}", ctx());

    // Decoder over random splits.
    let mut dec = Decoder::new();
    let mut got = Vec::new();
    let mut i = 0;
    while i < want.len() {
        let n = rng.piece(4, 80).min(want.len() - i);
        dec.decode_utf16(&want[i..i + n], &mut got).unwrap();
        i += n;
    }
    dec.finish().unwrap();
    assert_eq!(got, units, "Decoder utf16 pieces {}", ctx());

    if let Some(text) = &text {
        let mut dec = Decoder::new();
        let mut got = String::new();
        let mut i = 0;
        while i < want.len() {
            let n = rng.piece(4, 80).min(want.len() - i);
            dec.decode(&want[i..i + n], &mut got).unwrap();
            i += n;
        }
        dec.finish().unwrap();
        assert!(std::str::from_utf8(got.as_bytes()).is_ok());
        assert_eq!(&got, text, "Decoder str pieces {}", ctx());
    }
}

#[test]
fn fuzz_valid_values() {
    let mut rng = Rng(seed());
    let mut o = Oracle::new();
    let n = budget(3000);
    let mut total = 0;

    for i in 0..n {
        let surrogates = i % 3 == 0;
        let cps = fast_value(&mut rng, surrogates);
        total += cps.len();
        check_value(&mut o, &mut rng, &cps);

        // The same value after a prefix of 0..20 ASCII, to move every run against the blocks.
        let mut shifted: Vec<u32> = (0..rng.below(20)).map(|_| 0x61).collect();
        shifted.extend_from_slice(&cps);
        fix_value(&mut shifted, surrogates);
        check_value(&mut o, &mut rng, &shifted);
    }

    eprintln!("{n} values, {total} code points");
}

/// Every breaker at every offset around the block sizes, in runs of every kind.
#[test]
fn breaker_at_every_offset() {
    let mut rng = Rng(seed() ^ 77);
    let mut o = Oracle::new();
    let prefixes: &[&[u32]] = &[
        &[],
        &[0x416, 0x416],
        &[0x915, 0x915],
        &[0x1F600, 0x1F601],
        &[0xFF80, 0xFF81],
        &[0xFE80, 0xFE81],
        &[0x4E00],
        &[0x4E00, 0x3042],
        &[0x4E00, 0x30A2],
        &[0xAC00],
        &[0x10F80, 0x10F81],
        &[0xAB80, 0xAB81],
    ];
    let kinds = [0u32, 1, 2, 3, 4, 5, 6, 7];
    let breakers = [
        0x01, 0x7F, 0xE9, 0x416, 0x1F600, 0xD800, 0xDC00, 0xFEFF, 0xFFFE, 0x3041, 0x30A1, 0x41, 0x4E00, 0xAC00, 0xFF01, 0x10FFFF, 0x80,
        0x800,
    ];
    let mut count = 0;

    for &prefix in prefixes {
        for len in [20usize, 47, 70, 140] {
            for at in 0..len {
                if at % 3 != 0 && at > 40 {
                    continue;
                }
                let kind = rng.pick(&kinds);
                let breaker = rng.pick(&breakers);
                let mut cps = prefix.to_vec();
                // Runs inside the prefix's layout.
                let main = prefix.first().copied().unwrap_or(0x61);
                for j in 0..len {
                    let c = if j == at {
                        breaker
                    } else {
                        match kind {
                            0 | 1 => rng.range(0x20, 0x7E),
                            2 | 3 if main >= 0x80 && !(0x3000..0xAC00).contains(&main) && main != 0xAC00 => {
                                (main & !0x7F) + rng.below(0x80) as u32
                            }
                            4..=6 if main == 0x4E00 => {
                                if rng.chance(50) {
                                    rng.range(0x4E00, 0x9FFF)
                                } else {
                                    prefix.last().copied().unwrap()
                                }
                            }
                            7 if main == 0xAC00 => rng.range(0xAC00, 0xD7A3),
                            _ => rng.range(0x20, 0x7E),
                        }
                    };
                    cps.push(c);
                }
                fix_value(&mut cps, true);
                check_value(&mut o, &mut rng, &cps);
                count += 1;
            }
        }
    }

    eprintln!("{count} values");
}

fn check_bytes(o: &mut Oracle, bytes: &[u8]) {
    let want = o.decode(bytes);
    let ctx = || format!("bytes {}", hex(&bytes[..bytes.len().min(300)]));

    let got = code_points::decode(bytes);
    match (&want, &got) {
        (Ok(w), Ok(g)) => {
            let w: Vec<u32> = w.iter().map(|x| x.0).collect();
            assert_eq!(&w, g, "code_points::decode {}", ctx());
            // Uniqueness: re-encoding gives the bytes back.
            assert_eq!(code_points::encode(g).unwrap(), bytes, "re-encode {}", ctx());
        }
        (Err(w), Err(g)) => assert_eq!((w.reason, w.pos), (reason_name(g.reason()), g.position()), "code_points::decode {}", ctx()),
        _ => panic!("code_points::decode {want:?} vs {got:?} {}", ctx()),
    }

    let got16 = utf16::decode(bytes);
    match (&want, &got16) {
        (Ok(w), Ok(g)) => assert_eq!(&to_utf16(&w.iter().map(|x| x.0).collect::<Vec<_>>()), g, "{}", ctx()),
        (Err(w), Err(g)) => assert_eq!((w.reason, w.pos), (reason_name(g.reason()), g.position()), "utf16::decode {}", ctx()),
        _ => panic!("utf16::decode {}", ctx()),
    }

    match (&want, utf16::decoded_len(bytes)) {
        (Ok(w), Ok(n)) => assert_eq!(n, to_utf16(&w.iter().map(|x| x.0).collect::<Vec<_>>()).len()),
        (Err(w), Err(g)) => assert_eq!((w.reason, w.pos), (reason_name(g.reason()), g.position()), "utf16::decoded_len {}", ctx()),
        _ => panic!("utf16::decoded_len {}", ctx()),
    }

    let mut buf = vec![0u16; bytes.len() * 2];
    match (&want, utf16::decode_to_slice(bytes, &mut buf)) {
        (Ok(_), Ok(_)) => {}
        (Err(w), Err(DecodeToSliceError::Decode(g))) => {
            assert_eq!((w.reason, w.pos), (reason_name(g.reason()), g.position()), "utf16::decode_to_slice {}", ctx())
        }
        (w, g) => panic!("utf16::decode_to_slice {w:?} {g:?} {}", ctx()),
    }

    let wants = o.decode_str(bytes);
    let mut s = String::from("x");
    let got = steno::decode_to_string(bytes, &mut s);
    match (&wants, &got) {
        (Ok(w), Ok(())) => {
            assert!(std::str::from_utf8(s.as_bytes()).is_ok(), "invalid UTF-8 in a String {}", ctx());
            assert_eq!(&s[1..], w, "decode {}", ctx());
        }
        (Err(w), Err(g)) => {
            assert_eq!(*w, (reason_name(g.reason()), g.position()), "decode {}", ctx());
            assert_eq!(s, "x");
        }
        _ => panic!("decode {wants:?} vs {got:?} {}", ctx()),
    }

    match (&wants, steno::decoded_len(bytes)) {
        (Ok(w), Ok(n)) => assert_eq!(n, w.len()),
        (Err(w), Err(g)) => assert_eq!(*w, (reason_name(g.reason()), g.position()), "decoded_len {}", ctx()),
        (w, g) => panic!("decoded_len {w:?} {g:?} {}", ctx()),
    }

    let mut buf = vec![0u8; bytes.len() * 4];
    match (&wants, steno::decode_to_slice(bytes, &mut buf)) {
        (Ok(w), Ok(n)) => {
            assert!(std::str::from_utf8(&buf[..n]).is_ok());
            assert_eq!(&buf[..n], w.as_bytes())
        }
        (Err(w), Err(DecodeToSliceError::Decode(g))) => {
            assert_eq!(*w, (reason_name(g.reason()), g.position()), "decode_to_slice {}", ctx())
        }
        (w, g) => panic!("decode_to_slice {w:?} {g:?} {}", ctx()),
    }

    // The ASCII test agrees with decoding.
    let ascii = match &want {
        Ok(w) => w.iter().all(|&(c, _)| matches!(c, 0x09 | 0x0A | 0x0D | 0x20..=0x7E)),
        Err(_) => false,
    };
    assert_eq!(steno::is_ascii_text(bytes), ascii, "is_ascii_text {}", ctx());
}

fn check_bytes_streaming(o: &mut Oracle, rng: &mut Rng, bytes: &[u8]) {
    let want = o.decode(bytes);
    let mut dec = Decoder::new();
    let mut got = Vec::new();
    let mut i = 0;
    let mut err = None;

    while i < bytes.len() {
        let n = rng.piece(3, 50).min(bytes.len() - i);
        let before = got.clone();
        if let Err(e) = dec.decode_code_points(&bytes[i..i + n], &mut got) {
            assert_eq!(got, before, "a failed call changed the output");
            // The decoder is as it was: feeding the same piece fails the same way.
            let e2 = dec.clone().decode_code_points(&bytes[i..i + n], &mut got).unwrap_err();
            assert_eq!(e, e2);
            err = Some(e);
            break;
        }
        i += n;
    }

    let res = match err {
        Some(e) => Err(e),
        None => dec.finish().map(|()| got),
    };

    match (&want, &res) {
        (Ok(w), Ok(g)) => assert_eq!(&w.iter().map(|x| x.0).collect::<Vec<_>>(), g, "Decoder {}", hex(bytes)),
        (Err(w), Err(g)) => assert_eq!((w.reason, w.pos), (reason_name(g.reason()), g.position()), "Decoder {}", hex(bytes)),
        _ => panic!("Decoder {want:?} vs {res:?} {}", hex(bytes)),
    }
}

#[test]
fn fuzz_damaged_bytes() {
    let mut rng = Rng(seed() ^ 0xDA);
    let mut o = Oracle::new();
    let n = budget(6000);

    for i in 0..n {
        let cps = fast_value(&mut rng, i % 2 == 0);
        let mut bytes = o.encode(&cps).unwrap();
        let edits = 1 + rng.below(3);

        for _ in 0..edits {
            if bytes.is_empty() {
                bytes.push(rng.below(256) as u8);
                continue;
            }
            let at = rng.below(bytes.len() as u64) as usize;
            match rng.below(7) {
                0 => bytes[at] = rng.below(256) as u8,
                1 => bytes[at] = rng.pick(&[0x00, 0x01, 0x05, 0x07, 0x08, 0xFE, 0xFF, 0x66, 0x65]),
                2 => bytes.truncate(at),
                3 => bytes.insert(at, rng.below(256) as u8),
                4 => {
                    bytes.remove(at);
                }
                5 => bytes[at] ^= 1 << rng.below(8),
                _ => bytes[at] = bytes[at].wrapping_add(1),
            }
        }

        check_bytes(&mut o, &bytes);
        check_bytes_streaming(&mut o, &mut rng, &bytes);
    }
}

#[test]
fn truncation_at_every_position() {
    let mut rng = Rng(seed() ^ 0x7C);
    let mut o = Oracle::new();

    for i in 0..budget(150) {
        let cps = fast_value(&mut rng, i % 2 == 0);
        let bytes = o.encode(&cps).unwrap();
        let step = (bytes.len() / 300).max(1);
        let mut at = 0;
        while at <= bytes.len() {
            check_bytes(&mut o, &bytes[..at]);
            at += step;
        }
    }
}

/// Every byte value at every position of short encodings from many states, and all two-byte tails after state-setting prefixes.
#[test]
fn every_byte_at_every_position() {
    let mut o = Oracle::new();
    let prefixes: &[&[u32]] = &[
        &[],
        &[0x416, 0x438],
        &[0x1000, 0x1001, 0x1200],
        &[0x1230],
        &[0x4E00],
        &[0x4E00, 0x3042],
        &[0x4E00, 0x30A2],
        &[0xD55C],
        &[0x1F600, 0x1F601],
        &[0x10FF80, 0x10FF81],
        &[0xD800],
        &[0xDBFF],
        &[0x40000, 0x40001, 0xFF00],
        &[0x4E00, 0x141],
        &[0xAC00, 0x1F600],
        &[0xE000, 0xE001, 0x10000],
    ];

    let mut count = 0;

    for &p in prefixes {
        let base = o.encode(p).unwrap();

        // All tails of 1 and 2 bytes.
        for a in 0..=255u8 {
            let mut b = base.clone();
            b.push(a);
            check_bytes(&mut o, &b);
            count += 1;

            // All second bytes in the long run, every fifteenth by default.
            for c in (0..=255u8).step_by(if long_run() { 1 } else { 15 }) {
                let mut b2 = b.clone();
                b2.push(c);
                check_bytes(&mut o, &b2);
                count += 1;
            }
        }
    }

    eprintln!("{count} byte strings");
}

/// Long values (about a megabyte) through the one-shot and streaming forms.
#[test]
fn long_values() {
    let mut rng = Rng(seed() ^ 0x10);
    let mut o = Oracle::new();

    for kind in [0u32, 2, 3, 4, 5, 6, 7, 10, 11] {
        let mut cps = Vec::new();
        while cps.len() < if long_run() { 300_000 } else { 5_000 } {
            let n = run_length(&mut rng);
            push_run(&mut rng, &mut cps, kind, n);
            if rng.chance(30) {
                cps.push(rng.pick(EDGES));
            }
        }
        // U+FEFF and U+FFFE at the start.
        cps.insert(0, if kind % 2 == 0 { 0xFEFF } else { 0xFFFE });
        fix_value(&mut cps, true);
        check_value(&mut o, &mut rng, &cps);
    }
}
