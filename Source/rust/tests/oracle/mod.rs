//! Test support: an oracle ported line by line from the independent C# codec `Source/dotnet/Singulink.Steno.Tests/ReferenceSteno.cs` (written
//! from FORMAT.md alone), a vector reader, and a generator of text aimed at the fast paths. Shared by the test files that use the oracle (`oracle_fuzz`, `streaming_forms`, `memory`, `miri`).

#![allow(dead_code)]

use std::collections::HashMap;
use std::path::PathBuf;

// ---------- The oracle (port of ReferenceSteno.cs) ----------

pub const GENERAL: u8 = 0;
pub const CJK: u8 = 1;
pub const HANGUL: u8 = 2;
pub const LETTERS: u8 = 0;
pub const HIRAGANA: u8 = 1;
pub const KATAKANA: u8 = 2;

pub type Window = (u32, u32);

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct RState {
    pub ctx: u8,
    pub main: Window,
    pub second: Option<Window>,
    pub slot: u8,
    pub conf: u8,
    pub pending: Option<Window>,
}

impl RState {
    pub fn new() -> Self {
        RState { ctx: GENERAL, main: (0xC0, 0x13F), second: None, slot: LETTERS, conf: 0, pending: None }
    }
}

#[derive(Clone, Copy, Debug)]
pub struct RRange {
    pub lo: u32,
    pub hi: u32,
    pub size: u32,
    pub first: u32,
    pub leads: u32,
}

fn block(c: u32) -> Window {
    let s = c - c % 0x80;
    (s, s + 0x7F)
}

pub fn window_of(c: u32) -> Option<Window> {
    if c <= 0x00BF {
        return None;
    }
    if c <= 0x013F {
        return Some((0x00C0, 0x013F));
    }
    if c <= 0x01BF {
        return Some((0x0140, 0x01BF));
    }
    if c <= 0x023F {
        return Some((0x01C0, 0x023F));
    }
    if c <= 0x02BF {
        return Some((0x0240, 0x02BF));
    }
    if c <= 0x02FF {
        return Some((0x02C0, 0x02FF));
    }
    if c <= 0x036F {
        return Some((0x0300, 0x036F));
    }
    if c <= 0x037F {
        return None;
    }
    if c <= 0x04FF {
        return Some(block(c));
    }
    if c <= 0x052F {
        return Some((0x0500, 0x052F));
    }
    if c <= 0x058F {
        return Some((0x0530, 0x058F));
    }
    if c <= 0x05FF {
        return Some((0x0590, 0x05FF));
    }
    if c <= 0x1FFF {
        return Some(block(c));
    }
    if c <= 0x20FF {
        return None;
    }
    if c <= 0x2FFF {
        return Some(block(c));
    }
    if c <= 0x30FF {
        return None;
    }
    if c <= 0x33FF {
        return Some(block(c));
    }
    if c <= 0x9FFF {
        return None;
    }
    if c <= 0xABFF {
        return Some(block(c));
    }
    if c <= 0xDFFF {
        return None;
    }
    if c <= 0x1FFFF {
        return Some(block(c));
    }
    if c <= 0x3FFFF {
        return None;
    }
    if c <= 0x10FFFF {
        return Some(block(c));
    }
    panic!("not a code point");
}

const RADIX: i64 = 248;

fn pow_radix(e: u32) -> i64 {
    RADIX.pow(e)
}

