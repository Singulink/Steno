//! The windows (FORMAT.md §4), the state (§5) and the state update (§7).

/// No window. Window values are the first code point of the window.
pub(crate) const NONE: u32 = u32::MAX;

pub(crate) const GENERAL: u8 = 0;
pub(crate) const CJK: u8 = 1;
pub(crate) const HANGUL: u8 = 2;

pub(crate) const LETTERS: u8 = 0;
pub(crate) const HIRAGANA: u8 = 1;
pub(crate) const KATAKANA: u8 = 2;

/// Returns the window of `c` (its first code point), or [`NONE`] (§4).
#[inline]
pub(crate) fn window_of(c: u32) -> u32 {
    // Below U+3000 every window starts and ends on a multiple of 16, so a table by `c / 16` has the answer.
    match WINDOW_TABLE.get((c >> 4) as usize) {
        Some(&w) if w == u16::MAX => NONE,
        Some(&w) => u32::from(w),
        None => window_by_rows(c),
    }
}

/// The window of each group of 16 code points below U+3000, or `u16::MAX` for none.
static WINDOW_TABLE: [u16; 0x300] = window_table();

const fn window_table() -> [u16; 0x300] {
    let mut table = [0u16; 0x300];
    let mut i = 0;

    while i < 0x300 {
        let w = window_by_rows((i as u32) << 4);
        table[i] = if w == NONE { u16::MAX } else { w as u16 };
        i += 1;
    }

    table
}

/// The window of `c` by the rows of the table of §4.
const fn window_by_rows(c: u32) -> u32 {
    const fn block(c: u32) -> u32 {
        c & !0x7F
    }

    if c < 0x00C0 {
        NONE
    } else if c < 0x0300 {
        // U+00C0..U+013F, U+0140..U+01BF, U+01C0..U+023F, U+0240..U+02BF, U+02C0..U+02FF.
        0x00C0 + ((c - 0x00C0) & !0x7F)
    } else if c < 0x0370 {
        0x0300
    } else if c < 0x0380 {
        NONE
    } else if c < 0x0500 {
        block(c)
    } else if c < 0x0530 {
        0x0500
    } else if c < 0x0590 {
        0x0530
    } else if c < 0x0600 {
        0x0590
    } else if c < 0x2000 {
        block(c)
    } else if c < 0x2100 {
        NONE
    } else if c < 0x3000 {
        block(c)
    } else if c < 0x3100 {
        NONE
    } else if c < 0x3400 {
        block(c)
    } else if c < 0xA000 {
        NONE
    } else if c < 0xAC00 {
        block(c)
    } else if c < 0xE000 {
        NONE
    } else if c < 0x2_0000 {
        block(c)
    } else if c < 0x4_0000 {
        NONE
    } else {
        block(c)
    }
}

/// Returns the last code point of the window that starts at `start`.
#[inline]
pub(crate) const fn window_end(start: u32) -> u32 {
    match start {
        0x02C0 => 0x02FF,
        0x0300 => 0x036F,
        0x0500 => 0x052F,
        0x0530 => 0x058F,
        0x0590 => 0x05FF,
        _ => start + 0x7F,
    }
}

/// The state of FORMAT.md §5. Fields that the current context does not use may hold any value (§5).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct State {
    pub ctx: u8,
    pub slot: u8,
    pub conf: u8,
    pub main: u32,
    pub second: u32,
    pub pending: u32,
}

impl State {
    pub const INITIAL: State = State { ctx: GENERAL, slot: LETTERS, conf: 0, main: 0x00C0, second: NONE, pending: NONE };

    /// The key of the layout of this state: context, kana slot (CJK only), main window and second window. A second window that the other
    /// pieces cover with size 2 anyway does not change the layout (§14.3), so it is left out of the key.
    #[inline]
    pub fn key(&self) -> u64 {
        let slot = if self.ctx == CJK { self.slot } else { LETTERS };
        let second = if self.second == NONE || self.second_is_covered() { 0x3F_FFFF } else { self.second };
        u64::from(self.ctx) | (u64::from(slot) << 2) | (u64::from(self.main) << 4) | (u64::from(second) << 25)
    }

