// Writes the JavaScript examples of README.md as TypeScript modules into test-types/generated, for `npm run test:types`, which compiles them
// against the package's declarations under strict settings.

import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';

const readme = readFileSync(new URL('../README.md', import.meta.url), 'utf8').replace(/\r\n/g, '\n');
const blocks = [...readme.matchAll(/```js\n([\s\S]*?)```/g)].map((m) => m[1]);
const dir = new URL('../test-types/generated/', import.meta.url);

rmSync(dir, { recursive: true, force: true });
mkdirSync(dir, { recursive: true });
blocks.forEach((code, i) => writeFileSync(new URL(`example-${i + 1}.ts`, dir), `// Generated from README.md by scripts/type-examples.mjs.\n\n${code}`));
console.log(`Wrote ${blocks.length} README examples to test-types/generated.`);
