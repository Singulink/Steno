package com.singulink.steno;

import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * An independent oracle: a plain codec of FORMAT.md, ported from the independent .NET ReferenceSteno.cs (written from
 * the specification alone). It shares no code with the library. Layouts are cached by state only for speed.
 */
final class OracleCodec {
    static final int GENERAL = 0, CJK = 1, HANGUL = 2;
    static final int LETTERS = 0, HIRAGANA = 1, KATAKANA = 2;

    private OracleCodec() {}

    /** Windows by first code point; -1 is none. */
    static int windowStart(int c) {
        if (c <= 0xBF) return -1;
        if (c <= 0x13F) return 0xC0;
        if (c <= 0x1BF) return 0x140;
        if (c <= 0x23F) return 0x1C0;
        if (c <= 0x2BF) return 0x240;
        if (c <= 0x2FF) return 0x2C0;
        if (c <= 0x36F) return 0x300;
        if (c <= 0x37F) return -1;
        if (c <= 0x4FF) return c - c % 0x80;
        if (c <= 0x52F) return 0x500;
        if (c <= 0x58F) return 0x530;
        if (c <= 0x5FF) return 0x590;
        if (c <= 0x1FFF) return c - c % 0x80;
        if (c <= 0x20FF) return -1;
        if (c <= 0x2FFF) return c - c % 0x80;
        if (c <= 0x30FF) return -1;
        if (c <= 0x33FF) return c - c % 0x80;
        if (c <= 0x9FFF) return -1;
        if (c <= 0xABFF) return c - c % 0x80;
        if (c <= 0xDFFF) return -1;
        if (c <= 0x1FFFF) return c - c % 0x80;
        if (c <= 0x3FFFF) return -1;
        if (c <= 0x10FFFF) return c - c % 0x80;
        throw new IllegalArgumentException();
    }

    static int windowEndOf(int start) {
        switch (start) {
            case 0xC0:
                return 0x13F;
            case 0x140:
                return 0x1BF;
            case 0x1C0:
                return 0x23F;
            case 0x240:
                return 0x2BF;
            case 0x2C0:
                return 0x2FF;
            case 0x300:
                return 0x36F;
            case 0x500:
                return 0x52F;
            case 0x530:
                return 0x58F;
            case 0x590:
                return 0x5FF;
            default:
                return start + 0x7F;
        }
    }

    static final class State {
        int context = GENERAL;
        int main = 0xC0;
        int second = -1;
        int slot = LETTERS;
        int confidence;
        int pending = -1;

        String key() {
            return context + ":" + (context == CJK ? slot : 0) + ":" + main + ":" + second;
        }
    }

    static final class Range {
        final int lo, hi, size, firstLead, leadCount;

        Range(int lo, int hi, int size, int firstLead, int leadCount) {
            this.lo = lo;
            this.hi = hi;
            this.size = size;
            this.firstLead = firstLead;
            this.leadCount = leadCount;
        }
    }

    static final class Lay {
        final List<Range> ranges;
        final Range[] byLead = new Range[256];

        Lay(List<Range> ranges) {
            this.ranges = ranges;
            for (Range r : ranges) {
                for (int b = r.firstLead; b < r.firstLead + r.leadCount; b++) byLead[b] = r;
            }
        }

        Range find(int c) {
            for (Range r : ranges) if (r.lo <= c && c <= r.hi) return r;
            throw new IllegalStateException();
        }
    }

    private static final ThreadLocal<Map<String, Lay>> CACHE = ThreadLocal.withInitial(HashMap::new);

    static Lay layout(State s) {
        Map<String, Lay> m = CACHE.get();
        if (m.size() > 20000) m.clear();
        return m.computeIfAbsent(s.key(), k -> new Lay(build(s)));
    }

    private static final class Seg {
        final int lo, hi, size;

        Seg(int lo, int hi, int size) {
            this.lo = lo;
            this.hi = hi;
            this.size = size;
        }
    }

