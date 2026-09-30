/**
 * `.pm` 数据格式：**版本 / 迁移 / 校验**
 *
 * 导入是最容易「静默变脏」的入口：旧实现只做 `JSON.parse` + 两个字段判空，
 * 畸形文件会半截落地。这里提供统一入口 `parsePmText()`：
 *
 * ```
 * 原文  →  体积检查  →  JSON.parse  →  迁移(detect+migrate)  →  校验(带字段路径)  →  PmFile
 * ```
 *
 * 设计原则：
 * - **版本驱动迁移**：以后改字段只在 `STEPS` 里加一步，不再到处写「兼容旧数据」
 * - **必需的少**：只校验会引发崩溃/数据错乱的字段（id/name/tasks 结构等）
 * - **容错的多**：可选字段缺失由迁移补默认值，不算错误
 * - **有上限**：防止畸形/超大文件把浏览器拖死（任务数、字符串长度、总体积）
 *
 * @module utils/pm-schema
 */

import type { PmFile, Task, Tag, Milestone, ChangelogEntry, StatusCategory } from '$lib/types';
import { createLegacyTaskGroup, legacyStatusCategory, stableId } from './task-status';

/** 合法循环频率（与 types/task.ts 的 RecurrenceFreq 保持一致） */
const FREQS = ['daily', 'weekly', 'monthly', 'yearly'];

/** 当前 `.pm` 结构版本（历史文件无此字段 → 视为 v1） */
export const CURRENT_PM_SCHEMA = 4;

/** 单个 `.pm` 文件体积上限（字符数）：32MB */
export const MAX_PM_CHARS = 32 * 1024 * 1024;
/** 任务数上限 */
export const MAX_TASKS = 20000;
/** 单个字符串字段长度上限 */
export const MAX_TEXT = 200_000;

export interface PmValidationError {
  /** 字段路径，如 `tasks[3].title` */
  path: string;
  /** 人类可读的原因 */
  message: string;
}

export type PmValidationResult =
  | { ok: true; pm: PmFile; warnings: string[] }
  | { ok: false; errors: PmValidationError[] };

