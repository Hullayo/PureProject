#!/usr/bin/env node
/**
 * publish-github.mjs — 把当前版本发布到 GitHub Releases（供「更新源 = GitHub」使用）
 *
 * 与 `publish-update.mjs`（自建静态服务器）产出的清单格式一致，只是把 URL 换成
 * GitHub Release 资源地址：`https://github.com/<repo>/releases/latest/download/<asset>`。
 *
 * 上传资源：
 *   - ProjectManager_<ver>_x64-setup.exe / .exe.sig   （Windows NSIS + 更新签名）
 *   - ProjectManager-android-arm64-v<ver>.apk        （Android）
 *   - latest.json / android.json                      （清单，客户端实际请求的文件）
 *
 * 用法：
 *   GITHUB_TOKEN=ghp_xxx node scripts/publish-github.mjs \
 *     [--repo AlicDanclic/ProjectManager] [--tag v<version>] [--notes "…"] [--apk <path>]
 *
 * 环境变量：
 *   GITHUB_TOKEN / GH_TOKEN   必填（需 repo 权限）
 *   GITHUB_REPO               默认 AlicDanclic/ProjectManager
 *   UPDATE_NOTES              更新说明
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const CONF_PATH = path.join(ROOT, 'src-tauri', 'tauri.conf.json');

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const next = argv[i + 1];
      if (next && !next.startsWith('--')) { args[a.slice(2)] = next; i++; }
      else args[a.slice(2)] = true;
    }
  }
  return args;
}

const args = parseArgs(process.argv.slice(2));
const VERSION = JSON.parse(fs.readFileSync(CONF_PATH, 'utf8')).version;
const TAG = String(args.tag || `v${VERSION}`);
const REPO = String(args.repo || process.env.GITHUB_REPO || 'AlicDanclic/ProjectManager').replace(/^https?:\/\/github\.com\//, '').replace(/\.git$/, '');
const TOKEN = process.env.GITHUB_TOKEN || process.env.GH_TOKEN || '';
const NOTES = String(args.notes || process.env.UPDATE_NOTES || `v${VERSION}`);
const BASE = `https://github.com/${REPO}/releases/latest/download`;

if (!TOKEN) {
  console.error('✗ 缺少 GITHUB_TOKEN / GH_TOKEN');
  process.exit(1);
}

const api = (p) => `https://api.github.com/repos/${REPO}${p}`;
const headers = {
  Authorization: `Bearer ${TOKEN}`,
  Accept: 'application/vnd.github+json',
  'User-Agent': 'projectmanager-publish',
  'X-GitHub-Api-Version': '2022-11-28',
};

async function gh(method, url, body, extraHeaders = {}) {
  const res = await fetch(url, {
    method,
    headers: { ...headers, ...extraHeaders },
    body,
  });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* ignore */ }
  if (!res.ok) throw new Error(`${method} ${url} -> ${res.status} ${text.slice(0, 300)}`);
  return json;
}

/** 递归查找构建目录下匹配的文件 */
function walk(dir, filter, out = []) {
  if (!fs.existsSync(dir)) return out;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (['.fingerprint', 'deps', 'incremental', 'build'].includes(e.name)) continue;
      walk(full, filter, out);
    } else if (filter(e.name)) out.push(full);
  }
  return out;
}

// ─── 找工件 ────────────────────────────────────────────────────────────────
const exe = walk(path.join(ROOT, 'src-tauri', 'target'), (n) => n === `ProjectManager_${VERSION}_x64-setup.exe`)[0]
  || walk(path.join(ROOT, 'dist-updates'), (n) => n === `ProjectManager_${VERSION}_x64-setup.exe`)[0];
const sig = walk(path.join(ROOT, 'src-tauri', 'target'), (n) => n === `ProjectManager_${VERSION}_x64-setup.exe.sig`)[0]
  || walk(path.join(ROOT, 'dist-updates'), (n) => n === `ProjectManager_${VERSION}_x64-setup.exe.sig`)[0];
const apkArg = typeof args.apk === 'string' ? path.resolve(args.apk) : null;
const apk = apkArg
  || fs.readdirSync(ROOT).filter((f) => f.endsWith('.apk') && f.includes(VERSION)).map((f) => path.join(ROOT, f))[0]
  || walk(path.join(ROOT, 'dist-updates'), (n) => n.endsWith('.apk') && n.includes(VERSION))[0];

if (!exe || !sig) {
  console.error(`✗ 找不到 Windows 工件（ProjectManager_${VERSION}_x64-setup.exe / .sig），请先 pnpm build:win`);
  process.exit(1);
}

// ─── 生成清单（URL 指向 GitHub 最新 Release 资源）────────────────────────────
// ⚠️ 写到独立目录 dist-github，**绝不能写 dist-updates**：后者是自建更新服务器
// 的目录，两个脚本写同一份 latest.json/android.json 会互相覆盖，
// 导致「服务器源」拿到的下载地址变成 GitHub（国内常拉不动）。
const outDir = path.resolve(String(args.out || path.join(ROOT, 'dist-github')));
fs.mkdirSync(outDir, { recursive: true });

const latest = {
  version: VERSION,
  notes: NOTES,
  pub_date: new Date().toISOString(),
  platforms: {
    'windows-x86_64': {
      signature: fs.readFileSync(sig, 'utf8').trim(),
      url: `${BASE}/${path.basename(exe)}`,
    },
  },
};
const latestPath = path.join(outDir, 'latest.json');
fs.writeFileSync(latestPath, JSON.stringify(latest, null, 2) + '\n');

let androidPath = null;
if (apk) {
  const android = {
    version: VERSION,
    notes: NOTES,
    url: `${BASE}/${path.basename(apk)}`,
    required: false,
  };
  androidPath = path.join(outDir, 'android.json');
  fs.writeFileSync(androidPath, JSON.stringify(android, null, 2) + '\n');
}

// ─── 创建 / 复用 Release ───────────────────────────────────────────────────
console.log(`📦 GitHub Release — ${REPO} @ ${TAG}`);
let release;
try {
  release = await gh('GET', api(`/releases/tags/${TAG}`));
} catch {
  release = await gh('POST', api('/releases'), JSON.stringify({
    tag_name: TAG,
    name: TAG,
    body: NOTES,
    draft: false,
    prerelease: false,
  }));
}

// ─── 上传（同名资源先删后传）───────────────────────────────────────────────
async function upload(filePath) {
  const name = path.basename(filePath);
  const assets = release.assets || [];
  const old = assets.find((a) => a.name === name);
  if (old) await gh('DELETE', api(`/releases/assets/${old.id}`));
  const bytes = fs.readFileSync(filePath);
  await gh('POST', `https://uploads.github.com/repos/${REPO}/releases/${release.id}/assets?name=${encodeURIComponent(name)}`,
    bytes, { 'Content-Type': 'application/octet-stream' });
  console.log(`   ↑ ${name} (${(bytes.length / 1024).toFixed(0)} KB)`);
}

const files = [exe, sig, latestPath];
if (androidPath) files.push(androidPath);
if (apk) files.push(apk);
for (const f of files) await upload(f);

console.log(`\n✅ 已发布到 GitHub Releases`);
console.log(`   桌面清单: ${BASE}/latest.json`);
if (apk) console.log(`   Android : ${BASE}/android.json`);
