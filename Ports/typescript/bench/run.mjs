// Runs the speed benchmarks of the TypeScript reference implementation with mitata, on the shared workloads in Benchmarks/Workloads, and writes
// the measurements to bench/raw.json (or bench/smoke.json with --smoke). bench/report.mjs turns them into bench/results.csv and BENCHMARKS.md.
//
// Usage (after `npm run build`; run Node with --expose-gc so that mitata can collect garbage before each benchmark):
//
//   node --expose-gc bench/run.mjs                    the full run: all 14 texts, documents and 16-character values
//   node --expose-gc bench/run.mjs --smoke            a smoke run of a few seconds: english and chinese, short measurements
//   options: --texts a,b  --time <ms per benchmark>  --fresh (do not resume an unfinished run)  --no-alternatives (Steno and UTF-8 only)
//
// The alternatives (compressors from node:zlib and lz4-napi; see bench/alternatives.mjs) are measured on 10 of the 14 texts, and their sizes on
// all 14.
//
// Each text runs in a Node process of its own. The full run saves after each benchmark. If it is stopped, the next run resumes where it
// stopped; a finished run is started afresh.

import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import os from 'node:os';
import zlib from 'node:zlib';

import { do_not_optimize, measure } from 'mitata';

import { decode, encode, encodeInto, maxEncodedLength } from '../dist/index.js';
import { alternatives, lz4Module, notMeasured } from './alternatives.mjs';

const TEXTS = [
  'english',
  'french',
  'polish',
  'vietnamese',
  'russian',
  'greek',
  'arabic',
  'urdu',
  'hindi',
  'thai',
  'amharic',
  'chinese',
  'japanese',
  'korean',
];

const args = process.argv.slice(2);
const option = (name) => {
  const i = args.indexOf(name);
  return i < 0 ? undefined : args[i + 1];
};

const smoke = args.includes('--smoke');
const child = args.includes('--child');
const texts = (option('--texts') ?? (smoke ? 'english,chinese' : TEXTS.join(','))).split(',');
const minTimeMs = Number(option('--time') ?? (smoke ? 100 : 2000));
const minSamples = smoke ? 5 : 30;
const out = new URL(smoke ? './smoke.json' : './raw.json', import.meta.url);
const workloads = new URL('../../../Benchmarks/Workloads/', import.meta.url);

if (typeof globalThis.gc !== 'function') {
  // Without it mitata falls back to allocating a 1 GB array to provoke a collection.
  console.error('Run Node with --expose-gc (npm run bench does).');
  process.exit(1);
}

for (const t of texts) if (!TEXTS.includes(t)) throw new Error(`unknown text: ${t}`);

// The alternatives are measured on a representative subset of the texts, so that the full run stays within about 25 minutes; their sizes are
// recorded for all texts.
const ALTERNATIVE_TEXTS = ['english', 'french', 'russian', 'arabic', 'hindi', 'thai', 'amharic', 'chinese', 'japanese', 'korean'];
const withAlternatives = !args.includes('--no-alternatives');
const ALTS = withAlternatives ? alternatives() : [];
const benchmarksOf = (text) => 6 + (withAlternatives && ALTERNATIVE_TEXTS.includes(text) ? 2 * ALTS.length : 0);

// --- The workloads, prepared before any measurement. ---

function load(name, kind) {
  const content = readFileSync(new URL(`${name}.${kind}.txt`, workloads), 'utf8');
  if (content.includes('\r')) throw new Error(`${name}.${kind}.txt has CR characters; check out the repository with LF line endings`);
  if (!content.endsWith('\n')) throw new Error(`${name}.${kind}.txt does not end with LF`);

  // The document is the content without its final LF; each line of the values file without its LF is one value.
  const body = content.slice(0, -1);
  return kind === 'document' ? [body] : body.split('\n');
}

const utf8Encoder = new TextEncoder();
const utf8Decoder = new TextDecoder('utf-8');

