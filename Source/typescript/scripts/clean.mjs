// Removes a build output folder, so that files of sources that no longer exist are not left behind (and never shipped).

import { rmSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

for (const name of process.argv.slice(2)) {
  if (!/^[a-z-]+$/.test(name)) throw new Error(`not a build folder name: ${name}`);
  rmSync(fileURLToPath(new URL(`../${name}/`, import.meta.url)), { recursive: true, force: true });
}