pub fn build_layout(s: &RState) -> Vec<RRange> {
    let mut segs: Vec<(i64, i64, u32)> = vec![(0, 0x10FFFF, 0)];

    let mut piece = |lo: i64, hi: i64, size: u32| {
        if lo > hi {
            return;
        }

        let mut next = Vec::new();

        for &g in &segs {
            if g.1 < lo || g.0 > hi {
                next.push(g);
                continue;
            }

            if g.0 < lo {
                next.push((g.0, lo - 1, g.2));
            }

            if g.1 > hi {
                next.push((hi + 1, g.1, g.2));
            }
        }

        next.push((lo, hi, size));
        next.sort_by_key(|a| a.0);
        segs = next;
    };

    let (m0, m1) = (i64::from(s.main.0), i64::from(s.main.1));

    if s.ctx == GENERAL {
        piece((0x80i64).max(m0 - RADIX), m0 - 1, 2);
        if m1 < 0x10FFFF {
            piece(m1 + 1, (0x10FFFFi64).min(m1 + RADIX), 2);
        }
    }

    piece(0x00, 0x1F, 2);
    piece(0x09, 0x0A, 1);
    piece(0x0D, 0x0D, 1);

    if s.ctx == CJK && s.slot != LETTERS {
        piece(0x20, 0x40, 1);
        piece(0x41, 0x7E, 2);
    } else {
        piece(0x20, 0x7E, 1);
    }

    piece(0x7F, 0xBF, 2);

    if s.ctx != CJK {
        piece(0x80, 0xEFF, 2);
    }

    piece(0x2000, 0x20CF, 2);

    if let Some(sw) = s.second {
        piece(i64::from(sw.0), i64::from(sw.1), 2);
    }

    match s.ctx {
        GENERAL => piece(m0, m1, 1),
        HANGUL => {
            piece(m0, m1, 2);
            piece(0x3000, 0x303F, 2);
            piece(0xAC00, 0xD7A3, 2);
            piece(0xFF00, 0xFFFF, 2);
        }
        _ => {
            piece(m0, m1, 2);
            piece(0x3000, 0x300F, 1);
            piece(0x3010, 0x30FF, 2);
            if s.slot == HIRAGANA {
                piece(0x3041, 0x3096, 1);
            }
            if s.slot == KATAKANA {
                piece(0x30A1, 0x30FC, 1);
            }
            piece(0x4E00, 0x9FFF, 2);
            piece(0xFF00, 0xFFFF, 2);
            if s.slot == LETTERS {
                piece(0xFF00, 0xFF1F, 1);
            }
        }
    }

    let mut sized: Vec<(i64, i64, u32)> = Vec::new();
    let mut i = 0;

    while i < segs.len() {
        if segs[i].2 != 0 {
            sized.push(segs[i]);
            i += 1;
            continue;
        }

        let lo = segs[i].0;
        let mut hi = segs[i].1;
        i += 1;

        while i < segs.len() && segs[i].2 == 0 {
            hi = segs[i].1;
            i += 1;
        }

        // Cut at U+10FC0 and U+20000.
        let mut start = lo;
        let mut parts = Vec::new();

        for b in [0x10FC0i64, 0x20000] {
            if start < b && hi >= b {
                parts.push((start, b - 1));
                start = b;
            }
        }

        parts.push((start, hi));

        for (plo, phi) in parts {
            let n = phi - plo + 1;
            let size = if n <= RADIX {
                2
            } else if n <= RADIX * RADIX {
                3
            } else {
                4
            };
            sized.push((plo, phi, size));
        }
    }

    let mut ranges = Vec::new();
    let mut lead: i64 = 1;
    let mut j = 0;

    while j < sized.len() {
        let (lo, mut hi, size) = sized[j];
        j += 1;

        while j < sized.len() && sized[j].2 == size && sized[j].0 == hi + 1 {
            hi = sized[j].1;
            j += 1;
        }

        let n = hi - lo + 1;
        let per = pow_radix(size - 1);
        let leads = (n + per - 1) / per;
        ranges.push(RRange { lo: lo as u32, hi: hi as u32, size, first: lead as u32, leads: leads as u32 });
        lead += leads;
    }

    assert!(lead - 1 <= 254, "layout needs more than 254 lead bytes");
    ranges
}

fn in_(c: u32, lo: u32, hi: u32) -> bool {
    lo <= c && c <= hi
}

