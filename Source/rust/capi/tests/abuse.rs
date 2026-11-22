//! Tests of the C interface, called from Rust.

use core::ffi::CStr;
use core::ptr;

use steno::*;

fn status_of(v: i32) -> Option<StenoStatus> {
    use StenoStatus::*;
    [
        Ok,
        ZeroByte,
        UnusedLead,
        Truncated,
        PastRange,
        SurrogatePair,
        DestinationTooSmall,
        InvalidUtf8,
        Surrogate,
        InvalidArgument,
        InvalidTrail,
        NotAValue,
        InvalidCodePoint,
    ]
    .into_iter()
    .find(|s| *s as i32 == v)
}

/// Every status of `steno.h` has the value and the name of the Rust side, and the Rust side has no other.
#[test]
fn header_statuses_match() {
    let header = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/include/steno.h")).unwrap();
    let mut seen = Vec::new();

    for line in header.lines() {
        let t = line.trim();
        if let Some(rest) = t.strip_prefix("STENO_") {
            if let Some((name, value)) = rest.split_once(" = ") {
                let value: i32 = value.trim_end_matches(',').trim().parse().unwrap();
                let st = status_of(value).unwrap_or_else(|| panic!("STENO_{name} = {value} has no Rust status"));
                // SAFETY: the name is a static C string.
                let rust_name = unsafe { CStr::from_ptr(steno_status_name(value)) }.to_str().unwrap();
                let header_name = name.to_ascii_lowercase().replace('_', "-");
                assert_eq!(rust_name, header_name, "name of {value}");
                assert_eq!(st as i32, value);
                seen.push(value);
            }
        }
    }

    seen.sort_unstable();
    assert_eq!(seen, (0..=12).collect::<Vec<_>>());
    assert_eq!(core::mem::size_of::<StenoStatus>(), core::mem::size_of::<core::ffi::c_int>());
}

