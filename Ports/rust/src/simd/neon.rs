//! The primitive operations of the kernels with the NEON intrinsics of aarch64, which every aarch64 target has.
//!
//! Every function enables NEON, as the kernels do; NEON is a baseline feature of every aarch64 target. The loads and stores are the only
//! unsafe code: they take references to arrays of exactly one vector.

use core::arch::aarch64::*;

#[path = "kernels.rs"]
pub(super) mod kernels;

/// Eight 16-bit lanes.
pub(super) type V16 = uint16x8_t;
/// Sixteen 8-bit lanes.
pub(super) type V8 = uint8x16_t;

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn load_u16(a: &[u16; 8]) -> V16 {
    // SAFETY: `a` is eight readable `u16`, and `vld1q_u16` needs only the alignment of `u16`, which a reference has.
    unsafe { vld1q_u16(a.as_ptr()) }
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn load_u8(a: &[u8; 16]) -> V8 {
    // SAFETY: `a` is 16 readable bytes.
    unsafe { vld1q_u8(a.as_ptr()) }
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn store_u16(a: &mut [u16; 8], v: V16) {
    // SAFETY: `a` is eight writable `u16` that nothing else uses while it is borrowed, aligned for `u16`.
    unsafe { vst1q_u16(a.as_mut_ptr(), v) }
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn store_u8(a: &mut [u8; 16], v: V8) {
    // SAFETY: `a` is 16 writable bytes that nothing else uses while it is borrowed.
    unsafe { vst1q_u8(a.as_mut_ptr(), v) }
}

/// A vector with `x` in its low eight bytes and zeros above, without a memory access.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn from_u64(x: u64) -> V8 {
    vcombine_u8(vcreate_u8(x), vcreate_u8(0))
}

/// The low eight bytes of `v`.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn low_u64(v: V8) -> u64 {
    vgetq_lane_u64::<0>(vreinterpretq_u64_u8(v))
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn splat16(x: u16) -> V16 {
    vdupq_n_u16(x)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn splat8(x: u8) -> V8 {
    vdupq_n_u8(x)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn add16(a: V16, b: V16) -> V16 {
    vaddq_u16(a, b)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn sub16(a: V16, b: V16) -> V16 {
    vsubq_u16(a, b)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn sub8(a: V8, b: V8) -> V8 {
    vsubq_u8(a, b)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn or16(a: V16, b: V16) -> V16 {
    vorrq_u16(a, b)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn or8(a: V8, b: V8) -> V8 {
    vorrq_u8(a, b)
}

/// Unsigned `a < b` in each 16-bit lane, as all ones or all zeros.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn lt16(a: V16, b: V16) -> V16 {
    vcltq_u16(a, b)
}

/// Unsigned `a < b` in each 8-bit lane, as all ones or all zeros.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn lt8(a: V8, b: V8) -> V8 {
    vcltq_u8(a, b)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn eq16(a: V16, b: V16) -> V16 {
    vceqq_u16(a, b)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn eq8(a: V8, b: V8) -> V8 {
    vceqq_u8(a, b)
}

/// `a` where the mask is set, `b` elsewhere.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn sel16(mask: V16, a: V16, b: V16) -> V16 {
    vbslq_u16(mask, a, b)
}

/// The low bytes of the 16-bit lanes of `a`, then of `b`. Only lanes with values up to 255 are exact.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn narrow16(a: V16, b: V16) -> V8 {
    vcombine_u8(vmovn_u16(a), vmovn_u16(b))
}

/// Masks of 16-bit lanes (all ones or all zeros) as masks of 8-bit lanes: `a`, then `b`.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn narrow_mask16(a: V16, b: V16) -> V8 {
    vcombine_u8(vmovn_u16(a), vmovn_u16(b))
}

/// The low eight bytes of `v`, zero-extended to 16 bits.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn widen_lo(v: V8) -> V16 {
    vmovl_u8(vget_low_u8(v))
}

/// The high eight bytes of `v`, zero-extended to 16 bits.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn widen_hi(v: V8) -> V16 {
    vmovl_high_u8(v)
}

/// The top bit of each 8-bit lane (of a mask: all ones or all zeros), lane `i` as bit `i`.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn mask8(v: V8) -> u32 {
    const BITS: [u8; 16] = [1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128];
    let bits = vandq_u8(vcltq_s8(vreinterpretq_s8_u8(v), vdupq_n_s8(0)), load_u8(&BITS));
    u32::from(vaddv_u8(vget_low_u8(bits))) | (u32::from(vaddv_u8(vget_high_u8(bits))) << 8)
}

/// Bytes of `v` picked by `control`; an index of 16 or more (the top bit set, in the tables) gives 0.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn shuffle8(v: V8, control: V8) -> V8 {
    vqtbl1q_u8(v, control)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn shr16_8(v: V16) -> V16 {
    vshrq_n_u16::<8>(v)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn shl16_8(v: V16) -> V16 {
    vshlq_n_u16::<8>(v)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn as8(v: V16) -> V8 {
    vreinterpretq_u8_u16(v)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn as16(v: V8) -> V16 {
    vreinterpretq_u16_u8(v)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn and16(a: V16, b: V16) -> V16 {
    vandq_u16(a, b)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn and8(a: V8, b: V8) -> V8 {
    vandq_u8(a, b)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn shr16<const N: i32>(v: V16) -> V16 {
    vshrq_n_u16::<N>(v)
}

#[inline]
#[target_feature(enable = "neon")]
pub(super) fn shl16<const N: i32>(v: V16) -> V16 {
    vshlq_n_u16::<N>(v)
}

/// The bytes of `v` moved one lane up (lane `i` gets lane `i - 1`), with 0 in lane 0.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn prev1(v: V8) -> V8 {
    vextq_u8::<15>(vdupq_n_u8(0), v)
}

/// The bytes of `v` moved two lanes up, with 0 in lanes 0 and 1.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn prev2(v: V8) -> V8 {
    vextq_u8::<14>(vdupq_n_u8(0), v)
}

/// The high 16 bits of each unsigned 16 by 16-bit product.
#[inline]
#[target_feature(enable = "neon")]
pub(super) fn mulhi16(a: V16, b: V16) -> V16 {
    let lo = vmull_u16(vget_low_u16(a), vget_low_u16(b));
    let hi = vmull_high_u16(a, b);
    vcombine_u16(vshrn_n_u32::<16>(lo), vshrn_n_u32::<16>(hi))
}