pub fn update(s: &mut RState, c: u32, n: usize) {
    if c <= 0x7F {
        if s.ctx == CJK && (in_(c, 0x41, 0x5A) || in_(c, 0x61, 0x7A)) {
            s.slot = LETTERS;
        }
        return;
    }

    if in_(c, 0xAC00, 0xD7A3) {
        s.pending = None;
        s.ctx = HANGUL;
        return;
    }

    if in_(c, 0x3040, 0x30FF) || in_(c, 0x3400, 0x9FFF) || in_(c, 0x20000, 0x3FFFF) || (in_(c, 0x3000, 0x303F) && s.ctx != HANGUL) {
        if s.ctx != CJK {
            s.ctx = CJK;
            s.slot = LETTERS;
        }

        s.pending = None;
        if in_(c, 0x3041, 0x3096) {
            s.slot = HIRAGANA;
        } else if in_(c, 0x30A1, 0x30FA) {
            s.slot = KATAKANA;
        }
        return;
    }

    if s.ctx != GENERAL && in_(c, 0xFF00, 0xFFFF) {
        return;
    }

    let Some(w) = window_of(c) else { return };

    if s.ctx == GENERAL && w.0 == s.main.0 {
        s.conf = (s.conf + 1).min(2);
        return;
    }

    if n > 2 {
        s.second = Some(w);
    }

    if s.ctx == GENERAL {
        if s.conf > 0 {
            s.conf -= 1;
            return;
        }
    } else if s.pending.map(|p| p.0) != Some(w.0) {
        s.pending = Some(w);
        return;
    }

    if s.main.0 != w.0 {
        s.second = Some(s.main);
    } else if s.second.map(|x| x.0) == Some(w.0) {
        s.second = None;
    }

    s.main = w;
    s.ctx = GENERAL;
    s.pending = None;
    s.conf = 0;
}

/// The fields that a layout depends on: context, main window, second window, kana slot.
type LayoutKey = (u8, u32, Option<u32>, u8);

/// Layouts by the fields they depend on. Only a memo: the layout is built by `build_layout` exactly as the C# codec builds it.
#[derive(Default)]
pub struct Oracle {
    memo: HashMap<LayoutKey, std::rc::Rc<Vec<RRange>>>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RError {
    pub reason: &'static str,
    pub pos: usize,
    /// The code points decoded before the error, with their start offsets.
    pub before: Vec<(u32, usize)>,
}

impl Oracle {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn layout(&mut self, s: &RState) -> std::rc::Rc<Vec<RRange>> {
        let key = (s.ctx, s.main.0, s.second.map(|w| w.0), s.slot);
        self.memo.entry(key).or_insert_with(|| std::rc::Rc::new(build_layout(s))).clone()
    }

    fn encode_one(layout: &[RRange], c: u32, out: &mut Vec<u8>) {
        let r = layout.iter().find(|x| x.lo <= c && c <= x.hi).expect("one range");
        let o = i64::from(c - r.lo);
        let k = r.size;
        let q = o / pow_radix(k - 1);
        let mut rest = o - q * pow_radix(k - 1);
        out.push(u8::try_from(i64::from(r.first) + q).expect("lead fits"));

        for i in (0..k - 1).rev() {
            let p = pow_radix(i);
            let t = rest / p;
            rest -= t * p;
            out.push(u8::try_from(t + 8).expect("trail fits"));
        }
    }

    /// Encodes code points; `None` if they are not a value.
    pub fn encode(&mut self, cps: &[u32]) -> Option<Vec<u8>> {
        for i in 0..cps.len() {
            if cps[i] > 0x10FFFF {
                return None;
            }
            if i > 0 && in_(cps[i - 1], 0xD800, 0xDBFF) && in_(cps[i], 0xDC00, 0xDFFF) {
                return None;
            }
        }

        let mut s = RState::new();
        let mut out = Vec::new();

        for &c in cps {
            let layout = self.layout(&s);
            let before = out.len();
            Self::encode_one(&layout, c, &mut out);
            let n = out.len() - before;
            update(&mut s, c, n);
        }

        Some(out)
    }

