// Runs the compiled tests with Node's test runner. The files are listed here rather than with a glob, because neither Node 20 nor the
// Windows shell expands one.

import { spawnSync } from 'node:child_process';
import { readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const dir = fileURLToPath(new URL('../build-test/', import.meta.url));
const files = readdirSync(dir)
  .filter((f) => f.endsWith('.test.js'))
  .sort()
  .map((f) => dir + f);

if (files.length === 0) throw new Error('no compiled tests in build-test');

const result = spawnSync(process.execPath, ['--test', '--test-concurrency=2', ...files], { stdio: 'inherit' });
process.exit(result.status ?? 1);
