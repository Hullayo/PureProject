/**
 * 任务字段可见性（简洁 / 完整模式）
 *
 * 「简洁任务模型」不改 `Task` 类型、不动 `.pm` 校验：所有字段依旧存在，
 * 这里只决定 UI **渲不渲染**。切回 `full` 立即恢复，数据从不删除。
 *
 * 配置落在本机 `localStorage: pm_task_fields`：
 * ```jsonc
 * { "mode": "simple", "simple": ["title","status","priority","color","due_date","subtasks","recurrence"] }
 * ```
 *
 * 组件消费方式：`{#if $visibleFields.has('description')}`。
 *
 * @module stores/task-fields
 *
 * @example
 * ```svelte
 * <script>
 *   import { visibleFields, isFieldVisible } from '$lib/stores/task-fields';
 * </script>
 * {#if $visibleFields.has('tags')}<TaskDetailTags />{/if}
 * <p>description visible: {isFieldVisible('description')}</p>
 * ```
 */

import { writable, derived, get } from 'svelte/store';
import { safeGetRaw, safeSetItem } from './storage-health';

/** localStorage 键 */
export const TASK_FIELDS_KEY = 'pm_task_fields';

/** 字段模式：完整 / 简洁 */
export type TaskFieldMode = 'full' | 'simple';

/** 可配置的任务字段 */
export type TaskField =
  | 'title'
  | 'task_group'
  | 'status'
  | 'priority'
  | 'color'
  | 'due_date'
  | 'subtasks'
  | 'recurrence'
  | 'description'
  | 'tags'
  | 'comments'
  | 'tracked_start'
  | 'dependencies'
  | 'start_offset'
  | 'reminder';

/** 全部字段（顺序即设置页展示顺序） */
export const ALL_TASK_FIELDS: TaskField[] = [
  'title', 'task_group', 'status', 'priority', 'color', 'due_date', 'subtasks', 'recurrence',
  'description', 'tags', 'comments', 'tracked_start', 'dependencies', 'start_offset',
  'reminder',
];

/** 默认「核心字段」集（simple 模式下默认可见） */
export const DEFAULT_SIMPLE_FIELDS: TaskField[] = [
  'title', 'task_group', 'status', 'priority', 'color', 'due_date', 'subtasks', 'recurrence',
];

/** 永远可见、不可关闭的字段 */
export const ALWAYS_VISIBLE_FIELDS: TaskField[] = ['title', 'task_group'];

/** 配置结构 */
export interface TaskFieldsConfig {
  mode: TaskFieldMode;
  simple: TaskField[];
}

/** 归一化：只保留合法字段、去重、title 恒在 */
function normalizeSimple(list: unknown): TaskField[] {
  const arr = Array.isArray(list) ? list : [];
  const set = new Set<TaskField>();
  for (const f of arr) {
    if (ALL_TASK_FIELDS.includes(f as TaskField)) set.add(f as TaskField);
  }
  for (const f of ALWAYS_VISIBLE_FIELDS) set.add(f);
  return ALL_TASK_FIELDS.filter((f) => set.has(f));
}

/** 读取配置（失败回退 full） */
function load(): TaskFieldsConfig {
  const raw = safeGetRaw(TASK_FIELDS_KEY);
  if (!raw) return { mode: 'full', simple: [...DEFAULT_SIMPLE_FIELDS] };
  try {
    const parsed = JSON.parse(raw) as Partial<TaskFieldsConfig>;
    const mode: TaskFieldMode = parsed?.mode === 'simple' ? 'simple' : 'full';
    return { mode, simple: normalizeSimple(parsed?.simple ?? DEFAULT_SIMPLE_FIELDS) };
  } catch {
    return { mode: 'full', simple: [...DEFAULT_SIMPLE_FIELDS] };
  }
}

/** 任务字段配置（writable） */
export const taskFields = writable<TaskFieldsConfig>(load());

// 变更即持久化（notify=false：这是设置项，不值得打扰）
taskFields.subscribe((cfg) => {
  safeSetItem(TASK_FIELDS_KEY, JSON.stringify(cfg), '任务字段设置', false);
});

/**
 * 当前可见字段集合（派生）
 *
 * - `full`：全部字段
 * - `simple`：仅 `cfg.simple`（并始终包含 `title`）
 */
export const visibleFields = derived(taskFields, ($cfg): Set<TaskField> => {
  if ($cfg.mode === 'full') return new Set(ALL_TASK_FIELDS);
  const set = new Set<TaskField>($cfg.simple);
  for (const f of ALWAYS_VISIBLE_FIELDS) set.add(f);
  return set;
});

/** 判断某字段当前是否可见（脚本中调用；模板中建议用 `$visibleFields.has()`） */
export function isFieldVisible(field: TaskField): boolean {
  const cfg = get(taskFields);
  if (cfg.mode === 'full') return true;
  return ALWAYS_VISIBLE_FIELDS.includes(field) || cfg.simple.includes(field);
}

/** 切换模式 */
export function setFieldMode(mode: TaskFieldMode): void {
  taskFields.update((cfg) => ({ ...cfg, mode }));
}

/** 在 simple 模式下增删一个字段（title 不可关闭） */
export function toggleSimpleField(field: TaskField, on?: boolean): void {
  if (ALWAYS_VISIBLE_FIELDS.includes(field)) return;
  taskFields.update((cfg) => {
    const has = cfg.simple.includes(field);
    const want = on ?? !has;
    if (want === has) return cfg;
    return { ...cfg, simple: normalizeSimple(want ? [...cfg.simple, field] : cfg.simple.filter((f) => f !== field)) };
  });
}

/** 恢复默认核心字段集 */
export function resetSimpleFields(): void {
  taskFields.update((cfg) => ({ ...cfg, simple: [...DEFAULT_SIMPLE_FIELDS] }));
}
