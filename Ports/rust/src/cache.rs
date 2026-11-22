//! A bounded cache of layouts.
//!
//! With the `std` feature, each thread has one cache of its own, so no lock is taken and nothing is shared between threads. Without it,
//! each call builds a cache of its own. The cache holds at most [`SLOTS`] layouts; a layout holds no reference to another layout.

use alloc::boxed::Box;

use crate::layout::Layout;

const SETS: usize = 16;
const WAYS: usize = 2;

/// The most layouts that one cache holds.
pub(crate) const SLOTS: usize = SETS * WAYS;

/// A two-way set-associative cache of layouts by key.
pub(crate) struct LayoutCache {
    slots: [Option<Box<Layout>>; SLOTS],
    /// The way of each set to replace next.
    victim: [u8; SETS],
    /// For each slot, the last two layouts that the layout in it led to: their key and slot. An entry is only a hint, checked against the
    /// slot on use; it keeps no layout alive.
    next: [[(u64, u8); 2]; SLOTS],
}

impl LayoutCache {
    pub fn new() -> Self {
        LayoutCache { slots: [const { None }; SLOTS], victim: [0; SETS], next: [[(u64::MAX, 0); 2]; SLOTS] }
    }

    /// Returns the layout in a slot that `slot` or `next` returned.
    #[inline]
    pub fn at(&self, slot: usize) -> &Layout {
        match &self.slots[slot] {
            Some(layout) => layout,
            None => unreachable!("the slot was filled when it was returned"),
        }
    }

    /// Returns the slot of the layout with key `key`, if it is in the cache. `from` is the slot of the layout that led to it, if any: its
    /// hints are tried first, and a slot found by a lookup becomes a hint.
    #[inline]
    pub fn find(&mut self, from: Option<usize>, key: u64) -> Option<usize> {
        if let Some(from) = from {
            for &(k, s) in &self.next[from] {
                let s = usize::from(s);

                if k == key && self.slots[s].as_ref().is_some_and(|l| l.key == key) {
                    return Some(s);
                }
            }
        }

        let (set, base) = Self::set_of(key);
        let way = (0..WAYS).find(|&way| self.slots[base + way].as_ref().is_some_and(|l| l.key == key))?;
        // The way that was used last is kept; the other one goes next.
        self.victim[set] = (way as u8 + 1) % WAYS as u8;
        self.hint(from, key, base + way);
        Some(base + way)
    }

    /// Returns the slot of the layout with key `key`, building the layout if it is not in the cache.
    #[inline]
    pub fn slot(&mut self, from: Option<usize>, key: u64) -> usize {
        if let Some(slot) = self.find(from, key) {
            return slot;
        }

        let (set, base) = Self::set_of(key);
        let way = usize::from(self.victim[set]);
        let layout = Layout::build(key);

        match &mut self.slots[base + way] {
            Some(slot) => **slot = layout,
            empty => *empty = Some(Box::new(layout)),
        }

        // The hints of the layout that was replaced point elsewhere now.
        self.next[base + way] = [(u64::MAX, 0); 2];
        self.victim[set] = (way as u8 + 1) % WAYS as u8;
        self.hint(from, key, base + way);
        base + way
    }

    #[inline]
    fn set_of(key: u64) -> (usize, usize) {
        let set = (key.wrapping_mul(0x9E37_79B9_7F4A_7C15) >> 60) as usize % SETS;
        (set, set * WAYS)
    }

    #[inline]
    fn hint(&mut self, from: Option<usize>, key: u64, slot: usize) {
        if let Some(from) = from {
            let hints = &mut self.next[from];

            if hints[0].0 != key {
                hints[1] = hints[0];
                hints[0] = (key, slot as u8);
            }
        }
    }
}

/// The layouts that most values use, shared by all threads and built once, on first use: the general context with each main window below
/// U+3400 and no second window (the initial layout, and the layout after the first switch of a value in an alphabet), and the CJK and Hangul
/// contexts with the initial main window and no second window (the layouts of Chinese, Japanese and Korean text). They need no cache probe.
/// There are at most 104 of them (100 windows below U+3400 and 4 wide layouts), so they hold at most about 300 KB, and only for the
/// windows that text in the process used.
#[cfg(feature = "std")]
mod shared {
    use alloc::boxed::Box;
    use std::sync::OnceLock;

