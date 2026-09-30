/**
 * i18n 双语 key 对齐校验
 *
 * 中英文文案分别在 `src/lib/i18n/zh.ts` / `en.ts`，靠人工同步很容易漏：
 * 漏了不会报错，界面会直接显示 key 本身（例如 `sidebar.newProject`）。
 * 本脚本对比两份字典的 key 集合与占位符，把问题提前到本地。
 *
 * 校验内容：
 *   1. 两边 key 完全一致（缺哪个、多哪个都会列出）；
 *   2. 同一个 key 的 `{placeholder}` 集合一致（避免插值丢失）；
 *   3. 空值检查。
 *
 * 运行：pnpm check:i18n
 *
 * @module scripts/check-i18n
 */

import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(import.meta.dirname, '..');

/** 从字典源码里取出 key -> value（容忍多行字符串） */
function loadDict(file) {
  const text = fs.readFileSync(file, 'utf8');
  const dict = new Map();
  // 形如：  'key.name': 'value',   （value 里可能含转义引号）
  const re = /^\s*'([^']+)':\s*(?:(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)"))/gm;
  for (const m of text.matchAll(re)) {
    dict.set(m[1], m[2] ?? m[3] ?? '');
  }
  return dict;
}

const zh = loadDict(path.join(ROOT, 'src/lib/i18n/zh.ts'));
const en = loadDict(path.join(ROOT, 'src/lib/i18n/en.ts'));

const placeholders = (s) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');

let fails = 0;
const list = (arr, n = 12) => arr.slice(0, n).join(', ') + (arr.length > n ? ` …（共 ${arr.length} 个）` : '');

// 1) key 集合
const missingInEn = [...zh.keys()].filter((k) => !en.has(k));
const missingInZh = [...en.keys()].filter((k) => !zh.has(k));
if (missingInEn.length) { fails++; console.log(`FAIL  en.ts 缺少 key：${list(missingInEn)}`); }
if (missingInZh.length) { fails++; console.log(`FAIL  zh.ts 缺少 key：${list(missingInZh)}`); }

// 2) 占位符一致
const phMismatch = [];
for (const [k, v] of zh) {
  if (!en.has(k)) continue;
  if (placeholders(v) !== placeholders(en.get(k))) phMismatch.push(`${k}（zh: {${placeholders(v)}} / en: {${placeholders(en.get(k))}}）`);
}
if (phMismatch.length) { fails++; console.log(`FAIL  占位符不一致：${list(phMismatch, 8)}`); }

// 3) 空值
const empty = [...zh].filter(([, v]) => !v.trim()).map(([k]) => k);
if (empty.length) { fails++; console.log(`FAIL  zh.ts 存在空文案：${list(empty)}`); }

if (fails === 0) {
  console.log(`ok    zh/en 各 ${zh.size} 个 key，完全对齐，占位符一致`);
  console.log('\ni18n 校验通过 ✅');
} else {
  console.log(`\ni18n 校验失败：${fails} 类问题 ❌`);
}
process.exit(fails === 0 ? 0 : 1);
