#!/usr/bin/env node
/**
 * publish-update.mjs — 生成自动更新清单并汇集产物
 *
 * 作用：
 *   1. 从 tauri.conf.json 读取当前版本号
 *   2. 在 src-tauri/target 下查找所有 updater 工件（*.sig 及其对应文件）
 *   3. 将工件拷贝到输出目录（UPDATE_DIR）
 *   4. 生成桌面端清单 latest.json（tauri-plugin-updater 静态格式）
 *   5. 若找到 Android APK，生成 android.json（移动端自定义更新清单）
 *
 * 用法：
 *   node scripts/publish-update.mjs [--out <dir>] [--base-url <url>] [--apk <path>] [--notes-file <path>]
 *
 * 环境变量（命令行参数优先）：
 *   UPDATE_DIR       输出目录，默认 <repo>/dist-updates
 *   UPDATE_BASE_URL  更新服务器根地址（**必填**，不内置任何地址），如 https://example.com/updates
 *   UPDATE_NOTES     更新说明（优先级低于 --notes-file）
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const TAURI_DIR = path.join(ROOT, 'src-tauri');
const CONF_PATH = path.join(TAURI_DIR, 'tauri.conf.json');

// ─── 参数解析 ───────────────────────────────────────────────────────────────
function parseArgs(argv) {
  const args = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next && !next.startsWith('--')) { args[key] = next; i++; }
      else args[key] = true;
    } else args._.push(a);
  }
  return args;
}

const args = parseArgs(process.argv.slice(2));

if (args.help) {
  console.log(`用法: node scripts/publish-update.mjs [--out <dir>] [--base-url <url>] [--apk <path>] [--notes-file <path>]`);
  process.exit(0);
}

if (!fs.existsSync(CONF_PATH)) {
  console.error(`✗ 找不到 ${CONF_PATH}`);
  process.exit(1);
}

const conf = JSON.parse(fs.readFileSync(CONF_PATH, 'utf8'));
const VERSION = conf.version;
// 更新服务器地址不内置：必须由 --base-url / UPDATE_BASE_URL 显式给出（避免仓库/二进制里暴露真实服务器）
const BASE_URL = String(args['base-url'] || process.env.UPDATE_BASE_URL || '').replace(/\/+$/, '');
if (!BASE_URL) {
  console.error('✗ 缺少更新服务器根地址：请传 --base-url <url> 或设置 UPDATE_BASE_URL 环境变量（如 https://example.com/updates）');
  process.exit(1);
}
const OUT_DIR = path.resolve(String(args.out || process.env.UPDATE_DIR || path.join(ROOT, 'dist-updates')));

let NOTES = process.env.UPDATE_NOTES || '';
if (typeof args['notes-file'] === 'string') {
  NOTES = fs.readFileSync(path.resolve(args['notes-file']), 'utf8');
}

// ─── 工具函数 ───────────────────────────────────────────────────────────────
/** 由 Rust target triple 推导 tauri-plugin-updater 的平台 key（OS-ARCH） */
function platformKeyFromTriple(triple) {
  const arch = triple.startsWith('aarch64') ? 'aarch64'
    : triple.startsWith('x86_64') ? 'x86_64'
    : triple.startsWith('i686') ? 'i686'
    : triple.startsWith('armv7') ? 'armv7'
    : null;
  const os = triple.includes('windows') ? 'windows'
    : triple.includes('darwin') ? 'darwin'
    : (triple.includes('linux') && !triple.includes('android')) ? 'linux'
    : null;
  return arch && os ? `${os}-${arch}` : null;
}

/** 递归收集匹配的文件 */
function walk(dir, filter, out = []) {
  if (!fs.existsSync(dir)) return out;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      // 跳过无关的编译缓存目录，只保留 bundle 产物
      if (['.fingerprint', 'build', 'deps', 'incremental'].includes(entry.name)) continue;
      walk(full, filter, out);
    } else if (filter(entry.name, full)) {
      out.push(full);
    }
  }
  return out;
}

