//! Writing into a caller's slice, and the maximum-length functions.

mod common;

use common::{random_value, to_string, to_utf16, Rng};
use singulink_steno::{code_points, utf16, BufferTooSmall, DecodeReason, DecodeToSliceError};

#[test]
fn exact_and_short_destinations() {
    let mut rng = Rng::new(20);

    for _ in 0..1000 {
        let v = random_value(&mut rng, 30, true);
        let bytes = code_points::encode(&v).unwrap();
        let units = to_utf16(&v);

        // Encode from UTF-16.
        let mut buf = vec![0u8; bytes.len()];
        assert_eq!(utf16::encode_to_slice(&units, &mut buf), Ok(bytes.len()));
        assert_eq!(buf, bytes);

        if !bytes.is_empty() {
            assert_eq!(utf16::encode_to_slice(&units, &mut buf[..bytes.len() - 1]), Err(BufferTooSmall));
        }

        // Decode to UTF-16.
        let mut buf = vec![0u16; units.len()];
        assert_eq!(utf16::decode_to_slice(&bytes, &mut buf), Ok(units.len()));
        assert_eq!(buf, units);

        if !units.is_empty() {
            assert_eq!(utf16::decode_to_slice(&bytes, &mut buf[..units.len() - 1]), Err(DecodeToSliceError::BufferTooSmall));
        }

        // str and UTF-8.
        if let Some(s) = to_string(&v) {
            let mut buf = vec![0u8; bytes.len()];
            assert_eq!(singulink_steno::encode_to_slice(&s, &mut buf), Ok(bytes.len()));
            assert_eq!(buf, bytes);

            if !bytes.is_empty() {
                assert_eq!(singulink_steno::encode_to_slice(&s, &mut buf[..bytes.len() - 1]), Err(BufferTooSmall));
            }

            let mut buf = vec![0u8; s.len()];
            assert_eq!(singulink_steno::decode_to_slice(&bytes, &mut buf), Ok(s.len()));
            assert_eq!(buf, s.as_bytes());

            if !s.is_empty() {
                assert_eq!(singulink_steno::decode_to_slice(&bytes, &mut buf[..s.len() - 1]), Err(DecodeToSliceError::BufferTooSmall));
            }
        } else {
            let mut buf = vec![0u8; singulink_steno::max_decoded_len(bytes.len())];
            let e = singulink_steno::decode_to_slice(&bytes, &mut buf).unwrap_err();
            assert!(matches!(e, DecodeToSliceError::Decode(e) if e.reason() == DecodeReason::Surrogate));
        }
    }
}

#[test]
fn append_forms_keep_what_is_there() {
    let mut out = b"key:".to_vec();
    singulink_steno::encode_to_vec("Zürich", &mut out);
    assert_eq!(&out[..4], b"key:");
    assert_eq!(&out[4..], singulink_steno::encode("Zürich"));

    let mut text = String::from("x");
    singulink_steno::decode_to_string(&out[4..], &mut text).unwrap();
    assert_eq!(text, "xZürich");

    // An error leaves the output as it was.
    let mut text = String::from("x");
    assert!(singulink_steno::decode_to_string(&[0x48, 0x00], &mut text).is_err());
    assert_eq!(text, "x");
}

