// The layouts of FORMAT.md sections 6.6 and 11.1, the bounded layout cache, the helpers on bytes and the errors.

import assert from 'node:assert/strict';
import { test } from 'node:test';

import {
  STENO_ENCODING_NAME,
  StenoDecodeError,
  compareBytes,
  decode,
  decodeToCodePoints,
  encode,
  encodeCodePoints,
  isAsciiText,
  startsWith,
} from '../dist/index.js';
import { CJK, GENERAL, HANGUL, HIRAGANA, LAYOUT_CACHE_LIMIT, LETTERS, Layout, clearLayoutCache, layoutCacheSize } from '../dist/layout.js';
import { Random, hex, toStringFromCodePoints } from './helpers.js';
import { refEncode } from './reference.js';

/** Writes a layout as lines "first..last size firstLead..lastLead", in the hexadecimal of the tables of FORMAT.md. */
function describe(layout: Layout): string {
  const lines: string[] = [];
  const h = (n: number, d: number): string => n.toString(16).toUpperCase().padStart(d, '0');

  for (let r = 0; r < layout.count; r++) {
    const lo = layout.lo[r]!;
    const hi = layout.hi[r]!;
    const first = layout.first[r]!;
    const last = first + layout.leads[r]! - 1;
    const cps = lo === hi ? h(lo, 4) : `${h(lo, 4)}..${h(hi, 4)}`;
    const leads = first === last ? h(first, 2) : `${h(first, 2)}..${h(last, 2)}`;
    lines.push(`${cps} ${layout.size[r]} ${leads}`);
  }

  return lines.join('\n');
}

const COMMON = ['0000..0008 2 01', '0009..000A 1 02..03', '000B..000C 2 04', '000D 1 05', '000E..001F 2 06'];

test('the initial layout of FORMAT.md section 6.6', () => {
  const expected = [
    ...COMMON,
    '0020..007E 1 07..65',
    '007F..00BF 2 66',
    '00C0..013F 1 67..E6',
    '0140..0EFF 2 E7..F5',
    '0F00..1FFF 3 F6',
    '2000..20CF 2 F7',
    '20D0..1FFFF 3 F8..F9',
    '20000..10FFFF 4 FA',
  ];

  const layout = new Layout(GENERAL, LETTERS, 0xc0, -1);
  assert.equal(describe(layout), expected.join('\n'));
  assert.equal(layout.leadCount, 250);
});

test('the layouts of FORMAT.md section 11.1', () => {
  const cases: [Layout, string[]][] = [
    [
      new Layout(GENERAL, LETTERS, 0x400, 0x1200),
      [
        ...COMMON,
        '0020..007E 1 07..65',
        '007F..03FF 2 66..69',
        '0400..047F 1 6A..E9',
        '0480..0EFF 2 EA..F4',
        '0F00..11FF 3 F5',
        '1200..127F 2 F6',
        '1280..1FFF 3 F7',
        '2000..20CF 2 F8',
        '20D0..1FFFF 3 F9..FA',
        '20000..10FFFF 4 FB',
      ],
    ],
    [
      new Layout(CJK, HIRAGANA, 0xc0, -1),
      [
        ...COMMON,
        '0020..0040 1 07..27',
        '0041..013F 2 28..29',
        '0140..1FFF 3 2A',
        '2000..20CF 2 2B',
        '20D0..2FFF 3 2C',
        '3000..300F 1 2D..3C',
        '3010..3040 2 3D',
        '3041..3096 1 3E..93',
        '3097..30FF 2 94',
        '3100..4DFF 3 95',
        '4E00..9FFF 2 96..EA',
        'A000..FEFF 3 EB',
        'FF00..FFFF 2 EC..ED',
        '10000..1FFFF 3 EE..EF',
        '20000..10FFFF 4 F0',
      ],
    ],
    [
      new Layout(HANGUL, LETTERS, 0xc0, -1),
      [
        ...COMMON,
        '0020..007E 1 07..65',
        '007F..0EFF 2 66..74',
        '0F00..1FFF 3 75',
        '2000..20CF 2 76',
        '20D0..2FFF 3 77',
        '3000..303F 2 78',
        '3040..ABFF 3 79',
        'AC00..D7A3 2 7A..A7',
        'D7A4..FEFF 3 A8',
        'FF00..FFFF 2 A9..AA',
        '10000..1FFFF 3 AB..AC',
        '20000..10FFFF 4 AD',
      ],
    ],
    [
      new Layout(GENERAL, LETTERS, 0x1000, 0x1200),
      [
        ...COMMON,
        '0020..007E 1 07..65',
        '007F..0FFF 2 66..76',
        '1000..107F 1 77..F6',
        '1080..127F 2 F7..F9',
        '1280..1FFF 3 FA',
        '2000..20CF 2 FB',
        '20D0..1FFFF 3 FC..FD',
        '20000..10FFFF 4 FE',
      ],
    ],
  ];

  for (const [layout, expected] of cases) assert.equal(describe(layout), expected.join('\n'));

  // The fullest layout of section 13.6 needs 254 lead bytes.
  assert.equal(cases[3]![0].leadCount, 254);
});