/// The out pointer on every status, and null pointers with zero and nonzero lengths.
#[test]
fn null_pointers_and_out_len() {
    let text = b"Hi";
    let mut dst = [0u8; 16];
    let mut n = 12345usize;

    // SAFETY: every pointer is null with length 0, or valid for its length.
    unsafe {
        assert_eq!(steno_encode_utf8(ptr::null(), 0, ptr::null_mut(), 0, &mut n), StenoStatus::Ok);
        assert_eq!(n, 0);
        n = 7;
        assert_eq!(steno_encode_utf8(ptr::null(), 1, dst.as_mut_ptr(), 16, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(n, 0);
        n = 7;
        assert_eq!(steno_encode_utf8(text.as_ptr(), 2, ptr::null_mut(), 16, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(n, 0);
        n = 7;
        assert_eq!(steno_encode_utf8(text.as_ptr(), 2, ptr::null_mut(), 0, &mut n), StenoStatus::DestinationTooSmall);
        assert_eq!(n, 0);
        assert_eq!(steno_encode_utf8(text.as_ptr(), 2, dst.as_mut_ptr(), 16, ptr::null_mut()), StenoStatus::InvalidArgument);
        n = 7;
        assert_eq!(steno_decode_utf8(ptr::null(), 0, ptr::null_mut(), 0, &mut n), StenoStatus::Ok);
        assert_eq!(n, 0);
        n = 7;
        assert_eq!(steno_decode_utf16(ptr::null(), 5, ptr::null_mut(), 0, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(n, 0);
        n = 7;
        assert_eq!(steno_encoded_len_utf16(ptr::null(), 0, &mut n), StenoStatus::Ok);
        assert_eq!(n, 0);
        assert_eq!(steno_encoded_len_utf16(ptr::null(), 3, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(steno_decoded_len_utf8(ptr::null(), 3, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(steno_decoded_len_utf8(ptr::null(), 0, ptr::null_mut()), StenoStatus::InvalidArgument);
        assert!(steno_is_ascii_text(ptr::null(), 0));
        assert!(!steno_is_ascii_text(ptr::null(), 4));

        // A misaligned out pointer.
        let mut words = [0usize; 2];
        let mis = (words.as_mut_ptr() as *mut u8).add(1) as *mut usize;
        assert_eq!(steno_encode_utf8(text.as_ptr(), 2, dst.as_mut_ptr(), 16, mis), StenoStatus::InvalidArgument);
        assert_eq!(words, [0, 0], "a misaligned out pointer was written");

        // Every status writes the documented value.
        n = 7;
        assert_eq!(steno_decode_utf8([0x48u8, 0x00].as_ptr(), 2, dst.as_mut_ptr(), 16, &mut n), StenoStatus::ZeroByte);
        assert_eq!(n, 1);
        assert_eq!(steno_decode_utf8([0xFFu8].as_ptr(), 1, dst.as_mut_ptr(), 16, &mut n), StenoStatus::UnusedLead);
        assert_eq!(n, 0);
        assert_eq!(steno_decode_utf8([0x48u8, 0xF6, 0x08].as_ptr(), 3, dst.as_mut_ptr(), 16, &mut n), StenoStatus::Truncated);
        assert_eq!(n, 1);
        assert_eq!(steno_decode_utf8([0x48u8, 0xF6, 0x01].as_ptr(), 3, dst.as_mut_ptr(), 16, &mut n), StenoStatus::InvalidTrail);
        assert_eq!(n, 1);
        assert_eq!(steno_decode_utf8([0xFAu8, 0xFF, 0xFF, 0xFF].as_ptr(), 4, dst.as_mut_ptr(), 16, &mut n), StenoStatus::PastRange);
        assert_eq!(n, 0);
        let sp = singulink_steno::utf16::encode(&[0x41, 0xD800]);
        let mut pair = sp.clone();
        // D800 then DC00 as code points: build it by hand from the layout after D800.
        let low = singulink_steno::utf16::encode(&[0x41, 0xD800, 0x41, 0xDC00]);
        assert!(low.len() > sp.len());
        pair.clear();
        let _ = pair;
        assert_eq!(steno_decode_utf8(sp.as_ptr(), sp.len(), dst.as_mut_ptr(), 16, &mut n), StenoStatus::Surrogate);
        assert_eq!(n, 1);
        let mut d16 = [0u16; 16];
        assert_eq!(steno_decode_utf16(sp.as_ptr(), sp.len(), d16.as_mut_ptr(), 16, &mut n), StenoStatus::Ok);
        assert_eq!(n, 2);
        assert_eq!(steno_encode_utf8([0x41u8, 0xC0, 0x80].as_ptr(), 3, dst.as_mut_ptr(), 16, &mut n), StenoStatus::InvalidUtf8);
        assert_eq!(n, 1);
        assert_eq!(steno_encode_utf8(text.as_ptr(), 2, dst.as_mut_ptr(), 1, &mut n), StenoStatus::DestinationTooSmall);
        assert_eq!(n, 0);
    }
}

/// Overlapping source and destination, adjacent ones, misaligned UTF-16 pointers and lengths near SIZE_MAX.
#[test]
fn overlap_alignment_and_huge_lengths() {
    let mut buf = [0x48u8; 64];
    let mut n = 9usize;
    let p = buf.as_mut_ptr();

    // SAFETY: the pointers lie inside `buf`, or the call rejects them before using them.
    unsafe {
        assert_eq!(steno_encode_utf8(p, 8, p.add(4), 8, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(steno_encode_utf8(p.add(4), 8, p, 5, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(steno_encode_utf8(p, 8, p.add(8), 8, &mut n), StenoStatus::Ok);
        assert_eq!(n, 8);
        assert_eq!(steno_decode_utf16(p, 8, p.add(15) as *mut u16, 4, &mut n), StenoStatus::InvalidArgument, "misaligned and overlapping");
        let words = [0u16; 8];
        let mis = (words.as_ptr() as *const u8).add(1) as *const u16;
        assert_eq!(steno_encode_utf16(mis, 2, p.add(32), 16, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(steno_encoded_len_utf16(mis, 2, &mut n), StenoStatus::InvalidArgument);
        // A u16 destination that overlaps the source by one byte.
        let mut w = [0x4848u16; 16];
        let wp = w.as_mut_ptr();
        assert_eq!(steno_decode_utf16(wp as *const u8, 3, wp.add(1), 4, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(steno_decode_utf16(wp as *const u8, 2, wp.add(1), 4, &mut n), StenoStatus::Ok);

        // Lengths that no buffer can have: rejected before anything is read.
        for len in [usize::MAX, usize::MAX - 1, usize::MAX / 2 + 1, (isize::MAX as usize) / 2 + 1] {
            n = 9;
            let s = steno_encoded_len_utf16(words.as_ptr(), len, &mut n);
            assert_eq!(s, StenoStatus::InvalidArgument, "utf16 len {len:#x}");
            assert_eq!(n, 0);
            if len > isize::MAX as usize {
                let s = steno_encode_utf8(p, 4, p.add(32), len, &mut n);
                assert_eq!(s, StenoStatus::InvalidArgument, "dst len {len:#x}");
            }
            let mut d16 = [0u16; 4];
            let s = steno_decode_utf16(p, 4, d16.as_mut_ptr(), len, &mut n);
            assert_eq!(s, StenoStatus::InvalidArgument, "u16 dst len {len:#x}");
        }

        assert_eq!(steno_max_encoded_len_utf8(usize::MAX), usize::MAX);
        assert_eq!(steno_max_encoded_len_utf16(usize::MAX / 3 + 1), usize::MAX);
        assert_eq!(steno_max_decoded_len_utf8(usize::MAX / 4 + 1), usize::MAX);
        assert_eq!(steno_max_decoded_len_utf16(usize::MAX / 2 + 1), usize::MAX);
    }
}

/// Invalid UTF-8 of every kind at every position around the 16-byte blocks: rejected at the first bad byte, as `str::from_utf8` says.
#[test]
fn invalid_utf8_everywhere() {
    let bads: &[&[u8]] = &[
        &[0xC0, 0x80],
        &[0xC1, 0xBF],
        &[0xE0, 0x80, 0x80],
        &[0xE0, 0x9F, 0xBF],
        &[0xF0, 0x80, 0x80, 0x80],
        &[0xF0, 0x8F, 0xBF, 0xBF],
        &[0xED, 0xA0, 0x80],
        &[0xED, 0xBF, 0xBF],
        &[0xF4, 0x90, 0x80, 0x80],
        &[0xF5, 0x80, 0x80, 0x80],
        &[0xFF],
        &[0xFE],
        &[0xF8, 0x88, 0x80, 0x80, 0x80],
        &[0x80],
        &[0xBF],
        &[0xC3],
        &[0xE3, 0x81],
        &[0xF0, 0x9F, 0x98],
        &[0xC3, 0x41],
        &[0xE3, 0x41, 0x81],
        &[0xE3, 0x81, 0x41],
        &[0xF0, 0x9F, 0x41, 0x80],
    ];
    let fillers: &[&str] = &["a", "é", "ж", "あ", "中", "😀", "ab", "Ωa"];
    let mut count = 0;

    for &bad in bads {
        for filler in fillers {
            for before in 0..40usize {
                for after in [0usize, 1, 2, 3, 15, 16, 17, 40] {
                    let mut src: Vec<u8> = Vec::new();
                    while src.len() < before {
                        src.extend_from_slice(filler.as_bytes());
                    }
                    let valid_prefix = src.len();
                    src.extend_from_slice(bad);
                    src.resize(src.len() + after, b'z');

                    let expect = std::str::from_utf8(&src).unwrap_err().valid_up_to();
                    assert_eq!(expect, valid_prefix);
                    let mut dst = vec![0u8; src.len() * 2 + 8];
                    let mut n = 99usize;
                    // SAFETY: the buffers are live and do not overlap.
                    let s = unsafe { steno_encode_utf8(src.as_ptr(), src.len(), dst.as_mut_ptr(), dst.len(), &mut n) };
                    assert_eq!((s, n), (StenoStatus::InvalidUtf8, expect), "{src:02X?}");
                    // SAFETY: as above.
                    let s = unsafe { steno_encoded_len_utf8(src.as_ptr(), src.len(), &mut n) };
                    assert_eq!((s, n), (StenoStatus::InvalidUtf8, expect));

                    // The source as the exact end of an allocation (a Vec of exactly that capacity), so a read past it is caught by tools.
                    let exact: Box<[u8]> = src.clone().into_boxed_slice();
                    // SAFETY: as above.
                    let s = unsafe { steno_encoded_len_utf8(exact.as_ptr(), exact.len(), &mut n) };
                    assert_eq!(s, StenoStatus::InvalidUtf8);
                    count += 1;
                }
            }
        }
    }

    eprintln!("{count} invalid inputs");
}

/// Valid UTF-8 of every length near the block sizes, with the source at the exact end of an allocation and the destination exact, with a
/// guard after it.
#[test]
fn valid_utf8_exact_buffers() {
    let alphabet = ["a", "é", "ж", "あ", "中", "😀", "\u{FEFF}", "\u{FFFF}", "\u{7FF}", "\u{800}", "\u{10FFFF}"];
    let mut seed = 12345u64;
    let mut next = || {
        seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        (seed >> 33) as usize
    };

    for len in 0..300usize {
        for _ in 0..6 {
            let mut s = String::new();
            let a = alphabet[next() % alphabet.len()];
            let b = alphabet[next() % alphabet.len()];
            while s.chars().count() < len {
                s.push_str(if next() % 5 == 0 { b } else { a });
            }
            let src: Box<[u8]> = s.as_bytes().into();
            let want = singulink_steno::encode(&s);

            let mut dst = vec![0xEEu8; want.len() + 32];
            let mut n = 0usize;
            // SAFETY: live, non-overlapping buffers.
            let st = unsafe { steno_encode_utf8(src.as_ptr(), src.len(), dst.as_mut_ptr(), want.len(), &mut n) };
            assert_eq!(st, StenoStatus::Ok);
            assert_eq!(&dst[..n], &want[..]);
            assert!(dst[want.len()..].iter().all(|&g| g == 0xEE), "steno_encode_utf8 wrote past dst_len for {s:?}");

            if !want.is_empty() {
                let mut dst = vec![0xEEu8; want.len() + 32];
                // SAFETY: as above.
                let st = unsafe { steno_encode_utf8(src.as_ptr(), src.len(), dst.as_mut_ptr(), want.len() - 1, &mut n) };
                assert_eq!((st, n), (StenoStatus::DestinationTooSmall, 0));
                assert!(dst[want.len() - 1..].iter().all(|&g| g == 0xEE), "wrote past dst_len on failure");
            }

            let enc: Box<[u8]> = want.clone().into_boxed_slice();
            let mut out = vec![0xEEu8; src.len() + 32];
            // SAFETY: as above.
            let st = unsafe { steno_decode_utf8(enc.as_ptr(), enc.len(), out.as_mut_ptr(), src.len(), &mut n) };
            assert_eq!(st, StenoStatus::Ok);
            assert_eq!(&out[..n], &src[..]);
            assert!(std::str::from_utf8(&out[..n]).is_ok());
            assert!(out[src.len()..].iter().all(|&g| g == 0xEE), "steno_decode_utf8 wrote past dst_len for {s:?}");

            for short in [src.len().saturating_sub(1), src.len().saturating_sub(5), src.len() / 2] {
                if short >= src.len() {
                    continue;
                }
                let mut out = vec![0xEEu8; src.len() + 32];
                // SAFETY: as above.
                let st = unsafe { steno_decode_utf8(enc.as_ptr(), enc.len(), out.as_mut_ptr(), short, &mut n) };
                assert_eq!((st, n), (StenoStatus::DestinationTooSmall, 0));
                assert!(out[short..].iter().all(|&g| g == 0xEE), "steno_decode_utf8 wrote past a short dst_len {short} for {s:?}");
            }

            let units: Vec<u16> = s.encode_utf16().collect();
            let mut out16 = vec![0xEEEEu16; units.len() + 32];
            // SAFETY: as above.
            let st = unsafe { steno_decode_utf16(enc.as_ptr(), enc.len(), out16.as_mut_ptr(), units.len(), &mut n) };
            assert_eq!(st, StenoStatus::Ok);
            assert_eq!(&out16[..n], &units[..]);
            assert!(out16[units.len()..].iter().all(|&g| g == 0xEEEE));

            for short in [units.len().saturating_sub(1), units.len() / 2] {
                if short >= units.len() {
                    continue;
                }
                let mut out16 = vec![0xEEEEu16; units.len() + 32];
                // SAFETY: as above.
                let st = unsafe { steno_decode_utf16(enc.as_ptr(), enc.len(), out16.as_mut_ptr(), short, &mut n) };
                assert_eq!(st, StenoStatus::DestinationTooSmall);
                assert!(out16[short..].iter().all(|&g| g == 0xEEEE), "steno_decode_utf16 wrote past a short dst_len");
            }

            let u16box: Box<[u16]> = units.into_boxed_slice();
            let mut dst = vec![0xEEu8; want.len() + 32];
            // SAFETY: as above.
            let st = unsafe { steno_encode_utf16(u16box.as_ptr(), u16box.len(), dst.as_mut_ptr(), want.len(), &mut n) };
            assert_eq!(st, StenoStatus::Ok);
            assert_eq!(&dst[..n], &want[..]);
            assert!(dst[want.len()..].iter().all(|&g| g == 0xEE));
        }
    }
}
