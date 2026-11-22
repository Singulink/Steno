package com.singulink.steno;

import static com.singulink.steno.Layout.CJK;
import static com.singulink.steno.Layout.GENERAL;
import static com.singulink.steno.Layout.HANGUL;
import static com.singulink.steno.Layout.HIRAGANA;
import static com.singulink.steno.Layout.KATAKANA;
import static com.singulink.steno.Layout.LETTERS;
import static com.singulink.steno.Layout.NONE;

/**
 * The state of FORMAT.md section 5 that an encoder and a decoder keep, with the state update of section 7.
 *
 * <p>Not thread safe: each encode or decode works on its own state.
 */
class CoderState {
    int context = GENERAL;
    int slot = LETTERS;
    int main = 0xC0;
    int second = NONE;
    int confidence;
    int pending = NONE;

    /** The layout of the state, kept up to date by {@link #update}. */
    Layout layout = Layout.INITIAL;

    /**
     * The layouts that this coder used last, so that text going back and forth between a few states finds their layouts
     * here and never depends on the shared cache. They belong to the coder: a layout never refers to another.
     */
    private final Layout[] recent = new Layout[2];

    private int recentNext;

    /** Puts the state back to the initial state. */
    void resetState() {
        context = GENERAL;
        slot = LETTERS;
        main = 0xC0;
        second = NONE;
        confidence = 0;
        pending = NONE;
        layout = Layout.INITIAL;
        recent[0] = null;
        recent[1] = null;
    }

    /**
     * Updates the state after code point {@code c}, which took {@code k} bytes in the layout before the update (section
     * 7.1).
     */
    final void update(int c, int k) {
        // Step 1.
        if (c < 0x80) {
            if (context == CJK && slot != LETTERS && ((c >= 0x41 && c <= 0x5A) || (c >= 0x61 && c <= 0x7A))) {
                slot = LETTERS;
                refresh();
            }

            return;
        }

        // Step 2.
        if (c >= 0xAC00 && c <= 0xD7A3) {
            pending = NONE;

            if (context != HANGUL) {
                context = HANGUL;
                refresh();
            }

            return;
        }

        // Step 3.
        if ((c >= 0x3040 && c <= 0x30FF)
                || (c >= 0x3400 && c <= 0x9FFF)
                || (c >= 0x20000 && c <= 0x3FFFF)
                || (c >= 0x3000 && c <= 0x303F && context != HANGUL)) {
            if (context != CJK) {
                context = CJK;
                slot = LETTERS;
            }

            pending = NONE;

            if (c >= 0x3041 && c <= 0x3096) {
                slot = HIRAGANA;
            } else if (c >= 0x30A1 && c <= 0x30FA) {
                slot = KATAKANA;
            }

            refresh();
            return;
        }

        // Step 4.
        if (context != GENERAL && c >= 0xFF00 && c <= 0xFFFF) {
            return;
        }

        // Step 5.
        int w = Layout.windowOf(c);

        if (w == NONE) {
            return;
        }

        // Step 6.
        if (context == GENERAL && w == main) {
            if (confidence < 2) {
                confidence++;
            }

            return;
        }

        // Step 7.
        if (k >= 3) {
            second = w;
        }

        // Step 8.
        if (context == GENERAL) {
            if (confidence > 0) {
                confidence--;
                refresh();
                return;
            }
        } else if (pending != w) {
            pending = w;
            refresh();
            return;
        }

        // Step 9: a switch to w.
        if (main != w) {
            second = main;
        } else if (second == w) {
            second = NONE;
        }

        main = w;
        context = GENERAL;
        pending = NONE;
        confidence = 0;
        refresh();
    }

    private void refresh() {
        Layout current = layout;

        if (current.is(context, slot, main, second)) {
            return;
        }

        for (int i = 0; i < recent.length; i++) {
            Layout r = recent[i];

            if (r != null && r.is(context, slot, main, second)) {
                recent[i] = current;
                layout = r;
                return;
            }
        }

        recent[recentNext] = current;
        recentNext = (recentNext + 1) & 1;
        layout = Layout.get(context, slot, main, second);
    }
}