test('the division by 248 of the fast path is exact for every offset below 65,536', () => {
  for (let o = 0; o < 65536; o++) {
    const q = ((o + 1) * 33825) >>> 23;
    if (q !== Math.floor(o / 248)) assert.fail(`wrong for ${o}`);
    assert.ok((o + 1) * 33825 < 2 ** 32);
  }
});

test('the layout cache stays bounded and the bytes stay right', () => {
  clearLayoutCache();

  // Switch the main window through many windows, with a second window that changes too: every pair is another layout.
  const rng = new Random(40);
  const cps: number[] = [];

  for (let i = 0; i < 3 * LAYOUT_CACHE_LIMIT; i++) {
    const w = rng.next() < 0.5 ? 0xe000 + rng.int(0, 0x23f) * 0x80 : 0x40000 + rng.int(0, 0x1000) * 0x80;
    // A run of 20 code points in each window, so that each state builds its layout (a state builds one after a run of 16).
    for (let k = 0; k < 20; k++) cps.push(w + 5 + k);
  }

  const text = toStringFromCodePoints(cps);
  const bytes = encode(text);

  assert.ok(layoutCacheSize() <= LAYOUT_CACHE_LIMIT, `cache size ${layoutCacheSize()}`);
  assert.equal(layoutCacheSize(), LAYOUT_CACHE_LIMIT);
  assert.equal(hex(bytes), hex(refEncode(cps)));
  assert.equal(decode(bytes), text);
  assert.ok(layoutCacheSize() <= LAYOUT_CACHE_LIMIT);
});

test('isAsciiText decides from the bytes alone', () => {
  assert.equal(isAsciiText(encode('')), true);
  assert.equal(isAsciiText(encode('Hello, world!\t\r\n~ ')), true);
  assert.equal(isAsciiText(encode('Zürich')), false);
  assert.equal(isAsciiText(encode('\u0000')), false);
  assert.equal(isAsciiText(encode('\u007f')), false);
  assert.equal(isAsciiText(encode('\u000b')), false);

  // Exactly the bytes 02, 03, 05 and 07..65 qualify, and each decodes to ASCII text.
  for (let b = 0; b < 256; b++) {
    const ascii = b === 2 || b === 3 || b === 5 || (b >= 7 && b <= 0x65);
    assert.equal(isAsciiText(new Uint8Array([0x48, b])), ascii, `byte ${b}`);
    if (ascii) assert.match(decode(new Uint8Array([b])), /^[\t\n\r\x20-\x7e]$/);
  }
});

test('compareBytes and startsWith', () => {
  assert.equal(compareBytes(new Uint8Array([1, 2]), new Uint8Array([1, 2])), 0);
  assert.equal(compareBytes(new Uint8Array([1]), new Uint8Array([1, 2])), -1);
  assert.equal(compareBytes(new Uint8Array([0xff]), new Uint8Array([1, 2])), 1);
  assert.equal(compareBytes(new Uint8Array([]), new Uint8Array([])), 0);
  assert.equal(compareBytes(encode('Zug'), encode('Zürich')), -1);

  assert.equal(startsWith(encode('Zürich'), encode('Zür')), true);
  assert.equal(startsWith(encode('Zürich'), encode('Zur')), false);
  assert.equal(startsWith(encode('Zü'), encode('Zürich')), false);
  assert.equal(startsWith(encode('x'), new Uint8Array(0)), true);
});

test('errors carry the reason and the offset of the code point', () => {
  const cases: [number[], string, number][] = [
    [[0x00], 'zero-byte', 0],
    [[0x48, 0xe7, 0x00], 'zero-byte', 1],
    [[0x48, 0xff], 'unused-lead', 1],
    [[0x48, 0xfb], 'unused-lead', 1],
    [[0x48, 0xfa, 0x08, 0x08], 'truncated', 1],
    [[0x48, 0xe7, 0x07], 'invalid-trail', 1],
    [[0x48, 0xe7, 0x01], 'invalid-trail', 1],
    [[0x48, 0xfa, 0x05], 'invalid-trail', 1],
    [[0x48, 0xfa, 0x08, 0x00], 'zero-byte', 1],
    [[0xfa, 0xff, 0xff, 0xff], 'past-range', 0],
  ];

  for (const [bytes, reason, offset] of cases) {
    assert.throws(
      () => decode(new Uint8Array(bytes)),
      (e: unknown) =>
        e instanceof StenoDecodeError && e instanceof Error && e.name === 'StenoDecodeError' && e.reason === reason && e.offset === offset,
    );
  }
});

test('encodeCodePoints rejects what is not a value', () => {
  assert.throws(() => encodeCodePoints([0xd800, 0xdc00]), RangeError);
  assert.throws(() => encodeCodePoints([0x110000]), RangeError);
  assert.throws(() => encodeCodePoints([-1]), RangeError);
  assert.throws(() => encodeCodePoints([1.5]), RangeError);
  assert.throws(() => encodeCodePoints([Number.NaN]), RangeError);

  // A low surrogate then a high one is a value, as are surrogates apart.
  assert.deepEqual(decodeToCodePoints(encodeCodePoints([0xdc00, 0xd800])), [0xdc00, 0xd800]);
  assert.deepEqual(decodeToCodePoints(encodeCodePoints(new Set([0xd800, 0x61, 0xdc00]))), [0xd800, 0x61, 0xdc00]);
});

test('the encoding name', () => {
  assert.equal(STENO_ENCODING_NAME, 'x-steno');
});
