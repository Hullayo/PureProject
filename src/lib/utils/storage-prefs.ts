/**
 * 「本地文件夹偏好」本机记录（用于丢失检测与一键恢复）
 *
 * `Project.storage` 不写入 `.pm`，只存在本机。万一因跨设备恢复、ID 变化、误操作等
 * 原因丢失，我们希望还能**知道它原本用的是哪个文件夹**，从而在界面上给出可见提示
 * 并提供一键恢复，而不是静默回退为「应用内部存储」。
 *
 * 这里把「用过的本地文件夹配置」按**项目名**（归一化后）记录在一个独立键
 * `pm_storage_prefs`（走 `safeSetItem()`，不直接写 localStorage）。
 *
 * 注意：这只是**提示/恢复**用的旁路记录，权威来源始终是 `Project.storage`；
 * 三级继承逻辑见 `utils/storage-inherit.ts`，本模块不参与继承决策。
 *
 * @module utils/storage-prefs
 */

import { writable, get } from 'svelte/store';
import type { ProjectStorage } from '$lib/types';
import { safeGetRaw, safeSetItem } from '$lib/stores/storage-health';
import { normalizeProjectName } from './storage-inherit';

/** localStorage 键名 */
const PREFS_KEY = 'pm_storage_prefs';

/** 最多保留的记录条数（按插入顺序淘汰最旧，避免无限增长） */
const MAX_ENTRIES = 200;

type PrefMap = Record<string, ProjectStorage>;

/** 从 localStorage 读取（失败返回空对象，不抛） */
function load(): PrefMap {
  const raw = safeGetRaw(PREFS_KEY);
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return {};
    const out: PrefMap = {};
    for (const [k, v] of Object.entries(parsed as Record<string, unknown>)) {
      const s = v as Partial<ProjectStorage> | null;
      if (s && typeof s === 'object' && (s.type === 'local' || s.type === 'server')) {
        out[k] = s as ProjectStorage;
      }
    }
    return out;
  } catch {
    return {};
  }
}

/** 当前记录（按归一化项目名索引）；组件可直接 `$storagePrefs` 消费 */
export const storagePrefs = writable<PrefMap>(load());

/**
 * 记住一个项目的本地文件夹配置（同路径/同归属时跳过写入）
 *
 * 由 `stores/project.ts` 在持久化带 `storage` 的项目时调用。
 */
export function rememberStoragePref(name: string, storage: ProjectStorage): void {
  const key = normalizeProjectName(name);
  if (!key) return;
  if (storage.type !== 'local' || !storage.path) return;

  const current = get(storagePrefs);
  const prev = current[key];
  if (prev && prev.type === storage.type && prev.path === storage.path && prev.ownerDeviceId === storage.ownerDeviceId) {
    return;
  }

  const keys = Object.keys(current);
  const next: PrefMap = { ...current, [key]: storage };
  if (keys.length >= MAX_ENTRIES && !(key in current)) {
    // 简单淘汰：删掉第一条（对象键保持插入顺序）
    delete next[keys[0]];
  }

  storagePrefs.set(next);
  safeSetItem(PREFS_KEY, JSON.stringify(next), '本地文件夹偏好', false);
}

/** 读取某个项目名对应的历史本地文件夹配置 */
export function getStoragePref(name: string): ProjectStorage | undefined {
  const key = normalizeProjectName(name);
  if (!key) return undefined;
  return get(storagePrefs)[key];
}

/** 忘记某个项目名的记录（用户主动清除 / 已改回应用内部存储） */
export function forgetStoragePref(name: string): void {
  const key = normalizeProjectName(name);
  if (!key) return;
  const current = get(storagePrefs);
  if (!(key in current)) return;
  const next = { ...current };
  delete next[key];
  storagePrefs.set(next);
  safeSetItem(PREFS_KEY, JSON.stringify(next), '本地文件夹偏好', false);
}

/** 记录条数（测试/展示用） */
export function storagePrefCount(): number {
  return Object.keys(get(storagePrefs)).length;
}
