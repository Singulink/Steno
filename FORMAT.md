# Steno Encoding, version 1

**Status:** frozen (see [§15](#15-versioning)). Anyone may implement it (see [§16](#16-intellectual-property)).

Contents:

1. [Overview](#1-overview)
2. [Conventions](#2-conventions)
3. [Text model](#3-text-model)
4. [Windows](#4-windows)
5. [State](#5-state)
6. [Layout](#6-layout)
7. [State update](#7-state-update)
8. [Encoding a value](#8-encoding-a-value)
9. [Decoding a value](#9-decoding-a-value)
10. [Validity and conformance](#10-validity-and-conformance)
11. [Worked examples (informative)](#11-worked-examples-informative)
12. [Test vectors](#12-test-vectors)
13. [Properties (informative)](#13-properties-informative)
14. [Notes for implementers (informative)](#14-notes-for-implementers-informative)
15. [Versioning](#15-versioning)
16. [Intellectual property](#16-intellectual-property)

Appendix A. [Glossary](#appendix-a-glossary)

## 1. Overview

This section is informative.

### 1.1 What Steno is

The Steno Encoding (Steno for short) is a compact encoding of Unicode text into bytes. It is meant for storing text, and its bytes can also be
compared, sorted and prefix-matched without decoding them: comparing two encodings byte by byte gives the same order as comparing the two texts
code point by code point.

How much space text takes:

- Printable ASCII, tab, line feed and carriage return take 1 byte each, except that in Japanese text U+0041..U+007E (the ASCII letters and the
  punctuation among them) take 2 bytes while the kana slot holds hiragana or katakana. The other C0 controls and U+007F take 2 bytes.
- In the general context, the code points of one window take 1 byte each. A window is a run of at most 128 code points, usually of one script.
  Text in an alphabet whose letters fit in one window (for example Greek, Cyrillic, Hebrew, Arabic, Devanagari or Thai) takes about 1 byte per
  character, where UTF-8 takes 2 or 3.
- In the general context, the code points up to 248 positions before or after that window, the code points U+0080..U+0EFF, and the code points
  of a second window take 2 bytes. So text in a script that spans more than one window takes 1 or 2 bytes per letter.
- In Chinese and Japanese text, the ideographs U+4E00..U+9FFF take 2 bytes, and the kana and the most common punctuation take 1 or 2. In Korean
  text, the Hangul syllables take 2 bytes. UTF-8 takes 3 for all of them.

### 1.2 What it guarantees

- **Order:** comparing two encodings as unsigned byte strings gives the same result as comparing the two values as sequences of code points.
- **Prefixes:** the encoding of a prefix of a value is a prefix of the encoding of the value.
- **Uniqueness:** a value has exactly one encoding, and a byte string that a decoder accepts is the encoding of exactly one value.
- **No zero byte:** the byte `00` never occurs in an encoding, so `00` can end an encoded value inside a larger key.
- **No byte below `08` inside a code point:** the bytes `01`..`07` occur only as the first byte of a code point, and each is the first byte of a
  C0 control character or is the space: `01`, `04` and `06` start the other C0 controls, `02` is tab, `03` is line feed, `05` is carriage return and
  `07` is space.

Arguments for these properties are in [§13](#13-properties-informative).

So a program can find the line feeds, tabs, carriage returns and spaces of an encoded value at the byte level, without decoding it: the byte `03`
is a line feed wherever it occurs, and never part of another code point. Encoded values can be kept inside data that is split on line feeds or tabs
(one value per line, or tab-separated fields), and a tool can split encoded text into lines or words by its bytes. What this does not give: a line
cannot be decoded on its own, because the state at its start depends on all the code points of the value before it. An application that needs to
decode each line on its own must encode each line as a value of its own.

### 1.3 What it does not do

- **It is not a collation.** The order is code point order. It differs from the order of any language.
- **Code point order is not UTF-16 code unit order.** A supplementary code point (U+10000 and above) sorts after U+E000..U+FFFF in code point
  order, but its UTF-16 form starts with a surrogate code unit (`0xD800`..`0xDBFF`), which sorts before them. For example, U+FF61 sorts before
  U+1F600 in code point order and in Steno, but after it in UTF-16 code unit order. Ordinal string comparison in .NET (`string.CompareOrdinal`) and
  Java (`String.compareTo`) uses UTF-16 code unit order.
- **It is stateful.** The meaning of each byte depends on all the code points before it. A decoder must start at the first byte of a value, and
  the encoding of two values one after the other is not the two encodings one after the other.
- **It has no header.** The encoding carries no marker or version number. An application that stores Steno must record elsewhere that the bytes
  are in the Steno Encoding, version 1.
- **A change of script costs extra bytes.** The first code point of a new window takes 2 to 4 bytes. After it, the code points of that window take
  1 byte. No code point takes more than 4 bytes, and no code point below U+20000 (the BMP and plane 1, which has the emoji) takes more than 3.

Unpaired surrogate code units are allowed ([§3](#3-text-model)), so every sequence of UTF-16 code units can be encoded.

### 1.4 How it works

The encoder keeps a small **state** while it reads the code points of a value. The decoder keeps the same state, because the state depends only on
the code points already read.

The state has a **context**: general, CJK (Chinese and Japanese text) or Hangul (Korean text). In the general context, the state names a **main
window**, whose code points take 1 byte, and an optional **second window**, whose code points take 2 bytes. A code point of another window makes
that window the main window (a **switch**), unless the **confidence** is above 0. The confidence counts the recent code points of the main window,
up to 2, so that one code point of another window does not switch the main window away while text in the main window's script goes on.

CJK ideographs and kana put the state in the **CJK context**, and Hangul syllables put it in the **Hangul context**. These are the **wide
contexts**. In the CJK context, a **kana slot** decides which group of code points takes 1 byte: the hiragana letters, the katakana letters, or the
ASCII letters. Leaving a wide context takes two code points of the same window, with no CJK code point, Hangul syllable or code point of another
window between them. The first of the two is kept as the **pending window**, so that one Latin letter in Japanese text does not end the CJK
context.

From the state, the encoder and the decoder build the same **layout**. The layout splits all code points into ranges, and gives each range a size
of 1 to 4 bytes per code point and a run of lead byte values. Lead byte values are assigned in code point order, which is what makes the bytes sort
like the code points.

## 2. Conventions

### 2.1 Notation

- Code points are written `U+0041`. UTF-16 code units are written `0xD800`.
- In formulas, code points and other integers are written in hexadecimal with `0x` (`0x10000`) or in decimal (`248`). Large decimal numbers are
  written with commas (`7,356`).
- Bytes are written as two hexadecimal digits (`2F`). A byte string is written as bytes separated by spaces (`2F 50 08`).
- `a..b` is the run of integers from `a` to `b`, both included.
- `x^y` is `x` to the power `y`. Division `/` in a formula rounds down. `x mod y` is the remainder of that division.
- "From `a` to `b`" in a rule means `a..b`.
- References to other parts are written "§6.3", "§7 step 3" and "§10.1 rule 2".

### 2.2 What is binding

The rules define the format. The rules are §3 to §10 and the test vectors of §12, with these exceptions, which are informative:

- paragraphs that start with **Note**;
- tables that are derived from the rules: the initial layout and its derivation (§6.6), the decision table (§7.2) and the layouts of §11.1;
- §1, and the sections marked "(informative)": §11, §13 and §14.

If an informative part disagrees with a rule, the rule is correct.

The test vectors are normative together with the rules. If the rules and the test vectors are ever found to disagree, that is a defect in the text.
It must be reported, and it is corrected by an erratum to the text that keeps the bytes of the test vectors, because stored data depends on the
bytes that conforming encoders have produced (see [§15](#15-versioning)).

The words "must" and "must not" state requirements.

### 2.3 Terms

Each term is defined where it is first used. [Appendix A](#appendix-a-glossary) lists all terms with the section that defines them.

## 3. Text model

A **value** is a sequence of zero or more code points. A **code point** is an integer from U+0000 to U+10FFFF. The surrogate code points
U+D800..U+DFFF are allowed as code points of their own.

A sequence of code points in which a high surrogate code point (U+D800..U+DBFF) is directly followed by a low surrogate code point
(U+DC00..U+DFFF) is **not a value**. An encoder whose input is code points must reject such a sequence ([§10.2](#102-conforming-encoders)).

An encoder whose input is UTF-16 code units must read them from first to last and turn them into code points like this:

- A high surrogate code unit (`0xD800`..`0xDBFF`) directly followed by a low surrogate code unit (`0xDC00`..`0xDFFF`) is one code point:
  `0x10000 + (high - 0xD800) * 0x400 + (low - 0xDC00)`.
- Every other code unit is the code point with the same value. This includes a surrogate code unit that is not part of such a pair.

Such an encoder is never given a sequence that is not a value, because it reads each pair of surrogate code units as one code point.

A decoder whose output is UTF-16 must write a code point below U+10000 as one code unit with its value, and a code point from U+10000 as its
surrogate pair: high `0xD800 + (c - 0x10000) / 0x400`, low `0xDC00 + (c - 0x10000) mod 0x400`.

> **Note** Every sequence of UTF-16 code units, well-formed or not, is read as exactly one value, and writing a value as UTF-16 gives that sequence
> back. This works because a value never has a high surrogate code point directly followed by a low one.

Converting between UTF-8 and code points is outside this format. A surrogate code point has no UTF-8 form, so a conversion to UTF-8 must fail on
one rather than write another code point in its place, unless the caller asked for a lossy conversion.

The encoding does not normalize the text. It does not add or remove a byte order mark (U+FEFF is an ordinary code point). It does not end a value
with a terminator. The empty value encodes to the empty byte string.

## 4. Windows

A **window** is a run of consecutive code points. The function `window(c)` gives the window of a code point `c`, or none. It is defined by the first row of
this table that contains `c`. A **block** is the 128 code points from `c - (c mod 0x80)` to `c - (c mod 0x80) + 0x7F`. Every row that says
"block" starts and ends on a multiple of `0x80`, so a block never reaches outside its row.

| Code point c | window(c) |
|---|---|
| U+0000..U+00BF | none |
| U+00C0..U+013F | U+00C0..U+013F |
| U+0140..U+01BF | U+0140..U+01BF |
| U+01C0..U+023F | U+01C0..U+023F |
| U+0240..U+02BF | U+0240..U+02BF |
| U+02C0..U+02FF | U+02C0..U+02FF |
| U+0300..U+036F | U+0300..U+036F |
| U+0370..U+037F | none |
| U+0380..U+04FF | block |
| U+0500..U+052F | U+0500..U+052F |
| U+0530..U+058F | U+0530..U+058F |
| U+0590..U+05FF | U+0590..U+05FF |
| U+0600..U+1FFF | block |
| U+2000..U+20FF | none |
| U+2100..U+2FFF | block |
| U+3000..U+30FF | none |
| U+3100..U+33FF | block |
| U+3400..U+9FFF | none |
| U+A000..U+ABFF | block |
| U+AC00..U+DFFF | none |
| U+E000..U+1FFFF | block |
| U+20000..U+3FFFF | none |
| U+40000..U+10FFFF | block |

There are 7,356 windows. Two code points are in the same window when `window` gives the same window for both.

> **Note** The windows that are not blocks:
>
> - **U+00C0..U+013F and the Latin windows after it.** The Latin windows start at U+00C0, the first accented letter, so that the Latin-1 letters
>   and the first half of Latin Extended-A are one window. The windows then go on in steps of 128 code points; the last one, U+02C0..U+02FF, has
>   64 code points.
> - **U+0300..U+036F** is the Unicode block of combining diacritical marks. The window ends where the Greek block begins.
> - **U+0370..U+037F** has no window. As a window, these 16 code points (in front of the Greek letters) would make a main window of only 16 code
>   points.
> - **U+0500..U+05FF** is split at the boundaries of the Unicode blocks Cyrillic Supplement (U+0500..U+052F), Armenian (U+0530..U+058F) and
>   Hebrew (U+0590..U+05FF), so that each of these blocks is one window.

## 5. State

The state has these fields:

| Field | Values | Initial value |
|---|---|---|
| context | general, CJK, Hangul | general |
| main window | a window | U+00C0..U+013F |
| second window | a window, or none | none |
| kana slot | letters, hiragana, katakana | letters |
| confidence | 0, 1 or 2 | 0 |
| pending window | a window, or none | none |

Every value is encoded and decoded starting from the initial state.

What the fields are for:

- **context.** The **general context** is for all text but Chinese, Japanese and Korean. The **CJK context** is for Chinese and Japanese text
  (ideographs and kana). The **Hangul context** is for Korean text. The CJK context and the Hangul context are the **wide contexts**.
- **main window.** In the general context, the code points of the main window take 1 byte. In the wide contexts they take 2 bytes, unless a piece
  laid after the main window's piece covers them ([§6.2](#62-pieces)).
- **second window.** Its code points take 2 bytes, unless a piece laid after the second window's piece covers them.
- **kana slot.** It is used only in the CJK context. Its value says which group of code points has the 1-byte slot there; the value **letters**
  means that the ASCII letters have it. In detail, these code points take 1 byte in the CJK context, besides U+0009, U+000A, U+000D and
  U+3000..U+300F:
  - **letters:** U+0020..U+007E and U+FF00..U+FF1F;
  - **hiragana:** U+0020..U+0040 and U+3041..U+3096;
  - **katakana:** U+0020..U+0040 and U+30A1..U+30FC.

  Japanese text changes often between hiragana, katakana and Latin letters, and the layout has room for only one of the three groups at 1 byte.
- **confidence.** It is used only in the general context. It keeps one code point of another window from switching the main window away while
  text in the main window's script goes on.
- **pending window.** It is used only in the wide contexts. Leaving a wide context takes two code points of the same window, so that one code point
  of a window (one Latin letter in Japanese text, for example) does not end it.

When a field is not used in the current context, its value does not matter: the rules of §7 set the field before it is used again. An
implementation may keep the value or discard it.

The layout ([§6](#6-layout)) depends only on the context, the kana slot (in the CJK context only), the main window and the second window. It does
not depend on the confidence or the pending window.

## 6. Layout

### 6.1 Overview

The **layout** of a state gives every code point a **size** (the number of bytes of its encoding, 1 to 4) and every range of the layout its lead
bytes. It is built in four steps:

1. **Overlay the pieces** ([§6.2](#62-pieces)). A **piece** is a run of consecutive code points with a size. The pieces are laid one over the other in a fixed
   order.
2. **Size the stretches** ([§6.3](#63-stretches-and-parts)). A **stretch** is a maximal run of code points that no piece covers. Each stretch is
   split into parts, and each part gets a size from its length.
3. **Join into ranges** ([§6.4](#64-ranges)). A **range** is a maximal run of consecutive code points of the same size.
4. **Assign lead bytes** ([§6.5](#65-lead-bytes)) to the ranges, in code point order.

The **lead byte** of a code point is the first byte of its encoding (§8.2). Its value tells which range the code point is in, and for a range of
size 1 it is the only byte.

As pseudocode:

```
layout(state):
  size = an array over U+0000..U+10FFFF, every entry "not covered"
  for each piece (start, end, s) of state, in the order of 6.2:
    size[start..end] = s                          -- a later piece overwrites an earlier one
  for each stretch (maximal run of "not covered"):
    for each part of the stretch (6.3):
      size[part] = 2, 3 or 4, from the length of the part
  ranges = the maximal runs of equal size, in code point order (6.4)
  lead = 0x01
  for each range R:                               -- 6.5
    R.firstLead = lead
    lead = lead + ceil(length(R) / 248^(R.size - 1))
  return ranges
```

### 6.2 Pieces

The pieces of a state are listed below, in order. Where pieces overlap, a code point gets the size of the **last** piece that covers it.

Write the main window as `M0..M1` and the second window, if it is not none, as `S0..S1`.

1. Only in the general context, the **neighbours** of the main window:
   - the **lower neighbours**: from `max(0x80, M0 - 248)` to `M0 - 1`, size 2;
   - if `M1 < 0x10FFFF`, the **upper neighbours**: from `M1 + 1` to `min(0x10FFFF, M1 + 248)`, size 2.
2. U+0000..U+001F, size 2. Then U+0009..U+000A, size 1. Then U+000D, size 1.
3. If the context is CJK and the kana slot is not letters: U+0020..U+0040, size 1, and U+0041..U+007E, size 2.
   Otherwise: U+0020..U+007E, size 1.
4. U+007F..U+00BF, size 2. (It is the only piece that covers U+007F, and in the CJK context, where piece 5 is absent, also U+0080..U+00BF.)
5. If the context is not CJK: U+0080..U+0EFF, size 2.
6. U+2000..U+20CF, size 2.
7. If the second window is not none: `S0..S1`, size 2.
8. Depending on the context:
   - **general:** `M0..M1`, size 1.
   - **Hangul**, in this order: `M0..M1`, size 2; U+3000..U+303F, size 2; U+AC00..U+D7A3, size 2; U+FF00..U+FFFF, size 2.
   - **CJK**, in this order:
     1. `M0..M1`, size 2;
     2. U+3000..U+300F, size 1;
     3. U+3010..U+30FF, size 2;
     4. if the kana slot is hiragana: U+3041..U+3096, size 1. If it is katakana: U+30A1..U+30FC, size 1;
     5. U+4E00..U+9FFF, size 2;
     6. U+FF00..U+FFFF, size 2;
     7. if the kana slot is letters: U+FF00..U+FF1F, size 1.

> **Note** Piece 7 is laid before the pieces of item 8, so those pieces override the second window where they overlap it.

### 6.3 Stretches and parts

A **stretch** is a maximal run of consecutive code points that no piece covers.

Split each stretch into **parts**: cut it at U+10FC0 and at U+20000 where it contains them (that is, between U+10FBF and U+10FC0 and between
U+1FFFF and U+20000), and nowhere else. This gives one to three parts, and each part lies wholly inside one of the three runs U+0000..U+10FBF,
U+10FC0..U+1FFFF and U+20000..U+10FFFF. A part with `n` code points gets this size:

| n | Size |
|---|---|
| 1..248 | 2 |
| 249..61,504 | 3 |
| 61,505 or more | 4 |

Now every code point has a size.

> **Note** 61,504 is `248^2`, the number of code points that one lead byte holds at size 3 (§6.5), and U+10FC0 is `0x20000 - 61,504`. So the run
> U+10FC0..U+1FFFF holds exactly what one lead byte holds at size 3, and ends with plane 1: no code point below U+20000 takes more than 3 bytes. A
> stretch below U+10FC0 has at most 61,168 code points (U+20D0..U+10FBF, after piece 6), which is also less than `248^2`. With these bounds every
> part takes exactly one lead byte if it is not joined with a neighbour. This keeps the number of lead bytes small enough to fit in a byte (§13.6).

> **Note** No layout has a part of exactly 248, 249 or 61,505 code points, so the bounds between the rows of the table at those numbers are never
> met; they are there so that the rule is complete. A part of exactly 61,504 code points does occur: U+10FC0..U+1FFFF.

### 6.4 Ranges

The **ranges** of the layout are the maximal runs of consecutive code points of the same size, in increasing code point order.

This joins runs of the same size that are next to each other, also when they come from different sources: two pieces, a piece and a part, or
two parts of one stretch that §6.3 split.

### 6.5 Lead bytes

A range of size `k` with `n` code points needs `ceil(n / 248^(k-1))` lead bytes: `n` for size 1, `ceil(n / 248)` for size 2, `ceil(n / 61,504)`
for size 3 and `ceil(n / 15,252,992)` (always 1) for size 4.

Assign lead bytes to the ranges in code point order, one range after another, starting at `01`. The first lead byte of a range is its **first lead
byte**, written `F`.

A byte value that no range is assigned is an **unused** lead byte. The byte `00` is never a lead byte.

No layout needs more than 254 lead bytes ([§13.6](#136-every-layout-fits)), so lead bytes go up to `FE` at most, and `FF` is never a lead byte.

### 6.6 The initial layout

This section is informative: it derives the layout of the initial state from the rules above.

The initial state is the general context with the main window U+00C0..U+013F and no second window.

**Step 1: the pieces, in order.**

| Piece | Code points | Size |
|---|---|---|
| 1, lower neighbours | U+0080..U+00BF (from `max(0x80, 0xC0 - 248)`) | 2 |
| 1, upper neighbours | U+0140..U+0237 (to `0x13F + 248`) | 2 |
| 2 | U+0000..U+001F | 2 |
| 2 | U+0009..U+000A | 1 |
| 2 | U+000D | 1 |
| 3 | U+0020..U+007E | 1 |
| 4 | U+007F..U+00BF | 2 |
| 5 | U+0080..U+0EFF | 2 |
| 6 | U+2000..U+20CF | 2 |
| 8, general | U+00C0..U+013F | 1 |

After the overlay, the covered code points have these sizes: U+0000..U+0008 size 2, U+0009..U+000A size 1, U+000B..U+000C size 2, U+000D size 1,
U+000E..U+001F size 2, U+0020..U+007E size 1, U+007F..U+00BF size 2, U+00C0..U+013F size 1, U+0140..U+0EFF size 2, U+2000..U+20CF size 2.

**Step 2: the stretches and their parts.**

| Stretch | Part | Code points | Size |
|---|---|---|---|
| U+0F00..U+1FFF | U+0F00..U+1FFF | 4,352 | 3 |
| U+20D0..U+10FFFF | U+20D0..U+10FBF | 61,168 | 3 |
| | U+10FC0..U+1FFFF | 61,504 | 3 |
| | U+20000..U+10FFFF | 983,040 | 4 |

**Steps 3 and 4: the ranges and their lead bytes.** The two parts U+20D0..U+10FBF and U+10FC0..U+1FFFF have the same size and are next to each
other, so they join into one range.

| Range | Size | Code points | Lead bytes | Encoding of `c` |
|---|---|---|---|---|
| U+0000..U+0008 | 2 | 9 | `01` | `01`, then `c + 8` |
| U+0009..U+000A | 1 | 2 | `02`..`03` | `c - 0x07` |
| U+000B..U+000C | 2 | 2 | `04` | `04`, then `c - 0x03` |
| U+000D | 1 | 1 | `05` | `05` |
| U+000E..U+001F | 2 | 18 | `06` | `06`, then `c - 0x06` |
| U+0020..U+007E | 1 | 95 | `07`..`65` | `c - 0x19` |
| U+007F..U+00BF | 2 | 65 | `66` | `66`, then `c - 0x77` |
| U+00C0..U+013F | 1 | 128 | `67`..`E6` | `c - 0x59` |
| U+0140..U+0EFF | 2 | 3,520 | `E7`..`F5` | `0xE7 + (c - 0x140) / 248`, then `(c - 0x140) mod 248 + 8` |
| U+0F00..U+1FFF | 3 | 4,352 | `F6` | `F6`, then `(c - 0xF00) / 248 + 8`, then `(c - 0xF00) mod 248 + 8` |
| U+2000..U+20CF | 2 | 208 | `F7` | `F7`, then `c - 0x1FF8` |
| U+20D0..U+1FFFF | 3 | 122,672 | `F8`..`F9` | with `o = c - 0x20D0`: `0xF8 + o / 61,504`, then `(o mod 61,504) / 248 + 8`, then `o mod 248 + 8` |
| U+20000..U+10FFFF | 4 | 983,040 | `FA` | with `o = c - 0x20000`: `FA`, then `o / 61,504 + 8`, then `(o mod 61,504) / 248 + 8`, then `o mod 248 + 8` |

The initial layout needs 250 lead bytes. The bytes `FB`..`FF` are unused in it.

## 7. State update

### 7.1 Rules

After each code point `c`, the encoder and the decoder update the state. Let `k` be the size of the range that holds `c` in the layout of the
state before the update (the number of bytes that `c` took).

A **CJK code point** is a code point in U+3040..U+30FF, U+3400..U+9FFF or U+20000..U+3FFFF, or, when the context is not Hangul, in
U+3000..U+303F. An **ASCII letter** is a code point in U+0041..U+005A or U+0061..U+007A.

Go through the steps in order. A step whose condition holds does what it says. If it says "stop", the update ends. Otherwise go on to the next
step.

**Step 1.** If `c` is in U+0000..U+007F: if the context is CJK and `c` is an ASCII letter, set the kana slot to letters. Then stop, in every case.

**Step 2.** If `c` is in U+AC00..U+D7A3 (a Hangul syllable): set the pending window to none and the context to Hangul. Stop.

**Step 3.** If `c` is a CJK code point:

1. If the context is not CJK: set the context to CJK and the kana slot to letters.
2. Set the pending window to none.
3. If `c` is in U+3041..U+3096 (the hiragana letters), set the kana slot to hiragana. If `c` is in U+30A1..U+30FA (the katakana letters), set the
   kana slot to katakana. Any other code point leaves the kana slot as it is.

Stop.

> **Note** U+30FB (katakana middle dot) and U+30FC (prolonged sound mark) take 1 byte when the kana slot is katakana (§6.2), but they do not move
> the kana slot (step 3.3), because both kana use them. This difference is intended.

**Step 4.** If the context is a wide context and `c` is in U+FF00..U+FFFF: stop.

**Step 5.** Let `W = window(c)`. If `W` is none: stop.

**Step 6.** If the context is general and `W` is the main window: set the confidence to `min(confidence + 1, 2)`. Stop.

**Step 7.** If `k` is 3 or 4: set the second window to `W`. Then go on to step 8.

**Step 8.**

- In the general context: if the confidence is greater than 0, lower it by 1 and stop. Otherwise go on to step 9.
- In a wide context: if the pending window is not `W`, set the pending window to `W` and stop. Otherwise go on to step 9.

**Step 9 (a switch to `W`).**

1. If the main window is not `W`: set the second window to the main window.
   Otherwise, if the second window is `W`: set the second window to none.
2. Set the main window to `W`, the context to general, the pending window to none and the confidence to 0.

Stop.

> **Note** In a wide context, a code point of the main window never takes more than 2 bytes, so step 7 never applies to it. In the general
> context, a code point of another window that takes 2 bytes (because it is a neighbour, lies in U+0080..U+0EFF or is in the second window) lowers
> the confidence or switches, but it never changes the second window.

### 7.2 Decision table

This table is informative. It sums up §7.1; where the two disagree, §7.1 is correct. Each row is one outcome; the rows are in the order in which
their conditions are tested. "Step" names the row in the examples of §11. Steps 7 and 9 never appear as rows of their own: step 7 is part of
rows 8a and 8c, and step 9 (the switch) is part of rows 8b and 8d.

| Step | Condition | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|
| 1a | `c` in U+0000..U+007F, CJK context, ASCII letter | unchanged | unchanged | unchanged | letters | unchanged | unchanged |
| 1b | `c` in U+0000..U+007F, otherwise | unchanged | unchanged | unchanged | unchanged | unchanged | unchanged |
| 2 | Hangul syllable | Hangul | unchanged | unchanged | unchanged | unchanged | none |
| 3a | CJK code point, context not CJK | CJK | unchanged | unchanged | letters, then step 3.3 | unchanged | none |
| 3b | CJK code point, context CJK | unchanged | unchanged | unchanged | step 3.3 | unchanged | none |
| 4 | wide context, `c` in U+FF00..U+FFFF | unchanged | unchanged | unchanged | unchanged | unchanged | unchanged |
| 5 | `c` has no window | unchanged | unchanged | unchanged | unchanged | unchanged | unchanged |
| 6 | general context, `W` is the main window | unchanged | unchanged | unchanged | unchanged | +1, at most 2 | unchanged |
| 8a | general context, confidence above 0 | unchanged | unchanged | `W` if `k` is 3 or 4 (\*) | unchanged | lowered by 1 | unchanged |
| 8b | general context, confidence 0 | general | `W` | the old main window | unchanged | 0 | none |
| 8c | wide context, pending window is not `W` | unchanged | unchanged | `W` if `k` is 3 or 4 (\*) | unchanged | unchanged | `W` |
| 8d | wide context, pending window is `W`, main window is not `W` | general | `W` | the old main window | unchanged | 0 | none |
| 8e | wide context, pending window is `W`, main window is `W` | general | unchanged | none if it is `W` (\*\*) | unchanged | 0 | none |

(\*) Step 7. Otherwise unchanged.
(\*\*) Otherwise unchanged. Step 7 does not apply here (see the note in §7.1).

## 8. Encoding a value

### 8.1 The loop

```
encode(value):
  state = the initial state (5)
  for each code point c of value, in order:
    L = layout(state)                 -- 6
    write the bytes of c in L          -- 8.2
    update the state with c and the size of its range in L   -- 7
```

An encoder whose input is code points must first check that the input is a value (§3).

### 8.2 One code point

To encode code point `c` in a layout:

1. Find the range that holds `c`. Let `R0` be its first code point, `k` its size and `F` its first lead byte.
2. Let `o = c - R0`, the **offset** of `c` in its range.
3. Split `o` into `q = o / 248^(k-1)` and `k - 1` digits `t1`, ..., `t(k-1)` in base 248, each 0..247:
   `o = q * 248^(k-1) + t1 * 248^(k-2) + ... + t(k-1)`.
   `q` can be larger than 247: it selects one of the range's lead bytes.
4. The bytes are the lead byte `F + q`, then the **trail bytes** `t1 + 8`, ..., `t(k-1) + 8`. So every trail byte is `08`..`FF`.

For size 1 the only byte is `F + o`.

> **Note** Every number in these steps is below `248^3 = 15,252,992`, so 32-bit integers are enough (also in §9.2, where `o` can exceed
> `0x10FFFF` before the range check).

## 9. Decoding a value

### 9.1 The loop

```
decode(bytes):
  state = the initial state (5)
  while bytes are left:
    L = layout(state)                 -- 6
    read one code point c from the bytes in L     -- 9.2
    if c is a low surrogate code point and the code point before it in this value is a high surrogate code point:
      reject the input                -- 10.1 rule 5
    output c
    update the state with c and the size of its range in L   -- 7
```

### 9.2 One code point

To read one code point at the current position, in a layout:

1. Read the lead byte `b`. If `b` is `00`, reject the input (§10.1 rule 1). If `b` is unused, reject the input (§10.1 rule 2).
2. Let the range that is assigned `b` have first code point `R0`, last code point `R1`, size `k` and first lead byte `F`.
3. Read `k - 1` trail bytes, one at a time. Before reading each one: if the input has ended, reject the input (§10.1 rule 3). After reading each
   one: if it is `00`, reject the input (§10.1 rule 1); if it is `01`..`07`, reject the input (§10.1 rule 6).
4. Let `o = (b - F) * 248^(k-1) + (u1 - 8) * 248^(k-2) + ... + (u(k-1) - 8)`, where `u1`, ..., `u(k-1)` are the trail bytes in order.
5. The code point is `c = R0 + o`. If `c > R1`, reject the input (§10.1 rule 4).

The range check of step 5 comes after all trail bytes are read. So input that ends in the middle of a code point is rejected by rule 3, even when
the bytes that are present already show that the code point is past its range.

## 10. Validity and conformance

### 10.1 Rejection rules

A decoder must reject the input, with no partial result, if it meets any of these while it decodes as in §9. The numbers match the reasons of the
test vectors (§12).

| Rule | Reason in the test vectors | The input has |
|---|---|---|
| 1 | `zero-byte` | a byte `00`, as a lead byte or as a trail byte |
| 2 | `unused-lead` | a lead byte other than `00` that the layout of the current state does not use |
| 3 | `truncated` | an end inside a code point: after a lead byte of a range of size `k`, fewer than `k - 1` bytes are left, all of them `08`..`FF` |
| 4 | `past-range` | a code point beyond the last code point of its range (`c > R1` in §9.2). Every range ends at U+10FFFF at most, so this rule also covers every value above U+10FFFF |
| 5 | `surrogate-pair` | a low surrogate code point (U+DC00..U+DFFF) directly after a high surrogate code point (U+D800..U+DBFF), both decoded from the input |
| 6 | `invalid-trail` | a byte `01`..`07` where a trail byte is expected (trail bytes are `08`..`FF`; a byte `00` there is rule 1) |

The rules are numbered by name, not in the order a decoder meets them: §9.2 gives that order. For each code point, the lead byte is checked first
(rules 1 and 2), then each trail byte as it is read (rule 3 before it is read, then rule 1, then rule 6), then the range (rule 4), and last rule 5.

> **Note** Rule 5 keeps a value from having two encodings. In UTF-16, a high surrogate followed by a low surrogate is one supplementary code
> point. Without rule 5, the decoder would accept that code point in two forms: as itself, and as two surrogate code points.

### 10.2 Conforming encoders

A conforming encoder:

- must encode every value, from the initial state, to exactly the bytes that §6 to §8 give;
- must reject a sequence of code points that is not a value (§3), if its input is code points;
- must read UTF-16 input as §3 says, if its input is UTF-16.

Two conforming encoders give the same bytes for the same value.

How an encoder rejects a sequence that is not a value (an error, an exception or a status, for example) is left to the implementation. Such a
rejection has no reason name: the reasons of §10.1 and §12 are for decoders.

### 10.3 Conforming decoders

A conforming decoder:

- must decode, from the initial state, every byte string that a conforming encoder produces to the value that was encoded;
- must reject every other byte string, by the rules of §10.1;
- must write UTF-16 output as §3 says, if its output is UTF-16.

The reason a decoder reports for a rejection is for diagnosis only; only the rejection is required.

## 11. Worked examples (informative)

### 11.1 Layouts of other states

These layouts are derived from the rules, to check an implementation against.

Each layout is headed by its state. The code points in parentheses are one value that reaches the state from the initial state.

**General context; main window U+0400..U+047F; second window U+1200..U+127F.** (U+0416 U+0416 U+0416 U+1200)

| Range | Size | Lead bytes |
|---|---|---|
| U+0000..U+0008 | 2 | `01` |
| U+0009..U+000A | 1 | `02`..`03` |
| U+000B..U+000C | 2 | `04` |
| U+000D | 1 | `05` |
| U+000E..U+001F | 2 | `06` |
| U+0020..U+007E | 1 | `07`..`65` |
| U+007F..U+03FF | 2 | `66`..`69` |
| U+0400..U+047F | 1 | `6A`..`E9` |
| U+0480..U+0EFF | 2 | `EA`..`F4` |
| U+0F00..U+11FF | 3 | `F5` |
| U+1200..U+127F | 2 | `F6` |
| U+1280..U+1FFF | 3 | `F7` |
| U+2000..U+20CF | 2 | `F8` |
| U+20D0..U+1FFFF | 3 | `F9`..`FA` |
| U+20000..U+10FFFF | 4 | `FB` |

**CJK context; kana slot hiragana; main window U+00C0..U+013F; no second window.** (U+3042)

| Range | Size | Lead bytes |
|---|---|---|
| U+0000..U+0008 | 2 | `01` |
| U+0009..U+000A | 1 | `02`..`03` |
| U+000B..U+000C | 2 | `04` |
| U+000D | 1 | `05` |
| U+000E..U+001F | 2 | `06` |
| U+0020..U+0040 | 1 | `07`..`27` |
| U+0041..U+013F | 2 | `28`..`29` |
| U+0140..U+1FFF | 3 | `2A` |
| U+2000..U+20CF | 2 | `2B` |
| U+20D0..U+2FFF | 3 | `2C` |
| U+3000..U+300F | 1 | `2D`..`3C` |
| U+3010..U+3040 | 2 | `3D` |
| U+3041..U+3096 | 1 | `3E`..`93` |
| U+3097..U+30FF | 2 | `94` |
| U+3100..U+4DFF | 3 | `95` |
| U+4E00..U+9FFF | 2 | `96`..`EA` |
| U+A000..U+FEFF | 3 | `EB` |
| U+FF00..U+FFFF | 2 | `EC`..`ED` |
| U+10000..U+1FFFF | 3 | `EE`..`EF` |
| U+20000..U+10FFFF | 4 | `F0` |

**Hangul context; main window U+00C0..U+013F; no second window.** (U+D55C)

| Range | Size | Lead bytes |
|---|---|---|
| U+0000..U+0008 | 2 | `01` |
| U+0009..U+000A | 1 | `02`..`03` |
| U+000B..U+000C | 2 | `04` |
| U+000D | 1 | `05` |
| U+000E..U+001F | 2 | `06` |
| U+0020..U+007E | 1 | `07`..`65` |
| U+007F..U+0EFF | 2 | `66`..`74` |
| U+0F00..U+1FFF | 3 | `75` |
| U+2000..U+20CF | 2 | `76` |
| U+20D0..U+2FFF | 3 | `77` |
| U+3000..U+303F | 2 | `78` |
| U+3040..U+ABFF | 3 | `79` |
| U+AC00..U+D7A3 | 2 | `7A`..`A7` |
| U+D7A4..U+FEFF | 3 | `A8` |
| U+FF00..U+FFFF | 2 | `A9`..`AA` |
| U+10000..U+1FFFF | 3 | `AB`..`AC` |
| U+20000..U+10FFFF | 4 | `AD` |

**General context; main window U+1000..U+107F; second window U+1200..U+127F.** (U+1000 U+1001 U+1200) This layout needs 254 lead bytes, the most
that any layout needs (§13.6).

| Range | Size | Lead bytes |
|---|---|---|
| U+0000..U+0008 | 2 | `01` |
| U+0009..U+000A | 1 | `02`..`03` |
| U+000B..U+000C | 2 | `04` |
| U+000D | 1 | `05` |
| U+000E..U+001F | 2 | `06` |
| U+0020..U+007E | 1 | `07`..`65` |
| U+007F..U+0FFF | 2 | `66`..`76` |
| U+1000..U+107F | 1 | `77`..`F6` |
| U+1080..U+127F | 2 | `F7`..`F9` |
| U+1280..U+1FFF | 3 | `FA` |
| U+2000..U+20CF | 2 | `FB` |
| U+20D0..U+1FFFF | 3 | `FC`..`FD` |
| U+20000..U+10FFFF | 4 | `FE` |

### 11.2 Two code points worked out in full

The value U+041C U+0438 (Cyrillic "Ми"), from the initial state.

**U+041C.** The initial layout (§6.6) puts U+041C in the range U+0140..U+0EFF, size 2, first lead byte `E7`.
The offset is `o = 0x41C - 0x140 = 0x2DC = 732`. Then `q = 732 / 248 = 2` and `t1 = 732 - 2 * 248 = 236`.
The bytes are `E7 + 2 = E9` and `236 + 8 = 244 = F4`: `E9 F4`.

To decode `E9 F4`: the lead byte `E9` is in the run `E7`..`F5` of the same range; `o = (0xE9 - 0xE7) * 248 + (0xF4 - 8) = 496 + 236 = 732`,
and `c = 0x140 + 732 = 0x41C`.

State update with `k = 2`: step 5 gives `W = U+0400..U+047F`. Step 6 does not apply (the main window is U+00C0..U+013F). Step 7 does not apply
(`k` is 2). Step 8: the context is general and the confidence is 0, so step 9 switches: the second window becomes U+00C0..U+013F, the main window
becomes U+0400..U+047F, and the confidence stays 0.

**U+0438.** The new layout gives the main window U+0400..U+047F size 1, with first lead byte `6A` (see the first layout of §11.1, which has the same
ranges up to U+047F). So U+0438 is `0x6A + (0x438 - 0x400) = 0x6A + 0x38 = A2`. State update: step 6, the confidence becomes 1.

The value encodes to `E9 F4 A2`.

Each of the following examples shows the code points of a value, its bytes, and for each code point the bytes it took, the step of the decision table
(§7.2) that applied, and the state after the update. "not used" marks a field that is not used in the context (§5).

### 11.3 ASCII with tab, carriage return and line feed

Code points: U+0048 U+0069 U+0021 U+0009 U+004F U+004B U+000D U+000A

Bytes: `2F 50 08 02 36 32 05 03`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+0048 | `2F` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 2 | U+0069 | `50` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 3 | U+0021 | `08` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 4 | U+0009 | `02` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 5 | U+004F | `36` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 6 | U+004B | `32` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 7 | U+000D | `05` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 8 | U+000A | `03` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |

Every code point is in U+0000..U+007F, so the state does not change (step 1b), and the bytes are those of the initial layout (§14.1).

### 11.4 Accented Latin

Code points: U+0043 U+0061 U+0066 U+00E9 U+0020 U+0063 U+0072 U+00E8 U+006D U+0065

Bytes: `2A 48 4D 90 07 4A 59 8F 54 4C`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+0043 | `2A` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 2 | U+0061 | `48` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 3 | U+0066 | `4D` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 4 | U+00E9 | `90` | 6 | general | U+00C0..U+013F | none | not used | 1 | not used |
| 5 | U+0020 | `07` | 1b | general | U+00C0..U+013F | none | not used | 1 | not used |
| 6 | U+0063 | `4A` | 1b | general | U+00C0..U+013F | none | not used | 1 | not used |
| 7 | U+0072 | `59` | 1b | general | U+00C0..U+013F | none | not used | 1 | not used |
| 8 | U+00E8 | `8F` | 6 | general | U+00C0..U+013F | none | not used | 2 | not used |
| 9 | U+006D | `54` | 1b | general | U+00C0..U+013F | none | not used | 2 | not used |
| 10 | U+0065 | `4C` | 1b | general | U+00C0..U+013F | none | not used | 2 | not used |

U+00E9 and U+00E8 are in the main window U+00C0..U+013F and take 1 byte each (`c - 0x59`). Each raises the confidence by 1 (step 6).

### 11.5 Cyrillic with punctuation

Code points: U+041C U+0438 U+0440 U+002C U+0020 U+00AB U+0434 U+0430 U+00BB U+0021

Bytes: `E9 F4 A2 AA 13 07 66 34 9E 9A 66 44 08`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+041C | `E9 F4` | 8b | general | U+0400..U+047F | U+00C0..U+013F | not used | 0 | not used |
| 2 | U+0438 | `A2` | 6 | general | U+0400..U+047F | U+00C0..U+013F | not used | 1 | not used |
| 3 | U+0440 | `AA` | 6 | general | U+0400..U+047F | U+00C0..U+013F | not used | 2 | not used |
| 4 | U+002C | `13` | 1b | general | U+0400..U+047F | U+00C0..U+013F | not used | 2 | not used |
| 5 | U+0020 | `07` | 1b | general | U+0400..U+047F | U+00C0..U+013F | not used | 2 | not used |
| 6 | U+00AB | `66 34` | 5 | general | U+0400..U+047F | U+00C0..U+013F | not used | 2 | not used |
| 7 | U+0434 | `9E` | 6 | general | U+0400..U+047F | U+00C0..U+013F | not used | 2 | not used |
| 8 | U+0430 | `9A` | 6 | general | U+0400..U+047F | U+00C0..U+013F | not used | 2 | not used |
| 9 | U+00BB | `66 44` | 5 | general | U+0400..U+047F | U+00C0..U+013F | not used | 2 | not used |
| 10 | U+0021 | `08` | 1b | general | U+0400..U+047F | U+00C0..U+013F | not used | 2 | not used |

U+041C has another window and the confidence is 0, so it switches (step 8b): the main window becomes U+0400..U+047F, and the second window the old
main window. U+00AB and U+00BB have no window and take 2 bytes in the range U+007F..U+03FF.

### 11.6 Greek

Code points: U+039A U+03B1 U+03BB U+03AE U+0020 U+03BC U+03AD U+03C1 U+03B1

Bytes: `E9 72 9B A5 98 07 A6 97 AB 9B`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+039A | `E9 72` | 8b | general | U+0380..U+03FF | U+00C0..U+013F | not used | 0 | not used |
| 2 | U+03B1 | `9B` | 6 | general | U+0380..U+03FF | U+00C0..U+013F | not used | 1 | not used |
| 3 | U+03BB | `A5` | 6 | general | U+0380..U+03FF | U+00C0..U+013F | not used | 2 | not used |
| 4 | U+03AE | `98` | 6 | general | U+0380..U+03FF | U+00C0..U+013F | not used | 2 | not used |
| 5 | U+0020 | `07` | 1b | general | U+0380..U+03FF | U+00C0..U+013F | not used | 2 | not used |
| 6 | U+03BC | `A6` | 6 | general | U+0380..U+03FF | U+00C0..U+013F | not used | 2 | not used |
| 7 | U+03AD | `97` | 6 | general | U+0380..U+03FF | U+00C0..U+013F | not used | 2 | not used |
| 8 | U+03C1 | `AB` | 6 | general | U+0380..U+03FF | U+00C0..U+013F | not used | 2 | not used |
| 9 | U+03B1 | `9B` | 6 | general | U+0380..U+03FF | U+00C0..U+013F | not used | 2 | not used |

The same pattern as Cyrillic: U+039A switches to U+0380..U+03FF.

### 11.7 Polish: two Latin windows

Code points: U+0141 U+00F3 U+0064 U+017A U+0020 U+017C U+00F3 U+0142 U+0077

Bytes: `E7 09 66 7C 4B E7 42 07 A3 66 7C 69 5E`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+0141 | `E7 09` | 8b | general | U+0140..U+01BF | U+00C0..U+013F | not used | 0 | not used |
| 2 | U+00F3 | `66 7C` | 8b | general | U+00C0..U+013F | U+0140..U+01BF | not used | 0 | not used |
| 3 | U+0064 | `4B` | 1b | general | U+00C0..U+013F | U+0140..U+01BF | not used | 0 | not used |
| 4 | U+017A | `E7 42` | 8b | general | U+0140..U+01BF | U+00C0..U+013F | not used | 0 | not used |
| 5 | U+0020 | `07` | 1b | general | U+0140..U+01BF | U+00C0..U+013F | not used | 0 | not used |
| 6 | U+017C | `A3` | 6 | general | U+0140..U+01BF | U+00C0..U+013F | not used | 1 | not used |
| 7 | U+00F3 | `66 7C` | 8a | general | U+0140..U+01BF | U+00C0..U+013F | not used | 0 | not used |
| 8 | U+0142 | `69` | 6 | general | U+0140..U+01BF | U+00C0..U+013F | not used | 1 | not used |
| 9 | U+0077 | `5E` | 1b | general | U+0140..U+01BF | U+00C0..U+013F | not used | 1 | not used |

U+0141 switches to U+0140..U+01BF, and U+00F3 then switches back, because the confidence is 0. At #7, U+00F3 takes 2 bytes and lowers the confidence
from 1 to 0 (step 8a), so it does not switch.

### 11.8 Ethiopic (Amharic), above U+0F00

Code points: U+1230 U+120B U+121D U+0020 U+12D3 U+1208 U+121D

Bytes: `F6 0B 50 82 94 07 F7 5B 7F 94`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+1230 | `F6 0B 50` | 8b | general | U+1200..U+127F | U+00C0..U+013F | not used | 0 | not used |
| 2 | U+120B | `82` | 6 | general | U+1200..U+127F | U+00C0..U+013F | not used | 1 | not used |
| 3 | U+121D | `94` | 6 | general | U+1200..U+127F | U+00C0..U+013F | not used | 2 | not used |
| 4 | U+0020 | `07` | 1b | general | U+1200..U+127F | U+00C0..U+013F | not used | 2 | not used |
| 5 | U+12D3 | `F7 5B` | 8a | general | U+1200..U+127F | U+00C0..U+013F | not used | 1 | not used |
| 6 | U+1208 | `7F` | 6 | general | U+1200..U+127F | U+00C0..U+013F | not used | 2 | not used |
| 7 | U+121D | `94` | 6 | general | U+1200..U+127F | U+00C0..U+013F | not used | 2 | not used |

U+1230 takes 3 bytes in the initial layout and switches. U+12D3 is an upper neighbour of U+1200..U+127F: it takes 2 bytes and lowers the confidence
from 2 to 1 (step 8a). It does not change the second window, because it took 2 bytes.

### 11.9 Japanese: kanji, hiragana, katakana and an ASCII word

Code points: U+6771 U+4EAC U+3067 U+30C6 U+30B9 U+30C8 U+3092 U+3059 U+308B U+004F U+004B

Bytes: `F8 50 E9 7C B4 7A 5F 94 37 56 65 3D 8A 56 88 28 16 32`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+6771 | `F8 50 E9` | 3a | CJK | U+00C0..U+013F | none | letters | not used | none |
| 2 | U+4EAC | `7C B4` | 3b | CJK | U+00C0..U+013F | none | letters | not used | none |
| 3 | U+3067 | `7A 5F` | 3b | CJK | U+00C0..U+013F | none | hiragana | not used | none |
| 4 | U+30C6 | `94 37` | 3b | CJK | U+00C0..U+013F | none | katakana | not used | none |
| 5 | U+30B9 | `56` | 3b | CJK | U+00C0..U+013F | none | katakana | not used | none |
| 6 | U+30C8 | `65` | 3b | CJK | U+00C0..U+013F | none | katakana | not used | none |
| 7 | U+3092 | `3D 8A` | 3b | CJK | U+00C0..U+013F | none | hiragana | not used | none |
| 8 | U+3059 | `56` | 3b | CJK | U+00C0..U+013F | none | hiragana | not used | none |
| 9 | U+308B | `88` | 3b | CJK | U+00C0..U+013F | none | hiragana | not used | none |
| 10 | U+004F | `28 16` | 1a | CJK | U+00C0..U+013F | none | letters | not used | none |
| 11 | U+004B | `32` | 1a | CJK | U+00C0..U+013F | none | letters | not used | none |

U+6771 enters the CJK context (step 3a). U+3067, a hiragana letter, moves the kana slot to hiragana; it takes 2 bytes, because the layout before it
had the kana slot letters. U+30C6, a katakana letter, moves the kana slot to katakana. U+004F, an ASCII letter, moves it to letters (step 1a); it
takes 2 bytes, because when the kana slot is hiragana or katakana the ASCII letters take 2 bytes.

### 11.10 Korean

Code points: U+D55C U+AD6D U+C5B4 U+0020 U+BB38 U+C7A5

Bytes: `F8 C2 64 7B 7D 94 8C 07 89 B8 96 8D`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+D55C | `F8 C2 64` | 2 | Hangul | U+00C0..U+013F | none | not used | not used | none |
| 2 | U+AD6D | `7B 7D` | 2 | Hangul | U+00C0..U+013F | none | not used | not used | none |
| 3 | U+C5B4 | `94 8C` | 2 | Hangul | U+00C0..U+013F | none | not used | not used | none |
| 4 | U+0020 | `07` | 1b | Hangul | U+00C0..U+013F | none | not used | not used | none |
| 5 | U+BB38 | `89 B8` | 2 | Hangul | U+00C0..U+013F | none | not used | not used | none |
| 6 | U+C7A5 | `96 8D` | 2 | Hangul | U+00C0..U+013F | none | not used | not used | none |

U+D55C enters the Hangul context (step 2). The Hangul syllables take 2 bytes each.

### 11.11 Emoji

Code points: U+0048 U+0069 U+0020 U+1F600 U+1F44D

Bytes: `2F 50 07 F9 F4 58 78 F2 95`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+0048 | `2F` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 2 | U+0069 | `50` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 3 | U+0020 | `07` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 4 | U+1F600 | `F9 F4 58` | 8b | general | U+1F600..U+1F67F | U+00C0..U+013F | not used | 0 | not used |
| 5 | U+1F44D | `78 F2 95` | 8b | general | U+1F400..U+1F47F | U+1F600..U+1F67F | not used | 0 | not used |

U+1F600 switches the main window to a supplementary window. U+1F44D is in another window that is not a neighbour: it takes 3 bytes, and it switches
again, because the confidence is 0.

### 11.12 Lone surrogates

Code points: U+0061 U+D800 U+0062 U+DC00

Bytes: `48 F8 C5 20 49 F8 C9 40`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+0061 | `48` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 2 | U+D800 | `F8 C5 20` | 5 | general | U+00C0..U+013F | none | not used | 0 | not used |
| 3 | U+0062 | `49` | 1b | general | U+00C0..U+013F | none | not used | 0 | not used |
| 4 | U+DC00 | `F8 C9 40` | 5 | general | U+00C0..U+013F | none | not used | 0 | not used |

U+D800 and U+DC00 are surrogate code points of their own (they are not next to each other). They have no window and do not change the state (step 5).

### 11.13 Leaving the CJK context

Code points: U+6F22 U+5B57 U+0141 U+0141 U+0061

Bytes: `F8 58 DA 89 C7 67 08 09 66 CA 48`

| # | Code point | Bytes | Step | Context | Main window | Second window | Kana slot | Confidence | Pending window |
|---|---|---|---|---|---|---|---|---|---|
| 1 | U+6F22 | `F8 58 DA` | 3a | CJK | U+00C0..U+013F | none | letters | not used | none |
| 2 | U+5B57 | `89 C7` | 3b | CJK | U+00C0..U+013F | none | letters | not used | none |
| 3 | U+0141 | `67 08 09` | 8c | CJK | U+00C0..U+013F | U+0140..U+01BF | letters | not used | U+0140..U+01BF |
| 4 | U+0141 | `66 CA` | 8d | general | U+0140..U+01BF | U+00C0..U+013F | not used | 0 | not used |
| 5 | U+0061 | `48` | 1b | general | U+0140..U+01BF | U+00C0..U+013F | not used | 0 | not used |

U+0141 takes 3 bytes in the CJK context, because piece 5 (U+0080..U+0EFF) is absent there. So it becomes the second window (step 7) and the pending
window (step 8c). The second U+0141 is of the same window and ends the CJK context (step 8d).

## 12. Test vectors

The test vectors are in the file [`TestVectors/steno-v1-vectors.txt`](TestVectors/steno-v1-vectors.txt). The reference implementations and the test
vectors are published with this specification.

The file is UTF-8 text with one vector per line. Lines that start with `#` are comments, and empty lines are ignored. The fields of a vector are
separated by one tab character (`09`):

```
valid <tab> name <tab> code points <tab> bytes
invalid <tab> name <tab> bytes <tab> reason
not-a-value <tab> name <tab> code points
```

- **code points:** hexadecimal numbers without a prefix, in upper case, with at least 4 digits, separated by one space (`0048 1F600`).
  `-` is the empty value. (This is the only place where code points are written without `U+`.)
- **bytes:** pairs of hexadecimal digits in upper case, without separators (`2F5008`). `-` is the empty byte string.
- **name:** a label without spaces, for reference only.
- **reason:** the rule of §10.1 that the decoder meets first, reading from the start in the order of §9.2: `zero-byte` (rule 1), `unused-lead`
  (rule 2), `truncated` (rule 3), `past-range` (rule 4), `surrogate-pair` (rule 5) or `invalid-trail` (rule 6).

Each vector is one whole value, encoded or decoded from the initial state.

- **valid:** encoding the code points must give exactly the bytes, and decoding the bytes must give exactly the code points. Code points
  U+D800..U+DFFF are surrogate code points of their own.
- **invalid:** decoding the bytes must fail. A decoder that reports reasons should report the given one.
- **not-a-value:** the code points are not a value. An encoder whose input is code points must reject them. An encoder that reads UTF-16 cannot be
  given them.

## 13. Properties (informative)

This section adds no rules. It states what the rules guarantee and sketches why.

### 13.1 Order

**Property.** For values `x` and `y`, their encodings compare as unsigned byte strings (lexicographically, a proper prefix being smaller) the same
way as `x` and `y` compare as sequences of code points (lexicographically by code point value, a proper prefix being smaller).

**Argument.** In one layout, the encodings of code points are in code point order, and none is a prefix of another:

- Ranges are in code point order and are assigned lead bytes in that order, so a code point of an earlier range has a smaller lead byte than a code
  point of a later range.
- In one range, all code points have the same number of bytes, and the bytes are the digits of the offset with the most significant digit first,
  so a smaller offset gives a smaller byte string of the same length.

Let `i` be the first position where `x` and `y` differ, or where one of them ends. Before position `i`, both encoders have read the same code
points, so they are in the same state and have written the same bytes. If `x` ends at `i`, its encoding is a proper prefix of the encoding of `y`,
just as `x` is a proper prefix of `y` (and the same with `x` and `y` swapped). Otherwise the code points at position `i` differ and are encoded in
the same layout, so their encodings differ at a byte where neither has ended. That byte decides both comparisons the same way.

### 13.2 Prefixes

**Property.** If `x` is a prefix of `y`, the encoding of `x` is a prefix of the encoding of `y`.

**Argument.** The bytes of each code point depend only on the code points before it.

### 13.3 Uniqueness

**Property.** A value has exactly one encoding, and every byte string that a decoder accepts is the encoding of exactly one value.

**Argument.** There is only one way to encode a value: no step offers an option. In each layout the encodings of the code points are distinct and
none is a prefix of another (§13.1), so a decoder can read a byte string in only one way. The code points it reads form a value, because §10.1 rule
5 rejects a high surrogate code point followed by a low one. Encoding that value goes through the same states and writes the same bytes.

### 13.4 No zero byte

**Property.** The byte `00` never occurs in an encoding.

**Argument.** Lead bytes start at `01` (§6.5), and trail bytes are `08`..`FF` (§8.2).

### 13.5 No byte below 08 inside a code point

**Property.** The bytes `01`..`07` occur in an encoding only as the first byte of a code point, and each is the first byte of a C0 control character
or is the space: `01` starts U+0000..U+0008, `02` is U+0009 (tab), `03` is U+000A (line feed), `04` starts U+000B..U+000C, `05` is U+000D (carriage
return), `06` starts U+000E..U+001F, and `07` is U+0020 (space).

**Argument.** Trail bytes are `08`..`FF` (§8.2), so a byte below `08` is a lead byte. Lead bytes are assigned in code point order from `01` (§6.5),
and in every layout the first ranges are the same: piece 2 gives U+0000..U+001F the sizes 2, 1, 2, 1, 2 (U+0000..U+0008, U+0009..U+000A,
U+000B..U+000C, U+000D, U+000E..U+001F), with the lead bytes `01`, `02`..`03`, `04`, `05` and `06`, and no later piece covers these code points.
U+0020 takes 1 byte in every layout (piece 3, both forms) and is the next range, or starts it, so it takes `07`. The ranges of size 2 here need one
lead byte each, because they have fewer than 248 code points.

> **Note** So a byte `03` in an encoding is always a line feed, a byte `02` a tab, `05` a carriage return and `07` a space, wherever they occur.

### 13.6 Every layout fits

**Property.** Every layout needs at most 254 lead bytes. So every lead byte is a byte value `01`..`FE`, and `FF` is never a lead byte.

**What rests on enumeration.** The property itself rests on building layouts. Two facts were checked:

1. Without a second window, every layout needs at most 252 lead bytes (253 byte values counting the unused `00`). Every state without a second
   window was built: each of the 7,356 windows as the main window, in each of the five combinations of context and kana slot (general, Hangul, and
   CJK with each value of the kana slot). The fullest is the general context with the main window U+1000..U+107F.
2. With a second window, every layout needs at most 254 lead bytes. All 270,546,324 combinations of such a state with a window as its second window
   were built (in the general context, every window but the main window; in the wide contexts, every window). The fullest is the general context
   with the main window U+1000..U+107F and the second window U+1200..U+127F. A second window adds at most 3 lead bytes.

No code point below U+20000 takes more than 3 bytes (§6.3); from U+20000 a code point takes at most 4.

The .NET reference implementation, which is published with this specification, includes a program that repeats both checks.

**What an argument gives, and what it does not.**

- Without a second window: every part of a stretch needs exactly one lead byte (§6.3), and joining runs of the same size into one range never
  needs more lead bytes than the runs need apart. That explains why the layouts stay small, but the bound of 252 itself comes from the
  enumeration.
- A second window takes the code points of its window that no other piece covers out of a stretch. It needs one lead byte for its own range, and it
  splits the stretch into up to two parts. These two effects alone would add at most 2 lead bytes, but next to a split at U+10FC0 or U+20000 there
  are two more:
  1. The part between the window and the split can drop to size 2 and join the window's range, which then needs one more lead byte.
  2. The window can split a range of size 3 that was joined across the split.
- No argument for the bound of 3 is given here. It is known only from the enumeration.

> **Note** An example of 3: the general context, with the main window U+11200..U+1127F. Without a second window, the layout has one range of size 3,
> U+20D0..U+11107 (the part U+20D0..U+10FBF joined with the part U+10FC0..U+11107), with one lead byte. With the second window U+10E80..U+10EFF,
> the same code points make three ranges: U+20D0..U+10E7F (size 3, 1 lead byte), U+10E80..U+10FBF (size 2: the window and the 192 code points
> U+10F00..U+10FBF between it and the split, 320 code points, 2 lead bytes) and U+10FC0..U+11107 (size 3, 1 lead byte). The layout goes from 251
> to 254 lead bytes.

## 14. Notes for implementers (informative)

### 14.1 ASCII stays in the initial layout

A code point in U+0000..U+007F changes nothing outside the CJK context (§7 step 1). A value that has only such code points is encoded in the initial
layout throughout:

| Code points | Bytes |
|---|---|
| U+0020..U+007E | one byte, `c - 0x19` (`07`..`65`) |
| U+0009 (tab), U+000A (line feed) | `02`, `03` |
| U+000D (carriage return) | `05` |
| U+0000..U+0008 | `01`, then `c + 8` |
| U+000B, U+000C | `04`, then `08` or `09` |
| U+000E..U+001F | `06`, then `c - 0x06` |
| U+007F | `66 08` |

A value has only code points in U+0020..U+007E, U+0009, U+000A and U+000D if and only if every byte of its encoding is `02`, `03`, `05` or
`07`..`65`. Each such byte decodes to one code point: `b + 0x19` for `07`..`65`, and U+0009, U+000A and U+000D for `02`, `03` and `05`.

### 14.2 Code points that take 1 byte never change the layout

| Code points that take 1 byte | Context | Fields of the state that change |
|---|---|---|
| U+0009, U+000A, U+000D, U+0020..U+007E | any | none |
| the main window | general | confidence |
| U+3000..U+300F, the kana of the kana slot | CJK | pending window (set to none) |
| U+FF00..U+FF1F (when the kana slot is letters) | CJK | none |

In the CJK context, the ASCII letters take 1 byte only when the kana slot is letters; step 1 then sets it to letters again, which changes nothing.
So a run of code points that take 1 byte can be converted without updating the state for each one. Only the confidence (in the general context)
and the pending window (in the wide contexts) must be updated for the run, before the next code point that can change the state.

The main window can be a supplementary window (a window above U+FFFF, for example U+1F600..U+1F67F). Its code points still take 1 byte each, and
each decodes to a surrogate pair in UTF-16.

### 14.3 What can be computed once

- An implementation can cache layouts by context, kana slot (in the CJK context only), main window and second window (§5). A second window does not
  change the layout when the other pieces together cover all of its code points (for example a window inside U+0080..U+0EFF, outside the CJK
  context); a cache can treat such a second window as none.
- In the general context, a code point that has a window other than the main window and takes 2 bytes either lowers the confidence (the layout
  stays) or switches. The layout after a switch depends only on the layout and the window. In the CJK context, a move of the kana slot gives a
  layout that depends only on the layout and the new value of the kana slot. Both can be stored with the layout.
- In the CJK and Hangul layouts, the ideographs U+4E00..U+9FFF and the syllables U+AC00..U+D7A3 each lie in one range, so their encoding is
  `F + (c - R0) / 248`, then `(c - R0) mod 248 + 8`. `R0` is the first code point of the range; it may lie before U+4E00 or U+AC00 when the range
  was joined with a neighbour of the same size.
- For a 2-byte code point read as a 16-bit word `w = lead * 256 + trail`, the code point is `w - 8 * lead + (R0 - 248 * F - 8)`: the word minus 8
  times its high byte, plus a constant of the range. Both this and the division by 248 of the encoder can be done with shifts and additions, which
  suits vector code: `o / 248` is `h + t / 248` with `h = o / 256` and `t = 8 * h + o mod 256`, and for `t` below 2,304 that is
  `h + ((t / 8 + 1) * 33) / 1024`. `t` stays below that bound because the offsets of a range of size 2 are small: such a range has at most
  `254 * 248 = 62,992` code points (one per lead byte and trail byte), so `o` fits in 16 bits, and for any 16-bit `o`,
  `t <= 8 * 255 + 255 = 2,295`.

### 14.4 Rule 5 in a decoder that writes UTF-16

A decoder that writes UTF-16 can apply §10.1 rule 5 by looking at the last code unit it wrote. The output ends in a high surrogate code unit only
when the last code point was a high surrogate code point of its own, because a supplementary code point ends with a low surrogate code unit.

### 14.5 Finding control characters at the byte level

The bytes `01`, `04` and `06` are each followed by exactly one trail byte (`08`..`FF`), because they are the lead bytes of size-2 ranges (§13.5).
So a program that splits encoded text on `02` (tab), `03` (line feed), `05` (carriage return) or `07` (space) needs no lookahead. A program that
wants every C0 control character reads one byte more after `01`, `04` or `06`: the code point is U+0000..U+0008 for `01`, U+000B..U+000C for `04`
and U+000E..U+001F for `06`, given by that trail byte (§14.1).

## 15. Versioning

This specification is frozen. Its name is "Steno Encoding, version 1".

- Bytes written by any conforming version 1 encoder decode to the same value with any conforming version 1 decoder.
- Two conforming encoders give identical bytes for the same value.
- Any change to the encoding of any value is a different format, with a different name or version number. This document is never revised to describe
  a different encoding. A different format is not readable as version 1, and version 1 bytes carry no marker that tells them apart (§1.3).
- If this text and the test vectors disagree, the test vectors give the correct bytes: stored data depends on the bytes that conforming encoders have
  produced. The text is corrected by an erratum (§2.2). Corrections of wording that do not change the encoding of any value are allowed.

## 16. Intellectual property

Anyone may implement the Steno Encoding, in any language and for any purpose, without permission or payment. Singulink holds no patents and has
no patent applications that cover the Steno Encoding, and will not seek any.

This specification, the reference implementations and the test vectors are published under the MIT License.

## Appendix A. Glossary

| Term | Meaning | Defined in |
|---|---|---|
| ASCII letter | A code point in U+0041..U+005A or U+0061..U+007A. | [§7.1](#71-rules) |
| block | The 128 code points from `c - (c mod 0x80)` to `c - (c mod 0x80) + 0x7F`. | [§4](#4-windows) |
| CJK code point | A code point in U+3040..U+30FF, U+3400..U+9FFF or U+20000..U+3FFFF, or, when the context is not Hangul, in U+3000..U+303F. | [§7.1](#71-rules) |
| CJK context | The context for Chinese and Japanese text (ideographs and kana). | [§5](#5-state) |
| code point | An integer from U+0000 to U+10FFFF. | [§3](#3-text-model) |
| confidence | A field of the state, 0, 1 or 2, used in the general context: how many recent code points were in the main window. | [§5](#5-state) |
| context | A field of the state: general, CJK or Hangul. | [§5](#5-state) |
| first lead byte (`F`) | The first lead byte assigned to a range. | [§6.5](#65-lead-bytes) |
| general context | The context for all text but Chinese, Japanese and Korean. | [§5](#5-state) |
| Hangul context | The context for Korean text. | [§5](#5-state) |
| kana slot | A field of the state, used in the CJK context: letters, hiragana or katakana. Its value decides which group of code points takes 1 byte. | [§5](#5-state) |
| layout | The ranges of a state, with their sizes and lead bytes. | [§6.1](#61-overview) |
| lead byte | The first byte of the encoding of a code point. For a range of size 1 it is the only byte. | [§6.1](#61-overview) |
| main window | A field of the state: the window whose code points take 1 byte in the general context. | [§5](#5-state) |
| neighbours | In the general context, the up to 248 code points before (lower neighbours) and after (upper neighbours) the main window, which take 2 bytes. | [§6.2](#62-pieces) |
| not a value | A sequence of code points with a high surrogate code point directly followed by a low one. | [§3](#3-text-model) |
| offset (`o`) | `c - R0`: the position of a code point in its range. | [§8.2](#82-one-code-point) |
| part | A piece of a stretch that lies wholly inside U+0000..U+10FBF, U+10FC0..U+1FFFF or U+20000..U+10FFFF. | [§6.3](#63-stretches-and-parts) |
| pending window | A field of the state, used in the wide contexts: the window of the last code point that may end the wide context. | [§5](#5-state) |
| piece | A run of consecutive code points with a size, used to build a layout. | [§6.2](#62-pieces) |
| range | A maximal run of consecutive code points of the same size in a layout. | [§6.4](#64-ranges) |
| second window | A field of the state, a window or none, whose code points take 2 bytes. | [§5](#5-state) |
| size | The number of bytes of the encoding of each code point of a range: 1, 2, 3 or 4. | [§6.1](#61-overview) |
| state | The fields that the encoder and the decoder keep while they read a value. | [§5](#5-state) |
| stretch | A maximal run of consecutive code points that no piece covers. | [§6.3](#63-stretches-and-parts) |
| switch | Step 9 of the state update: a window becomes the main window. | [§7.1](#71-rules) |
| trail byte | Each byte of the encoding of a code point after the lead byte, `08`..`FF`. | [§8.2](#82-one-code-point) |
| unused (lead byte) | A byte value that the layout does not assign to any range. | [§6.5](#65-lead-bytes) |
| value | A sequence of zero or more code points, encoded as a whole from the initial state. | [§3](#3-text-model) |
| wide context | The CJK context or the Hangul context. | [§5](#5-state) |
| window | A run of consecutive code points that can be the main window or the second window. | [§4](#4-windows) |