/** 从路径中提取 target triple（target/<triple>/release/...），原生构建取不到则返回 null */
function tripleFromPath(p) {
  const m = p.split(path.sep).join('/').match(/\/target\/([^/]+)\/release\//);
  return m ? m[1] : null;
}

/** 无 target triple（原生构建）时，按构建主机推断平台 key */
function hostPlatformKey() {
  const arch = process.arch === 'x64' ? 'x86_64'
    : process.arch === 'arm64' ? 'aarch64'
    : process.arch;
  const os = process.platform === 'win32' ? 'windows'
    : process.platform === 'darwin' ? 'darwin'
    : 'linux';
  return `${os}-${arch}`;
}

/** 无 target triple（如已在输出目录中的成品）时，按文件名推断平台 key */
function platformKeyFromFilename(name) {
  if (/\.apk$/i.test(name)) return null; // Android 单独走 android.json
  const arch = /aarch64|arm64/i.test(name) ? 'aarch64'
    : /x86_64|x64|amd64/i.test(name) ? 'x86_64'
    : /i686|ia32/i.test(name) ? 'i686'
    : /armv7/i.test(name) ? 'armv7'
    : 'x86_64';
  const os = /\.exe$|\.msi$|\.nsis\.zip$|\.msi\.zip$/i.test(name) ? 'windows'
    : /\.app\.tar\.gz$|\.dmg$|macos|darwin/i.test(name) ? 'darwin'
    : /\.AppImage$|\.deb$|\.rpm$/i.test(name) ? 'linux'
    : null;
  return os ? `${os}-${arch}` : null;
}

/**
 * updater 工件优先级：同一平台若存在多个（如 NSIS exe 与 MSI），
 * 选择更适合自动更新的一个。
 */
const ARTIFACT_PRIORITY = [
  (f) => f.endsWith('-setup.exe'),   // Windows NSIS 安装器（v2 直接作为 updater 工件）
  (f) => f.endsWith('.nsis.zip'),    // Windows NSIS（v1Compatible 压缩包）
  (f) => f.endsWith('.msi.zip'),
  (f) => f.endsWith('.AppImage'),    // Linux
  (f) => f.endsWith('.app.tar.gz'),  // macOS
  (f) => f.endsWith('.msi'),
];

function artifactRank(file) {
  const i = ARTIFACT_PRIORITY.findIndex((fn) => fn(file));
  return i === -1 ? ARTIFACT_PRIORITY.length : i;
}

// ─── 收集桌面端 updater 工件 ─────────────────────────────────────────────────
// 同时扫描构建目录（bundle 下的 .sig）与输出目录（已拷贝的成品，便于幂等重跑）。
// 输出目录用 r 前缀以区分；最终按平台去重，构建目录优先。
const targetSigs = walk(
  path.join(TAURI_DIR, 'target'),
  (name, full) => name.endsWith('.sig') && full.split(path.sep).includes('bundle')
);

/** platform key -> { artifact, sig, rank } */
const byPlatform = new Map();

/**
 * 文件名里的版本号是否正好是当前版本
 *
 * 用词边界而非 `includes`，避免 `0.5.2` 误匹配 `0.5.20`。
 */
function matchesVersion(name) {
  const escaped = VERSION.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`(^|[^0-9.])${escaped}([^0-9.]|$)`).test(name);
}

function registerArtifact(sig, platform, requireVersion = false) {
  const artifact = sig.slice(0, -'.sig'.length);
  if (!platform || !fs.existsSync(artifact)) return;
  // 输出目录里的历史工件（上一版）绝不能混进本版清单：
  // 否则会出现 version=0.5.2 却指向 0.5.0 安装包的“假清单”，客户端反复更新同一版。
  if (requireVersion && !matchesVersion(path.basename(artifact))) return;
  const rank = artifactRank(artifact);
  const current = byPlatform.get(platform);
  // 构建目录（rank 相同时先入）优先，其次取优先级更高的工件
  if (!current || rank < current.rank) {
    byPlatform.set(platform, { artifact, sig, rank });
  }
}

for (const sig of targetSigs) {
  registerArtifact(sig, tripleFromPath(sig) ? platformKeyFromTriple(tripleFromPath(sig)) : hostPlatformKey());
}

// ─── 输出目录 ───────────────────────────────────────────────────────────────
fs.mkdirSync(OUT_DIR, { recursive: true });

// 输出目录中已存在的 .sig（先于生成 latest.json 注册，保证重跑不丢桌面清单）
// ⚠️ 只认“当前版本”的工件，避免把上一版安装包写进新版清单。
const outSigs = walk(OUT_DIR, (name) => name.endsWith('.sig'));
for (const sig of outSigs) {
  registerArtifact(sig, platformKeyFromFilename(path.basename(sig).replace(/\.sig$/, '')), true);
}

