//! Tests of the parts of the vector code that do not need a CPU feature: the tables and the arithmetic they rest on. The kernels themselves
//! are compared with the scalar code in `tests/simd.rs`.

#[cfg(all(feature = "simd", any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))))]
mod tables {
    use crate::simd::tables::{lead_bytes, DECODE_WIDE, DECODE_WIDE_COUNT, ENCODE_WIDE};

    /// The lead bytes of a run of bytes, one at a time: a byte that may be a lead byte and starts a code is one, and takes the next byte.
    fn sequential(may_lead: u32, bits: u32) -> u32 {
        let mut leads = 0;
        let mut i = 0;

        while i < bits {
            if may_lead >> i & 1 == 1 {
                leads |= 1 << i;
                i += 2;
            } else {
                i += 1;
            }
        }

        leads
    }

    #[test]
    fn lead_bytes_of_every_16_bit_mask() {
        for m in (0..=0xFFFFu32).step_by(if cfg!(miri) { 97 } else { 1 }) {
            assert_eq!(lead_bytes(m) & 0xFFFF, sequential(m, 16) & 0xFFFF, "{m:016b}");
        }
    }

    #[test]
    fn lead_bytes_of_random_32_bit_masks() {
        let mut x: u64 = 1;

        for _ in 0..if cfg!(miri) { 2_000 } else { 200_000 } {
            x = x.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            let m = (x >> 32) as u32;
            // Bit 31 may be a lead byte whose trail byte lies past the mask; the bits up to 31 agree.
            assert_eq!(lead_bytes(m), sequential(m, 32), "{m:032b}");
        }
    }

    #[test]
    fn encode_table_keeps_the_bytes_of_each_unit() {
        for (mask, row) in ENCODE_WIDE.iter().enumerate() {
            let mut expected = alloc::vec::Vec::new();

            for j in 0..8u8 {
                expected.push(2 * j);

                if mask >> j & 1 == 0 {
                    expected.push(2 * j + 1);
                }
            }

            assert_eq!(&row[..expected.len()], &expected[..]);
            assert!(row[expected.len()..].iter().all(|&b| b == 0x80));
        }
    }

    #[test]
    fn decode_table_counts_the_codes() {
        for mask in 0..256usize {
            let leads = sequential(mask as u32, 8);

            if leads != mask as u32 {
                continue; // a mask with a lead byte at a trail position never occurs
            }

            let trails_inside = ((leads << 1) & 0xFF).count_ones();
            assert_eq!(u32::from(DECODE_WIDE_COUNT[mask]), 8 - trails_inside, "{mask:08b}");
            let row = DECODE_WIDE[mask];
            assert!(row.iter().all(|&b| b == 0x80 || b <= 8));
        }
    }
}

/// The division by 248 of the wide encoder, `((o >> 3) * DIV31) >> 20` as the high half of a 16 by 16-bit product shifted right by 4, is
/// exact for every 16-bit offset (an offset in a range of size 2 is below 254 * 248), and so is the trail byte that follows from it.
#[test]
fn division_by_248_with_one_multiply() {
    for o in (0..=u16::MAX as u32).step_by(if cfg!(miri) { 61 } else { 1 }) {
        let high = ((o >> 3) * u32::from(crate::simd::DIV31)) >> 16;
        let q = high >> 4;
        assert_eq!(q, o / 248, "{o}");
        // In 16-bit lanes, which wrap.
        let trail = (o as u16).wrapping_sub((q << 8) as u16).wrapping_add((q << 3) as u16).wrapping_add(8);
        assert_eq!(u32::from(trail), o % 248 + 8);
    }
}

/// The kernels through the codec, on texts long enough for every kernel, with the vector code and without. Small enough for Miri, which
/// checks the loads and stores (`cargo +nightly miri test --lib simd`, with `RUSTFLAGS="-C target-feature=+ssse3"` on x86-64).
#[cfg(feature = "std")]
#[test]
fn kernels_through_the_codec() {
    use alloc::string::String;
    use alloc::vec::Vec;

    let texts = [
        "The quick brown fox jumps over the lazy dog.\tAnd again.\r\nThe quick brown fox jumps over the lazy dog.",
        "Все люди рождаются свободными и равными в своем достоинстве и правах, Все люди рождаются свободными.",
        "人人生而自由，在尊严和权利上一律平等。他们赋有理性和良心，并应以兄弟关系的精神相对待。人人生而自由。",
        "すべての人間は、生まれながらにして自由であり、かつ、尊厳と権利とについて平等である。すべての人間は。",
        "모든 인간은 태어날 때부터 자유로우며 그 존엄과 권리에 있어 동등하다. 인간은 천부적으로 이성과 양심을 부여받았으며.",
    ];

    // With SSSE3 among the compile-time target features (as for the Miri run), the vector code must be what runs.
    #[cfg(all(feature = "simd", target_arch = "x86_64", target_feature = "ssse3"))]
    if std::env::var_os("STENO_SIMD").is_none() {
        assert!(crate::simd::is_active());
    }

    for text in texts {
        let units: Vec<u16> = text.encode_utf16().collect();
        let mut results = Vec::new();

        for on in [true, false] {
            crate::simd::set_thread_enabled(on);
            let bytes = crate::utf16::encode(&units);
            let mut exact = alloc::vec![0u8; bytes.len()];
            let n = crate::utf16::encode_to_slice(&units, &mut exact).unwrap();
            let back = crate::utf16::decode(&bytes).unwrap();
            let mut back_exact = alloc::vec![0u16; units.len()];
            let m = crate::utf16::decode_to_slice(&bytes, &mut back_exact).unwrap();
            let string: String = crate::decode(&bytes).unwrap();
            results.push((bytes.clone(), exact[..n].to_vec(), back, back_exact[..m].to_vec(), string, crate::is_ascii_text(&bytes)));
        }

        crate::simd::set_thread_enabled(true);
        assert_eq!(results[0], results[1], "{text}");
        assert_eq!(results[0].2, units);
        assert_eq!(results[0].4, text);
    }
}

