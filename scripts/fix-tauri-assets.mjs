import { readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';

const buildDir = path.resolve('build');
const files = await readdir(buildDir);

for (const file of files) {
  if (!file.endsWith('.html')) continue;

  const fullPath = path.join(buildDir, file);
  const original = await readFile(fullPath, 'utf8');
  const updated = original
    .replaceAll('href="/_app/', 'href="./_app/')
    .replaceAll('src="/_app/', 'src="./_app/')
    .replaceAll('import("/_app/', 'import("./_app/')
    .replaceAll('href="/favicon.', 'href="./favicon.');

  if (updated !== original) {
    await writeFile(fullPath, updated);
  }
}
