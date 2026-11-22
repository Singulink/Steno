//! A plain codec of the Steno Encoding, written from FORMAT.md alone and sharing no code with the library. It builds the layout again
//! before every code point, so it is slow, but each step can be checked against the text. The tests use it as an oracle on random text.

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Ctx {
    General,
    Cjk,
    Hangul,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Slot {
    Letters,
    Hiragana,
    Katakana,
}

type Window = (u32, u32);

#[derive(Clone, Copy, Debug)]
struct State {
    ctx: Ctx,
    main: Window,
    second: Option<Window>,
    slot: Slot,
    conf: u32,
    pending: Option<Window>,
}

const INITIAL: State = State { ctx: Ctx::General, main: (0xC0, 0x13F), second: None, slot: Slot::Letters, conf: 0, pending: None };

fn window(c: u32) -> Option<Window> {
    let block = |c: u32| {
        let s = c - c % 0x80;
        Some((s, s + 0x7F))
    };

    match c {
        0x0000..=0x00BF => None,
        0x00C0..=0x013F => Some((0x00C0, 0x013F)),
        0x0140..=0x01BF => Some((0x0140, 0x01BF)),
        0x01C0..=0x023F => Some((0x01C0, 0x023F)),
        0x0240..=0x02BF => Some((0x0240, 0x02BF)),
        0x02C0..=0x02FF => Some((0x02C0, 0x02FF)),
        0x0300..=0x036F => Some((0x0300, 0x036F)),
        0x0370..=0x037F => None,
        0x0380..=0x04FF => block(c),
        0x0500..=0x052F => Some((0x0500, 0x052F)),
        0x0530..=0x058F => Some((0x0530, 0x058F)),
        0x0590..=0x05FF => Some((0x0590, 0x05FF)),
        0x0600..=0x1FFF => block(c),
        0x2000..=0x20FF => None,
        0x2100..=0x2FFF => block(c),
        0x3000..=0x30FF => None,
        0x3100..=0x33FF => block(c),
        0x3400..=0x9FFF => None,
        0xA000..=0xABFF => block(c),
        0xAC00..=0xDFFF => None,
        0xE000..=0x1FFFF => block(c),
        0x20000..=0x3FFFF => None,
        _ => block(c),
    }
}

/// (lo, hi, size, first lead, lead count)
type Range = (u32, u32, u32, u32, u32);

fn layout(s: &State) -> Vec<Range> {
    // Segments that cover U+0000..U+10FFFF; size 0 = no piece covers it.
    let mut segs: Vec<(u32, u32, u32)> = vec![(0, 0x10FFFF, 0)];

    let mut piece = |lo: u32, hi: u32, size: u32| {
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
        next.sort();
        segs = next;
    };

    let (m0, m1) = s.main;

    if s.ctx == Ctx::General {
        piece((i64::from(m0) - 248).max(0x80) as u32, m0 - 1, 2);

        if m1 < 0x10FFFF {
            piece(m1 + 1, 0x10FFFF.min(m1 + 248), 2);
        }
    }

    piece(0x00, 0x1F, 2);
    piece(0x09, 0x0A, 1);
    piece(0x0D, 0x0D, 1);

    if s.ctx == Ctx::Cjk && s.slot != Slot::Letters {
        piece(0x20, 0x40, 1);
        piece(0x41, 0x7E, 2);
    } else {
        piece(0x20, 0x7E, 1);
    }

    piece(0x7F, 0xBF, 2);

    if s.ctx != Ctx::Cjk {
        piece(0x80, 0xEFF, 2);
    }

    piece(0x2000, 0x20CF, 2);

    if let Some((s0, s1)) = s.second {
        piece(s0, s1, 2);
    }

    match s.ctx {
        Ctx::General => piece(m0, m1, 1),
        Ctx::Hangul => {
            piece(m0, m1, 2);
            piece(0x3000, 0x303F, 2);
            piece(0xAC00, 0xD7A3, 2);
            piece(0xFF00, 0xFFFF, 2);
        }
        Ctx::Cjk => {
            piece(m0, m1, 2);
            piece(0x3000, 0x300F, 1);
            piece(0x3010, 0x30FF, 2);

            if s.slot == Slot::Hiragana {
                piece(0x3041, 0x3096, 1);
            }

            if s.slot == Slot::Katakana {
                piece(0x30A1, 0x30FC, 1);
            }

            piece(0x4E00, 0x9FFF, 2);
            piece(0xFF00, 0xFFFF, 2);

            if s.slot == Slot::Letters {
                piece(0xFF00, 0xFF1F, 1);
            }
        }
    }

    // Stretches, cut into parts at U+10FC0 and U+20000, sized by length.
    let mut sized: Vec<(u32, u32, u32)> = Vec::new();
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

        let mut start = lo;

        for b in [0x10FC0, 0x20000] {
            if start < b && hi >= b {
                sized.push((start, b - 1, part_size(b - start)));
                start = b;
            }
        }

        sized.push((start, hi, part_size(hi - start + 1)));
    }

    // Ranges and lead bytes.
    let mut ranges: Vec<Range> = Vec::new();
    let mut lead = 1;
    let mut j = 0;

    while j < sized.len() {
        let (lo, mut hi, size) = sized[j];
        j += 1;

        while j < sized.len() && sized[j].2 == size && sized[j].0 == hi + 1 {
            hi = sized[j].1;
            j += 1;
        }

        let n = u64::from(hi - lo + 1);
        let per = 248u64.pow(size - 1);
        let leads = n.div_ceil(per) as u32;
        ranges.push((lo, hi, size, lead, leads));
        lead += leads;
    }

    assert!(lead - 1 <= 254);
    ranges
}