/** 是否普通对象 */
function isObj(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

/** 校验字符串字段 */
function checkText(
  value: unknown,
  path: string,
  errors: PmValidationError[],
  { required = false, max = MAX_TEXT }: { required?: boolean; max?: number } = {}
): void {
  if (value === undefined || value === null) {
    if (required) errors.push({ path, message: '缺失或为 null' });
    return;
  }
  if (typeof value !== 'string') {
    errors.push({ path, message: `应为字符串，实际是 ${Array.isArray(value) ? 'array' : typeof value}` });
    return;
  }
  if (value.length > max) errors.push({ path, message: `长度 ${value.length} 超过上限 ${max}` });
}

/** 校验数组字段 */
function checkArray(value: unknown, path: string, errors: PmValidationError[], max: number): unknown[] {
  if (!Array.isArray(value)) {
    errors.push({ path, message: `应为数组，实际是 ${value === null ? 'null' : typeof value}` });
    return [];
  }
  if (value.length > max) errors.push({ path, message: `元素数 ${value.length} 超过上限 ${max}` });
  return value;
}

/**
 * 校验一个已经 `JSON.parse` 过的 `.pm` 对象
 *
 * @param raw - 解析后的对象
 * @param opts - 可选：来源描述（用于错误信息）
 */
export function validatePm(raw: unknown, opts: { source?: string } = {}): PmValidationResult {
  const errors: PmValidationError[] = [];
  const warnings: string[] = [];
  const where = opts.source ? `（来自 ${opts.source}）` : '';

  if (!isObj(raw)) {
    return { ok: false, errors: [{ path: '', message: `顶层不是对象${where}` }] };
  }

  if (typeof raw.version !== 'string' || !raw.version.trim()) {
    errors.push({ path: 'version', message: '缺失或不是字符串' });
  }
  if (raw.schema_version !== CURRENT_PM_SCHEMA) {
    errors.push({ path: 'schema_version', message: `应为 ${CURRENT_PM_SCHEMA}` });
  }

  // ── project ────────────────────────────────────────────────────────────────
  if (!isObj(raw.project)) {
    errors.push({ path: 'project', message: `缺失或不是对象${where}` });
  } else {
    const p = raw.project;
    checkText(p.name, 'project.name', errors, { required: true, max: 500 });
    checkText(p.description, 'project.description', errors);
    checkText(p.color, 'project.color', errors, { max: 64 });
    checkText(p.created_at, 'project.created_at', errors, { max: 64 });
    checkText(p.updated_at, 'project.updated_at', errors, { max: 64 });
    if (p.template !== undefined) checkText(p.template, 'project.template', errors, { max: 32 });
    if (p.id !== undefined) checkText(p.id, 'project.id', errors, { max: 200 });
    checkText(p.default_task_group_id, 'project.default_task_group_id', errors, { required: true, max: 200 });
    if (p.storage !== undefined && !isObj(p.storage)) {
      errors.push({ path: 'project.storage', message: '应为对象' });
    }
  }

  // ── task_groups / statuses ─────────────────────────────────────────────────
  const groups = checkArray(raw.task_groups, 'task_groups', errors, 2000);
  const groupIds = new Set<string>();
  const statusIds = new Set<string>();
  const statusGroup = new Map<string, string>();
  const statusCategory = new Map<string, StatusCategory>();
  groups.forEach((g, i) => {
    const at = `task_groups[${i}]`;
    if (!isObj(g)) { errors.push({ path: at, message: '应为对象' }); return; }
    checkText(g.id, `${at}.id`, errors, { required: true, max: 200 });
    checkText(g.name, `${at}.name`, errors, { required: true, max: 500 });
    checkText(g.initial_status_id, `${at}.initial_status_id`, errors, { required: true, max: 200 });
    checkText(g.completion_status_id, `${at}.completion_status_id`, errors, { required: true, max: 200 });
    const groupId = typeof g.id === 'string' ? g.id : '';
    if (groupId && groupIds.has(groupId)) errors.push({ path: `${at}.id`, message: '任务组 ID 重复' });
    if (groupId) groupIds.add(groupId);
    const statuses = checkArray(g.statuses, `${at}.statuses`, errors, 500);
    if (statuses.length === 0) errors.push({ path: `${at}.statuses`, message: '任务组至少需要一个状态' });
    const localIds = new Set<string>();
    statuses.forEach((s, j) => {
      const sat = `${at}.statuses[${j}]`;
      if (!isObj(s)) { errors.push({ path: sat, message: '应为对象' }); return; }
      checkText(s.id, `${sat}.id`, errors, { required: true, max: 200 });
      checkText(s.name, `${sat}.name`, errors, { required: true, max: 500 });
      checkText(s.color, `${sat}.color`, errors, { required: true, max: 64 });
      if (!['todo', 'active', 'done', 'cancelled'].includes(String(s.category))) {
        errors.push({ path: `${sat}.category`, message: '应为 todo/active/done/cancelled' });
      }
      const statusId = typeof s.id === 'string' ? s.id : '';
      if (statusId && statusIds.has(statusId)) errors.push({ path: `${sat}.id`, message: '状态 ID 在项目内重复' });
      if (statusId) {
        statusIds.add(statusId);
        localIds.add(statusId);
        statusGroup.set(statusId, groupId);
        statusCategory.set(statusId, s.category as StatusCategory);
      }
    });
    if (typeof g.initial_status_id === 'string' && !localIds.has(g.initial_status_id)) {
      errors.push({ path: `${at}.initial_status_id`, message: '必须指向当前任务组的状态' });
    }
    if (typeof g.completion_status_id === 'string') {
      if (!localIds.has(g.completion_status_id)) errors.push({ path: `${at}.completion_status_id`, message: '必须指向当前任务组的状态' });
      else if (statusCategory.get(g.completion_status_id) !== 'done') errors.push({ path: `${at}.completion_status_id`, message: '完成状态的类别必须为 done' });
    }
  });
  const projectRecord = isObj(raw.project) ? raw.project : null;
  const defaultGroupId = projectRecord && typeof projectRecord.default_task_group_id === 'string' ? projectRecord.default_task_group_id : '';
  const defaultGroup = groups.find((g): g is Record<string, unknown> => isObj(g) && g.id === defaultGroupId);
  if (defaultGroupId && !defaultGroup) errors.push({ path: 'project.default_task_group_id', message: '指向不存在的任务组' });
  if (defaultGroup && defaultGroup.archived === true) errors.push({ path: 'project.default_task_group_id', message: '不能指向已归档任务组' });
  if (!groups.some(g => isObj(g) && g.archived !== true)) errors.push({ path: 'task_groups', message: '项目至少需要一个未归档任务组' });

  // ── tasks ──────────────────────────────────────────────────────────────────
  const tasks = checkArray(raw.tasks, 'tasks', errors, MAX_TASKS);
  const taskIds = new Set<string>();
  tasks.forEach((t, i) => {
    const at = `tasks[${i}]`;
    if (!isObj(t)) {
      errors.push({ path: at, message: '应为对象' });
      return;
    }
    const task = t as Record<string, unknown>;
    checkText(task.id, `${at}.id`, errors, { required: true, max: 200 });
    checkText(task.title, `${at}.title`, errors, { required: true, max: 2000 });
    checkText(task.description, `${at}.description`, errors);
    checkText(task.task_group_id, `${at}.task_group_id`, errors, { required: true, max: 200 });
    checkText(task.status_id, `${at}.status_id`, errors, { required: true, max: 200 });
    const taskId = typeof task.id === 'string' ? task.id : '';
    if (taskId && taskIds.has(taskId)) errors.push({ path: `${at}.id`, message: '任务 ID 重复' });
    if (taskId) taskIds.add(taskId);
    if (typeof task.task_group_id === 'string' && !groupIds.has(task.task_group_id)) {
      errors.push({ path: `${at}.task_group_id`, message: '指向不存在的任务组' });
    }
    if (typeof task.status_id === 'string' && statusGroup.get(task.status_id) !== task.task_group_id) {
      errors.push({ path: `${at}.status_id`, message: '状态不属于任务指定的任务组' });
    }
    const category = typeof task.status_id === 'string' ? statusCategory.get(task.status_id) : undefined;
    if (category === 'done' && typeof task.completed_at !== 'string') {
      errors.push({ path: `${at}.completed_at`, message: 'done 类别任务必须有完成时间' });
    }
    if (category !== 'done' && task.completed_at !== null) {
      errors.push({ path: `${at}.completed_at`, message: '仅 done 类别任务可以有完成时间' });
    }
    if (task.dependencies !== undefined) checkArray(task.dependencies, `${at}.dependencies`, errors, 500);
    if (task.recurrence !== undefined && task.recurrence !== null) {
      const rec = task.recurrence;
      if (!isObj(rec)) {
        errors.push({ path: `${at}.recurrence`, message: '应为对象或 null' });
      } else {
        const freq = rec.freq;
        if (!FREQS.includes(String(freq))) {
          errors.push({ path: `${at}.recurrence.freq`, message: `缺失或非法（应为 ${FREQS.join('/')}）` });
        }
        if (rec.interval !== undefined && (typeof rec.interval !== 'number' || !(rec.interval >= 1))) {
          errors.push({ path: `${at}.recurrence.interval`, message: '应为 ≥1 的数字' });
        }
        if (rec.end !== undefined && !['never', 'count', 'until'].includes(String(rec.end))) {
          errors.push({ path: `${at}.recurrence.end`, message: '应为 never/count/until' });
        }
        if (rec.count !== undefined && (typeof rec.count !== 'number' || rec.count < 0)) {
          errors.push({ path: `${at}.recurrence.count`, message: '应为 ≥0 的数字' });
        }
        if (rec.until !== undefined) checkText(rec.until, `${at}.recurrence.until`, errors, { max: 32 });
        if (rec.byWeekday !== undefined) checkArray(rec.byWeekday, `${at}.recurrence.byWeekday`, errors, 7);
      }
    }
    if (task.subtasks !== undefined) checkArray(task.subtasks, `${at}.subtasks`, errors, 2000);
    if (task.comments !== undefined) checkArray(task.comments, `${at}.comments`, errors, 5000);
    if (task.tags !== undefined) checkArray(task.tags, `${at}.tags`, errors, 200);
  });
  tasks.forEach((t, i) => {
    if (!isObj(t) || !Array.isArray(t.dependencies)) return;
    for (let j = 0; j < t.dependencies.length; j++) {
      const dep = t.dependencies[j];
      if (!isObj(dep) || typeof dep.taskId !== 'string') continue;
      if (!taskIds.has(dep.taskId)) errors.push({ path: `tasks[${i}].dependencies[${j}].taskId`, message: '依赖目标不存在' });
    }
  });
  const dependencyGraph = new Map<string, string[]>();
  tasks.forEach((task) => {
    if (!isObj(task) || typeof task.id !== 'string') return;
    const targets = Array.isArray(task.dependencies)
      ? task.dependencies
          .filter((dep): dep is Record<string, unknown> => isObj(dep) && typeof dep.taskId === 'string')
          .map(dep => dep.taskId as string)
          .filter(id => taskIds.has(id))
      : [];
    dependencyGraph.set(task.id, targets);
  });
  const visiting = new Set<string>();
  const visited = new Set<string>();
  const hasCycleFrom = (taskId: string): boolean => {
    if (visiting.has(taskId)) return true;
    if (visited.has(taskId)) return false;
    visiting.add(taskId);
    for (const dependencyId of dependencyGraph.get(taskId) ?? []) {
      if (hasCycleFrom(dependencyId)) return true;
    }
    visiting.delete(taskId);
    visited.add(taskId);
    return false;
  };
  for (const taskId of dependencyGraph.keys()) {
    if (hasCycleFrom(taskId)) {
      errors.push({ path: 'tasks', message: '任务依赖不能形成循环' });
      break;
    }
  }

  // ── tags / milestones / changelog ─────────────────────────────────────────
  if (raw.tags !== undefined) {
    checkArray(raw.tags, 'tags', errors, 5000).forEach((t, i) => {
      if (!isObj(t)) { errors.push({ path: `tags[${i}]`, message: '应为对象' }); return; }
      checkText((t as unknown as Tag).name, `tags[${i}].name`, errors, { required: true, max: 200 });
    });
  }
  if (raw.milestones !== undefined) {
    checkArray(raw.milestones, 'milestones', errors, 2000).forEach((m, i) => {
      if (!isObj(m)) { errors.push({ path: `milestones[${i}]`, message: '应为对象' }); return; }
      checkText((m as unknown as Milestone).title, `milestones[${i}].title`, errors, { required: true, max: 500 });
      checkText((m as unknown as Milestone).date, `milestones[${i}].date`, errors, { required: true, max: 64 });
    });
  }
  if (raw.changelog !== undefined) {
    checkArray(raw.changelog, 'changelog', errors, 20000).forEach((c, i) => {
      if (!isObj(c)) { errors.push({ path: `changelog[${i}]`, message: '应为对象' }); return; }
      checkText((c as unknown as ChangelogEntry).version, `changelog[${i}].version`, errors, { required: true, max: 32 });
    });
  }

  // ── legacy template（可选；V1.1 只透传，不生成）───────────────────────────
  if (raw.readme_file !== undefined) {
    checkText(raw.readme_file, 'readme_file', errors, { max: 1000 });
  }
  if (raw.template !== undefined) {
    if (!isObj(raw.template)) {
      errors.push({ path: 'template', message: '应为对象' });
    } else {
      const template = raw.template as Record<string, unknown>;
      if (template.dirs !== undefined) {
        checkArray(template.dirs, 'template.dirs', errors, 20000)
          .forEach((value, index) => checkText(value, `template.dirs[${index}]`, errors, { max: 1000 }));
      }
      if (template.files !== undefined) {
        checkArray(template.files, 'template.files', errors, 20000)
          .forEach((value, index) => checkText(value, `template.files[${index}]`, errors, { max: 1000 }));
      }
      if (template.file_contents !== undefined) {
        if (!isObj(template.file_contents)) {
          errors.push({ path: 'template.file_contents', message: '应为对象（路径 → 内容）' });
        } else {
          for (const [path, content] of Object.entries(template.file_contents)) {
            checkText(content, `template.file_contents[${JSON.stringify(path)}]`, errors);
          }
        }
      }
    }
  }

  if (errors.length > 0) return { ok: false, errors };

  // 类型已在上面校验过，这里做一次安全转型（缺的可选字段由 migratePm 补）
  const pm = raw as unknown as PmFile;
  if (!Array.isArray(pm.tasks)) pm.tasks = [] as Task[];
  return { ok: true, pm, warnings };
}

/**
 * 解析 `.pm` JSON 字符串：体积检查 → JSON.parse → **校验（不迁移）**
 *
 * ⚠️ 导入请用 {@link parsePmText}（它会先迁移，老文件才不会被判为非法）。
 *
 * @param text - 文件内容
 * @param source - 来源描述（文件名），用于错误信息
 */
export function parsePm(text: string, source?: string): PmValidationResult {
  const label = source ? `（来自 ${source}）` : '';
  if (typeof text !== 'string' || text.trim() === '') {
    return { ok: false, errors: [{ path: '', message: `文件为空${label}` }] };
  }
  if (text.length > MAX_PM_CHARS) {
    return {
      ok: false,
      errors: [{ path: '', message: `文件过大（${(text.length / 1024 / 1024).toFixed(1)}MB，上限 32MB）${label}` }],
    };
  }
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch (e) {
    return {
      ok: false,
      errors: [{ path: '', message: `JSON 解析失败：${e instanceof Error ? e.message : String(e)}${label}` }],
    };
  }
  return validatePm(raw, { source });
}

/** 把错误列表压成一行可读文案（只列前几条） */
export function formatPmErrors(errors: PmValidationError[], limit = 3): string {
  const shown = errors.slice(0, limit).map((e) => (e.path ? `${e.path}：${e.message}` : e.message));
  const more = errors.length > limit ? ` …另有 ${errors.length - limit} 处问题` : '';
  return shown.join('；') + more;
}


// ─── 迁移 ───────────────────────────────────────────────────────────────────

/** 迁移步骤：把 v(i+1) 升到 v(i+2) */
type MigrationStep = (raw: Record<string, unknown>) => Record<string, unknown>;

/** 推断对象的结构版本（无字段 → v1） */
export function detectSchemaVersion(raw: unknown): number {
  if (typeof raw !== 'object' || raw === null) return CURRENT_PM_SCHEMA;
  const v = (raw as Record<string, unknown>).schema_version;
  const n = typeof v === 'number' ? v : parseInt(String(v ?? ''), 10);
  return Number.isFinite(n) && n > 0 ? n : 1;
}

/** v1 → v2：补 `schema_version`，并补齐 v2 之前可能缺失的数组/可空字段 */
const v1_to_v2: MigrationStep = (raw) => {
  const next: Record<string, unknown> = { ...raw, schema_version: 2 };

  // ⚠️ 只补「缺失/空」的字段，**不纠正类型**：
  // 把 `tasks: {}` 默默改成 `[]` 等于把用户数据吞了，应当交给校验器报错。
  const fillArray = (v: unknown): unknown => (v === undefined || v === null ? [] : v);
  next.tasks = fillArray(next.tasks);
  next.tags = fillArray(next.tags);
  next.milestones = fillArray(next.milestones);
  next.changelog = fillArray(next.changelog);

  // 任务：补齐可选字段的类型稳定默认值（避免下游 `t.subtasks.filter` 之类炸掉）
  if (Array.isArray(next.tasks)) {
    next.tasks = (next.tasks as unknown[]).map((t) => {
      if (typeof t !== 'object' || t === null) return t;
      const task = t as Record<string, unknown>;
      const fillArr2 = (v: unknown): unknown => (v === undefined || v === null ? [] : v);
      return {
        ...task,
        tags: fillArr2(task.tags),
        dependencies: fillArr2(task.dependencies),
        subtasks: fillArr2(task.subtasks),
        comments: fillArr2(task.comments),
        description: typeof task.description === 'string' ? task.description : (task.description ?? ''),
        due_date: task.due_date ?? null,
        due_time: task.due_time ?? null,
        start_offset: task.start_offset ?? null,
        tracked_start: task.tracked_start ?? null,
        reminder: task.reminder ?? null,
      };
    });
  }
  return next;
};

/** v2 → v3：任务补 `recurrence: null`（**只补缺失，不动已存在的值**） */
const v2_to_v3: MigrationStep = (raw) => {
  const next: Record<string, unknown> = { ...raw, schema_version: 3 };
  if (Array.isArray(next.tasks)) {
    next.tasks = (next.tasks as unknown[]).map((t) => {
      if (typeof t !== 'object' || t === null) return t;
      const task = t as Record<string, unknown>;
      // ⚠️ 只处理「没有这个字段」的情况：`recurrence: {}` 这类类型错误要留给校验器报错，
      //    迁移里默默改成 null 等于吞掉用户/第三方的错误数据。
      if ('recurrence' in task) return task;
      return { ...task, recurrence: null };
    });
  }
  return next;
};

/** v3 → v4：把项目的固定/自定义状态统一收进一个确定性“默认任务组”。 */
const v3_to_v4: MigrationStep = (raw) => {
  const project = isObj(raw.project) ? raw.project : {};
  const projectKey = typeof project.id === 'string' && project.id.trim()
    ? project.id
    : stableId('project', `${String(project.name ?? '')}:${String(project.created_at ?? '')}`);
  const values: string[] = [];
  if (Array.isArray(project.kanban_columns)) {
    for (const value of project.kanban_columns) if (typeof value === 'string' && value.trim()) values.push(value);
  }
  if (Array.isArray(raw.tasks)) {
    for (const item of raw.tasks) {
      if (isObj(item) && typeof item.status === 'string' && item.status.trim()) values.push(item.status);
    }
  }
  if (values.length === 0) values.push('todo', 'in_progress', 'done');
  const group = createLegacyTaskGroup(projectKey, values);
  const statusFor = (legacy: string) => group.statuses.find(s => s.id === stableId('status', `v4:status:${projectKey}:${legacy}`))
    ?? group.statuses.find(s => s.category === legacyStatusCategory(legacy))
    ?? group.statuses[0];
  const tasks = Array.isArray(raw.tasks) ? raw.tasks.map(item => {
    if (!isObj(item)) return item;
    const legacy = typeof item.status === 'string' ? item.status : 'todo';
    const status = statusFor(legacy);
    const { status: _legacyStatus, ...rest } = item;
    const completedAt = status.category === 'done'
      ? (typeof item.completed_at === 'string' ? item.completed_at : String(item.updated_at ?? item.created_at ?? project.updated_at ?? project.created_at ?? new Date(0).toISOString()))
      : null;
    return {
      ...rest,
      task_group_id: group.id,
      status_id: status.id,
      completed_at: completedAt,
    };
  }) : raw.tasks;
  return {
    ...raw,
    schema_version: 4,
    project: { ...project, default_task_group_id: group.id },
    task_groups: [group],
    tasks,
  };
};

/** 迁移链：索引 i 负责把 v(i+1) 升到 v(i+2) */
const STEPS: MigrationStep[] = [v1_to_v2, v2_to_v3, v3_to_v4];

/**
 * 把任意版本的 `.pm` 对象迁移到当前版本
 *
 * @returns `{ pm, migratedFrom }`；`migratedFrom` 为 null 表示本来就是当前版本
 */
export function migratePm(raw: unknown): { pm: Record<string, unknown>; migratedFrom: number | null } {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    return { pm: {}, migratedFrom: null };
  }
  const from = detectSchemaVersion(raw);
  let current = raw as Record<string, unknown>;
  if (from >= CURRENT_PM_SCHEMA) {
    return { pm: current, migratedFrom: null };
  }
  for (let v = from; v < CURRENT_PM_SCHEMA; v++) {
    const step = STEPS[v - 1];
    if (!step) break;
    current = step(current);
  }
  current.schema_version = CURRENT_PM_SCHEMA;
  return { pm: current, migratedFrom: from };
}

