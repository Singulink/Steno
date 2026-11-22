// Runs every JavaScript example of README.md as a plain .mjs file that imports the package by its name (through the exports map of
// package.json), and checks what each console.log prints against the comment at the end of its line.

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const readme = readFileSync(new URL('../README.md', import.meta.url), 'utf8').replace(/\r\n/g, '\n');
const blocks = [...readme.matchAll(/```js\n([\s\S]*?)```/g)].map((m) => m[1]!);

test('README has examples', () => {
  assert.ok(blocks.length >= 8, `${blocks.length} examples`);
});

blocks.forEach((code, i) => {
  test(`README example ${i + 1}`, () => {
    const expected = code
      .split('\n')
      .filter((line) => line.includes('console.log(') && line.includes('// '))
      .map((line) => line.slice(line.lastIndexOf('// ') + 3));

    assert.ok(expected.length > 0, 'the example prints something to check');

    const file = new URL(`./readme-example-${i + 1}.mjs`, import.meta.url);
    writeFileSync(file, code);

    try {
      const output = execFileSync(process.execPath, [fileURLToPath(file)], { encoding: 'utf8' });
      assert.deepEqual(output.replace(/\r\n/g, '\n').trimEnd().split('\n'), expected);
    } finally {
      rmSync(file);
    }
  });
});
