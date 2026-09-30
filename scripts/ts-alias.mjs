/**
 * 注册 TS 解析钩子（给 `--import` 用）
 *
 * @example
 * node --experimental-strip-types --import ./scripts/ts-alias.mjs scripts/test-xxx.mjs
 *
 * @module scripts/ts-alias
 */

import { register } from 'node:module';

register('./ts-resolve.mjs', import.meta.url);