/**
 * The benchmarks of one text and kind. Each operation processes every value once in a plain loop and consumes the result through
 * do_not_optimize, the sink that mitata provides.
 */
function benchmarks(values) {
  const n = values.length;
  const stenoBytes = values.map((v) => encode(v));
  const utf8Bytes = values.map((v) => utf8Encoder.encode(v));
  const longest = values.reduce((m, v) => Math.max(m, v.length), 0);
  const stenoBuffer = new Uint8Array(maxEncodedLength(longest));
  const utf8Buffer = new Uint8Array(longest * 3);

  for (let i = 0; i < n; i++) if (decode(stenoBytes[i]) !== values[i]) throw new Error('round trip failed');

  return [
    {
      operation: 'encode',
      codec: 'steno',
      api: 'encodeInto',
      fn() {
        let s = 0;
        for (let i = 0; i < n; i++) s += encodeInto(values[i], stenoBuffer).written;
        do_not_optimize(s);
      },
    },
    {
      operation: 'encode',
      codec: 'steno',
      api: 'encode',
      fn() {
        let s = 0;
        for (let i = 0; i < n; i++) s += encode(values[i]).length;
        do_not_optimize(s);
      },
    },
    {
      operation: 'decode',
      codec: 'steno',
      api: 'decode',
      fn() {
        let s = 0;
        for (let i = 0; i < n; i++) s += decode(stenoBytes[i]).length;
        do_not_optimize(s);
      },
    },
    {
      operation: 'encode',
      codec: 'utf8',
      api: 'encodeInto',
      fn() {
        let s = 0;
        for (let i = 0; i < n; i++) s += utf8Encoder.encodeInto(values[i], utf8Buffer).written;
        do_not_optimize(s);
      },
    },
    {
      operation: 'encode',
      codec: 'utf8',
      api: 'encode',
      fn() {
        let s = 0;
        for (let i = 0; i < n; i++) s += utf8Encoder.encode(values[i]).length;
        do_not_optimize(s);
      },
    },
    {
      operation: 'decode',
      codec: 'utf8',
      api: 'decode',
      fn() {
        let s = 0;
        for (let i = 0; i < n; i++) s += utf8Decoder.decode(utf8Bytes[i]).length;
        do_not_optimize(s);
      },
    },
  ];
}

/**
 * The benchmarks of one alternative on one text and kind: compress every value (a string, which the library converts to UTF-8), and decompress
 * every value to a string. The loops call the library functions directly, with their options prepared before.
 */
function alternativeBenchmarks(alt, values) {
  const n = values.length;
  const packed = values.map((v) => alt.compress(v));

  for (let i = 0; i < n; i++) if (alt.decompress(packed[i]) !== values[i]) throw new Error(`${alt.codec}: round trip failed`);

  const options = alt.options;
  let enc;
  let dec;

  switch (alt.family) {
    case 'deflate':
      enc = () => {
        let s = 0;
        for (let i = 0; i < n; i++) s += zlib.deflateRawSync(values[i], options).length;
        do_not_optimize(s);
      };
      dec = () => {
        let s = 0;
        for (let i = 0; i < n; i++) s += zlib.inflateRawSync(packed[i]).toString('utf8').length;
        do_not_optimize(s);
      };
      break;
    case 'brotli':
      enc = () => {
        let s = 0;
        for (let i = 0; i < n; i++) s += zlib.brotliCompressSync(values[i], options).length;
        do_not_optimize(s);
      };
      dec = () => {
        let s = 0;
        for (let i = 0; i < n; i++) s += zlib.brotliDecompressSync(packed[i]).toString('utf8').length;
        do_not_optimize(s);
      };
      break;
    case 'zstd':
      enc = () => {
        let s = 0;
        for (let i = 0; i < n; i++) s += zlib.zstdCompressSync(values[i], options).length;
        do_not_optimize(s);
      };
      dec = () => {
        let s = 0;
        for (let i = 0; i < n; i++) s += zlib.zstdDecompressSync(packed[i]).toString('utf8').length;
        do_not_optimize(s);
      };
      break;
    case 'lz4': {
      const lz4 = lz4Module();
      const compressSync = lz4.compressSync;
      const uncompressSync = lz4.uncompressSync;
      enc = () => {
        let s = 0;
        for (let i = 0; i < n; i++) s += compressSync(values[i]).length;
        do_not_optimize(s);
      };
      dec = () => {
        let s = 0;
        for (let i = 0; i < n; i++) s += uncompressSync(packed[i]).toString('utf8').length;
        do_not_optimize(s);
      };
      break;
    }
    default:
      throw new Error(`unknown family ${alt.family}`);
  }

  return [
    { operation: 'encode', codec: alt.codec, api: 'compress', fn: enc },
    { operation: 'decode', codec: alt.codec, api: 'decompress', fn: dec },
  ];
}