/// Random text of 1-, 2-, 3- and 4-byte UTF-8 sequences in runs, with a seeded generator.
#[cfg(feature = "std")]
fn random_text(seed: &mut u64, len: usize) -> alloc::string::String {
    let mut next = |n: u32| {
        *seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        ((*seed >> 33) as u32) % n
    };
    let groups: [(u32, u32); 6] = [(0x20, 0x7E), (0x400, 0x47F), (0x900, 0x97F), (0x4E00, 0x9FFF), (0xAC00, 0xD7A3), (0x1F600, 0x1F64F)];
    let mut group = groups[0];
    let mut s = alloc::string::String::new();

    for _ in 0..len {
        if next(4) == 0 {
            group = groups[next(groups.len() as u32) as usize];
        }

        let c = group.0 + next(group.1 - group.0 + 1);
        s.push(char::from_u32(c).unwrap_or(' '));
    }

    s
}

/// The UTF-8 to UTF-16 kernel against the standard library, on every length to 80 and from every start, with room to spare and with as
/// little room as it needs.
#[cfg(feature = "std")]
#[test]
fn utf8_to_utf16_agrees_with_std() {
    use alloc::vec::Vec;

    let mut seed = 1;
    // Under Miri, which checks every load and store of the vector code, a sample of the cases: the interpreter is slow.
    let (lengths, cases, starts) = if cfg!(miri) { (40, 1, 3) } else { (80, 12, 20) };

    for len in (0..=lengths).step_by(if cfg!(miri) { 7 } else { 1 }) {
        for _ in 0..cases {
            let text = random_text(&mut seed, len);
            let bytes = text.as_bytes();

            for start in (0..=bytes.len()).filter(|&i| text.is_char_boundary(i)).take(starts) {
                let mut out = [0u16; 128];
                let p = crate::simd::utf8_to_utf16(bytes, start, &mut out);
                let read = &text[start..start + p.read];
                let expected: Vec<u16> = read.encode_utf16().collect();
                assert_eq!(&out[..p.written], &expected[..], "{text:?} from {start}");
                assert!(read.chars().all(|c| (c as u32) < 0x1_0000), "{text:?}");

                // It stops only before a 4-byte sequence, near the end (fewer than `MIN_TAIL` bytes left), or for room.
                let rest = &text[start + p.read..];
                assert!(
                    rest.len() < crate::simd::MIN_TAIL
                        || rest.chars().next().is_some_and(|c| c as u32 >= 0x1_0000)
                        || p.written + 16 > out.len()
                        || !crate::simd::active(),
                    "{text:?} from {start}: stopped at {}",
                    p.read
                );
            }
        }
    }
}

/// The UTF-16 to UTF-8 kernel against the standard library, on every length to 40, with surrogates that stop it.
#[cfg(feature = "std")]
#[test]
fn utf16_to_utf8_agrees_with_std() {
    let mut seed = 2;
    let cases = if cfg!(miri) { 2 } else { 30 };

    for len in (0..=40).step_by(if cfg!(miri) { 5 } else { 1 }) {
        for _ in 0..cases {
            let text = random_text(&mut seed, len);
            let mut units: alloc::vec::Vec<u16> = text.encode_utf16().collect();

            // Every second case: one lone surrogate somewhere.
            if len > 0 && seed % 2 == 0 {
                let at = (seed >> 40) as usize % units.len();
                units[at] = 0xD900;
            }

            let mut out = [0u8; 256];
            let p = crate::simd::utf16_to_utf8(&units, &mut out);
            let expected = alloc::string::String::from_utf16(&units[..p.read]).expect("no surrogate is read");
            assert_eq!(&out[..p.written], expected.as_bytes());
        }
    }
}
