//! Fuzz-style tests with a short fixed budget: random, cut-off and mutated bytes never panic, and every byte string that is accepted is the
//! encoding of what it decodes to.

mod common;

use common::{random_value, reference, Rng};
use singulink_steno::{code_points, utf16, Decoder};

/// Checks one byte string against every decoder.
fn check(bytes: &[u8]) {
    match code_points::decode(bytes) {
        Ok(v) => {
            assert_eq!(code_points::encode(&v).unwrap(), bytes, "accepted bytes re-encode to themselves");
            assert_eq!(reference::decode(bytes), Ok(v.clone()));
            assert!(!bytes.contains(&0));

            match v.iter().map(|&c| char::from_u32(c)).collect::<Option<String>>() {
                Some(s) => assert_eq!(singulink_steno::decode(bytes).unwrap(), s),
                None => assert!(singulink_steno::decode(bytes).is_err()),
            }
        }
        Err(e) => {
            assert!(e.position() < bytes.len());
            let (reason, at) = reference::decode(bytes).unwrap_err();
            assert_eq!(e.reason().as_str(), reason);
            assert_eq!(e.position(), at);
            assert_eq!(utf16::decode(bytes).unwrap_err(), e);
        }
    }

    // The streaming decoder agrees, split in two.
    let mid = bytes.len() / 2;
    let mut dec = Decoder::new();
    let mut out = Vec::new();
    let streamed = dec
        .decode_code_points(&bytes[..mid], &mut out)
        .and_then(|()| dec.decode_code_points(&bytes[mid..], &mut out))
        .and_then(|()| dec.finish());
    assert_eq!(streamed.map(|()| out), code_points::decode(bytes));

    let _ = singulink_steno::is_ascii_text(bytes);
}

#[test]
fn random_bytes() {
    let mut rng = Rng::new(30);

    for _ in 0..20_000 {
        let len = rng.below(12) as usize;
        let bytes: Vec<u8> = (0..len).map(|_| rng.next_u64() as u8).collect();
        check(&bytes);
    }
}

#[test]
fn cut_and_mutated_encodings() {
    let mut rng = Rng::new(31);

    for _ in 0..4000 {
        let v = random_value(&mut rng, 24, true);
        let bytes = code_points::encode(&v).unwrap();

        // Every cut.
        for n in 0..bytes.len() {
            check(&bytes[..n]);
        }

        // Mutations: change, insert or delete one to three bytes.
        for _ in 0..4 {
            let mut m = bytes.clone();

            for _ in 0..1 + rng.below(3) {
                let at = rng.below(m.len() as u64 + 1) as usize;

                match rng.below(3) {
                    0 if at < m.len() => m[at] = rng.next_u64() as u8,
                    1 => m.insert(at, rng.next_u64() as u8),
                    _ if at < m.len() => {
                        m.remove(at);
                    }
                    _ => {}
                }
            }

            check(&m);
        }
    }
}

#[test]
fn every_lead_byte_in_some_states() {
    // Each lead byte followed by each trail byte, after prefixes that put the decoder into the general, CJK and Hangul contexts.
    for prefix in ["", "Ж", "東", "あ", "ア", "한", "\u{1F600}"] {
        let p = singulink_steno::encode(prefix);

        for lead in 0..=255u8 {
            for trail in [0x00, 0x01, 0x80, 0xFE, 0xFF] {
                let mut b = p.clone();
                b.extend_from_slice(&[lead, trail, trail, trail]);
                check(&b);
                b.truncate(p.len() + 1);
                check(&b);
            }
        }
    }
}
