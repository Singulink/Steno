//! The incremental encoder and decoder give the one-shot results over any split.

mod common;

use common::{random_string, random_value, to_utf16, Rng};
use singulink_steno::{code_points, utf16, DecodeReason, Decoder, EncodeErrorKind, Encoder};

/// Splits `len` into random pieces, empty pieces included.
fn random_cuts(rng: &mut Rng, len: usize) -> Vec<usize> {
    let mut cuts = vec![0];

    while *cuts.last().unwrap() < len {
        let step = if rng.chance(10) { 0 } else { 1 + rng.below(6) as usize };
        cuts.push((cuts.last().unwrap() + step).min(len));
    }

    cuts
}

#[test]
fn encoder_over_random_splits() {
    let mut rng = Rng::new(10);

    for _ in 0..2000 {
        let v = random_value(&mut rng, 50, true);
        let expected = code_points::encode(&v).unwrap();
        let units = to_utf16(&v);

        // UTF-16 pieces, which split surrogate pairs.
        let mut enc = Encoder::new();
        let mut out = Vec::new();
        let cuts = random_cuts(&mut rng, units.len());

        for w in cuts.windows(2) {
            enc.encode_utf16(&units[w[0]..w[1]], &mut out).unwrap();
        }

        enc.finish(&mut out);
        assert_eq!(out, expected);

        // Code point pieces.
        let mut enc = Encoder::new();
        let mut out = Vec::new();
        let cuts = random_cuts(&mut rng, v.len());

        for w in cuts.windows(2) {
            enc.encode_code_points(&v[w[0]..w[1]], &mut out).unwrap();
        }

        enc.finish(&mut out);
        assert_eq!(out, expected);
    }
}

#[test]
fn encoder_mixes_input_forms() {
    let mut rng = Rng::new(11);

    for _ in 0..1000 {
        let a = random_string(&mut rng, 20);
        let b = random_string(&mut rng, 20);
        let c = random_string(&mut rng, 20);
        let expected = singulink_steno::encode(&format!("{a}{b}{c}"));

        let mut enc = Encoder::new();
        let mut out = Vec::new();
        enc.encode(&a, &mut out);
        enc.encode_utf16(&b.encode_utf16().collect::<Vec<_>>(), &mut out).unwrap();
        enc.encode_code_points(&c.chars().map(u32::from).collect::<Vec<_>>(), &mut out).unwrap();
        enc.finish(&mut out);
        assert_eq!(out, expected);
    }
}

#[test]
fn encoder_held_high_surrogate() {
    // A high surrogate at the end of a piece pairs with a low surrogate at the start of the next one.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0x48, 0xD83D], &mut out).unwrap();
    assert_eq!(out, [0x2F], "the high surrogate is held");
    enc.encode_utf16(&[0xDE00], &mut out).unwrap();
    enc.finish(&mut out);
    assert_eq!(out, singulink_steno::encode("H\u{1F600}"));

    // At the end, it is written as an unpaired surrogate.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0x48, 0xD83D], &mut out).unwrap();
    enc.finish(&mut out);
    assert_eq!(out, utf16::encode(&[0x48, 0xD83D]));

    // Followed by a str piece, it is unpaired too.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0xD83D], &mut out).unwrap();
    enc.encode("a", &mut out);
    enc.finish(&mut out);
    assert_eq!(out, utf16::encode(&[0xD83D, 0x61]));

    // Followed by a low surrogate code point, it is not a value, and the encoder is left as it was.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0x61, 0xD83D], &mut out).unwrap();
    let e = enc.encode_code_points(&[0xDE00], &mut out).unwrap_err();
    assert_eq!(e.kind(), EncodeErrorKind::NotAValue);
    assert_eq!(e.position(), 0);
    assert_eq!(out, [0x48]);
    enc.encode_utf16(&[0xDE00], &mut out).unwrap();
    enc.finish(&mut out);
    assert_eq!(out, singulink_steno::encode("a\u{1F600}"));

    // reset drops it.
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_utf16(&[0xD83D], &mut out).unwrap();
    enc.reset();
    enc.encode("x", &mut out);
    enc.finish(&mut out);
    assert_eq!(out, singulink_steno::encode("x"));
}

