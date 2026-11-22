// The alternatives that a JavaScript developer on Node.js would use instead of the Steno Encoding, for bench/run.mjs: general compressors on
// UTF-8, each value compressed on its own, through the APIs their documentation shows.
//
// - Deflate, Brotli and Zstandard: node:zlib, the standard library, with its synchronous functions, which take a string (converted to UTF-8 by
//   Node) and return a Buffer. node:zlib has no reusable synchronous compressor object: each call sets up and frees its own compression state,
//   as it does for every user of these functions. Zstandard is measured only where the running Node has it (zlib.zstdCompressSync).
// - LZ4: Node has none; lz4-napi (the maintained LZ4 package, a binding of the Rust crate lz4_flex with prebuilt binaries) with compressSync and
//   uncompressSync, the block format with the size in front.
// - SCSU and BOCU-1: no implementation in common use for JavaScript, so none is measured.
//
// Decoding is decompression followed by Buffer.toString('utf8'), which is how such a Buffer becomes a string in Node.

import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import zlib from 'node:zlib';

const require = createRequire(import.meta.url);

function packageVersion(name) {
  // Read from node_modules directly: a package's exports map may not expose its package.json.
  return JSON.parse(readFileSync(new URL(`../node_modules/${name}/package.json`, import.meta.url), 'utf8')).version;
}

/**
 * The alternatives available in this Node, each with what it is, its family and options (from which bench/run.mjs writes benchmark loops that
 * call the library directly), and functions to compress one string and to decompress one Buffer to a string, for preparing the inputs, checking
 * the round trip and measuring the sizes.
 */
export function alternatives() {
  const list = [];
  const C = zlib.constants;

  const deflate = (codec, level) => {
    const options = { level };
    list.push({
      codec,
      family: 'deflate',
      options,
      library: 'node:zlib deflateRawSync / inflateRawSync (raw Deflate, no header)',
      version: `zlib ${process.versions.zlib}`,
      setting: `level ${level}`,
      compress: (s) => zlib.deflateRawSync(s, options),
      decompress: (b) => zlib.inflateRawSync(b).toString('utf8'),
    });
  };

  const brotli = (codec, quality) => {
    const options = { params: { [C.BROTLI_PARAM_QUALITY]: quality } };
    list.push({
      codec,
      family: 'brotli',
      options,
      library: 'node:zlib brotliCompressSync / brotliDecompressSync',
      version: `brotli ${process.versions.brotli}`,
      setting: `quality ${quality}`,
      compress: (s) => zlib.brotliCompressSync(s, options),
      decompress: (b) => zlib.brotliDecompressSync(b).toString('utf8'),
    });
  };

  deflate('deflate-fast', 1);
  deflate('deflate-max', 9);
  brotli('brotli-1', 1);
  brotli('brotli-11', 11);

  if (typeof zlib.zstdCompressSync === 'function') {
    const zstd = (codec, level) => {
      const options = { params: { [C.ZSTD_c_compressionLevel]: level } };
      list.push({
        codec,
        family: 'zstd',
        options,
        library: 'node:zlib zstdCompressSync / zstdDecompressSync',
        version: `zstd ${process.versions.zstd ?? 'unknown'}`,
        setting: `level ${level}`,
        compress: (s) => zlib.zstdCompressSync(s, options),
        decompress: (b) => zlib.zstdDecompressSync(b).toString('utf8'),
      });
    };

    zstd('zstd-1', 1);
    zstd('zstd-19', 19);
  }

  const lz4 = lz4Module();
  list.push({
    codec: 'lz4',
    family: 'lz4',
    options: null,
    library: 'lz4-napi compressSync / uncompressSync (lz4_flex, block format with the size in front)',
    version: `lz4-napi ${packageVersion('lz4-napi')}`,
    setting: 'no levels',
    compress: (s) => lz4.compressSync(s),
    decompress: (b) => lz4.uncompressSync(b).toString('utf8'),
  });

  return list;
}

/** What the run records about the alternatives that it could not measure. */
export function notMeasured() {
  const notes = ['SCSU and BOCU-1: JavaScript has no implementation in common use (ICU in Node does not expose them), so they are not measured.'];
  if (typeof zlib.zstdCompressSync !== 'function') notes.push(`Zstandard: Node.js ${process.version} has no zlib.zstdCompressSync, so it is not measured.`);
  return notes;
}

/** The lz4-napi module, loaded through require because it is a CommonJS package with a native binding. */
export function lz4Module() {
  return require('lz4-napi');
}
