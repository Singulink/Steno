//! The primitive operations of the kernels with the SSE2 and SSSE3 intrinsics of x86_64.
//!
//! Every function enables SSSE3, so the kernels (which enable it too) can call them safely, and nothing else can call them without first
//! detecting SSSE3. The loads and stores are the only unsafe code: they take references to arrays of exactly one vector.

use core::arch::x86_64::*;

#[path = "kernels.rs"]
pub(super) mod kernels;

/// Eight 16-bit lanes.
pub(super) type V16 = __m128i;
/// Sixteen 8-bit lanes.
pub(super) type V8 = __m128i;

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn load_u16(a: &[u16; 8]) -> V16 {
    // SAFETY: `a` is 16 readable bytes, and `_mm_loadu_si128` has no alignment requirement.
    unsafe { _mm_loadu_si128(a.as_ptr().cast()) }
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn load_u8(a: &[u8; 16]) -> V8 {
    // SAFETY: `a` is 16 readable bytes, and `_mm_loadu_si128` has no alignment requirement.
    unsafe { _mm_loadu_si128(a.as_ptr().cast()) }
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn store_u16(a: &mut [u16; 8], v: V16) {
    // SAFETY: `a` is 16 writable bytes that nothing else uses while it is borrowed, and `_mm_storeu_si128` has no alignment requirement.
    unsafe { _mm_storeu_si128(a.as_mut_ptr().cast(), v) }
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn store_u8(a: &mut [u8; 16], v: V8) {
    // SAFETY: `a` is 16 writable bytes that nothing else uses while it is borrowed, and `_mm_storeu_si128` has no alignment requirement.
    unsafe { _mm_storeu_si128(a.as_mut_ptr().cast(), v) }
}

/// A vector with `x` in its low eight bytes and zeros above, without a memory access.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn from_u64(x: u64) -> V8 {
    _mm_cvtsi64_si128(x as i64)
}

/// The low eight bytes of `v`.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn low_u64(v: V8) -> u64 {
    _mm_cvtsi128_si64(v) as u64
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn splat16(x: u16) -> V16 {
    _mm_set1_epi16(x as i16)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn splat8(x: u8) -> V8 {
    _mm_set1_epi8(x as i8)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn add16(a: V16, b: V16) -> V16 {
    _mm_add_epi16(a, b)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn sub16(a: V16, b: V16) -> V16 {
    _mm_sub_epi16(a, b)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn sub8(a: V8, b: V8) -> V8 {
    _mm_sub_epi8(a, b)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn or16(a: V16, b: V16) -> V16 {
    _mm_or_si128(a, b)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn or8(a: V8, b: V8) -> V8 {
    _mm_or_si128(a, b)
}

/// Unsigned `a < b` in each 16-bit lane, as all ones or all zeros.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn lt16(a: V16, b: V16) -> V16 {
    let bias = _mm_set1_epi16(i16::MIN);
    _mm_cmplt_epi16(_mm_xor_si128(a, bias), _mm_xor_si128(b, bias))
}

/// Unsigned `a < b` in each 8-bit lane, as all ones or all zeros.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn lt8(a: V8, b: V8) -> V8 {
    let bias = _mm_set1_epi8(i8::MIN);
    _mm_cmplt_epi8(_mm_xor_si128(a, bias), _mm_xor_si128(b, bias))
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn eq16(a: V16, b: V16) -> V16 {
    _mm_cmpeq_epi16(a, b)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn eq8(a: V8, b: V8) -> V8 {
    _mm_cmpeq_epi8(a, b)
}

/// `a` where the mask is set, `b` elsewhere.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn sel16(mask: V16, a: V16, b: V16) -> V16 {
    _mm_or_si128(_mm_and_si128(mask, a), _mm_andnot_si128(mask, b))
}

/// The low bytes of the 16-bit lanes of `a`, then of `b`. Only lanes with values up to 255 are exact.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn narrow16(a: V16, b: V16) -> V8 {
    _mm_packus_epi16(a, b)
}

/// Masks of 16-bit lanes (all ones or all zeros) as masks of 8-bit lanes: `a`, then `b`.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn narrow_mask16(a: V16, b: V16) -> V8 {
    _mm_packs_epi16(a, b)
}

/// The low eight bytes of `v`, zero-extended to 16 bits.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn widen_lo(v: V8) -> V16 {
    _mm_unpacklo_epi8(v, _mm_setzero_si128())
}

/// The high eight bytes of `v`, zero-extended to 16 bits.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn widen_hi(v: V8) -> V16 {
    _mm_unpackhi_epi8(v, _mm_setzero_si128())
}

/// The top bit of each 8-bit lane, lane `i` as bit `i`.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn mask8(v: V8) -> u32 {
    _mm_movemask_epi8(v) as u32
}

/// Bytes of `v` picked by `control`; an index with the top bit set gives 0.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn shuffle8(v: V8, control: V8) -> V8 {
    _mm_shuffle_epi8(v, control)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn shr16_8(v: V16) -> V16 {
    _mm_srli_epi16::<8>(v)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn shl16_8(v: V16) -> V16 {
    _mm_slli_epi16::<8>(v)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn as8(v: V16) -> V8 {
    v
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn as16(v: V8) -> V16 {
    v
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn and16(a: V16, b: V16) -> V16 {
    _mm_and_si128(a, b)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn and8(a: V8, b: V8) -> V8 {
    _mm_and_si128(a, b)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn shr16<const N: i32>(v: V16) -> V16 {
    _mm_srli_epi16::<N>(v)
}

#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn shl16<const N: i32>(v: V16) -> V16 {
    _mm_slli_epi16::<N>(v)
}

/// The bytes of `v` moved one lane up (lane `i` gets lane `i - 1`), with 0 in lane 0.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn prev1(v: V8) -> V8 {
    _mm_slli_si128::<1>(v)
}

/// The bytes of `v` moved two lanes up, with 0 in lanes 0 and 1.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn prev2(v: V8) -> V8 {
    _mm_slli_si128::<2>(v)
}

/// The high 16 bits of each unsigned 16 by 16-bit product.
#[inline]
#[target_feature(enable = "ssse3")]
pub(super) fn mulhi16(a: V16, b: V16) -> V16 {
    _mm_mulhi_epu16(a, b)
}
