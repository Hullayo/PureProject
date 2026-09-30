/**
 * Android ACL（权限）自检
 *
 * 背景：`android-fs:default` 等价于插件的 `all-without-delete`，
 * **不包含** `write_file` / `write_text_file` / `remove_file` 等“写/删”命令。
 * 一旦前端调用了没授权的命令，只会在**真机**上报：
 *   `Command plugin:android-fs|write_text_file not allowed by ACL`
 * —— 桌面/浏览器开发时完全看不出来。本脚本把这类问题提前到本地检查。
 *
 * 做法：
 *   1. 解析 `src-tauri/capabilities/*.json`，取出 platform 含 android 的权限标识符；
 *   2. 在 cargo registry 里找到对应插件的 `permissions/*.toml`，
 *      把权限集（如 `default` → `all-without-delete`、`all`）展开成命令集合；
 *   3. 用 `REQUIRED`（前端实际用到的命令，改动导出代码时要同步更新）逐一比对。
 *
 * 运行：pnpm check:acl（无需构建、无需 Android SDK）
 *
 * @module scripts/check-android-acl
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const ROOT = path.resolve(import.meta.dirname, '..');
const CAP_DIR = path.join(ROOT, 'src-tauri/capabilities');

/**
 * 前端实际调用的插件命令（**修改导出/文件相关代码后必须同步这里**）
 *
 * 来源：`src/lib/utils/html-export.ts`、`src/lib/components/shared/ExportDialog.svelte`、
 * `src/lib/updater/*`（APK 安装）
 */
const REQUIRED = {
  'android-fs': [
    'create_new_public_file',   // 新建 Downloads 下的文件
    'write_text_file',          // 导出 HTML
    'write_file',               // 导出 PDF（写字节）
    'set_public_file_pending',  // Downloads pending 收尾
    'scan_public_file',         // 通知媒体库
    'get_name',                 // 保存后回显文件名
    'remove_file',              // 下载失败时清理空文件
    'show_save_file_picker',    // 「另存为」系统对话框
    'show_view_file_app_chooser', // 导出后「打开」
  ],
  'android-installer': [
    'install',                  // 拉起系统安装器
    'can_install',
    'request_install_permission',
  ],
};

// ─── 找到插件源码（cargo registry） ─────────────────────────────────────────
function registryRoot() {
  const home = process.env.CARGO_HOME || path.join(os.homedir(), '.cargo');
  const src = path.join(home, 'registry', 'src');
  if (!fs.existsSync(src)) return null;
  for (const dir of fs.readdirSync(src)) {
    const full = path.join(src, dir);
    if (fs.statSync(full).isDirectory()) return full;
  }
  return null;
}

/** 在 registry 里找插件目录（取版本最大的那个） */
function findPluginDir(registry, plugin) {
  const prefix = `tauri-plugin-${plugin}-`;
  const dirs = fs.readdirSync(registry)
    .filter((d) => d.startsWith(prefix))
    .sort((a, b) => {
      const va = a.slice(prefix.length).split('.').map(Number);
      const vb = b.slice(prefix.length).split('.').map(Number);
      return (vb[0] - va[0]) || (vb[1] - va[1]) || (vb[2] - va[2]);
    });
  return dirs.length ? path.join(registry, dirs[0]) : null;
}

/**
 * 极简 TOML 读取
 *
 * 插件权限有两种写法：
 *   - `[[permission]]` + `identifier` + `commands.allow = [...]`  → 单个权限
 *   - `[<identifier>]` + `permissions = [...]`                  → 权限集（如 `[default]` → all-without-delete）
 * 所以按“段”切分，两种都收。
 */