/// Worst cases for each bound: the code points that take the most bytes per input unit, and the values that take the most output units per
/// Steno byte.
#[test]
fn maximum_lengths_hold_on_worst_cases() {
    // ASCII controls: 2 bytes per UTF-8 byte.
    let s: String = (0..1000).map(|i| char::from_u32([0x01, 0x0B, 0x1F, 0x7F][i % 4]).unwrap()).collect();
    let n = singulink_steno::encode(&s).len();
    assert_eq!(n, singulink_steno::max_encoded_len(s.len()));

    // Code points that jump between windows far apart: 3 bytes per UTF-16 unit, 4 bytes per supplementary code point.
    let mut rng = Rng::new(21);
    let bmp: Vec<u32> = (0..2000)
        .map(|_| [rng.range(0x0F00, 0x1FFF), rng.range(0xA000, 0xABFF), rng.range(0xE000, 0xF8FF)][rng.below(3) as usize])
        .collect();
    let e = code_points::encode(&bmp).unwrap();
    assert!(e.len() <= utf16::max_encoded_len(to_utf16(&bmp).len()));
    assert!(e.len() <= singulink_steno::max_encoded_len(to_string(&bmp).unwrap().len()));
    assert!(e.len() > 2 * bmp.len(), "most of them take 3 bytes");

    let sup: Vec<u32> = (0..2000).map(|_| rng.range(0x4_0000, 0x10_FFFF)).collect();
    let e = code_points::encode(&sup).unwrap();
    assert!(e.len() <= code_points::max_encoded_len(sup.len()));
    assert!(e.len() > 3 * sup.len(), "most of them take 4 bytes");

    // A supplementary main window: 1 byte per code point, 4 UTF-8 bytes and 2 UTF-16 units each.
    let emoji: String = std::iter::repeat_n("\u{1F600}\u{1F601}\u{1F602}", 300).collect();
    let e = singulink_steno::encode(&emoji);
    assert!(emoji.len() <= singulink_steno::max_decoded_len(e.len()));
    assert!(emoji.len() + 8 >= singulink_steno::max_decoded_len(e.len()), "the bound is nearly reached");
    assert!(emoji.encode_utf16().count() <= utf16::max_decoded_len(e.len()));

    // Random values.
    for _ in 0..3000 {
        let v = random_value(&mut rng, 40, true);
        let bytes = code_points::encode(&v).unwrap();
        let units = to_utf16(&v);
        assert!(bytes.len() <= code_points::max_encoded_len(v.len()));
        assert!(bytes.len() <= utf16::max_encoded_len(units.len()));
        assert!(units.len() <= utf16::max_decoded_len(bytes.len()));
        assert!(v.len() <= code_points::max_decoded_len(bytes.len()));

        if let Some(s) = to_string(&v) {
            assert!(bytes.len() <= singulink_steno::max_encoded_len(s.len()));
            assert!(s.len() <= singulink_steno::max_decoded_len(bytes.len()));
        }
    }
}

#[test]
fn every_code_point_alone_fits_the_bounds() {
    for c in 0..=0x10_FFFFu32 {
        let bytes = code_points::encode(&[c]).unwrap();
        assert!(bytes.len() <= 4);

        // FORMAT.md §1.3: no code point below U+20000 takes more than 3 bytes.
        if c < 0x2_0000 {
            assert!(bytes.len() <= 3);
        }

        if let Some(ch) = char::from_u32(c) {
            assert!(bytes.len() <= 2 * ch.len_utf8());
        }
    }
}

#[test]
fn code_point_slices_and_lengths() {
    use singulink_steno::{EncodeErrorKind, EncodeToSliceError};

    let mut rng = Rng::new(22);

    for _ in 0..500 {
        let v = random_value(&mut rng, 30, true);
        let bytes = code_points::encode(&v).unwrap();
        assert_eq!(code_points::encoded_len(&v), Ok(bytes.len()));
        assert_eq!(code_points::decoded_len(&bytes), Ok(v.len()));

        let mut out = vec![0u8; bytes.len()];
        assert_eq!(code_points::encode_to_slice(&v, &mut out), Ok(bytes.len()));
        assert_eq!(out, bytes);

        let mut back = vec![0u32; v.len()];
        assert_eq!(code_points::decode_to_slice(&bytes, &mut back), Ok(v.len()));
        assert_eq!(back, v);

        if !bytes.is_empty() {
            assert_eq!(code_points::encode_to_slice(&v, &mut out[..bytes.len() - 1]), Err(EncodeToSliceError::BufferTooSmall));
            assert_eq!(code_points::decode_to_slice(&bytes, &mut back[..v.len() - 1]), Err(DecodeToSliceError::BufferTooSmall));
        }
    }

    // The first problem met wins: a short destination before the bad code point, the bad code point when all before it fits.
    let v = [0x61, 0x62, 0xD800, 0xDC00];
    let mut out = [0u8; 2];
    assert_eq!(code_points::encode_to_slice(&v, &mut out[..1]), Err(EncodeToSliceError::BufferTooSmall));
    let e = code_points::encode_to_slice(&v, &mut [0u8; 16]).unwrap_err();
    assert!(matches!(e, EncodeToSliceError::Encode(e) if e.kind() == EncodeErrorKind::NotAValue && e.position() == 3));
    assert_eq!(code_points::encoded_len(&[0x110000]).unwrap_err().kind(), EncodeErrorKind::NotACodePoint);
}
