//! Helpers shared by the integration tests: a seeded random generator, random text, the vector file reader and a plain reference codec.

#![allow(dead_code)]

pub mod reference;

use std::path::PathBuf;

/// SplitMix64: a small seeded generator, so that every run tests the same inputs.
pub struct Rng(u64);

impl Rng {
    pub fn new(seed: u64) -> Self {
        Rng(seed)
    }

    pub fn next_u64(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    /// A number in `0..n`.
    pub fn below(&mut self, n: u64) -> u64 {
        self.next_u64() % n
    }

    pub fn range(&mut self, lo: u32, hi: u32) -> u32 {
        lo + self.below(u64::from(hi - lo + 1)) as u32
    }

    pub fn chance(&mut self, percent: u64) -> bool {
        self.below(100) < percent
    }
}

/// Groups of code points that random text is drawn from: scripts, kana, kanji, Hangul, emoji, controls, the edges of the windows and of the
/// layout ranges, and surrogates (when `surrogates` is true).
fn pick_code_point(rng: &mut Rng, surrogates: bool) -> u32 {
    const GROUPS: &[(u32, u32)] = &[
        (0x20, 0x7E),
        (0x20, 0x7E),
        (0x00, 0x1F),
        (0x7F, 0xBF),
        (0xC0, 0x24F),
        (0x250, 0x36F),
        (0x370, 0x3FF),
        (0x400, 0x52F),
        (0x530, 0x5FF),
        (0x600, 0x6FF),
        (0x900, 0x97F),
        (0xE00, 0xE7F),
        (0xF00, 0xFFF),
        (0x1200, 0x137F),
        (0x1E00, 0x1FFF),
        (0x2000, 0x20FF),
        (0x2100, 0x2FFF),
        (0x3000, 0x303F),
        (0x3040, 0x30FF),
        (0x3041, 0x3096),
        (0x30A1, 0x30FC),
        (0x3100, 0x33FF),
        (0x3400, 0x4DFF),
        (0x4E00, 0x9FFF),
        (0x4E00, 0x9FFF),
        (0xA000, 0xABFF),
        (0xAC00, 0xD7A3),
        (0xAC00, 0xD7A3),
        (0xD7A4, 0xD7FF),
        (0xE000, 0xF8FF),
        (0xFE00, 0xFEFF),
        (0xFF00, 0xFFFF),
        (0xFF00, 0xFF1F),
        (0x1_0000, 0x1_0FFF),
        (0x1_F300, 0x1_F6FF),
        (0x1_FD00, 0x1_FFFF),
        (0x2_0000, 0x3_FFFF),
        (0x4_0000, 0x10_FFFF),
        (0xF_FF00, 0x10_FFFF),
    ];

    if surrogates && rng.chance(4) {
        return rng.range(0xD800, 0xDFFF);
    }

    let (lo, hi) = GROUPS[rng.below(GROUPS.len() as u64) as usize];
    rng.range(lo, hi)
}

/// Random text as code points: runs of nearby code points (as real text has), mixed with jumps to other groups. With `surrogates`, lone
/// surrogate code points occur too, but never a high one directly followed by a low one, so the result is always a value.
pub fn random_value(rng: &mut Rng, max_len: usize, surrogates: bool) -> Vec<u32> {
    let len = rng.below(max_len as u64 + 1) as usize;
    let mut out: Vec<u32> = Vec::with_capacity(len);
    let mut base = pick_code_point(rng, surrogates);

    while out.len() < len {
        let c = if rng.chance(25) {
            base = pick_code_point(rng, surrogates);
            base
        } else if rng.chance(30) {
            rng.range(0x20, 0x7E)
        } else {
            // A code point near the current one, in the same block of 128 most of the time.
            let lo = base & !0x7F;
            (lo + rng.below(0x80) as u32).min(0x10_FFFF)
        };

        if let Some(&prev) = out.last() {
            if (0xD800..=0xDBFF).contains(&prev) && (0xDC00..=0xDFFF).contains(&c) {
                continue;
            }
        }

        out.push(c);
    }

    out
}

/// Random text that a `str` can hold.
pub fn random_string(rng: &mut Rng, max_len: usize) -> String {
    random_value(rng, max_len, false).into_iter().map(|c| char::from_u32(c).unwrap()).collect()
}

/// Converts a value to UTF-16 (FORMAT.md §3).
pub fn to_utf16(cps: &[u32]) -> Vec<u16> {
    let mut out = Vec::new();

    for &c in cps {
        if c < 0x1_0000 {
            out.push(c as u16);
        } else {
            let v = c - 0x1_0000;
            out.push(0xD800 + (v >> 10) as u16);
            out.push(0xDC00 + (v & 0x3FF) as u16);
        }
    }

    out
}

/// Returns the value as a `String`, or `None` if it has a surrogate code point.
pub fn to_string(cps: &[u32]) -> Option<String> {
    cps.iter().map(|&c| char::from_u32(c)).collect()
}

pub fn repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..")
}

/// One line of a vector file.
#[derive(Debug, Clone)]
pub enum Vector {
    Valid { name: String, cps: Vec<u32>, bytes: Vec<u8> },
    Invalid { name: String, bytes: Vec<u8>, reason: String },
    NotAValue { name: String, cps: Vec<u32> },
}

fn parse_cps(s: &str) -> Vec<u32> {
    if s == "-" {
        return Vec::new();
    }

    s.split(' ').map(|h| u32::from_str_radix(h, 16).expect("code point")).collect()
}

fn parse_bytes(s: &str) -> Vec<u8> {
    if s == "-" {
        return Vec::new();
    }

    assert!(s.len().is_multiple_of(2), "odd number of hex digits");
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).expect("byte")).collect()
}

/// Reads a vector file (FORMAT.md §12).
pub fn read_vectors(path: &std::path::Path) -> Vec<Vector> {
    let text = std::fs::read_to_string(path).unwrap_or_else(|e| panic!("cannot read {}: {e}", path.display()));
    let mut out = Vec::new();

    for line in text.lines() {
        if line.is_empty() || line.starts_with('#') {
            continue;
        }

        let f: Vec<&str> = line.split('\t').collect();

        out.push(match f[0] {
            "valid" => Vector::Valid { name: f[1].into(), cps: parse_cps(f[2]), bytes: parse_bytes(f[3]) },
            "invalid" => Vector::Invalid { name: f[1].into(), bytes: parse_bytes(f[2]), reason: f[3].into() },
            "not-a-value" => Vector::NotAValue { name: f[1].into(), cps: parse_cps(f[2]) },
            other => panic!("unknown kind {other}"),
        });
    }

    out
}