function parsePermissionFiles(permissionsDir) {
  const sets = new Map();       // identifier -> { commands: string[], includes: string[] }

  const parseText = (text) => {
    const parts = text.split(/(?=^\[\[permission\]\]|^\[[^\[\]]+\])/m);
    for (const part of parts) {
      const header = part.match(/^(\[\[permission\]\]|\[[^\[\]]+\])/);
      if (!header) continue;
      const isPermissionBlock = header[1] === '[[permission]]';
      const tableName = isPermissionBlock ? null : header[1].slice(1, -1);
      const identifier = part.match(/identifier\s*=\s*"([^"]+)"/)?.[1] ?? tableName;
      if (!identifier) continue;
      const commands = [...part.matchAll(/commands\.allow\s*=\s*\[([\s\S]*?)\]/g)]
        .flatMap((m) => [...m[1].matchAll(/"([^"]+)"/g)].map((x) => x[1]));
      const includes = [...part.matchAll(/(?:^|\n)\s*permissions\s*=\s*\[([\s\S]*?)\]/g)]
        .flatMap((m) => [...m[1].matchAll(/"([^"]+)"/g)].map((x) => x[1]));
      sets.set(identifier, { commands, includes });
    }
  };

  const walk = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (entry.name.endsWith('.toml')) parseText(fs.readFileSync(full, 'utf8'));
    }
  };
  walk(permissionsDir);
  return sets;
}

/** 展开权限集（含嵌套）为命令集合 */
function expand(sets, id, depth = 0) {
  if (depth > 5) return [];
  const node = sets.get(id);
  if (!node) return [];
  return [
    ...node.commands,
    ...node.includes.flatMap((child) => expand(sets, child, depth + 1)),
  ];
}

// ─── 主流程 ─────────────────────────────────────────────────────────────────
let fails = 0;
const registry = registryRoot();
if (!registry) {
  console.error('✗ 找不到 cargo registry（~/.cargo/registry/src），无法解析插件权限');
  process.exit(2);
}

/** 收集本仓库 Android 能力里声明的权限（identifier 去掉插件前缀） */
const declared = new Map();   // plugin -> Set<permissionId>
let sawAndroidCapability = false;

for (const file of fs.readdirSync(CAP_DIR).filter((f) => f.endsWith('.json'))) {
  const cap = JSON.parse(fs.readFileSync(path.join(CAP_DIR, file), 'utf8'));
  const platforms = cap.platforms ?? [];
  const androidish = platforms.length === 0 || platforms.includes('android');
  if (!androidish) continue;
  sawAndroidCapability = true;
  for (const perm of cap.permissions ?? []) {
    const raw = typeof perm === 'string' ? perm : perm.identifier;
    if (!raw || raw.startsWith('core:')) continue;
    const [plugin, id] = raw.split(':');
    if (!id) continue;
    if (!declared.has(plugin)) declared.set(plugin, new Set());
    declared.get(plugin).add(id);
  }
}

if (!sawAndroidCapability) {
  console.error('✗ src-tauri/capabilities 下没有覆盖 android 的能力文件');
  process.exit(1);
}

for (const [plugin, requiredCommands] of Object.entries(REQUIRED)) {
  const pluginDir = findPluginDir(registry, plugin);
  if (!pluginDir) {
    console.log(`skip  插件 ${plugin} 不在 cargo registry 中（跳过）`);
    continue;
  }
  const sets = parsePermissionFiles(path.join(pluginDir, 'permissions'));
  const allowed = new Set();
  for (const id of declared.get(plugin) ?? []) {
    for (const cmd of expand(sets, id)) allowed.add(cmd);
  }

  const missing = requiredCommands.filter((c) => !allowed.has(c));
  const version = path.basename(pluginDir).slice(`tauri-plugin-${plugin}-`.length);
  if (missing.length === 0) {
    console.log(`ok    ${plugin}@${version}：${requiredCommands.length} 个命令均已授权`);
  } else {
    fails += missing.length;
    console.log(`FAIL  ${plugin}@${version}：以下命令未授权，真机会报 “…not allowed by ACL”`);
    for (const cmd of missing) {
      console.log(`        - ${cmd}   → 请在 capabilities 里加 "${plugin}:allow-${cmd.replace(/_/g, '-')}"`);
    }
  }
}

console.log(fails === 0 ? '\nACL 自检通过 ✅' : `\nACL 自检失败：${fails} 个命令未授权 ❌`);
process.exit(fails === 0 ? 0 : 1);
