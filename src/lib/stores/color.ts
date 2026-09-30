/**
 * 颜色方案管理 store
 *
 * 管理用户自定义颜色方案，持久化到 localStorage（键 pm_custom_colors）。
 * 提供默认内置颜色，支持添加、编辑、删除自定义颜色。
 *
 * @module stores/color
 */

import { writable, get } from 'svelte/store';
import type { ColorItem } from '$lib/types';
import { genId } from '$lib/utils/id';

const STORAGE_KEY = 'pm_custom_colors';

/** 内置默认颜色（不可删除） */
export const DEFAULT_COLORS: ColorItem[] = [
  { id: 'default-indigo', name: '朱砂', value: '#a33b32', builtin: true },
  { id: 'default-emerald', name: '松绿', value: '#3e7562', builtin: true },
  { id: 'default-amber', name: '藤黄', value: '#ad7622', builtin: true },
  { id: 'default-red', name: '绛红', value: '#b94a43', builtin: true },
  { id: 'default-violet', name: '烟紫', value: '#73576f', builtin: true },
  { id: 'default-cyan', name: '黛青', value: '#477477', builtin: true },
  { id: 'default-pink', name: '胭脂', value: '#a85769', builtin: true },
  { id: 'default-lime', name: '竹青', value: '#6e7f48', builtin: true },
  { id: 'default-orange', name: '赭石', value: '#a85e37', builtin: true },
  { id: 'default-teal', name: '孔雀青', value: '#2e6f68', builtin: true },
];

/** 安全读取 localStorage */
function safeGet(): string | null {
  try { return localStorage.getItem(STORAGE_KEY); } catch { return null; }
}

/** 安全写入 localStorage */
function safeSet(val: string): void {
  try { localStorage.setItem(STORAGE_KEY, val); } catch {}
}

/**
 * 从 localStorage 加载自定义颜色
 *
 * 解析失败或无数据时返回空数组。
 */
function loadCustomColors(): ColorItem[] {
  const raw = safeGet();
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw) as ColorItem[];
    if (Array.isArray(parsed)) return parsed.filter(c => c && typeof c.value === 'string');
  } catch {}
  return [];
}

/**
 * 获取当前完整颜色方案
 *
 * 内置颜色 + 自定义颜色合并，按 id 去重（自定义可覆盖内置同名 id，但通常不会）。
 */
function buildScheme(custom: ColorItem[]): ColorItem[] {
  const map = new Map<string, ColorItem>();
  for (const c of DEFAULT_COLORS) map.set(c.id, c);
  for (const c of custom) {
    if (c && typeof c.value === 'string' && typeof c.name === 'string') {
      map.set(c.id, { ...c, builtin: false });
    }
  }
  return Array.from(map.values());
}

/** 当前完整颜色方案 store */
export const colorScheme = writable<ColorItem[]>(buildScheme(loadCustomColors()));

/**
 * 持久化当前颜色方案到 localStorage
 *
 * 只保存非内置的自定义颜色。
 */
function persist(): void {
  const custom = get(colorScheme).filter(c => !c.builtin);
  safeSet(JSON.stringify(custom));
}

/**
 * 添加自定义颜色
 *
 * @param name - 语义名称
 * @param value - 十六进制色值
 */
export function addColor(name: string, value: string): void {
  const item: ColorItem = { id: genId(), name: name.trim() || value, value, builtin: false };
  colorScheme.update(list => [...list, item]);
  persist();
}

/**
 * 更新自定义颜色
 *
 * @param id - 颜色 ID
 * @param data - 要更新的字段
 */
export function updateColor(id: string, data: Partial<Pick<ColorItem, 'name' | 'value'>>): void {
  colorScheme.update(list => list.map(c => {
    if (c.id !== id || c.builtin) return c;
    return { ...c, ...data, name: data.name?.trim() || c.name };
  }));
  persist();
}

/**
 * 删除颜色（包括内置颜色）
 *
 * 删除后保证至少保留 MIN_KEEP_COLORS 个默认颜色。
 *
 * @param id - 要删除的颜色 ID
 */
export function deleteColor(id: string): void {
  colorScheme.update(list => {
    const filtered = list.filter(c => c.id !== id);
    // 保证至少保留 5 个颜色
    const minKeep = 5;
    if (filtered.length < minKeep) return list;
    return filtered;
  });
  persist();
}

/**
 * 获取指定色值对应的颜色名称
 *
 * @param value - 十六进制色值
 * @returns 颜色名称，找不到时返回色值本身
 */
export function getColorName(value: string | undefined | null): string {
  if (!value) return '';
  const found = get(colorScheme).find(c => c.value.toLowerCase() === value.toLowerCase());
  return found?.name || value;
}

/**
 * 获取当前所有可用色值
 *
 * 用于任务自动分配颜色等场景。
 */
export function getAllColorValues(): string[] {
  return get(colorScheme).map(c => c.value);
}

/**
 * 判断颜色是否可删除（至少保留 5 个颜色）
 *
 * @param id - 颜色 ID
 */
export function isColorDeletable(id: string): boolean {
  const list = get(colorScheme);
  if (list.length <= 5) return false;
  return list.some(c => c.id === id);
}

/**
 * 重置为默认颜色方案
 *
 * 删除所有自定义颜色。
 */
export function resetColors(): void {
  colorScheme.set([...DEFAULT_COLORS]);
  persist();
}
