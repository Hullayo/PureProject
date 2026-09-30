/**
 * 插件加载器
 *
 * 把 `StoredPlugin`（manifest + 源码）编译为可执行模块并注入 {@link PluginContext}：
 * 使用 `new Function('ctx', code)`（**不是 `eval`**），执行失败一律捕获并写入错误日志，
 * 绝不把异常抛给主应用。
 *
 * @module plugins/loader
 *
 * @example
 * ```ts
 * import { loadPlugin } from '$lib/plugins/loader';
 * loadPlugin({ manifest, code });
 * ```
 */

import { get } from 'svelte/store';
import { safeGetRaw, safeSetItem } from '$lib/stores/storage-health';
import { t as i18nT } from '$lib/i18n';
import {
  pushPluginError,
  registerPluginCommand,
  registerPluginHook,
  registerPluginView,
} from './registry';
import {
  APP_VERSION,
  PLUGIN_API_VERSION,
  isCompatible,
  pluginStorageKey,
  type PluginContext,
  type PluginManifest,
  type StoredPlugin,
} from './types';

/** 为一个插件构造注入上下文 */
export function createPluginContext(manifest: PluginManifest): PluginContext {
  const prefix = `${manifest.id}:`;
  return {
    apiVersion: PLUGIN_API_VERSION,
    plugin: manifest,
    registerCommand: (cmd) =>
      registerPluginCommand({
        ...cmd,
        id: cmd.id.startsWith(prefix) ? cmd.id : `${prefix}${cmd.id}`,
        category: cmd.category ?? manifest.name,
      }),
    registerView: (view) =>
      registerPluginView({
        ...view,
        id: view.id.startsWith(prefix) ? view.id : `${prefix}${view.id}`,
      }),
    on: (event, handler) =>
      registerPluginHook(event, (payload) => {
        try {
          handler(payload);
        } catch (e) {
          pushPluginError({
            pluginId: manifest.id,
            message: `钩子 ${event} 执行出错：${e instanceof Error ? e.message : String(e)}`,
            stack: e instanceof Error ? e.stack : undefined,
          });
        }
      }),
    storage: {
      get: (key) => safeGetRaw(pluginStorageKey(manifest.id, key)),
      set: (key, value) =>
        safeSetItem(pluginStorageKey(manifest.id, key), String(value), `插件「${manifest.name}」存储`, false),
      remove: (key) => {
        try {
          localStorage.removeItem(pluginStorageKey(manifest.id, key));
        } catch {
          /* 忽略 */
        }
      },
    },
    log: (...args) => console.log(`[Plugin:${manifest.id}]`, ...args),
    i18n: { t: (key) => i18nT.get()(key) },
  };
}

/**
 * 加载并执行一个插件
 *
 * @returns 是否加载成功（失败已记入 `pluginErrors`）
 */
export function loadPlugin(stored: StoredPlugin, appVersion: string = APP_VERSION): boolean {
  const { manifest, code } = stored;

  if (!isCompatible(manifest.version, appVersion)) {
    pushPluginError({
      pluginId: manifest.id,
      message: `主版本不兼容：插件 ${manifest.version} / 应用 ${appVersion}（需同为 ${appVersion.split('.')[0]}.x）`,
    });
    return false;
  }

  try {
    // eslint-disable-next-line no-new-func
    const factory = new Function('ctx', `"use strict";\n${code}\n`);
    factory(createPluginContext(manifest));
    return true;
  } catch (e) {
    pushPluginError({
      pluginId: manifest.id,
      message: `加载失败：${e instanceof Error ? e.message : String(e)}`,
      stack: e instanceof Error ? e.stack : undefined,
    });
    return false;
  }
}

/** 当前语言下取词（供内置示例/宿主复用） */
export function tr(key: string): string {
  return get(i18nT)(key);
}