    /// True if every code point of the second window has size 2 without it. Only simple cases are recognised: then the key can leave the
    /// window out. Missing a case costs only cache entries, never correctness.
    #[inline]
    fn second_is_covered(&self) -> bool {
        let s0 = self.second;
        let s1 = window_end(s0);

        if self.ctx == CJK {
            return false;
        }

        // Piece 5, U+0080..U+0EFF, size 2, in the general and Hangul contexts.
        if s1 <= 0x0EFF {
            return true;
        }

        if self.ctx == GENERAL {
            let m0 = self.main;
            let m1 = window_end(m0);

            // The neighbours, size 2. Pieces 2 to 6 above U+00BF are size 2 as well, so nothing between can give a size other than 2.
            if s1 < m0 && s0 >= m0.saturating_sub(248) {
                return true;
            }

            if s0 > m1 && s1 <= m1 + 248 {
                return true;
            }
        }

        false
    }

    /// Updates the state after code point `c`, which took `k` bytes, and returns true if a field that the layout depends on may have
    /// changed (false means that the layout is the same).
    #[inline]
    pub fn update_moves(&mut self, c: u32, k: usize) -> bool {
        let before = (self.ctx, self.slot, self.main, self.second);
        self.update(c, k);
        before != (self.ctx, self.slot, self.main, self.second)
    }

    /// Updates the state after code point `c`, which took `k` bytes (§7.1).
    pub fn update(&mut self, c: u32, k: usize) {
        // Step 1.
        if c < 0x80 {
            if self.ctx == CJK && is_ascii_letter(c) {
                self.slot = LETTERS;
            }

            return;
        }

        // Step 2.
        if (0xAC00..=0xD7A3).contains(&c) {
            self.pending = NONE;
            self.ctx = HANGUL;
            return;
        }

        // Step 3.
        if is_cjk(c, self.ctx) {
            if self.ctx != CJK {
                self.ctx = CJK;
                self.slot = LETTERS;
            }

            self.pending = NONE;

            if (0x3041..=0x3096).contains(&c) {
                self.slot = HIRAGANA;
            } else if (0x30A1..=0x30FA).contains(&c) {
                self.slot = KATAKANA;
            }

            return;
        }

        // Step 4.
        if self.ctx != GENERAL && (0xFF00..=0xFFFF).contains(&c) {
            return;
        }

        // Step 5.
        let w = window_of(c);

        if w == NONE {
            return;
        }

        // Step 6.
        if self.ctx == GENERAL && w == self.main {
            self.conf = (self.conf + 1).min(2);
            return;
        }

        // Step 7.
        if k >= 3 {
            self.second = w;
        }

        // Step 8.
        if self.ctx == GENERAL {
            if self.conf > 0 {
                self.conf -= 1;
                return;
            }
        } else if self.pending != w {
            self.pending = w;
            return;
        }

        // Step 9.
        if self.main != w {
            self.second = self.main;
        } else if self.second == w {
            self.second = NONE;
        }

        self.main = w;
        self.ctx = GENERAL;
        self.pending = NONE;
        self.conf = 0;
    }
}

#[inline]
const fn is_ascii_letter(c: u32) -> bool {
    (c >= 0x41 && c <= 0x5A) || (c >= 0x61 && c <= 0x7A)
}

#[inline]
const fn is_cjk(c: u32, ctx: u8) -> bool {
    (c >= 0x3040 && c <= 0x30FF)
        || (c >= 0x3400 && c <= 0x9FFF)
        || (c >= 0x2_0000 && c <= 0x3_FFFF)
        || (ctx != HANGUL && c >= 0x3000 && c <= 0x303F)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn there_are_7356_windows() {
        let mut count = 0;
        let mut last = NONE;

        for c in 0..=0x10_FFFF {
            let w = window_of(c);

            if w != NONE {
                assert!(w <= c && c <= window_end(w), "U+{c:04X}");
                assert_eq!(window_of(w), w);

                if w != last {
                    count += 1;
                }
            }

            last = w;
        }

        assert_eq!(count, 7356);
    }
}
