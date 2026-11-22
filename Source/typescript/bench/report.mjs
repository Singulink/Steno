// Turns the measurements of bench/run.mjs into bench/results.csv (the common format of all reference implementations) and BENCHMARKS.md, so
// that nobody types numbers. With --readme it also replaces the figures between the benchmark markers of README.md.
//
//   node bench/report.mjs [--readme]     from bench/raw.json to bench/results.csv and BENCHMARKS.md
//   node bench/report.mjs --smoke        from bench/smoke.json to bench/smoke.csv and bench/smoke.md (README.md is never touched)

import { readFileSync, writeFileSync } from 'node:fs';

const args = process.argv.slice(2);
const smoke = args.includes('--smoke');
const here = (name) => new URL(name, import.meta.url);

const data = JSON.parse(readFileSync(here(smoke ? './smoke.json' : './raw.json'), 'utf8'));
const csvFile = here(smoke ? './smoke.csv' : './results.csv');
const mdFile = here(smoke ? './smoke.md' : '../BENCHMARKS.md');
const librariesFile = here(smoke ? './smoke.libraries.csv' : './libraries.csv');
const sizesFile = here(smoke ? './smoke.sizes.csv' : './sizes.csv');
const libraries = data.libraries ?? [];
const ALT_CODECS = libraries.map((l) => l.codec).filter((c) => c !== 'steno' && c !== 'utf8');

const TEXTS = [...new Set(data.results.map((r) => r.text))];
const find = (text, kind, codec, api) => data.results.find((r) => r.text === text && r.kind === kind && r.codec === codec && r.api === api);
const title = (t) => t[0].toUpperCase() + t.slice(1);

// --- results.csv: the operations of the common rules. Encoding is encodeInto into a buffer allocated once; decoding returns a new string. ---

const csv = ['implementation,text,kind,operation,form,codec,ns_per_operation,error_ns,values,utf16_units'];

for (const text of TEXTS) {
  for (const kind of ['document', 'values']) {
    for (const codec of ['steno', 'utf8', ...ALT_CODECS]) {
      const alt = codec !== 'steno' && codec !== 'utf8';

      for (const [operation, api] of [
        ['encode', alt ? 'compress' : 'encodeInto'],
        ['decode', alt ? 'decompress' : 'decode'],
      ]) {
        const r = find(text, kind, codec, api);
        if (r === undefined) continue;
        csv.push(['typescript', text, kind, operation, 'native', codec, r.ns.toFixed(1), r.error.toFixed(1), r.values, r.utf16_units].join(','));
      }
    }
  }
}

writeFileSync(csvFile, csv.join('\n') + '\n');

// --- libraries.csv and sizes.csv. ---