/** The total encoded size of the values of a text and kind, for every codec. */
function sizes(values) {
  const total = (f) => values.reduce((a, v) => a + f(v).length, 0);
  const result = { steno: total(encode), utf8: total((v) => utf8Encoder.encode(v)) };
  for (const alt of ALTS) result[alt.codec] = total(alt.compress);
  return result;
}

// --- Statistics. ---

/** The two-sided 99.9% quantile of Student's t distribution (Cornish-Fisher expansion around the normal quantile; exact enough from 4 df). */
function t999(df) {
  const z = 3.2905267314919;
  const z3 = z ** 3;
  const z5 = z ** 5;
  const z7 = z ** 7;
  return z + (z3 + z) / (4 * df) + (5 * z5 + 16 * z3 + 3 * z) / (96 * df ** 2) + (3 * z7 + 19 * z5 + 17 * z3 - 15 * z) / (384 * df ** 3);
}

function summarise(stats) {
  const s = stats.samples;
  const n = s.length;
  const mean = s.reduce((a, v) => a + v, 0) / n;
  const sd = n > 1 ? Math.sqrt(s.reduce((a, v) => a + (v - mean) ** 2, 0) / (n - 1)) : 0;

  // The error is the half-width of the 99.9% confidence interval of the mean, as BenchmarkDotNet reports it.
  return {
    ns: mean,
    error: n > 1 ? (t999(n - 1) * sd) / Math.sqrt(n) : 0,
    sd,
    samples: n,
    min: stats.min,
    median: stats.p50,
    max: stats.max,
    calls: stats.ticks,
  };
}

// --- The run. ---

const settings = { minTimeMs, minSamples, withAlternatives };

function environment() {
  const cpus = os.cpus();
  const mitata = JSON.parse(readFileSync(new URL('../node_modules/mitata/package.json', import.meta.url), 'utf8'));
  return {
    tool: `mitata ${mitata.version}`,
    runtime: `Node.js ${process.version} (V8 ${process.versions.v8})`,
    machine: `${cpus[0]?.model.trim() ?? 'unknown CPU'}, ${cpus.length} logical cores, ${Math.round(os.totalmem() / 2 ** 30)} GB, ${os.type()} ${os.release()} ${os.arch()}`,
    libraries: [
      { codec: 'steno', library: '@singulink/steno (this package) encodeInto / decode', version: '0.1.0', setting: '' },
      { codec: 'utf8', library: 'TextEncoder encodeInto / TextDecoder decode', version: `Node.js ${process.version}`, setting: '' },
      ...ALTS.map((a) => ({ codec: a.codec, library: a.library, version: a.version, setting: a.setting })),
    ],
    notMeasured: notMeasured(),
    alternativeTexts: ALTERNATIVE_TEXTS,
  };
}

let data;

