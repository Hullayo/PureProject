/**
 * 同步服务器 schema 防降级集成测试。
 *
 * 真实启动零依赖同步服务，验证 v4 项目不会被旧客户端的 v3 快照覆盖。
 */
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';

const port = 19000 + (process.pid % 1000);
const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pm-sync-schema-'));
const origin = `http://127.0.0.1:${port}`;
const token = 'schema-test-token';
let failures = 0;

function check(condition, message, extra = '') {
  if (condition) console.log('ok  ', message);
  else {
    failures++;
    console.log('FAIL', message, extra);
  }
}

const server = spawn(process.execPath, ['server/pm-sync-server.mjs'], {
  cwd: process.cwd(),
  env: {
    ...process.env,
    PM_SYNC_HOST: '127.0.0.1',
    PM_SYNC_PORT: String(port),
    PM_SYNC_DATA: dataDir,
    PM_SYNC_TOKEN: token,
    PM_REPORTS_DIR: path.join(dataDir, 'reports'),
  },
  stdio: ['ignore', 'pipe', 'pipe'],
});

let serverOutput = '';
server.stdout.on('data', chunk => { serverOutput += chunk.toString(); });
server.stderr.on('data', chunk => { serverOutput += chunk.toString(); });

async function waitForServer() {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${origin}/api/health`, { headers: { Authorization: `Bearer ${token}` } });
      if (response.ok) return;
    } catch {
      // 服务仍在启动。
    }
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error(`同步服务启动超时\n${serverOutput}`);
}

async function putProject(schemaVersion, baseRev) {
  return fetch(`${origin}/api/projects/project-v4`, {
    method: 'PUT',
    headers: {
      Authorization: `Bearer ${token}`,
      'Content-Type': 'application/json',
      'X-Base-Rev': String(baseRev),
    },
    body: JSON.stringify({
      version: '1.0',
      schema_version: schemaVersion,
      project: {
        id: 'project-v4',
        name: `Schema ${schemaVersion}`,
        updated_at: `2026-09-${schemaVersion === 4 ? '30' : '29'}T00:00:00.000Z`,
      },
    }),
  });
}

try {
  await waitForServer();

  const first = await putProject(4, 0);
  const firstBody = await first.json();
  check(first.status === 200 && firstBody.schema_version === 4, '首次上传 schema v4 成功', JSON.stringify(firstBody));

  const downgrade = await putProject(3, firstBody.rev);
  const downgradeBody = await downgrade.json();
  check(downgrade.status === 409, 'schema v3 覆盖 v4 返回 HTTP 409', JSON.stringify(downgradeBody));
  check(downgradeBody.reason === 'schema_downgrade', '冲突原因是 schema_downgrade');
  check(downgradeBody.serverSchemaVersion === 4 && downgradeBody.incomingSchemaVersion === 3, '响应同时携带服务器与客户端 schema');

  const stored = await fetch(`${origin}/api/projects/project-v4`, { headers: { Authorization: `Bearer ${token}` } });
  const storedBody = await stored.json();
  const storedProject = JSON.parse(storedBody.data);
  check(storedBody.schema_version === 4, '服务器索引仍记录 schema v4');
  check(storedProject.schema_version === 4 && storedProject.project.name === 'Schema 4', '服务器快照未被旧数据改写');

  const stale = await putProject(4, 0);
  const staleBody = await stale.json();
  check(stale.status === 409 && staleBody.reason === 'revision_conflict', '同 schema 的过期 revision 仍按乐观锁拒绝');
} finally {
  server.kill('SIGTERM');
  await new Promise(resolve => {
    const timer = setTimeout(resolve, 2500);
    server.once('exit', () => { clearTimeout(timer); resolve(); });
  });
  fs.rmSync(dataDir, { recursive: true, force: true });
}

console.log(failures === 0 ? '\n全部通过' : `\n${failures} 个失败`);
process.exit(failures === 0 ? 0 : 1);