// ─── 统一入口 ───────────────────────────────────────────────────────────────

export interface PmParseOk {
  ok: true;
  pm: PmFile;
  /** 迁移来源版本（null = 本来就是当前版本） */
  migratedFrom: number | null;
  warnings: string[];
}

/**
 * 解析 `.pm` 文本：体积检查 → JSON.parse → 迁移 → 校验
 *
 * **所有导入路径都应该走这里**（本地文件、服务器拉取、备份恢复、demo 加载）。
 *
 * @param text - 文件内容
 * @param source - 来源描述（文件名 / 项目名），用于错误信息
 */
export function parsePmText(text: string, source?: string): PmValidationResult & { migratedFrom?: number | null } {
  const label = source ? `（来自 ${source}）` : '';
  if (typeof text !== 'string' || text.trim() === '') {
    return { ok: false, errors: [{ path: '', message: `文件为空${label}` }] };
  }
  if (text.length > MAX_PM_CHARS) {
    return {
      ok: false,
      errors: [{ path: '', message: `文件过大（${(text.length / 1024 / 1024).toFixed(1)}MB，上限 32MB）${label}` }],
    };
  }
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch (e) {
    return {
      ok: false,
      errors: [{ path: '', message: `JSON 解析失败：${e instanceof Error ? e.message : String(e)}${label}` }],
    };
  }
  const incomingSchema = detectSchemaVersion(raw);
  if (incomingSchema > CURRENT_PM_SCHEMA) {
    return {
      ok: false,
      errors: [{ path: 'schema_version', message: `该项目使用 schema v${incomingSchema}，当前客户端仅支持 v${CURRENT_PM_SCHEMA}，请升级客户端` }],
    };
  }
  const migrationWarnings: string[] = [];
  if (incomingSchema < 4 && isObj(raw)) {
    const project = isObj(raw.project) ? raw.project : null;
    const declaredStatuses = project && Array.isArray(project.kanban_columns)
      ? new Set(project.kanban_columns.filter((value): value is string => typeof value === 'string'))
      : null;
    const recognized = new Set([
      'todo', 'inprogress', 'active', 'doing', 'done', 'complete', 'completed', 'finished',
      'cancelled', 'canceled', 'cancel', '待办', '待处理', '进行中', '处理中', '开发中',
      '修复中', '评审中', '已完成', '完成', '已关闭', '关闭', '已取消', '取消',
    ]);
    const warned = new Set<string>();
    for (const task of Array.isArray(raw.tasks) ? raw.tasks : []) {
      if (!isObj(task) || typeof task.status !== 'string' || !task.status.trim()) continue;
      const status = task.status.trim();
      const normalized = status.toLowerCase().replace(/[\s_-]+/g, '');
      const unknown = declaredStatuses ? !declaredStatuses.has(status) : !recognized.has(normalized);
      if (unknown && !warned.has(status)) {
        warned.add(status);
        migrationWarnings.push(`旧任务状态“${status}”未在项目状态定义中声明，已保留为待处理类别`);
      }
    }
  }
  const { pm, migratedFrom } = migratePm(raw);
  const result = validatePm(pm, { source });
  if (!result.ok) return result;
  return { ...result, warnings: [...migrationWarnings, ...result.warnings], migratedFrom };
}