    /// Decodes bytes. On error, the reason, the offset of the first byte of the code point and what was decoded before it.
    pub fn decode(&mut self, bytes: &[u8]) -> Result<Vec<(u32, usize)>, RError> {
        let mut s = RState::new();
        let mut result: Vec<(u32, usize)> = Vec::new();
        let mut pos = 0;

        macro_rules! fail {
            ($r:expr, $at:expr) => {
                return Err(RError { reason: $r, pos: $at, before: result })
            };
        }

        while pos < bytes.len() {
            let layout = self.layout(&s);
            let b = u32::from(bytes[pos]);
            if b == 0 {
                fail!("zero-byte", pos);
            }

            let Some(rg) = layout.iter().find(|r| r.first <= b && b < r.first + r.leads).copied() else {
                fail!("unused-lead", pos);
            };

            let start = pos;
            pos += 1;
            let mut o = i64::from(b - rg.first);

            for _ in 0..rg.size - 1 {
                if pos >= bytes.len() {
                    fail!("truncated", start);
                }
                let u = bytes[pos];
                if u == 0 {
                    fail!("zero-byte", start);
                }
                if u < 8 {
                    fail!("invalid-trail", start);
                }
                o = o * RADIX + i64::from(u - 8);
                pos += 1;
            }

            let c = i64::from(rg.lo) + o;
            if c > i64::from(rg.hi) {
                fail!("past-range", start);
            }

            let cp = c as u32;

            if let Some(&(prev, _)) = result.last() {
                if in_(prev, 0xD800, 0xDBFF) && in_(cp, 0xDC00, 0xDFFF) {
                    fail!("surrogate-pair", start);
                }
            }

            result.push((cp, start));
            update(&mut s, cp, pos - start);
        }

        Ok(result)
    }

    /// What a decoder to `str` must give: the code points, or the first surrogate before the first rule broken, or that rule.
    pub fn decode_str(&mut self, bytes: &[u8]) -> Result<String, (&'static str, usize)> {
        match self.decode(bytes) {
            Ok(cps) => {
                if let Some(&(_, at)) = cps.iter().find(|(c, _)| (0xD800..=0xDFFF).contains(c)) {
                    return Err(("surrogate", at));
                }
                Ok(cps.iter().map(|&(c, _)| char::from_u32(c).unwrap()).collect())
            }
            Err(e) => {
                if let Some(&(_, at)) = e.before.iter().find(|(c, _)| (0xD800..=0xDFFF).contains(c)) {
                    return Err(("surrogate", at));
                }
                Err((e.reason, e.pos))
            }
        }
    }
}

// ---------- Helpers ----------

pub struct Rng(pub u64);

impl Rng {
    pub fn next_u64(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    pub fn below(&mut self, n: u64) -> u64 {
        self.next_u64() % n
    }

    pub fn range(&mut self, lo: u32, hi: u32) -> u32 {
        lo + self.below(u64::from(hi - lo + 1)) as u32
    }

    pub fn chance(&mut self, percent: u64) -> bool {
        self.below(100) < percent
    }

    /// A piece length: short (below `short`) half the time, else below `long`.
    pub fn piece(&mut self, short: u64, long: u64) -> usize {
        let lim = if self.chance(50) { short } else { long };
        self.below(lim) as usize
    }

    pub fn pick<T: Copy>(&mut self, s: &[T]) -> T {
        s[self.below(s.len() as u64) as usize]
    }
}

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

pub fn to_string(cps: &[u32]) -> Option<String> {
    cps.iter().map(|&c| char::from_u32(c)).collect()
}

pub fn hex(b: &[u8]) -> String {
    b.iter().map(|x| format!("{x:02X}")).collect()
}

pub fn cps_hex(c: &[u32]) -> String {
    c.iter().map(|x| format!("{x:04X}")).collect::<Vec<_>>().join(" ")
}

pub fn repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..")
}

pub enum Vector {
    Valid { name: String, cps: Vec<u32>, bytes: Vec<u8> },
    Invalid { name: String, bytes: Vec<u8>, reason: String },
    NotAValue { name: String, cps: Vec<u32> },
}

fn parse_cps(s: &str) -> Vec<u32> {
    if s == "-" {
        return Vec::new();
    }
    s.split(' ').map(|h| u32::from_str_radix(h, 16).unwrap()).collect()
}

fn parse_bytes(s: &str) -> Vec<u8> {
    if s == "-" {
        return Vec::new();
    }
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
}

pub fn read_vectors(path: &std::path::Path) -> Vec<Vector> {
    let text = std::fs::read_to_string(path).unwrap();
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
            other => panic!("kind {other}"),
        });
    }

    out
}

