/**
 * 运行平台判断
 *
 * 依赖 `vite.config.js` 的 `envPrefix` 暴露 `TAURI_ENV_*`（否则恒为空）。
 *
 * @module utils/platform
 */

const PLATFORM = (
  (import.meta.env as unknown as Record<string, string | undefined>).TAURI_ENV_PLATFORM ?? ''
).toLowerCase();

/** 当前主机平台原始值（`'windows'` / `'darwin'` / `'linux'` / `'android'` / `'ios'` / `''`） */
export const currentPlatform = PLATFORM;

/** 是否运行在 Tauri 壳里（非纯浏览器） */
export const isTauriEnv = PLATFORM !== '';

/** 桌面端（Windows / macOS / Linux） */
export const isDesktopPlatform = PLATFORM === 'windows' || PLATFORM === 'darwin' || PLATFORM === 'linux';

/** Android */
export const isAndroidPlatform = PLATFORM === 'android';

/** 移动端（Android / iOS） */
export const isMobilePlatform = PLATFORM === 'android' || PLATFORM === 'ios';
