//! Tests: memory growth of the layout caches, and many threads at once with enough layouts to force evictions.

mod oracle;

use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicIsize, Ordering};

use oracle::*;
use singulink_steno::{self as steno, code_points, utf16};

struct Counting;

static LIVE: AtomicIsize = AtomicIsize::new(0);

// SAFETY: forwards to the system allocator and only counts.
unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, l: Layout) -> *mut u8 {
        LIVE.fetch_add(l.size() as isize, Ordering::Relaxed);
        // SAFETY: as the caller of `alloc` promises.
        unsafe { System.alloc(l) }
    }

    unsafe fn dealloc(&self, p: *mut u8, l: Layout) {
        LIVE.fetch_sub(l.size() as isize, Ordering::Relaxed);
        // SAFETY: as the caller of `dealloc` promises.
        unsafe { System.dealloc(p, l) }
    }

    unsafe fn realloc(&self, p: *mut u8, l: Layout, new: usize) -> *mut u8 {
        LIVE.fetch_add(new as isize - l.size() as isize, Ordering::Relaxed);
        // SAFETY: as the caller of `realloc` promises.
        unsafe { System.realloc(p, l, new) }
    }
}

#[global_allocator]
static A: Counting = Counting;

/// A value that visits many layouts that are not shared: main windows above U+3400, second windows, wide contexts with other main windows.
fn layout_heavy_value(rng: &mut Rng) -> Vec<u32> {
    let mut cps = Vec::new();

    for _ in 0..20 {
        let w = match rng.below(3) {
            0 => 0xE000 + (rng.below((0x20000 - 0xE000) / 0x80) as u32) * 0x80,
            1 => 0x40000 + (rng.below((0x110000 - 0x40000) / 0x80) as u32) * 0x80,
            _ => 0x600 + (rng.below((0x2000 - 0x600) / 0x80) as u32) * 0x80,
        };
        cps.push(w + rng.below(0x80) as u32);
        cps.push(w + rng.below(0x80) as u32);
        if rng.chance(30) {
            cps.push(0x4E00 + rng.below(100) as u32);
        }
        if rng.chance(20) {
            cps.push(0xAC00 + rng.below(100) as u32);
        }
        if rng.chance(20) {
            cps.push(0x3042);
        }
    }

    cps
}

#[test]
fn memory_is_bounded() {
    let mut rng = Rng(seed() ^ 0x3E3);

    // Warm up: the shared layouts and the cache of this thread.
    for _ in 0..2000 {
        let cps = layout_heavy_value(&mut rng);
        let b = code_points::encode(&cps).unwrap();
        assert_eq!(code_points::decode(&b).unwrap(), cps);
    }

    let base = LIVE.load(Ordering::Relaxed);
    let mut most = base;

    for _ in 0..budget(20_000) {
        let cps = layout_heavy_value(&mut rng);
        let b = code_points::encode(&cps).unwrap();
        let d = code_points::decode(&b).unwrap();
        assert_eq!(d, cps);
        drop((b, d));
        most = most.max(LIVE.load(Ordering::Relaxed));
    }

    let after = LIVE.load(Ordering::Relaxed);
    eprintln!("one thread: base {base}, most {most}, after {after}");
    assert!(after - base < 200_000, "memory grew by {} bytes on one thread", after - base);

    // Many short threads, each with its own cache, 8 at a time.
    let before_threads = LIVE.load(Ordering::Relaxed);

    for round in 0..budget(25) {
        let handles: Vec<_> = (0..8)
            .map(|t| {
                std::thread::spawn(move || {
                    let mut rng = Rng(round as u64 * 100 + t);
                    for _ in 0..200 {
                        let cps = layout_heavy_value(&mut rng);
                        let b = code_points::encode(&cps).unwrap();
                        assert_eq!(code_points::decode(&b).unwrap(), cps);
                    }
                })
            })
            .collect();

        for h in handles {
            h.join().unwrap();
        }
    }

    let after_threads = LIVE.load(Ordering::Relaxed);
    eprintln!("threads: before {before_threads}, after {after_threads}");
    assert!(after_threads - before_threads < 400_000, "memory grew by {} bytes after the threads ended", after_threads - before_threads);
}

#[test]
fn many_threads_agree_with_the_oracle() {
    let mut rng = Rng(seed() ^ 0x7777);
    let mut o = Oracle::new();
    let mut cases = Vec::new();

    for i in 0..budget(400) {
        let cps = if i % 2 == 0 { layout_heavy_value(&mut rng) } else { fast_value(&mut rng, true) };
        let bytes = o.encode(&cps).unwrap();
        cases.push((cps, bytes));
    }

    let cases = std::sync::Arc::new(cases);
    let threads = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4).min(16);

    let handles: Vec<_> = (0..threads)
        .map(|t| {
            let cases = cases.clone();
            std::thread::spawn(move || {
                for round in 0..6 {
                    for (j, (cps, bytes)) in cases.iter().enumerate() {
                        if (j + t + round) % 3 == 0 {
                            continue;
                        }
                        let units = to_utf16(cps);
                        assert_eq!(&utf16::encode(&units), bytes);
                        assert_eq!(&code_points::decode(bytes).unwrap(), cps);
                        assert_eq!(&utf16::decode(bytes).unwrap(), &units);
                        if let Some(s) = to_string(cps) {
                            assert_eq!(&steno::encode(&s), bytes);
                            assert_eq!(steno::decode(bytes).unwrap(), s);
                        }
                    }
                }
            })
        })
        .collect();

    for h in handles {
        h.join().unwrap();
    }
}