fn part_size(n: u32) -> u32 {
    if n <= 248 {
        2
    } else if n <= 248 * 248 {
        3
    } else {
        4
    }
}

fn update(s: &mut State, c: u32, k: u32) {
    let is_in = |lo: u32, hi: u32| lo <= c && c <= hi;

    if c <= 0x7F {
        if s.ctx == Ctx::Cjk && (is_in(0x41, 0x5A) || is_in(0x61, 0x7A)) {
            s.slot = Slot::Letters;
        }

        return;
    }

    if is_in(0xAC00, 0xD7A3) {
        s.pending = None;
        s.ctx = Ctx::Hangul;
        return;
    }

    if is_in(0x3040, 0x30FF) || is_in(0x3400, 0x9FFF) || is_in(0x20000, 0x3FFFF) || (is_in(0x3000, 0x303F) && s.ctx != Ctx::Hangul) {
        if s.ctx != Ctx::Cjk {
            s.ctx = Ctx::Cjk;
            s.slot = Slot::Letters;
        }

        s.pending = None;

        if is_in(0x3041, 0x3096) {
            s.slot = Slot::Hiragana;
        } else if is_in(0x30A1, 0x30FA) {
            s.slot = Slot::Katakana;
        }

        return;
    }

    if s.ctx != Ctx::General && is_in(0xFF00, 0xFFFF) {
        return;
    }

    let Some(w) = window(c) else {
        return;
    };

    if s.ctx == Ctx::General && w == s.main {
        s.conf = (s.conf + 1).min(2);
        return;
    }

    if k > 2 {
        s.second = Some(w);
    }

    if s.ctx == Ctx::General {
        if s.conf > 0 {
            s.conf -= 1;
            return;
        }
    } else if s.pending != Some(w) {
        s.pending = Some(w);
        return;
    }

    if s.main != w {
        s.second = Some(s.main);
    } else if s.second == Some(w) {
        s.second = None;
    }

    s.main = w;
    s.ctx = Ctx::General;
    s.pending = None;
    s.conf = 0;
}

/// Encodes a value. Panics if the code points are not a value.
pub fn encode(cps: &[u32]) -> Vec<u8> {
    for i in 1..cps.len() {
        assert!(!((0xD800..=0xDBFF).contains(&cps[i - 1]) && (0xDC00..=0xDFFF).contains(&cps[i])), "not a value");
    }

    let mut s = INITIAL;
    let mut out = Vec::new();

    for &c in cps {
        assert!(c <= 0x10FFFF);
        let r = *layout(&s).iter().find(|r| r.0 <= c && c <= r.1).unwrap();
        let o = c - r.0;
        let k = r.2;
        let per = 248u32.pow(k - 1);
        out.push((r.3 + o / per) as u8);
        let mut rest = o % per;

        for e in (0..k - 1).rev() {
            let p = 248u32.pow(e);
            out.push((rest / p + 8) as u8);
            rest %= p;
        }

        update(&mut s, c, k);
    }

    out
}

/// Decodes a value, or returns the reason of FORMAT.md §10.1 and the offset of the code point that broke the rule.
pub fn decode(bytes: &[u8]) -> Result<Vec<u32>, (&'static str, usize)> {
    let mut s = INITIAL;
    let mut out: Vec<u32> = Vec::new();
    let mut pos = 0;

    while pos < bytes.len() {
        let start = pos;
        let b = u32::from(bytes[pos]);

        if b == 0 {
            return Err(("zero-byte", start));
        }

        let Some(r) = layout(&s).into_iter().find(|r| r.3 <= b && b < r.3 + r.4) else {
            return Err(("unused-lead", start));
        };

        pos += 1;
        let mut o = u64::from(b - r.3);

        for _ in 0..r.2 - 1 {
            if pos >= bytes.len() {
                return Err(("truncated", start));
            }

            let u = bytes[pos];

            if u == 0 {
                return Err(("zero-byte", start));
            }

            if u < 8 {
                return Err(("invalid-trail", start));
            }

            o = o * 248 + u64::from(u - 8);
            pos += 1;
        }

        let c = u64::from(r.0) + o;

        if c > u64::from(r.1) {
            return Err(("past-range", start));
        }

        let c = c as u32;

        if let Some(&prev) = out.last() {
            if (0xD800..=0xDBFF).contains(&prev) && (0xDC00..=0xDFFF).contains(&c) {
                return Err(("surrogate-pair", start));
            }
        }

        out.push(c);
        update(&mut s, c, (pos - start) as u32);
    }

    Ok(out)
}