#[test]
fn encoder_rejects_a_pair_across_code_point_pieces() {
    let mut enc = Encoder::new();
    let mut out = Vec::new();
    enc.encode_code_points(&[0x61, 0xD800], &mut out).unwrap();
    let before = out.clone();
    let e = enc.encode_code_points(&[0x62, 0xDC00], &mut out);
    assert!(e.is_ok(), "not adjacent");
    let e = enc.encode_code_points(&[0xD800], &mut out);
    assert!(e.is_ok());
    let len = out.len();
    let e = enc.encode_code_points(&[0xDC00], &mut out).unwrap_err();
    assert_eq!(e.kind(), EncodeErrorKind::NotAValue);
    assert_eq!(out.len(), len);
    assert!(out.starts_with(&before));
}

#[test]
fn decoder_over_random_splits() {
    let mut rng = Rng::new(12);

    for _ in 0..2000 {
        let v = random_value(&mut rng, 50, true);
        let bytes = code_points::encode(&v).unwrap();
        let cuts = random_cuts(&mut rng, bytes.len());

        let mut dec = Decoder::new();
        let mut out = Vec::new();

        for w in cuts.windows(2) {
            dec.decode_code_points(&bytes[w[0]..w[1]], &mut out).unwrap();
        }

        dec.finish().unwrap();
        assert_eq!(out, v);

        let mut dec = Decoder::new();
        let mut out = Vec::new();

        for w in cuts.windows(2) {
            dec.decode_utf16(&bytes[w[0]..w[1]], &mut out).unwrap();
        }

        dec.finish().unwrap();
        assert_eq!(out, to_utf16(&v));

        let s = random_string(&mut rng, 50);
        let bytes = singulink_steno::encode(&s);
        let mut dec = Decoder::new();
        let mut out = String::new();

        for b in &bytes {
            dec.decode(std::slice::from_ref(b), &mut out).unwrap();
        }

        dec.finish().unwrap();
        assert_eq!(out, s);
    }
}

#[test]
fn decoder_finishing_early_is_truncated() {
    let bytes = singulink_steno::encode("東京");
    assert_eq!(bytes.len(), 5);

    for n in 1..bytes.len() {
        let mut dec = Decoder::new();
        let mut out = String::new();
        dec.decode(&bytes[..n], &mut out).unwrap();
        let complete = n == 3; // 東 takes 3 bytes, 京 takes 2.
        let result = dec.finish();

        if complete {
            assert!(result.is_ok());
        } else {
            let e = result.unwrap_err();
            assert_eq!(e.reason(), DecodeReason::Truncated);
            assert_eq!(e.position(), if n < 3 { 0 } else { 3 });
        }
    }
}

#[test]
fn decoder_error_leaves_it_as_it_was() {
    let bytes = singulink_steno::encode("Привет");
    let mut dec = Decoder::new();
    let mut out = String::new();
    dec.decode(&bytes[..1], &mut out).unwrap();

    // A zero byte in the second piece fails; the decoder and the output are unchanged.
    let e = dec.decode(&[bytes[1], bytes[2], 0], &mut out).unwrap_err();
    assert_eq!(e.reason(), DecodeReason::ZeroByte);
    assert_eq!(e.position(), 3);
    assert_eq!(out, "");

    dec.decode(&bytes[1..], &mut out).unwrap();
    dec.finish().unwrap();
    assert_eq!(out, "Привет");
}

#[test]
fn decoder_reset_starts_a_new_value() {
    let a = singulink_steno::encode("Ελληνικά");
    let b = singulink_steno::encode("Ελ");
    let mut dec = Decoder::new();
    let mut out = String::new();
    dec.decode(&a[..a.len() - 1], &mut out).unwrap();
    dec.reset();
    out.clear();
    dec.decode(&b, &mut out).unwrap();
    dec.finish().unwrap();
    assert_eq!(out, "Ελ");
}

#[test]
fn decoder_positions_count_from_the_start_of_the_value() {
    let mut bytes = singulink_steno::encode("abcdef");
    bytes.push(0xFF); // never a lead byte
    let mut dec = Decoder::new();
    let mut out = String::new();
    dec.decode(&bytes[..4], &mut out).unwrap();
    let e = dec.decode(&bytes[4..], &mut out).unwrap_err();
    assert_eq!(e.reason(), DecodeReason::UnusedLead);
    assert_eq!(e.position(), 6);
}

#[test]
fn encoder_and_decoder_are_send_and_sync() {
    fn check<T: Send + Sync>() {}
    check::<Encoder>();
    check::<Decoder>();
}
