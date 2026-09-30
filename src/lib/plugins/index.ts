/**
 * 本地插件系统：统一入口
 *
 * 职责：
 * - `initPlugins()`：清空运行期注册表，加载全部**已启用**插件（应用启动 / 安装后调用）
 * - `installPluginFromJson()`：校验 manifest + 版本兼容后安装并启用
 * - `setPluginEnabledAndReload()` / `uninstallPluginById()`：启停与卸载
 * - `runHook()`：向所有插件派发生命周期 / 数据事件（错误隔离）
 *
 * 安全提示：插件代码在本进程内执行，**只安装可信来源的插件**。详见 `docs/PLUGINS.md`。
 *
 * @module plugins
 *
 * @example
 * ```ts
 * import { initPlugins, runHook } from '$lib/plugins';
 * initPlugins();
 * runHook('onTaskCreate', { projectId, task });
 * ```
 */

import {
  installPlugin,
  listPlugins,
  pluginHooks,
  pushPluginError,
  resetRuntimeRegistries,
  setPluginEnabled,
  uninstallPlugin,
} from './registry';
import { loadPlugin } from './loader';
import {
  APP_VERSION,
  isCompatible,
  validateManifest,
  type PluginEvent,
  type PluginEventPayloads,
  type PluginManifest,
  type StoredPlugin,
} from './types';

export * from './types';
export {
  plugins,
  pluginErrors,
  pluginCommands,
  pluginViews,
  listPlugins,
  installPlugin,
  setPluginEnabled,
  uninstallPlugin,
  clearPluginErrors,
} from './registry';
export { loadPlugin, createPluginContext } from './loader';

/**
 * 加载全部已启用插件（可重复调用；会先清空运行期注册表）
 *
 * @returns 成功加载的插件数
 */
export function initPlugins(): number {
  resetRuntimeRegistries();
  let loaded = 0;
  for (const stored of listPlugins()) {
    if (!stored.manifest.enabled) continue;
    if (loadPlugin(stored)) loaded++;
  }
  return loaded;
}

/** 安装结果 */
export type InstallResult = { ok: true; manifest: PluginManifest } | { ok: false; error: string };

/**
 * 从 manifest JSON 文本 + 源码安装并启用一个插件
 *
 * @param manifestJson - manifest 的 JSON 文本
 * @param code - 插件源码
 */
export function installPluginFromJson(manifestJson: string, code: string): InstallResult {
  let raw: unknown;
  try {
    raw = JSON.parse(manifestJson);
  } catch (e) {
    return { ok: false, error: `manifest 不是合法 JSON：${e instanceof Error ? e.message : String(e)}` };
  }

  const res = validateManifest(raw);
  if (!res.ok) return { ok: false, error: res.error };
  if (!isCompatible(res.manifest.version, APP_VERSION)) {
    return { ok: false, error: `主版本不兼容：插件 ${res.manifest.version} / 应用 ${APP_VERSION}` };
  }
  if (!code.trim()) return { ok: false, error: '插件源码为空' };

  const stored: StoredPlugin = { manifest: { ...res.manifest, enabled: true }, code };
  installPlugin(stored);
  initPlugins();
  return { ok: true, manifest: stored.manifest };
}

/** 启用/禁用插件并立即重新加载 */
export function setPluginEnabledAndReload(id: string, enabled: boolean): void {
  setPluginEnabled(id, enabled);
  initPlugins();
}

/** 卸载插件并立即重新加载 */
export function uninstallPluginById(id: string): void {
  uninstallPlugin(id);
  initPlugins();
}

/**
 * 派发一个生命周期 / 数据事件给所有插件
 *
 * 每个处理器在注册时已被 try/catch 包裹（见 `createPluginContext`），这里再兜一层，
 * 保证任何插件异常都不会影响主流程。
 */
export function runHook<E extends PluginEvent>(event: E, payload: PluginEventPayloads[E]): void {
  const handlers = pluginHooks.get(event);
  if (!handlers || handlers.length === 0) return;
  for (const handler of handlers) {
    try {
      (handler as (value: PluginEventPayloads[E]) => void)(payload);
    } catch (e) {
      pushPluginError({
        pluginId: '<hook>',
        message: `事件 ${event} 派发失败：${e instanceof Error ? e.message : String(e)}`,
      });
    }
  }
}