const copied = [];

function copyToOut(file) {
  const basename = path.basename(file);
  const dest = path.join(OUT_DIR, basename);
  if (path.resolve(file) !== path.resolve(dest)) {
    fs.copyFileSync(file, dest);
  }
  if (!copied.includes(basename)) copied.push(basename);
  return basename;
}

// ─── 生成 latest.json ───────────────────────────────────────────────────────
const platforms = {};
for (const [platform, { artifact, sig }] of byPlatform) {
  const fileName = copyToOut(artifact);
  copyToOut(sig);
  platforms[platform] = {
    signature: fs.readFileSync(sig, 'utf8').trim(),
    url: `${BASE_URL}/${encodeURIComponent(fileName)}`,
  };
}

/**
 * 写桌面清单
 *
 * 若本次没有任何桌面工件（例如只编了 Android），**保留已有 latest.json 不覆盖**：
 * 直接写 `platforms: {}` 会让桌面端拿到空清单（历史踩坑：“重跑 publish 后 latest.json 变空”）。
 */
const latestPath = path.join(OUT_DIR, 'latest.json');
const hasPlatforms = Object.keys(platforms).length > 0;
if (hasPlatforms || !fs.existsSync(latestPath)) {
  const latest = {
    version: VERSION,
    notes: NOTES,
    pub_date: new Date().toISOString(),
    platforms,
  };
  fs.writeFileSync(latestPath, JSON.stringify(latest, null, 2) + '\n');
} else {
  let kept = '?';
  try { kept = JSON.parse(fs.readFileSync(latestPath, 'utf8')).version ?? '?'; } catch { /* ignore */ }
  console.warn(`   ⚠ 未找到 v${VERSION} 的桌面工件（*.sig），保留现有 latest.json（v${kept}）不覆盖。`);
}

// ─── 生成 android.json ──────────────────────────────────────────────────────
/** 查找 Android APK：命令行 --apk 优先，否则在仓库根目录匹配当前版本 */
function findApk() {
  if (typeof args.apk === 'string') {
    const p = path.resolve(args.apk);
    return fs.existsSync(p) ? p : null;
  }
  const candidates = fs.readdirSync(ROOT).filter(
    (f) => f.endsWith('.apk') && f.includes(VERSION)
  );
  if (candidates.length === 0) return null;
  return path.join(ROOT, candidates[0]);
}

const apk = findApk();
let androidWritten = false;
if (apk) {
  const apkName = copyToOut(apk);
  const android = {
    version: VERSION,
    notes: NOTES,
    url: `${BASE_URL}/${encodeURIComponent(apkName)}`,
    required: false,
  };
  fs.writeFileSync(path.join(OUT_DIR, 'android.json'), JSON.stringify(android, null, 2) + '\n');
  androidWritten = true;
}

// ─── 汇总 ───────────────────────────────────────────────────────────────────
console.log(`\n📦 PureProject 更新发布 — v${VERSION}`);
console.log(`   输出目录 : ${OUT_DIR}`);
console.log(`   服务器   : ${BASE_URL}\n`);

if (Object.keys(platforms).length === 0) {
  console.warn('   ⚠ 未找到桌面端 updater 工件（*.sig）。');
  console.warn('     请先用 createUpdaterArtifacts 构建，例如：');
  console.warn('       pnpm tauri build --runner cargo-xwin --target x86_64-pc-windows-msvc\n');
} else {  console.log('   桌面端最新清单 latest.json:');
  for (const [p, v] of Object.entries(platforms)) {
    console.log(`     • ${p.padEnd(16)} -> ${v.url}`);
  }
}

if (androidWritten) {
  console.log('   Android 清单 android.json:');
  console.log(`     • android        -> ${BASE_URL}/${encodeURIComponent(path.basename(apk))}`);
} else {
  console.log('   （未找到当前版本的 APK，跳过 android.json）');
}

console.log(`\n   拷贝文件 (${copied.length}):`);
for (const f of copied) console.log(`     - ${f}`);
console.log(`\n✅ 完成。将上述文件上传到服务器目录后即可生效。\n`);