    use crate::layout::Layout;
    use crate::state::{CJK, GENERAL, HANGUL, HIRAGANA, KATAKANA, LETTERS};

    const GENERAL_WINDOWS: usize = 0x3400 >> 4;

    static GENERAL_LAYOUTS: [OnceLock<Box<Layout>>; GENERAL_WINDOWS] = [const { OnceLock::new() }; GENERAL_WINDOWS];
    static WIDE_LAYOUTS: [OnceLock<Box<Layout>>; 4] = [const { OnceLock::new() }; 4];

    /// The key bits of "no second window".
    const NO_SECOND: u64 = 0x3F_FFFF << 25;

    /// Returns the shared layout with this key, if the key is one of them.
    #[inline]
    pub(crate) fn get(key: u64) -> Option<&'static Layout> {
        if key & (0x3F_FFFF << 25) != NO_SECOND {
            return None;
        }

        let ctx = (key & 3) as u8;
        let slot = ((key >> 2) & 3) as u8;
        let main = ((key >> 4) & 0x1F_FFFF) as usize;

        let cell = if ctx == GENERAL {
            GENERAL_LAYOUTS.get(main >> 4)?
        } else if main == 0xC0 {
            match (ctx, slot) {
                (CJK, LETTERS) => &WIDE_LAYOUTS[0],
                (CJK, HIRAGANA) => &WIDE_LAYOUTS[1],
                (CJK, KATAKANA) => &WIDE_LAYOUTS[2],
                (HANGUL, _) => &WIDE_LAYOUTS[3],
                _ => return None,
            }
        } else {
            return None;
        };

        Some(cell.get_or_init(|| Box::new(Layout::build(key))))
    }
}

/// Returns the shared layout with this key, if the key is one of them (see `shared`).
#[cfg(feature = "std")]
#[inline]
pub(crate) fn shared(key: u64) -> Option<&'static Layout> {
    shared::get(key)
}

/// Without `std` there are no shared layouts.
#[cfg(not(feature = "std"))]
#[inline(always)]
pub(crate) fn shared(_key: u64) -> Option<&'static Layout> {
    None
}

#[cfg(feature = "std")]
std::thread_local! {
    static CACHE: core::cell::RefCell<LayoutCache> = core::cell::RefCell::new(LayoutCache::new());
}

/// Runs `f` with the layout cache of this thread, or with a new cache if that one cannot be used (while the thread is being torn down).
#[cfg(feature = "std")]
#[inline]
pub(crate) fn with_cache<R>(f: impl FnOnce(&mut LayoutCache) -> R) -> R {
    let mut f = Some(f);
    let result = CACHE.try_with(|cell| match (cell.try_borrow_mut(), f.take()) {
        (Ok(mut cache), Some(f)) => Some(f(&mut cache)),
        (_, f_back) => {
            f = f_back;
            None
        }
    });

    match (result, f) {
        (Ok(Some(r)), _) => r,
        (_, Some(f)) => f(&mut LayoutCache::new()),
        (_, None) => unreachable!("f was neither run nor given back"),
    }
}

/// Runs `f` with a new layout cache.
#[cfg(not(feature = "std"))]
#[inline]
pub(crate) fn with_cache<R>(f: impl FnOnce(&mut LayoutCache) -> R) -> R {
    f(&mut LayoutCache::new())
}

#[cfg(all(test, feature = "std"))]
mod tests {
    use crate::state::{window_end, window_of, NONE};

    /// The README and the comment of `shared` count the shared layouts: 100 windows below U+3400.
    #[test]
    fn windows_below_3400() {
        let mut c = 0;
        let mut count = 0;

        while c < 0x3400 {
            let w = window_of(c);

            if w == NONE {
                c += 1;
            } else {
                count += 1;
                c = window_end(w) + 1;
            }
        }

        assert_eq!(count, 100);
    }
}