    static List<Range> build(State s) {
        class P {
            List<Seg> segs = new ArrayList<>(List.of(new Seg(0, 0x10FFFF, 0)));

            void piece(int lo, int hi, int size) {
                if (lo > hi) return;
                List<Seg> next = new ArrayList<>();
                for (Seg g : segs) {
                    if (g.hi < lo || g.lo > hi) {
                        next.add(g);
                        continue;
                    }
                    if (g.lo < lo) next.add(new Seg(g.lo, lo - 1, g.size));
                    if (g.hi > hi) next.add(new Seg(hi + 1, g.hi, g.size));
                }
                next.add(new Seg(lo, hi, size));
                next.sort((a, b) -> Integer.compare(a.lo, b.lo));
                segs = next;
            }
        }
        P p = new P();
        int m0 = s.main, m1 = windowEndOf(s.main);
        if (s.context == GENERAL) {
            p.piece(Math.max(0x80, m0 - 248), m0 - 1, 2);
            if (m1 < 0x10FFFF) p.piece(m1 + 1, Math.min(0x10FFFF, m1 + 248), 2);
        }
        p.piece(0, 0x1F, 2);
        p.piece(9, 0xA, 1);
        p.piece(0xD, 0xD, 1);
        if (s.context == CJK && s.slot != LETTERS) {
            p.piece(0x20, 0x40, 1);
            p.piece(0x41, 0x7E, 2);
        } else {
            p.piece(0x20, 0x7E, 1);
        }
        p.piece(0x7F, 0xBF, 2);
        if (s.context != CJK) p.piece(0x80, 0xEFF, 2);
        p.piece(0x2000, 0x20CF, 2);
        if (s.second >= 0) p.piece(s.second, windowEndOf(s.second), 2);
        switch (s.context) {
            case GENERAL:
                p.piece(m0, m1, 1);
                break;
            case HANGUL:
                p.piece(m0, m1, 2);
                p.piece(0x3000, 0x303F, 2);
                p.piece(0xAC00, 0xD7A3, 2);
                p.piece(0xFF00, 0xFFFF, 2);
                break;
            default:
                p.piece(m0, m1, 2);
                p.piece(0x3000, 0x300F, 1);
                p.piece(0x3010, 0x30FF, 2);
                if (s.slot == HIRAGANA) p.piece(0x3041, 0x3096, 1);
                if (s.slot == KATAKANA) p.piece(0x30A1, 0x30FC, 1);
                p.piece(0x4E00, 0x9FFF, 2);
                p.piece(0xFF00, 0xFFFF, 2);
                if (s.slot == LETTERS) p.piece(0xFF00, 0xFF1F, 1);
        }
        List<Seg> list = p.segs;
        List<Seg> sized = new ArrayList<>();
        int i = 0;
        while (i < list.size()) {
            if (list.get(i).size != 0) {
                sized.add(list.get(i++));
                continue;
            }
            int lo = list.get(i).lo, hi = list.get(i).hi;
            i++;
            while (i < list.size() && list.get(i).size == 0) hi = list.get(i++).hi;
            int start = lo;
            for (int b : new int[] {0x10FC0, 0x20000}) {
                if (start < b && hi >= b) {
                    sized.add(sizedPart(start, b - 1));
                    start = b;
                }
            }
            sized.add(sizedPart(start, hi));
        }
        List<Range> ranges = new ArrayList<>();
        int lead = 1;
        int j = 0;
        while (j < sized.size()) {
            int lo = sized.get(j).lo, hi = sized.get(j).hi, size = sized.get(j).size;
            j++;
            while (j < sized.size() && sized.get(j).size == size && sized.get(j).lo == hi + 1) hi = sized.get(j++).hi;
            long n = (long) hi - lo + 1;
            long per = pow(size - 1);
            int leads = (int) ((n + per - 1) / per);
            ranges.add(new Range(lo, hi, size, lead, leads));
            lead += leads;
        }
        if (lead - 1 > 254) throw new IllegalStateException("more than 254 leads");
        return ranges;
    }

    private static Seg sizedPart(int lo, int hi) {
        long n = (long) hi - lo + 1;
        return new Seg(lo, hi, n <= 248 ? 2 : n <= 248L * 248 ? 3 : 4);
    }

    static long pow(int e) {
        long r = 1;
        for (int i = 0; i < e; i++) r *= 248;
        return r;
    }

    static boolean in(int c, int lo, int hi) {
        return lo <= c && c <= hi;
    }