/// The budget of a fuzz test. By default a fortieth of `base`, so that `cargo test` stays fast; with `STENO_FUZZ=n`, `n` times `base` (the
/// long run: `STENO_FUZZ=1 cargo test --release` takes about a minute and a half). Under Miri, a small sample.
pub fn budget(base: usize) -> usize {
    if cfg!(miri) {
        return (base / 300).max(2);
    }

    match std::env::var("STENO_FUZZ").ok().and_then(|v| v.parse::<usize>().ok()) {
        Some(scale) => base * scale,
        None => (base / 40).max(2),
    }
}

/// True for the long run (`STENO_FUZZ` set), which runs the exhaustive tests in full.
pub fn long_run() -> bool {
    std::env::var_os("STENO_FUZZ").is_some()
}

pub fn seed() -> u64 {
    std::env::var("STENO_FUZZ_SEED").ok().and_then(|v| v.parse().ok()).unwrap_or(0x5EED_0001)
}

// ---------- Text aimed at the fast paths ----------

/// Window starts that make interesting main windows: Latin, Greek, Cyrillic, the split windows, Ethiopic, U+2F80 (next to U+3000), U+3300,
/// U+A000, U+AB80 (next to Hangul), U+E000, U+FF00, U+FF80, U+FE80 (holds U+FEFF), U+10F80 (crosses U+10FC0), U+1FF80 (ends at U+1FFFF),
/// U+40000, U+10FF80.
pub const MAIN_WINDOWS: &[u32] = &[
    0xC0, 0x140, 0x1C0, 0x240, 0x2C0, 0x300, 0x380, 0x400, 0x480, 0x500, 0x530, 0x590, 0x600, 0x900, 0xE00, 0xE80, 0xF00, 0x1000, 0x1200,
    0x1F80, 0x2100, 0x2F80, 0x3100, 0x3380, 0xA000, 0xAB80, 0xE000, 0xFE80, 0xFF00, 0xFF80, 0x10000, 0x10F80, 0x1F600, 0x1FF80, 0x40000,
    0x10FF80,
];

fn window_cp(rng: &mut Rng, w: u32) -> u32 {
    let end = match w {
        0x2C0 => 0x2FF,
        0x300 => 0x36F,
        0x500 => 0x52F,
        0x530 => 0x58F,
        0x590 => 0x5FF,
        _ => w + 0x7F,
    };
    rng.range(w, end)
}

pub const EDGES: &[u32] = &[
    0x00, 0x01, 0x08, 0x09, 0x0A, 0x0B, 0x0D, 0x1F, 0x20, 0x40, 0x41, 0x5A, 0x60, 0x7A, 0x7E, 0x7F, 0x80, 0xBF, 0xC0, 0x13F, 0x140, 0x2FF,
    0x36F, 0x370, 0x37F, 0x380, 0x52F, 0x5FF, 0xEFF, 0xF00, 0x1FFF, 0x2000, 0x20CF, 0x20D0, 0x20FF, 0x2FFF, 0x3000, 0x300F, 0x3010, 0x303F,
    0x3040, 0x3041, 0x3096, 0x3097, 0x30A0, 0x30A1, 0x30FA, 0x30FB, 0x30FC, 0x30FF, 0x3100, 0x33FF, 0x3400, 0x4DFF, 0x4E00, 0x9FFF, 0xA000,
    0xABFF, 0xAC00, 0xD7A3, 0xD7A4, 0xD7FF, 0xD800, 0xDBFF, 0xDC00, 0xDFFF, 0xE000, 0xFEFF, 0xFF00, 0xFF1F, 0xFF20, 0xFFFE, 0xFFFF,
    0x10000, 0x10FBF, 0x10FC0, 0x1FFFF, 0x20000, 0x3FFFF, 0x40000, 0x10FFFE, 0x10FFFF,
];

