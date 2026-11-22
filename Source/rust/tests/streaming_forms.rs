//! Tests of the incremental encoder and decoder.

mod oracle;

use oracle::*;
use singulink_steno::{self as steno, code_points, utf16, Decoder, Encoder};

/// A high surrogate code point given through `encode_code_points`, then a low surrogate given through `encode_utf16`: the code points
/// D800 DC00 are not a value (FORMAT.md §3). The high surrogate is already written, so the low one is rejected, and the encoder's output is
/// still the encoding of a value.
#[test]
fn code_point_high_then_utf16_low_is_rejected() {
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_code_points(&[0xD800], &mut out).unwrap();
    let before = out.clone();
    let e = enc.encode_utf16(&[0xDC00], &mut out).unwrap_err();
    assert_eq!(e.kind(), singulink_steno::EncodeErrorKind::NotAValue);
    assert_eq!(e.position(), 0);
    assert_eq!(out, before, "the output is left as it was");

    // The encoder is left as it was too: it goes on with another piece.
    enc.encode_utf16(&[0x41], &mut out).unwrap();
    enc.finish(&mut out);
    let decoded = code_points::decode(&out);
    assert_eq!(decoded, Ok(vec![0xD800, 0x41]), "the Encoder wrote {}", hex(&out));
}

/// A high surrogate written as a code point of its own because a `str` piece came after it while it was held: a UTF-16 low surrogate after
/// that is rejected too. Every other order of forms joins (UTF-16 to UTF-16) or rejects.
#[test]
fn every_mix_of_forms_is_a_value_or_an_error() {
    let mut o = Oracle::new();

    // UTF-16 high, then UTF-16 low: joined.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0xD83D], &mut out).unwrap();
    enc.encode_utf16(&[0xDE00], &mut out).unwrap();
    enc.finish(&mut out);
    assert_eq!(out, o.encode(&[0x1F600]).unwrap());

    // UTF-16 high, then an empty str (nothing written), then UTF-16 low: joined.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0xD83D], &mut out).unwrap();
    enc.encode("", &mut out);
    enc.encode_utf16(&[0xDE00], &mut out).unwrap();
    enc.finish(&mut out);
    assert_eq!(out, o.encode(&[0x1F600]).unwrap());

    // Code point high, then each form of a low surrogate: rejected (a str cannot hold one).
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_code_points(&[0xD83D], &mut out).unwrap();
    assert!(enc.encode_code_points(&[0xDE00], &mut out).is_err());
    assert!(enc.encode_utf16(&[0xDE00], &mut out).is_err());
    enc.finish(&mut out);
    assert_eq!(out, o.encode(&[0xD83D]).unwrap());

    // UTF-16 high written as unpaired by a code point piece, then UTF-16 low: rejected.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0xD83D], &mut out).unwrap();
    assert!(enc.encode_code_points(&[0xDE00], &mut out).is_err());
    enc.encode_code_points(&[0xD83E], &mut out).unwrap();
    assert!(enc.encode_utf16(&[0xDE00], &mut out).is_err());
    enc.finish(&mut out);
    assert_eq!(out, o.encode(&[0xD83D, 0xD83E]).unwrap());

    // A high surrogate in the middle of a UTF-16 piece is unpaired when a non-low unit follows; a low unit in the next piece is then fine.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0xD83D, 0x41], &mut out).unwrap();
    enc.encode_utf16(&[0xDE00], &mut out).unwrap();
    enc.finish(&mut out);
    assert_eq!(out, o.encode(&[0xD83D, 0x41, 0xDE00]).unwrap());
}

/// The same through the other order of calls, which the encoder does reject, for comparison.
#[test]
fn utf16_high_then_code_point_low_is_rejected() {
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0xD800], &mut out).unwrap();
    assert!(enc.encode_code_points(&[0xDC00], &mut out).is_err());
}

/// A held high surrogate then an empty UTF-16 piece, a str piece and a code point piece: each must give the one-shot bytes.
#[test]
fn held_surrogate_then_other_pieces() {
    let mut o = Oracle::new();

    // held D83D, then "" (utf16, empty), then DE00 -> U+1F600
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0x41, 0xD83D], &mut out).unwrap();
    enc.encode_utf16(&[], &mut out).unwrap();
    enc.encode("", &mut out);
    enc.encode_code_points(&[], &mut out).unwrap();
    enc.encode_utf16(&[0xDE00, 0x42], &mut out).unwrap();
    enc.finish(&mut out);
    assert_eq!(out, o.encode(&[0x41, 0x1F600, 0x42]).unwrap());

    // held D83D, then a str: the high surrogate is unpaired.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0xD83D], &mut out).unwrap();
    enc.encode("x", &mut out);
    enc.finish(&mut out);
    assert_eq!(out, o.encode(&[0xD83D, 0x78]).unwrap());

    // held D83D, then a failing code point piece: the encoder and the output stay as they were, and the held unit is still held.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0xD83D], &mut out).unwrap();
    let before = out.clone();
    assert!(enc.encode_code_points(&[0x41, 0x110000], &mut out).is_err());
    assert_eq!(out, before);
    enc.encode_utf16(&[0xDE00], &mut out).unwrap();
    enc.finish(&mut out);
    assert_eq!(out, o.encode(&[0x1F600]).unwrap());
}