if (child) {
  // A child measures its text into the file that the parent prepared.
  data = JSON.parse(readFileSync(out, 'utf8'));
} else {
  if (!args.includes('--fresh') && existsSync(out)) {
    const old = JSON.parse(readFileSync(out, 'utf8'));
    const same = old.complete === false && old.runtime === environment().runtime && JSON.stringify(old.settings) === JSON.stringify(settings);
    if (same) data = old;
  }

  if (data === undefined) {
    data = { ...environment(), settings, started: new Date().toISOString(), finished: null, complete: false, results: [], sizes: [] };
  } else {
    console.log(`Resuming the unfinished run of ${data.started} (${data.results.length} benchmarks done).`);
  }

  writeFileSync(out, JSON.stringify(data, null, 1) + '\n');
}

const keyOf = (r) => `${r.text}/${r.kind}/${r.codec}/${r.api}`;
const done = new Set(data.results.map(keyOf));
const total = texts.reduce((a, t) => a + 2 * benchmarksOf(t), 0);
const started = performance.now();

if (!child) {
  // Each text is measured in a process of its own, so that what the engine compiled for one text does not change the figures of the next: in
  // one process the figures of a text depended on the texts measured before it.
  console.log(`${data.tool}, ${data.runtime}. ${total} benchmarks, at least ${minTimeMs} ms and ${minSamples} samples each, one process per text.`);

  for (const text of texts) {
    const missing = data.results.filter((r) => r.text === text).length < 2 * benchmarksOf(text);
    if (!missing) continue;

    const childArgs = ['--expose-gc', '--max-old-space-size=512', '--no-warnings', fileURLToPath(import.meta.url), '--child', '--texts', text, '--time', String(minTimeMs)];
    if (smoke) childArgs.push('--smoke');
    if (!withAlternatives) childArgs.push('--no-alternatives');
    const result = spawnSync(process.execPath, childArgs, { stdio: 'inherit' });
    if (result.status !== 0) throw new Error(`the benchmarks of ${text} failed`);
  }

  data = JSON.parse(readFileSync(out, 'utf8'));
  data.complete = new Set(data.results.map(keyOf)).size >= total;
  data.finished = new Date().toISOString();
  writeFileSync(out, JSON.stringify(data, null, 1) + '\n');
  console.log(`Done in ${((performance.now() - started) / 1000).toFixed(0)} s. Wrote ${out.pathname.split('/').pop()}.`);
} else {
  for (const text of texts) {
    for (const kind of ['values', 'document']) {
      const values = load(text, kind);
      const units = values.reduce((a, v) => a + v.length, 0);

      if (!data.sizes.some((z) => z.text === text && z.kind === kind)) {
        data.sizes.push({ text, kind, utf16_units: units, values: values.length, bytes: sizes(values) });
      }

      const list = benchmarks(values);
      if (withAlternatives && ALTERNATIVE_TEXTS.includes(text)) for (const alt of ALTS) list.push(...alternativeBenchmarks(alt, values));

      for (const b of list) {
        const key = `${text}/${kind}/${b.codec}/${b.api}`;
        if (done.has(key)) continue;

        // Batches of 256 calls for operations under 65 µs keep each sample at 17 ms at most, so a benchmark gets many samples.
        const stats = await measure(b.fn, { min_cpu_time: minTimeMs * 1e6, min_samples: minSamples, batch_samples: 256 });
        const result = { text, kind, operation: b.operation, codec: b.codec, api: b.api, values: values.length, utf16_units: units, ...summarise(stats) };
        data.results.push(result);
        writeFileSync(out, JSON.stringify(data, null, 1) + '\n');

        const perValue = result.ns / values.length;
        const rate = (units / result.ns) * 1000;
        console.log(
          `${key.padEnd(34)} ${result.ns.toFixed(0).padStart(10)} ns ± ${result.error.toFixed(0).padStart(7)} ` +
            `(${perValue.toFixed(1)} ns/value, ${rate.toFixed(0)} M units/s, ${result.samples} samples)`,
        );
      }
    }
  }
}
