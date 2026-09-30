/** Cross-platform runner for the standalone atomic-file Rust tests. */
import { spawnSync } from 'node:child_process';
import { rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';

const suffix = process.platform === 'win32' ? '.exe' : '';
const output = join(tmpdir(), `pm-atomic-test-${process.pid}${suffix}`);

try {
  const compile = spawnSync('rustc', ['--test', 'src-tauri/src/atomic_file.rs', '-o', output], {
    cwd: process.cwd(),
    encoding: 'utf8',
    stdio: 'inherit',
  });
  if (compile.error) throw compile.error;
  if (compile.status !== 0) process.exit(compile.status ?? 1);

  const test = spawnSync(output, [], { cwd: process.cwd(), encoding: 'utf8', stdio: 'inherit' });
  if (test.error) throw test.error;
  process.exitCode = test.status ?? 1;
} finally {
  try { rmSync(output, { force: true }); } catch {}
}