/// Every split of values into pieces of every size, in UTF-16 and as code points, and of the bytes for the decoder, one byte at a time
/// included, with resets in the middle.
#[test]
fn every_split() {
    let mut rng = Rng(seed() ^ 0x5717);
    let mut o = Oracle::new();

    for i in 0..budget(300) {
        let cps = {
            let mut v = Vec::new();
            for _ in 0..1 + rng.below(4) {
                let kind = rng.below(12) as u32;
                let n = rng.below(40) as usize;
                push_run(&mut rng, &mut v, kind, n);
            }
            fix_value(&mut v, i % 2 == 0);
            v
        };
        let want = o.encode(&cps).unwrap();
        let units = to_utf16(&cps);

        for piece in [1usize, 2, 3, 5, 7, 16, 17] {
            let mut enc = Encoder::new();
            let mut out = Vec::new();

            // A reset after a first, different value.
            enc.encode_utf16(&[0x4E00, 0xD800], &mut out).unwrap();
            enc.reset();
            out.clear();

            for c in units.chunks(piece) {
                enc.encode_utf16(c, &mut out).unwrap();
            }
            enc.finish(&mut out);
            assert_eq!(out, want, "utf16 pieces of {piece}: {}", cps_hex(&cps));

            let mut enc = Encoder::new();
            let mut out = Vec::new();
            for c in cps.chunks(piece) {
                enc.encode_code_points(c, &mut out).unwrap();
            }
            enc.finish(&mut out);
            assert_eq!(out, want, "code point pieces of {piece}");

            let mut dec = Decoder::new();
            let mut got = Vec::new();
            dec.decode_code_points(&[0xF8, 0x08], &mut got).ok();
            dec.reset();
            got.clear();
            for c in want.chunks(piece) {
                dec.decode_code_points(c, &mut got).unwrap();
            }
            dec.finish().unwrap();
            assert_eq!(got, cps, "decoder pieces of {piece}");

            let mut dec = Decoder::new();
            let mut got = Vec::new();
            for c in want.chunks(piece) {
                dec.decode_utf16(c, &mut got).unwrap();
            }
            dec.finish().unwrap();
            assert_eq!(got, units);

            if let Some(text) = to_string(&cps) {
                let mut dec = Decoder::new();
                let mut got = String::new();
                for c in want.chunks(piece) {
                    dec.decode(c, &mut got).unwrap();
                }
                dec.finish().unwrap();
                assert_eq!(got, text);
            }
        }
    }
}

/// A piece that ends inside a code point whose missing bytes include a 00 or a byte below 08: the error must be what the one-shot decoder
/// reports, at the same position, and the decoder must stay usable after it as the docs say.
#[test]
fn split_inside_code_point_with_bad_tail() {
    let mut o = Oracle::new();
    let prefixes: &[&[u32]] = &[&[], &[0x4E00], &[0x1F600, 0x1F601], &[0xAC00]];

    for &p in prefixes {
        for tail_cp in [0x10FFFFu32, 0x1F600, 0x4E00, 0x416, 0xF00, 0x20000] {
            let mut cps = p.to_vec();
            cps.push(tail_cp);
            let good = o.encode(&cps).unwrap();
            let start = o.encode(p).unwrap().len();

            for cut in start + 1..good.len() {
                for bad in [0x00u8, 0x01, 0x07, 0x08, 0xFF] {
                    for keep in cut..good.len() {
                        let mut bytes = good[..keep].to_vec();
                        bytes.push(bad);
                        bytes.extend_from_slice(&good[keep + 1..]);
                        let want = o.decode(&bytes);

                        let mut dec = Decoder::new();
                        let mut got = Vec::new();
                        let r1 = dec.decode_code_points(&bytes[..cut], &mut got);
                        let r = r1.and_then(|()| dec.decode_code_points(&bytes[cut..], &mut got)).and_then(|()| dec.finish());

                        match (&want, &r) {
                            (Ok(_), Ok(())) => {}
                            (Err(w), Err(e)) => assert_eq!((w.reason, w.pos), (e.reason().as_str(), e.position()), "{}", hex(&bytes)),
                            _ => panic!("{want:?} vs {r:?} for {} split at {cut}", hex(&bytes)),
                        }
                    }
                }
            }
        }
    }
}

/// `encode` from a `str` with a prefix that is not ASCII, then pieces of a String of every length: one-shot equals streaming.
#[test]
fn str_pieces_on_char_boundaries() {
    let mut rng = Rng(seed() ^ 0x57);
    let mut o = Oracle::new();

    for _ in 0..budget(500) {
        let cps = fast_value(&mut rng, false);
        let text = to_string(&cps).unwrap();
        let want = o.encode(&cps).unwrap();
        let mut enc = Encoder::new();
        let mut out = Vec::new();
        let mut rest = text.as_str();

        while !rest.is_empty() {
            let mut n = (rng.below(130) as usize).min(rest.len());
            while !rest.is_char_boundary(n) {
                n += 1;
            }
            enc.encode(&rest[..n], &mut out);
            rest = &rest[n..];
        }

        enc.finish(&mut out);
        assert_eq!(out, want);
        assert_eq!(steno::encode(&text), want);
        assert_eq!(utf16::encode(&to_utf16(&cps)), want);
    }
}