    static void update(State s, int c, int n) {
        if (c <= 0x7F) {
            if (s.context == CJK && (in(c, 0x41, 0x5A) || in(c, 0x61, 0x7A))) s.slot = LETTERS;
            return;
        }
        if (in(c, 0xAC00, 0xD7A3)) {
            s.pending = -1;
            s.context = HANGUL;
            return;
        }
        if (in(c, 0x3040, 0x30FF)
                || in(c, 0x3400, 0x9FFF)
                || in(c, 0x20000, 0x3FFFF)
                || (in(c, 0x3000, 0x303F) && s.context != HANGUL)) {
            if (s.context != CJK) {
                s.context = CJK;
                s.slot = LETTERS;
            }
            s.pending = -1;
            if (in(c, 0x3041, 0x3096)) s.slot = HIRAGANA;
            else if (in(c, 0x30A1, 0x30FA)) s.slot = KATAKANA;
            return;
        }
        if (s.context != GENERAL && in(c, 0xFF00, 0xFFFF)) return;
        int w = windowStart(c);
        if (w < 0) return;
        if (s.context == GENERAL && w == s.main) {
            s.confidence = Math.min(s.confidence + 1, 2);
            return;
        }
        if (n > 2) s.second = w;
        if (s.context == GENERAL) {
            if (s.confidence > 0) {
                s.confidence--;
                return;
            }
        } else if (s.pending != w) {
            s.pending = w;
            return;
        }
        if (s.main != w) s.second = s.main;
        else if (s.second == w) s.second = -1;
        s.main = w;
        s.context = GENERAL;
        s.pending = -1;
        s.confidence = 0;
    }

    static boolean isValue(int[] cps) {
        for (int i = 0; i < cps.length; i++) {
            if (cps[i] < 0 || cps[i] > 0x10FFFF) return false;
            if (i > 0 && in(cps[i - 1], 0xD800, 0xDBFF) && in(cps[i], 0xDC00, 0xDFFF)) return false;
        }
        return true;
    }

    static byte[] encode(int[] cps) {
        if (!isValue(cps)) throw new IllegalArgumentException("not a value");
        State s = new State();
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        for (int c : cps) {
            Lay lay = layout(s);
            Range r = lay.find(c);
            long o = c - r.lo;
            int k = r.size;
            long q = o / pow(k - 1);
            long rest = o - q * pow(k - 1);
            int lead = (int) (r.firstLead + q);
            if (lead > 0xFE || lead < 1) throw new IllegalStateException();
            out.write(lead);
            for (int i = k - 2; i >= 0; i--) {
                long pp = pow(i);
                long t = rest / pp;
                rest -= t * pp;
                out.write((int) (t + 8));
            }
            update(s, c, k);
        }
        return out.toByteArray();
    }

    /** UTF-16 to code points as FORMAT.md section 3 says. */
    static int[] codePoints(CharSequence text) {
        int[] r = new int[text.length()];
        int n = 0;
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (Character.isHighSurrogate(c) && i + 1 < text.length() && Character.isLowSurrogate(text.charAt(i + 1))) {
                r[n++] = Character.toCodePoint(c, text.charAt(++i));
            } else {
                r[n++] = c;
            }
        }
        return java.util.Arrays.copyOf(r, n);
    }

    static byte[] encode(CharSequence text) {
        return encode(codePoints(text));
    }

    /** Result of decoding: either code points, or a reason and the offset where the bad code point starts. */
    static final class Result {
        final int[] cps;
        final String reason;
        final int offset;
        final int length;

        Result(int[] cps, String reason, int offset, int length) {
            this.cps = cps;
            this.reason = reason;
            this.offset = offset;
            this.length = length;
        }

        boolean ok() {
            return reason == null;
        }

        String text() {
            StringBuilder b = new StringBuilder();
            for (int c : cps) b.appendCodePoint(c);
            return b.toString();
        }
    }

    static Result decode(byte[] bytes) {
        State s = new State();
        int[] out = new int[bytes.length];
        int n = 0;
        int pos = 0;
        while (pos < bytes.length) {
            Lay lay = layout(s);
            int b = bytes[pos] & 0xFF;
            int start = pos;
            if (b == 0) return new Result(null, "zero-byte", start, 1);
            Range r = lay.byLead[b];
            if (r == null) return new Result(null, "unused-lead", start, 1);
            pos++;
            long o = b - r.firstLead;
            for (int i = 0; i < r.size - 1; i++) {
                if (pos >= bytes.length) return new Result(null, "truncated", start, pos - start);
                int u = bytes[pos] & 0xFF;
                if (u == 0) return new Result(null, "zero-byte", start, pos - start + 1);
                if (u < 8) return new Result(null, "invalid-trail", start, pos - start + 1);
                o = o * 248 + (u - 8);
                pos++;
            }
            long c = r.lo + o;
            if (c > r.hi) return new Result(null, "past-range", start, pos - start);
            int cp = (int) c;
            if (n > 0 && in(out[n - 1], 0xD800, 0xDBFF) && in(cp, 0xDC00, 0xDFFF))
                return new Result(null, "surrogate-pair", start, pos - start);
            out[n++] = cp;
            update(s, cp, pos - start);
        }
        return new Result(java.util.Arrays.copyOf(out, n), null, -1, 0);
    }
}
