//! The C functions, called from Rust: the same checks as `tests/c/test.c`, for platforms where the C program is not built.

use core::ffi::CStr;
use core::ptr;

use steno::*;

fn encode_utf8(s: &[u8], cap: usize) -> (StenoStatus, Vec<u8>, usize) {
    let mut buf = vec![0u8; cap];
    let mut n = usize::MAX;
    // SAFETY: the pointers and lengths describe live buffers that do not overlap.
    let st = unsafe { steno_encode_utf8(s.as_ptr(), s.len(), buf.as_mut_ptr(), buf.len(), &mut n) };
    buf.truncate(if st == StenoStatus::Ok { n } else { 0 });
    (st, buf, n)
}

fn decode_utf8(b: &[u8], cap: usize) -> (StenoStatus, Vec<u8>, usize) {
    let mut buf = vec![0u8; cap];
    let mut n = usize::MAX;
    // SAFETY: as above.
    let st = unsafe { steno_decode_utf8(b.as_ptr(), b.len(), buf.as_mut_ptr(), buf.len(), &mut n) };
    buf.truncate(if st == StenoStatus::Ok { n } else { 0 });
    (st, buf, n)
}

fn decode_utf16(b: &[u8], cap: usize) -> (StenoStatus, Vec<u16>, usize) {
    let mut buf = vec![0u16; cap];
    let mut n = usize::MAX;
    // SAFETY: as above.
    let st = unsafe { steno_decode_utf16(b.as_ptr(), b.len(), buf.as_mut_ptr(), buf.len(), &mut n) };
    buf.truncate(if st == StenoStatus::Ok { n } else { 0 });
    (st, buf, n)
}

#[test]
fn valid_values_both_ways() {
    for text in ["Hi!\tOK\r\n", "Мир, «да»!", "東京でテストをするOK", "한국어 문장", "Hi \u{1F600}\u{1F44D}", ""] {
        let bytes = singulink_steno::encode(text);
        let units: Vec<u16> = text.encode_utf16().collect();

        let (st, got, _) = encode_utf8(text.as_bytes(), 256);
        assert_eq!(st, StenoStatus::Ok);
        assert_eq!(got, bytes);

        let mut buf = vec![0u8; 256];
        let mut n = 0;
        // SAFETY: as above.
        let st = unsafe { steno_encode_utf16(units.as_ptr(), units.len(), buf.as_mut_ptr(), buf.len(), &mut n) };
        assert_eq!((st, &buf[..n]), (StenoStatus::Ok, &bytes[..]));

        assert_eq!(decode_utf8(&bytes, 256).1, text.as_bytes());
        assert_eq!(decode_utf16(&bytes, 256).1, units);

        // SAFETY: as above.
        unsafe {
            assert_eq!(steno_encoded_len_utf8(text.as_ptr(), text.len(), &mut n), StenoStatus::Ok);
            assert_eq!(n, bytes.len());
            assert_eq!(steno_encoded_len_utf16(units.as_ptr(), units.len(), &mut n), StenoStatus::Ok);
            assert_eq!(n, bytes.len());
            assert_eq!(steno_decoded_len_utf8(bytes.as_ptr(), bytes.len(), &mut n), StenoStatus::Ok);
            assert_eq!(n, text.len());
            assert_eq!(steno_decoded_len_utf16(bytes.as_ptr(), bytes.len(), &mut n), StenoStatus::Ok);
            assert_eq!(n, units.len());
        }

        // Exact and short destinations.
        assert_eq!(encode_utf8(text.as_bytes(), bytes.len()).0, StenoStatus::Ok);

        if !bytes.is_empty() {
            assert_eq!(encode_utf8(text.as_bytes(), bytes.len() - 1).0, StenoStatus::DestinationTooSmall);
            assert_eq!(decode_utf8(&bytes, text.len() - 1).0, StenoStatus::DestinationTooSmall);
        }

        assert!(bytes.len() <= steno_max_encoded_len_utf8(text.len()));
        assert!(bytes.len() <= steno_max_encoded_len_utf16(units.len()));
        assert!(text.len() <= steno_max_decoded_len_utf8(bytes.len()));
        assert!(units.len() <= steno_max_decoded_len_utf16(bytes.len()));
    }
}

