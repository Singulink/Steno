//! The test vectors of the specification (FORMAT.md §12), through every form of the API.

mod common;

use common::{read_vectors, reference, to_string, to_utf16, Vector};
use singulink_steno::{code_points, utf16, DecodeReason, Decoder, EncodeErrorKind, Encoder};

fn check_valid(name: &str, cps: &[u32], bytes: &[u8]) {
    // Code points.
    assert_eq!(code_points::encode(cps).unwrap(), bytes, "{name}: encode code points");
    assert_eq!(code_points::decode(bytes).unwrap(), cps, "{name}: decode code points");

    // UTF-16. A value never has a high surrogate code point followed by a low one, so its UTF-16 form reads back as the same code points.
    let units = to_utf16(cps);
    assert_eq!(utf16::encode(&units), bytes, "{name}: encode UTF-16");
    assert_eq!(utf16::decode(bytes).unwrap(), units, "{name}: decode UTF-16");
    assert_eq!(utf16::encoded_len(&units), bytes.len(), "{name}");
    assert_eq!(utf16::decoded_len(bytes).unwrap(), units.len(), "{name}");

    // str, when the value has no surrogate code point.
    match to_string(cps) {
        Some(text) => {
            assert_eq!(singulink_steno::encode(&text), bytes, "{name}: encode str");
            assert_eq!(singulink_steno::decode(bytes).unwrap(), text, "{name}: decode str");
            assert_eq!(singulink_steno::encoded_len(&text), bytes.len(), "{name}");
            assert_eq!(singulink_steno::decoded_len(bytes).unwrap(), text.len(), "{name}");
        }
        None => {
            let e = singulink_steno::decode(bytes).unwrap_err();
            assert_eq!(e.reason(), DecodeReason::Surrogate, "{name}");
        }
    }

    // Streaming, one unit per call.
    let mut enc = Encoder::new();
    let mut out = Vec::new();

    for u in &units {
        enc.encode_utf16(std::slice::from_ref(u), &mut out).unwrap();
    }

    enc.finish(&mut out);
    assert_eq!(out, bytes, "{name}: stream encode UTF-16 one unit at a time");

    let mut dec = Decoder::new();
    let mut got = Vec::new();

    for b in bytes {
        dec.decode_code_points(std::slice::from_ref(b), &mut got).unwrap();
    }

    dec.finish().unwrap();
    assert_eq!(got, cps, "{name}: stream decode one byte at a time");
}

fn check_invalid(name: &str, bytes: &[u8], reason: &str) {
    let e = code_points::decode(bytes).unwrap_err();
    assert_eq!(e.reason().as_str(), reason, "{name}: decode code points");

    let e = utf16::decode(bytes).unwrap_err();
    assert_eq!(e.reason().as_str(), reason, "{name}: decode UTF-16");

    // The str decoder can meet a surrogate code point before the defect.
    let e = singulink_steno::decode(bytes).unwrap_err();
    assert!(e.reason().as_str() == reason || e.reason() == DecodeReason::Surrogate, "{name}: decode str");

    // Streaming, one byte at a time: the defect is met in some call, or by finish for a truncated value.
    let mut dec = Decoder::new();
    let mut got = Vec::new();
    let mut failed = None;

    for b in bytes {
        if let Err(e) = dec.decode_code_points(std::slice::from_ref(b), &mut got) {
            failed = Some(e);
            break;
        }
    }

    let e = match failed {
        Some(e) => e,
        None => dec.finish().unwrap_err(),
    };

    assert_eq!(e.reason().as_str(), reason, "{name}: stream decode");

    // The reason and the position agree with the plain reference codec.
    let (r, at) = reference::decode(bytes).unwrap_err();
    assert_eq!(r, reason, "{name}: reference codec");
    assert_eq!(code_points::decode(bytes).unwrap_err().position(), at, "{name}: position");
}

fn check_not_a_value(name: &str, cps: &[u32]) {
    let e = code_points::encode(cps).unwrap_err();
    assert_eq!(e.kind(), EncodeErrorKind::NotAValue, "{name}");
    assert!((0xDC00..=0xDFFF).contains(&cps[e.position()]) && (0xD800..=0xDBFF).contains(&cps[e.position() - 1]), "{name}");

    let mut enc = Encoder::new();
    let mut out = Vec::new();
    let failed = cps.iter().any(|c| enc.encode_code_points(std::slice::from_ref(c), &mut out).is_err());
    assert!(failed, "{name}: stream encode");
}

fn run_file(path: &std::path::Path) -> (usize, usize, usize) {
    let vectors = read_vectors(path);
    let mut counts = (0, 0, 0);

    for v in &vectors {
        match v {
            Vector::Valid { name, cps, bytes } => {
                check_valid(name, cps, bytes);
                counts.0 += 1;
            }
            Vector::Invalid { name, bytes, reason } => {
                check_invalid(name, bytes, reason);
                counts.1 += 1;
            }
            Vector::NotAValue { name, cps } => {
                check_not_a_value(name, cps);
                counts.2 += 1;
            }
        }
    }

    counts
}

#[test]
fn published_vectors() {
    let counts = run_file(&common::repo_root().join("TestVectors/steno-v1-vectors.txt"));
    println!("published vectors: {} valid, {} invalid, {} not-a-value", counts.0, counts.1, counts.2);
    assert_eq!(counts, (6798, 829, 13));
}

/// The large vector file, when the environment variable `STENO_LARGE_VECTORS` names it. Skipped otherwise.
#[test]
fn large_vectors() {
    let Some(path) = std::env::var_os("STENO_LARGE_VECTORS") else {
        println!("STENO_LARGE_VECTORS is not set: skipped");
        return;
    };

    let counts = run_file(std::path::Path::new(&path));
    println!("large vectors: {} valid, {} invalid, {} not-a-value", counts.0, counts.1, counts.2);
    assert!(counts.0 + counts.1 + counts.2 > 0);
}

#[test]
fn worked_examples() {
    // FORMAT.md §11.2.
    assert_eq!(singulink_steno::encode("\u{041C}\u{0438}"), [0xE9, 0xF4, 0xA2]);
    // §11.13.
    assert_eq!(
        code_points::encode(&[0x6F22, 0x5B57, 0x0141, 0x0141, 0x0061]).unwrap(),
        [0xF8, 0x58, 0xDA, 0x89, 0xC7, 0x67, 0x08, 0x09, 0x66, 0xCA, 0x48]
    );
}
