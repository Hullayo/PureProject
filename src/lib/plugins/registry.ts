/**
 * 插件注册表（内存态 + localStorage 持久化）
 *
 * - `plugins`：已安装插件（manifest + 源码），持久化到 `pm_plugins`
 * - `pluginErrors`：最近 50 条插件错误，持久化到 `pm_plugin_errors`
 * - `pluginCommands` / `pluginViews`：运行期注册表（内存态，插件加载后填充）
 * - `pluginHooks`：事件 → 处理器（内存态）
 *
 * 所有写盘一律走 `safeSetItem()`（主存储失败语义），不直接在组件里碰 localStorage。
 *
 * @module plugins/registry
 *
 * @example
 * ```ts
 * import { plugins, installPlugin, pushPluginError } from '$lib/plugins/registry';
 * installPlugin({ manifest, code });
 * ```
 */

import { writable, get } from 'svelte/store';
import { safeGetRaw, safeSetItem } from '$lib/stores/storage-health';
import type {
  PluginCommand,
  PluginError,
  PluginEvent,
  PluginHookHandler,
  PluginView,
  StoredPlugin,
} from './types';

/** localStorage 键：已安装插件 */
export const PLUGINS_KEY = 'pm_plugins';
/** localStorage 键：插件错误日志 */
export const PLUGIN_ERRORS_KEY = 'pm_plugin_errors';
/** 错误日志保留条数 */
export const MAX_PLUGIN_ERRORS = 50;

/** 从 localStorage 读取插件表（坏数据 → 空表） */
function loadPlugins(): Record<string, StoredPlugin> {
  const raw = safeGetRaw(PLUGINS_KEY);
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    if (!parsed || typeof parsed !== 'object') return {};
    const out: Record<string, StoredPlugin> = {};
    for (const [id, v] of Object.entries(parsed)) {
      const sp = v as Partial<StoredPlugin> | null;
      if (sp && typeof sp === 'object' && sp.manifest && typeof sp.code === 'string') {
        out[id] = { manifest: sp.manifest, code: sp.code };
      }
    }
    return out;
  } catch {
    return {};
  }
}

/** 从 localStorage 读取错误日志 */
function loadErrors(): PluginError[] {
  const raw = safeGetRaw(PLUGIN_ERRORS_KEY);
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? (parsed as PluginError[]).slice(0, MAX_PLUGIN_ERRORS) : [];
  } catch {
    return [];
  }
}

/** 已安装插件（id → {manifest, code}） */
export const plugins = writable<Record<string, StoredPlugin>>(loadPlugins());

/** 插件错误日志（最近 50 条，新的在前） */
export const pluginErrors = writable<PluginError[]>(loadErrors());

/** 运行期：插件注册的命令 */
export const pluginCommands = writable<PluginCommand[]>([]);

/** 运行期：插件注册的视图 */
export const pluginViews = writable<PluginView[]>([]);

/** 运行期：事件 → 处理器 */
export const pluginHooks = new Map<PluginEvent, PluginHookHandler[]>();

plugins.subscribe((map) => {
  safeSetItem(PLUGINS_KEY, JSON.stringify(map), '插件列表', false);
});

pluginErrors.subscribe((list) => {
  safeSetItem(PLUGIN_ERRORS_KEY, JSON.stringify(list), '插件错误日志', false);
});

/** 安装（或覆盖）一个插件 */
export function installPlugin(stored: StoredPlugin): void {
  plugins.update((m) => ({ ...m, [stored.manifest.id]: stored }));
}

/** 卸载插件（同时清掉运行期注册项） */
export function uninstallPlugin(id: string): void {
  plugins.update((m) => {
    if (!(id in m)) return m;
    const next = { ...m };
    delete next[id];
    return next;
  });
  removePluginRegistrations(id);
}

/** 启用 / 禁用插件 */
export function setPluginEnabled(id: string, enabled: boolean): void {
  plugins.update((m) => (m[id] ? { ...m, [id]: { ...m[id], manifest: { ...m[id].manifest, enabled } } } : m));
}

/** 记录一条插件错误（保留最近 MAX_PLUGIN_ERRORS 条，新的在前） */
export function pushPluginError(err: Omit<PluginError, 'at'>): void {
  const entry: PluginError = { ...err, at: new Date().toISOString() };
  pluginErrors.update((list) => [entry, ...list].slice(0, MAX_PLUGIN_ERRORS));
  console.warn('[Plugin] error', entry);
}

/** 清空错误日志 */
export function clearPluginErrors(): void {
  pluginErrors.set([]);
}

/** 清空运行期注册表（重新加载全部插件前调用） */
export function resetRuntimeRegistries(): void {
  pluginCommands.set([]);
  pluginViews.set([]);
  pluginHooks.clear();
}

/** 注册一条命令 */
export function registerPluginCommand(cmd: PluginCommand): void {
  pluginCommands.update((list) => [...list.filter((c) => c.id !== cmd.id), cmd]);
}

/** 注册一个视图 */
export function registerPluginView(view: PluginView): void {
  pluginViews.update((list) => [...list.filter((v) => v.id !== view.id), view]);
}

/** 订阅一个事件 */
export function registerPluginHook<E extends PluginEvent>(event: E, handler: PluginHookHandler<E>): void {
  const list = pluginHooks.get(event) ?? [];
  list.push(handler as PluginHookHandler);
  pluginHooks.set(event, list);
}

/** 移除某插件的全部运行期注册项（按 id 前缀约定 `pluginId:`） */
export function removePluginRegistrations(pluginId: string): void {
  const prefix = `${pluginId}:`;
  pluginCommands.update((list) => list.filter((c) => !c.id.startsWith(prefix)));
  pluginViews.update((list) => list.filter((v) => !v.id.startsWith(prefix)));
}

/** 当前已安装插件列表（数组形式，便于 UI 渲染） */
export function listPlugins(): StoredPlugin[] {
  return Object.values(get(plugins));
}
