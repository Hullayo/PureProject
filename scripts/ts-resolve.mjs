/**
 * Node 端模块解析钩子（配合 `--experimental-strip-types`）
 *
 * 目的：让 `scripts/test-*.mjs` 能直接 `import` 应用里的 TS 模块，哪怕它用了：
 *   - 无扩展名相对导入：`import { x } from './pm-schema'`（Vite 能解析，Node 不能）
 *   - 路径别名：`import { y } from '$lib/types'`
 *
 * 用法（在测试脚本的 npm script 里）：
 * ```
 * node --experimental-strip-types --import ./scripts/ts-alias.mjs scripts/test-xxx.mjs
 * ```
 * 类型只在运行时被剥离（Node 原生 strip-types），不做任何转译。
 *
 * @module scripts/ts-resolve
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = path.resolve(import.meta.dirname, '..');
const LIB = path.join(ROOT, 'src', 'lib');

/** 尝试补全扩展名 / index 文件 */
function tryResolve(filePath, specifier, parentURL) {
  const candidates = [
    `${filePath}.ts`,
    `${filePath}.tsx`,
    path.join(filePath, 'index.ts'),
  ];
  for (const c of candidates) {
    if (fs.existsSync(c) && fs.statSync(c).isFile()) {
      return pathToFileURL(c).href;
    }
  }
  return null;
}

export async function resolve(specifier, context, nextResolve) {
  // 1) `$lib/xxx` → src/lib/xxx
  if (specifier.startsWith('$lib/')) {
    const target = path.join(LIB, specifier.slice('$lib/'.length));
    const resolved = tryResolve(target, specifier, context.parentURL) ?? pathToFileURL(`${target}.ts`).href;
    return nextResolve(resolved, context);
  }

  // 2) 相对/绝对路径但缺扩展名 → 补 .ts
  if ((specifier.startsWith('./') || specifier.startsWith('../') || specifier.startsWith('/'))
      && !/\.[a-z0-9]+$/i.test(specifier)) {
    const base = specifier.startsWith('/')
      ? specifier
      : fileURLToPath(new URL(specifier, context.parentURL));
    const resolved = tryResolve(base, specifier, context.parentURL);
    if (resolved) return nextResolve(resolved, context);
  }

  return nextResolve(specifier, context);
}