#[test]
fn errors() {
    let cases: [(&[u8], StenoStatus, usize); 6] = [
        (&[0x48, 0x00], StenoStatus::ZeroByte, 1),
        (&[0x48, 0xFF], StenoStatus::UnusedLead, 1),
        (&[0x48, 0xE9], StenoStatus::Truncated, 1),
        (&[0xF9, 0xFF, 0xFF, 0xFF], StenoStatus::PastRange, 0),
        (&[0xF8, 0xC5, 0x20, 0xF8, 0xC9, 0x40], StenoStatus::SurrogatePair, 3),
        (&[0x48, 0xE9, 0x05], StenoStatus::InvalidTrail, 1),
    ];

    for (bytes, status, at) in cases {
        // The UTF-8 decoder meets the high surrogate before the pair, and that has no UTF-8 form.
        let utf8 = if status == StenoStatus::SurrogatePair { (StenoStatus::Surrogate, 0) } else { (status, at) };
        let (st, _, n) = decode_utf8(bytes, 64);
        assert_eq!((st, n), utf8);
        let (st, _, n) = decode_utf16(bytes, 64);
        assert_eq!((st, n), (status, at));
    }

    // A lone surrogate has no UTF-8 form, but UTF-16 holds it.
    let lone = [0x48, 0xF8, 0xC5, 0x20, 0x49, 0xF8, 0xC9, 0x40];
    assert_eq!(decode_utf8(&lone, 64).0, StenoStatus::Surrogate);
    assert_eq!(decode_utf8(&lone, 64).2, 1);
    assert_eq!(decode_utf16(&lone, 64).1, [0x61, 0xD800, 0x62, 0xDC00]);

    let (st, _, n) = encode_utf8(b"ab\xFF", 16);
    assert_eq!((st, n), (StenoStatus::InvalidUtf8, 2));
}

#[test]
fn invalid_arguments() {
    let mut buf = [0u8; 16];
    let mut n = 0;

    // SAFETY: the pointers are null, or describe live buffers; the functions must reject the bad combinations before using them.
    unsafe {
        assert_eq!(steno_encode_utf8(ptr::null(), 1, buf.as_mut_ptr(), 16, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(steno_encode_utf8(b"a".as_ptr(), 1, buf.as_mut_ptr(), 16, ptr::null_mut()), StenoStatus::InvalidArgument);
        assert_eq!(steno_encode_utf8(ptr::null(), 0, ptr::null_mut(), 0, &mut n), StenoStatus::Ok);
        assert_eq!(n, 0);
        let p = buf.as_mut_ptr();
        assert_eq!(steno_decode_utf8(p, 4, p.add(2), 4, &mut n), StenoStatus::InvalidArgument);
        assert_eq!(steno_encode_utf16(p.add(1).cast::<u16>(), 2, p.add(8), 8, &mut n), StenoStatus::InvalidArgument);
        assert!(!steno_is_ascii_text(ptr::null(), 3));
        assert!(steno_is_ascii_text(ptr::null(), 0));
    }

    // SAFETY: the function returns a pointer to a static C string.
    let name = unsafe { CStr::from_ptr(steno_status_name(StenoStatus::PastRange as i32)) };
    assert_eq!(name.to_str().unwrap(), "past-range");
    // SAFETY: as above.
    let name = unsafe { CStr::from_ptr(steno_status_name(42)) };
    assert_eq!(name.to_str().unwrap(), "unknown");
}

#[test]
fn invalid_and_truncated_utf8_around_the_blocks() {
    // Text of 2- and 3-byte sequences, broken at every position up to 48 bytes in: a stray continuation byte, a truncated sequence at the
    // end, and a surrogate encoded in UTF-8. The C function must report invalid UTF-8 at the first bad byte, as `str::from_utf8` does.
    let text = "Всё течёт, всё меняется: पानी बहता है и 漢字かな交じり文";
    let bytes = text.as_bytes();

    for at in 0..48.min(bytes.len()) {
        let mut stray = bytes.to_vec();
        stray.insert(at, 0x80);
        let truncated = &bytes[..at];
        let mut surrogate = bytes[..at].to_vec();
        surrogate.extend_from_slice(&[0xED, 0xA0, 0x80]);

        for input in [&stray[..], truncated, &surrogate[..]] {
            let (st, out, n) = encode_utf8(input, 512);

            match core::str::from_utf8(input) {
                Ok(s) => {
                    assert_eq!(st, StenoStatus::Ok);
                    assert_eq!(out, singulink_steno::encode(s));
                }
                Err(e) => assert_eq!((st, n), (StenoStatus::InvalidUtf8, e.valid_up_to())),
            }
        }
    }
}

#[test]
fn out_len_inside_the_source_or_the_destination_is_rejected() {
    let mut src = [0u8; 64];
    src[..29].copy_from_slice(b"Hello, world! 123456789012345");
    let mut dst = [0u8; 64];
    let p = src.as_mut_ptr();

    // SAFETY: the pointers describe live buffers; `out_len` points into one of them, which the function must reject without writing.
    unsafe {
        let st = steno_encode_utf8(p, 29, dst.as_mut_ptr(), 64, p.add(8).cast::<usize>());
        assert_eq!(st, StenoStatus::InvalidArgument);
        assert_eq!(&src[..29], b"Hello, world! 123456789012345", "nothing was written into the source");

        let d = dst.as_mut_ptr();
        let st = steno_encode_utf8(p, 29, d, 64, d.add(16).cast::<usize>());
        assert_eq!(st, StenoStatus::InvalidArgument);

        let st = steno_encoded_len_utf8(p, 29, p.cast::<usize>());
        assert_eq!(st, StenoStatus::InvalidArgument);
    }
}
