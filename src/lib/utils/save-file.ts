/**
 * 跨平台「保存文本文件」
 *
 * 三个平台各有各的正确做法，之前散落在各处（有的地方用 Blob 下载，
 * 在 Android WebView 里会**静默失败**）：
 *
 * | 平台 | 做法 |
 * |---|---|
 * | 纯浏览器 | `Blob` + `<a download>` |
 * | Android | `tauri-plugin-android-fs` 的 `showSaveFilePicker` + `writeTextFile`（系统「保存到」对话框） |
 * | 桌面 | `plugin-dialog` 的 `save()` 选路径 → Rust `save_text_file` |
 *
 * @module utils/save-file
 */

import { isAndroidPlatform, isTauriEnv } from './platform';

export type SaveOutcomeKind = 'saved' | 'cancelled' | 'error';

export interface SaveOutcome {
  kind: SaveOutcomeKind;
  /** 落盘路径 / 展示用位置 */
  path?: string;
  /** Android 的 content:// URI（用于「打开」；类型由 android-fs 插件决定） */
  uri?: unknown;
  /** 失败原因 */
  message?: string;
}

/** 纯浏览器下载 */
function browserDownload(text: string, name: string, mime: string): void {
  const blob = new Blob([text], { type: mime });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  a.click();
  URL.revokeObjectURL(a.href);
}

/**
 * 保存文本文件（自动按平台分流）
 *
 * @param name - 建议文件名（含扩展名）
 * @param text - 文件内容
 * @param mime - MIME 类型
 */
export async function saveTextFile(name: string, text: string, mime = 'application/json'): Promise<SaveOutcome> {
  try {
    // ── 纯浏览器 ──────────────────────────────────────────────────────────
    if (!isTauriEnv) {
      browserDownload(text, name, mime);
      return { kind: 'saved', path: name };
    }

    // ── Android：系统「保存到」对话框（Blob 下载在这里无效）──────────────
    if (isAndroidPlatform) {
      const A = await import('tauri-plugin-android-fs-api');
      const uri = await A.showSaveFilePicker(name, mime);
      if (!uri) return { kind: 'cancelled' };
      await A.writeTextFile(uri, text);
      let path = name;
      try { path = (await A.getName(uri)) || name; } catch { /* ignore */ }
      return { kind: 'saved', path, uri };
    }

    // ── 桌面：原生「另存为」────────────────────────────────────────────────
    const { save } = await import('@tauri-apps/plugin-dialog');
    const ext = name.split('.').pop() ?? 'txt';
    const picked = await save({ defaultPath: name, filters: [{ name: ext.toUpperCase(), extensions: [ext] }] });
    if (!picked) return { kind: 'cancelled' };
    const { invoke } = await import('@tauri-apps/api/core');
    await invoke('save_text_file', { path: String(picked), content: text });
    return { kind: 'saved', path: String(picked) };
  } catch (e) {
    return { kind: 'error', message: e instanceof Error ? e.message : String(e) };
  }
}
