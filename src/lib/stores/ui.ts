/**
 * UI 状态 stores
 *
 * 管理应用的 UI 相关状态：主题、侧边栏、设置弹窗、教程弹窗、待执行动作。
 *
 * @module stores/ui
 */

import { derived, writable } from 'svelte/store';

/** 安全读取 localStorage */
function safeGet(key: string): string | null { try { return localStorage.getItem(key); } catch { return null; } }
function safeSet(key: string, val: string): void { try { localStorage.setItem(key, val); } catch {} }

/**
 * 主题模式：浅色 / 深色 / **跟随系统**
 *
 * 持久化仍用 `pm_theme` 这一个键（值域从 light|dark 扩到 light|dark|system）：
 * - 旧值 `'light'` / `'dark'` 天然是合法值 → **零迁移**，老用户保持原选择
 * - 键不存在（全新安装）→ `'system'`
 */
export type ThemeMode = 'light' | 'dark' | 'system';

function loadThemeMode(): ThemeMode {
  const v = safeGet('pm_theme');
  return v === 'light' || v === 'dark' ? v : 'system';
}

/** 当前主题模式（唯一写入口是 setThemeMode） */
export const themeMode = writable<ThemeMode>(loadThemeMode());
themeMode.subscribe(v => safeSet('pm_theme', v));

/** 系统是否处于深色（非浏览器环境恒为 false → 回退浅色） */
const systemDark = writable(false);

if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
  try {
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    systemDark.set(mq.matches);
    // 系统切换时实时跟随（Android WebView 的 uiMode 变化会触发；Activity 已声明 configChanges=uiMode）
    mq.addEventListener('change', (e) => systemDark.set(e.matches));
  } catch { /* 不支持 matchMedia 时保持浅色 */ }
}

/**
 * 当前是否深色（解析后的结果）
 *
 * 对外仍是 boolean store：`$isDark` / `get(isDark)` 的所有既有用法都不用改。
 */
export const isDark = derived(
  [themeMode, systemDark],
  ([$mode, $system]) => ($mode === 'system' ? $system : $mode === 'dark')
);

/** 切换主题模式（设置界面 / 命令面板都走它） */
export function setThemeMode(mode: ThemeMode): void {
  themeMode.set(mode);
}

/** 系统当前解析出的明暗（给设置界面显示「跟随系统 · 当前深色」用） */
export const systemPrefersDark = derived(systemDark, ($v) => $v);

/** 移动端侧边栏显示状态 */
export const showSidebar = writable(false);

/** 设置弹窗显示状态 */
export const showSettings = writable(false);

/** 教程弹窗显示状态 */
export const showTutorial = writable(false);

/** Vditor README 编辑器主题 */
export const vditorTheme = writable<'auto' | 'light' | 'dark'>(safeGet('pm_vditor_theme') as 'auto' | 'light' | 'dark' || 'auto');
vditorTheme.subscribe(v => safeSet('pm_vditor_theme', v));

/** 命令面板触发的待执行动作，消费后应重置为 null */
export const pendingAction = writable<string | null>(null);

/** 流程图全屏模式显示状态 */
export const showFlowchart = writable(false);