/// One run of a kind that a fast path handles, of length `n`.
pub fn push_run(rng: &mut Rng, out: &mut Vec<u32>, kind: u32, n: usize) {
    let main = rng.pick(MAIN_WINDOWS);

    for _ in 0..n {
        let c = match kind {
            0 => rng.range(0x20, 0x7E),
            1 => rng.pick(&[0x09, 0x0A, 0x0D, 0x20, 0x41, 0x7E, 0x61]),
            2 => window_cp(rng, main),
            3 => {
                if rng.chance(70) {
                    window_cp(rng, main)
                } else {
                    rng.pick(&[0x20, 0x2C, 0x2E, 0x0A, 0x09, 0x0D, 0x7E])
                }
            }
            4 => match rng.below(6) {
                0 | 1 => rng.range(0x4E00, 0x9FFF),
                2 => rng.range(0x3000, 0x300F),
                3 => rng.range(0x20, 0x7E),
                4 => rng.range(0xFF00, 0xFF1F),
                _ => 0x0A,
            },
            5 => match rng.below(5) {
                0 | 1 => rng.range(0x4E00, 0x9FFF),
                2 => rng.range(0x3041, 0x3096),
                3 => rng.range(0x20, 0x40),
                _ => rng.range(0x3000, 0x300F),
            },
            6 => match rng.below(5) {
                0 | 1 => rng.range(0x4E00, 0x9FFF),
                2 => rng.range(0x30A1, 0x30FC),
                3 => rng.range(0x20, 0x40),
                _ => rng.range(0x3000, 0x300F),
            },
            7 => match rng.below(4) {
                0 | 1 => rng.range(0xAC00, 0xD7A3),
                2 => 0x20,
                _ => rng.range(0x21, 0x7E),
            },
            8 => rng.pick(EDGES),
            9 => rng.range(0xD800, 0xDFFF),
            10 => {
                // anything
                match rng.below(4) {
                    0 => rng.range(0, 0xFFFF),
                    1 => rng.range(0, 0x10FFFF),
                    2 => rng.range(0x80, 0x7FF),
                    _ => rng.range(0x800, 0xFFFF),
                }
            }
            _ => rng.range(0x10000, 0x1FFFF),
        };
        out.push(c);
    }
}

/// A run length that favours the sizes around the blocks of the kernels (4, 8, 16, 32, 40, 64, 128, 512).
pub fn run_length(rng: &mut Rng) -> usize {
    match rng.below(5) {
        0 => rng.below(20) as usize,
        1 => {
            let b = rng.pick(&[4usize, 8, 15, 16, 31, 32, 39, 40, 41, 63, 64, 65, 127, 128, 129, 255, 256, 511, 512, 513]);
            (b as i64 + rng.below(5) as i64 - 2).max(0) as usize
        }
        2 => rng.below(300) as usize,
        3 => rng.below(70) as usize,
        _ => rng.below(1200) as usize,
    }
}

/// A random value aimed at the fast paths: runs of the kinds above, joined, with single breakers between them.
pub fn fast_value(rng: &mut Rng, surrogates: bool) -> Vec<u32> {
    let mut out = Vec::new();
    let segments = 1 + rng.below(6);

    for _ in 0..segments {
        let mut kind = rng.below(12) as u32;
        if kind == 9 && !surrogates {
            kind = 0;
        }
        let n = run_length(rng);
        push_run(rng, &mut out, kind, n);

        if rng.chance(50) {
            let mut c = rng.pick(EDGES);
            if !surrogates && (0xD800..=0xDFFF).contains(&c) {
                c = 0x1F600;
            }
            out.push(c);
        }
    }

    fix_value(&mut out, surrogates);
    out
}

/// Makes `cps` a value: no high surrogate directly before a low one, and no surrogate at all unless `surrogates`.
pub fn fix_value(cps: &mut [u32], surrogates: bool) {
    for i in 0..cps.len() {
        if !surrogates && (0xD800..=0xDFFF).contains(&cps[i]) {
            cps[i] = 0xFFFD;
        }
        if i > 0 && (0xD800..=0xDBFF).contains(&cps[i - 1]) && (0xDC00..=0xDFFF).contains(&cps[i]) {
            cps[i] = 0x41;
        }
    }
}
