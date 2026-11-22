//! The properties of FORMAT.md §13 on seeded random text, and agreement with the plain reference codec.

mod common;

use common::{random_string, random_value, reference, to_utf16, Rng};
use singulink_steno::{code_points, utf16};

#[test]
fn agrees_with_the_reference_codec() {
    let mut rng = Rng::new(1);

    for _ in 0..1500 {
        let v = random_value(&mut rng, 40, true);
        let bytes = code_points::encode(&v).unwrap();
        assert_eq!(bytes, reference::encode(&v), "{v:X?}");
        assert_eq!(reference::decode(&bytes).unwrap(), v);
    }
}

#[test]
fn round_trip() {
    let mut rng = Rng::new(2);

    for _ in 0..5000 {
        let v = random_value(&mut rng, 64, true);
        let bytes = code_points::encode(&v).unwrap();
        assert_eq!(code_points::decode(&bytes).unwrap(), v);

        let units = to_utf16(&v);
        assert_eq!(utf16::encode(&units), bytes);
        assert_eq!(utf16::decode(&bytes).unwrap(), units);

        let s = random_string(&mut rng, 64);
        let bytes = singulink_steno::encode(&s);
        assert_eq!(singulink_steno::decode(&bytes).unwrap(), s);
        assert_eq!(singulink_steno::encoded_len(&s), bytes.len());
    }
}

#[test]
fn byte_order_is_code_point_order() {
    let mut rng = Rng::new(3);

    for _ in 0..5000 {
        let a = random_value(&mut rng, 12, true);

        // Half of the pairs share a prefix, so that the comparison is decided after the start.
        let b = if rng.chance(50) && !a.is_empty() {
            let keep = rng.below(a.len() as u64 + 1) as usize;
            let mut b = a[..keep].to_vec();
            let tail = random_value(&mut rng, 6, true);

            for c in tail {
                if b.last().is_some_and(|&p| (0xD800..=0xDBFF).contains(&p)) && (0xDC00..=0xDFFF).contains(&c) {
                    continue;
                }

                b.push(c);
            }

            b
        } else {
            random_value(&mut rng, 12, true)
        };

        let ea = code_points::encode(&a).unwrap();
        let eb = code_points::encode(&b).unwrap();
        assert_eq!(ea.cmp(&eb), a.cmp(&b), "{a:X?} {b:X?}");
    }
}

#[test]
fn str_order_is_byte_order() {
    // Rust compares `str` by UTF-8 bytes, which is code point order, so the Steno bytes compare the same way.
    let mut rng = Rng::new(4);

    for _ in 0..3000 {
        let a = random_string(&mut rng, 10);
        let b = random_string(&mut rng, 10);
        assert_eq!(singulink_steno::encode(&a).cmp(&singulink_steno::encode(&b)), a.cmp(&b));
    }
}

#[test]
fn prefix_encodes_to_prefix() {
    let mut rng = Rng::new(5);

    for _ in 0..2000 {
        let v = random_value(&mut rng, 40, true);
        let full = code_points::encode(&v).unwrap();

        for n in 0..=v.len() {
            let part = code_points::encode(&v[..n]).unwrap();
            assert!(full.starts_with(&part));
        }
    }
}

#[test]
fn no_zero_byte() {
    let mut rng = Rng::new(6);

    for _ in 0..5000 {
        let v = random_value(&mut rng, 64, true);
        assert!(!code_points::encode(&v).unwrap().contains(&0));
    }

    // U+0000 itself.
    assert_eq!(singulink_steno::encode("\0"), [0x01, 0x08]);
}

#[test]
fn is_ascii_text_agrees_with_the_text() {
    let mut rng = Rng::new(7);

    for _ in 0..3000 {
        let s = if rng.chance(50) {
            (0..rng.below(30)).map(|_| char::from_u32(rng.range(0, 0x7F)).unwrap()).collect::<String>()
        } else {
            random_string(&mut rng, 20)
        };

        let expected = s.chars().all(|c| matches!(c, '\t' | '\n' | '\r' | ' '..='~'));
        assert_eq!(singulink_steno::is_ascii_text(&singulink_steno::encode(&s)), expected, "{s:?}");
    }
}

/// The offset in the encoding where each code point starts, and the end: by the prefix property, the length of the encoding of each prefix.
fn starts(v: &[u32]) -> Vec<usize> {
    (0..=v.len()).map(|n| code_points::encode(&v[..n]).unwrap().len()).collect()
}

/// FORMAT.md §13.5: a byte below 08 occurs only as the first byte of a code point, and it is the first byte of a C0 control or the space.
#[test]
fn bytes_below_08_start_c0_controls_or_the_space() {
    let mut rng = Rng::new(8);

    for _ in 0..1500 {
        let mut v = random_value(&mut rng, 40, true);

        // More controls and spaces than random text has.
        for _ in 0..rng.below(6) {
            let at = rng.below(v.len() as u64 + 1) as usize;
            let c = [0x00, 0x05, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x1F, 0x20][rng.below(11) as usize];
            v.insert(at, c);
        }

        let bytes = code_points::encode(&v).unwrap();
        let starts = starts(&v);

        for (p, &b) in bytes.iter().enumerate().filter(|&(_, &b)| b < 8) {
            let i = starts.iter().position(|&s| s == p).unwrap_or_else(|| panic!("byte {b:02X} at {p} is inside a code point: {v:X?}"));
            let expected = match v[i] {
                0x00..=0x08 => 0x01,
                0x09 => 0x02,
                0x0A => 0x03,
                0x0B..=0x0C => 0x04,
                0x0D => 0x05,
                0x0E..=0x1F => 0x06,
                0x20 => 0x07,
                c => panic!("byte {b:02X} at {p} starts U+{c:04X}"),
            };
            assert_eq!(b, expected, "U+{:04X}", v[i]);
        }
    }
}

/// Splitting the bytes of a multi-line text on byte 03 finds exactly its line feeds.
#[test]
fn line_feeds_are_found_at_the_byte_level() {
    let mut rng = Rng::new(9);

    for _ in 0..500 {
        let lines: Vec<String> = (0..1 + rng.below(6)).map(|_| random_string(&mut rng, 20).replace('\n', " ")).collect();
        let text = lines.join("\n");
        let v: Vec<u32> = text.chars().map(u32::from).collect();
        let bytes = singulink_steno::encode(&text);
        let starts = starts(&v);

        let found: Vec<usize> = bytes.iter().enumerate().filter(|&(_, &b)| b == 0x03).map(|(p, _)| p).collect();
        let expected: Vec<usize> = v.iter().enumerate().filter(|&(_, &c)| c == 0x0A).map(|(i, _)| starts[i]).collect();
        assert_eq!(found, expected, "{text:?}");
        assert_eq!(bytes.split(|&b| b == 0x03).count(), lines.len());
    }
}