const quote = (v) => (/[",]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v);
writeFileSync(librariesFile, ['codec,library,version', ...libraries.map((l) => [l.codec, l.library, l.version].map(quote).join(','))].join('\n') + '\n');

const sizes = data.sizes ?? [];
const sizeRows = ['implementation,text,kind,codec,bytes'];

for (const z of sizes) for (const [codec, bytes] of Object.entries(z.bytes)) sizeRows.push(['typescript', z.text, z.kind, codec, bytes].join(','));

writeFileSync(sizesFile, sizeRows.join('\n') + '\n');

// --- BENCHMARKS.md. ---

const nsPerValue = (r) => (r === undefined ? '' : `${(r.ns / r.values).toFixed(1)} ± ${(r.error / r.values).toFixed(1)}`);
const rate = (r) => (r === undefined ? '' : `${((r.utf16_units / r.ns) * 1000).toFixed(0)} ± ${((r.utf16_units / r.ns) * 1000 * (r.error / r.ns)).toFixed(0)}`);
const ratio = (a, b) => (a === undefined || b === undefined ? '' : (a.ns / b.ns).toFixed(2));

function table(kind, cell) {
  const rows = [
    `| Text | Steno encode | UTF-8 encode | Steno ÷ UTF-8 | Steno decode | UTF-8 decode | Steno ÷ UTF-8 |`,
    '|---|--:|--:|--:|--:|--:|--:|',
  ];

  for (const text of TEXTS) {
    const se = find(text, kind, 'steno', 'encodeInto');
    const ue = find(text, kind, 'utf8', 'encodeInto');
    const sd = find(text, kind, 'steno', 'decode');
    const ud = find(text, kind, 'utf8', 'decode');
    rows.push(`| ${title(text)} | ${cell(se)} | ${cell(ue)} | ${ratio(se, ue)} | ${cell(sd)} | ${cell(ud)} | ${ratio(sd, ud)} |`);
  }

  return rows.join('\n');
}

function allocTable() {
  const rows = [
    '| Text | Steno `encode` (16-character values, ns/value) | Steno `encodeInto` | UTF-8 `encode` | UTF-8 `encodeInto` | Steno `encode` (documents, M units/s) | Steno `encodeInto` |',
    '|---|--:|--:|--:|--:|--:|--:|',
  ];

  for (const text of TEXTS) {
    rows.push(
      `| ${title(text)} | ${nsPerValue(find(text, 'values', 'steno', 'encode'))} | ${nsPerValue(find(text, 'values', 'steno', 'encodeInto'))} | ` +
        `${nsPerValue(find(text, 'values', 'utf8', 'encode'))} | ${nsPerValue(find(text, 'values', 'utf8', 'encodeInto'))} | ` +
        `${rate(find(text, 'document', 'steno', 'encode'))} | ${rate(find(text, 'document', 'steno', 'encodeInto'))} |`,
    );
  }

  return rows.join('\n');
}

// --- The alternatives. ---

const NAMES = {
  steno: 'Steno',
  utf8: 'UTF-8',
  'deflate-fast': 'Deflate 1',
  'deflate-max': 'Deflate 9',
  'brotli-1': 'Brotli 1',
  'brotli-11': 'Brotli 11',
  'zstd-1': 'Zstd 1',
  'zstd-19': 'Zstd 19',
  lz4: 'LZ4',
};
const nameOf = (c) => NAMES[c] ?? c;
const ALT_TEXTS = TEXTS.filter((t) => ALT_CODECS.some((c) => find(t, 'values', c, 'compress') !== undefined));

function speedTable(kind, cell) {
  const codecs = ['steno', ...ALT_CODECS];
  const rows = [`| Text | ${codecs.map(nameOf).join(' | ')} |`, `|---|${codecs.map(() => '--:').join('|')}|`];

  for (const text of ALT_TEXTS) {
    const cells = codecs.map((c) => {
      const steno = c === 'steno';
      const e = find(text, kind, c, steno ? 'encodeInto' : 'compress');
      const d = find(text, kind, c, steno ? 'decode' : 'decompress');
      return e === undefined || d === undefined ? '' : `${cell(e)} / ${cell(d)}`;
    });
    rows.push(`| ${title(text)} | ${cells.join(' | ')} |`);
  }

  return rows.join('\n');
}

function sizeTable(kind) {
  const codecs = ['steno', 'utf8', ...ALT_CODECS];
  const rows = [`| Text | ${codecs.map(nameOf).join(' | ')} |`, `|---|${codecs.map(() => '--:').join('|')}|`];
  const totals = Object.fromEntries(codecs.map((c) => [c, 0]));
  let units = 0;

  for (const z of sizes.filter((x) => x.kind === kind)) {
    rows.push(`| ${title(z.text)} | ${codecs.map((c) => (z.bytes[c] / z.utf16_units).toFixed(2)).join(' | ')} |`);
    for (const c of codecs) totals[c] += z.bytes[c];
    units += z.utf16_units;
  }

  if (units > 0) rows.push(`| **All** | ${codecs.map((c) => `**${(totals[c] / units).toFixed(2)}**`).join(' | ')} |`);
  return rows.join('\n');
}

const nsOnly = (r) => (r.ns / r.values).toFixed(0);
const rateOnly = (r) => {
  const m = (r.utf16_units / r.ns) * 1000;
  return m >= 10 ? m.toFixed(0) : m >= 1 ? m.toFixed(1) : m.toFixed(2);
};

const libraryList = libraries.map((l) => `- **${nameOf(l.codec)}** (\`${l.codec}\`): ${l.library}, ${l.version}${l.setting ? `, ${l.setting}` : ''}.`).join('\n');

const alternativesSection =
  ALT_CODECS.length === 0
    ? ''
    : `## What a JavaScript developer would use instead

The codecs measured, and the API through which a developer on Node.js would call each of them; the compressors work on UTF-8 and compress each
value on its own:

${libraryList}

How they are driven: each compressor gets the value as a string (the library converts it to UTF-8) and returns a new Buffer; decoding
decompresses and turns the Buffer into a string with \`toString('utf8')\`. The synchronous functions of \`node:zlib\` have no reusable
compressor object, so each call sets up and frees its own compression state: that set-up cost is part of every figure, as it is for every user of
these functions. lz4-napi has no levels and keeps no state between calls. Steno is \`encodeInto\` into a reused buffer and \`decode\`.

${(data.notMeasured ?? []).map((n) => `- ${n}`).join('\n')}

The compressors are measured on ${ALT_TEXTS.length} of the texts (${ALT_TEXTS.join(', ')}), to keep the full run within about 25 minutes; their sizes
are given for all texts.

### 16-character values: nanoseconds per value, encode / decode

Lower is better.

${speedTable('values', nsOnly)}

### Documents: millions of UTF-16 code units per second, encode / decode

Higher is better.

${speedTable('document', rateOnly)}

### Size: bytes per UTF-16 code unit

Smaller is better. Every value compressed on its own, as above.

16-character values:

${sizeTable('values')}

Documents:

${sizeTable('document')}

`;

const date = (data.finished ?? data.started).slice(0, 10);
const { minTimeMs, minSamples } = data.settings;

const md = `# Benchmarks of the TypeScript reference implementation

Generated by \`bench/report.mjs\` from the measurements of \`bench/run.mjs\`${smoke ? ' (a SMOKE RUN: short measurements, not for publication)' : ''}. Do not edit by hand.

- **Tool:** ${data.tool}, at least ${minTimeMs} ms and ${minSamples} samples per benchmark, after mitata's own warm-up, with a garbage collection
  before each benchmark. Each text is measured in a Node process of its own.
- **Runtime:** ${data.runtime}.
- **Machine:** ${data.machine}.
- **Date:** ${date}.${data.complete ? '' : ' The run did not finish: some figures are missing.'}
- **Workloads:** the shared files in \`Benchmarks/Workloads\`: for each text the whole document as one value, and the document cut into values of
  at most 16 UTF-16 code units. One operation processes every value of the text once, in a plain loop.
- **Operations:** encoding is \`encodeInto\` into a buffer allocated once (for UTF-8, \`TextEncoder.encodeInto\`); decoding returns a new string,
  which is the only form a JavaScript string has (for UTF-8, \`TextDecoder.decode\`). The baseline is the platform's UTF-8, in the same harness.
- **Spread:** every figure is the mean ± the half-width of its 99.9% confidence interval, from mitata's samples.

## 16-character values: nanoseconds per value

Lower is better. "Steno ÷ UTF-8" is the time of Steno divided by the time of UTF-8: above 1, Steno is slower.

${table('values', nsPerValue)}

## Documents: millions of UTF-16 code units per second

Higher is better. "Steno ÷ UTF-8" is still the ratio of the times: above 1, Steno is slower.

${table('document', rate)}

## Into a new array

\`encode\` returns a new \`Uint8Array\` for each value; \`encodeInto\` writes into the caller's buffer. The difference is the cost of the
allocation.

${allocTable()}

${alternativesSection}## Caveats

- The machine was in use while the benchmarks ran, so the spread is part of the result; compare figures only within one run.
- A JavaScript engine compiles code while it runs. mitata warms each benchmark up, but a figure is that of one engine version on one machine.
- Each value goes through a function call and the creation of a small result object (\`{ read, written }\`) or a new string, as a caller's code
  would. Decoding always creates a string; no buffer form of the decoded text exists in JavaScript.
- UTF-8 here is the engine's native code (\`TextEncoder\` and \`TextDecoder\`); Steno is JavaScript.
`;

writeFileSync(mdFile, md);
console.log(`Wrote ${csvFile.pathname.split('/').pop()} (${csv.length - 1} rows), ${librariesFile.pathname.split('/').pop()}, ${sizesFile.pathname.split('/').pop()} and ${mdFile.pathname.split('/').pop()}.`);

// --- README.md: the figures between the markers. ---

if (args.includes('--readme') && !smoke) {
  const readmeFile = here('../README.md');
  const readme = readFileSync(readmeFile, 'utf8');
  const start = '<!-- benchmarks:start -->';
  const end = '<!-- benchmarks:end -->';
  const a = readme.indexOf(start);
  const b = readme.indexOf(end);
  if (a < 0 || b < a) throw new Error('README.md has no benchmark markers');

  const rows = ['| Text | Steno encode | Steno decode | UTF-8 encode | UTF-8 decode |', '|---|--:|--:|--:|--:|'];

  for (const text of TEXTS) {
    const m = (codec, api) => {
      const r = find(text, 'document', codec, api);
      return r === undefined ? '' : ((r.utf16_units / r.ns) * 1000).toFixed(0);
    };
    rows.push(`| ${title(text)} | ${m('steno', 'encodeInto')} | ${m('steno', 'decode')} | ${m('utf8', 'encodeInto')} | ${m('utf8', 'decode')} |`);
  }

  const values = TEXTS.map((t) => [find(t, 'values', 'steno', 'encodeInto'), find(t, 'values', 'steno', 'decode')]).flat().filter((r) => r);
  const perValue = values.map((r) => r.ns / r.values);
  const block = `${start}
Measured with ${data.tool} on ${data.runtime}, on ${data.machine}, on ${date}, while the machine was in use. Whole documents, in millions of
UTF-16 code units per second (higher is better):

${rows.join('\n')}

On 16-character values, encoding or decoding one value takes ${Math.min(...perValue).toFixed(0)} to ${Math.max(...perValue).toFixed(0)} ns. [BENCHMARKS.md](BENCHMARKS.md)
has every figure with its spread, the 16-character values and the ratios to UTF-8. The figures depend on the machine and the JavaScript engine;
\`npm run bench\` measures them on yours (about 20 minutes).
${end}`;

  writeFileSync(readmeFile, readme.slice(0, a) + block + readme.slice(b + end.length));
  console.log('Updated the figures in README.md.');
}
