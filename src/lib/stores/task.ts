/**
 * 任务 CRUD stores
 *
 * 管理任务的创建、更新、删除、状态切换、排序。
 * 管理子任务、依赖关系、评论、工时追踪、提醒。
 * 所有数据变更自动持久化到 localStorage。
 *
 * @module stores/task
 */

import { get } from 'svelte/store';
import type { Task, Dependency, Subtask, RecurrenceRule } from '$lib/types';
import { consumeRule, nextOccurrence, normalizeDate, normalizeRule } from '$lib/utils/recurrence';

import { genId } from '$lib/utils/id';
import { now, today } from '$lib/utils/date';
import { projects, persistProject } from './project';
import { activeTaskId } from './filter';
import { pushHistory } from './history';
import { runHook } from '$lib/plugins';
import {
  getCompletionStatus,
  getInitialStatus,
  getTaskGroup,
  getTaskStatus,
  isTaskClosed,
  isTaskCompleted,
  satisfiesDependency,
} from '$lib/utils/task-status';

/**
 * 调整任务在列表中的顺序
 *
 * @param projectId - 项目 ID
 * @param taskId - 要移动的任务 ID
 * @param newIndex - 目标位置索引
 * @param skipHistory - 跳过历史快照（用于「改状态 + 调位置」这类一次拖拽两步变更，只记一步撤销）
 */
export function reorderTask(projectId: string, taskId: string, newIndex: number, skipHistory = false): void {
  if (!skipHistory) pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const idx = p.tasks.findIndex(t => t.id === taskId);
    if (idx === -1 || idx === newIndex) return p;
    const tasks = [...p.tasks];
    const [moved] = tasks.splice(idx, 1);
    tasks.splice(newIndex, 0, moved);
    const updated = { ...p, tasks, updated_at: now() };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 在指定项目中创建新任务
 *
 * @param projectId - 项目 ID
 * @param title - 任务标题
 */
export interface CreateTaskInput {
  projectId: string;
  title: string;
  taskGroupId?: string;
  statusId?: string;
}

export function createTask(input: CreateTaskInput): string | null;
export function createTask(projectId: string, title: string, statusId?: string, taskGroupId?: string): string | null;
export function createTask(
  inputOrProjectId: CreateTaskInput | string,
  legacyTitle?: string,
  legacyStatusId?: string,
  legacyTaskGroupId?: string,
): string | null {
  const input: CreateTaskInput = typeof inputOrProjectId === 'string'
    ? { projectId: inputOrProjectId, title: legacyTitle ?? '', statusId: legacyStatusId, taskGroupId: legacyTaskGroupId }
    : inputOrProjectId;
  const project = get(projects).find(item => item.id === input.projectId);
  if (!project || !input.title.trim()) return null;
  const group = project.task_groups.find(item => item.id === (input.taskGroupId ?? project.default_task_group_id) && !item.archived);
  if (!group) return null;
  const status = group.statuses.find(item => item.id === input.statusId) ?? getInitialStatus(group);
  if (!status) return null;
  pushHistory();
  const t: Task = {
    id: genId(), title: input.title.trim(), description: '', task_group_id: group.id, status_id: status.id,
    completed_at: status.category === 'done' ? now() : null, priority: 'medium',
    color: pickTaskColor(input.projectId),
    tags: [], due_date: null, due_time: null, start_offset: null, dependencies: [], subtasks: [], comments: [],
    tracked_start: null, reminder: null, recurrence: null, created_at: now(), updated_at: now()
  };
  projects.update(list => list.map(p => {
    if (p.id !== input.projectId) return p;
    const updated = { ...p, tasks: [...p.tasks, t], updated_at: now() };
    persistProject(updated);
    return updated;
  }));
  runHook('onTaskCreate', { projectId: input.projectId, taskGroupId: group.id, task: t });
  return t.id;
}

/**
 * 复制任务组内某个状态下的全部任务。
 *
 * 新副本保留任务内容和排期，重建任务、子任务及评论 ID，并清空正在计时状态。
 * 同一批任务之间的依赖会自动指向对应副本；批次外依赖仍指向原任务。
 */
export function duplicateTasksInStatus(projectId: string, taskGroupId: string, statusId: string): number {
  const project = get(projects).find(item => item.id === projectId);
  const group = project?.task_groups.find(item => item.id === taskGroupId);
  const status = group?.statuses.find(item => item.id === statusId);
  if (!project || !group || !status) return 0;

  const sourceTasks = project.tasks.filter(task => task.task_group_id === taskGroupId && task.status_id === statusId);
  if (sourceTasks.length === 0) return 0;

  const createdAt = now();
  const idMap = new Map(sourceTasks.map(task => [task.id, genId()]));
  const copies: Task[] = sourceTasks.map(task => ({
    ...task,
    id: idMap.get(task.id)!,
    tags: [...task.tags],
    dependencies: task.dependencies.map(dependency => ({
      ...dependency,
      taskId: idMap.get(dependency.taskId) ?? dependency.taskId,
    })),
    subtasks: task.subtasks.map(subtask => ({ ...subtask, id: genId() })),
    comments: task.comments.map(comment => ({ ...comment, id: genId() })),
    recurrence: task.recurrence
      ? {
          ...task.recurrence,
          byWeekday: task.recurrence.byWeekday ? [...task.recurrence.byWeekday] : undefined,
          sourceTaskId: task.recurrence.sourceTaskId
            ? (idMap.get(task.recurrence.sourceTaskId) ?? task.recurrence.sourceTaskId)
            : undefined,
        }
      : task.recurrence,
    completed_at: status.category === 'done' ? createdAt : null,
    tracked_start: null,
    created_at: createdAt,
    updated_at: createdAt,
  }));

  pushHistory();
  projects.update(list => list.map(item => {
    if (item.id !== projectId) return item;
    const updated = { ...item, tasks: [...item.tasks, ...copies], updated_at: createdAt };
    persistProject(updated);
    return updated;
  }));
  for (const task of copies) runHook('onTaskCreate', { projectId, taskGroupId, task });
  return copies.length;
}

/**
 * 更新任务属性
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param data - 要更新的属性（部分更新）
 */
/** 预设任务颜色数组 */
export const TASK_COLORS = ['#a33b32','#3e7562','#ad7622','#b94a43','#73576f','#477477','#a85769','#6e7f48','#a85e37','#2e6f68'];

/** 从预设颜色中取一个（基于项目现有任务数取模，尽量分散） */
export function pickTaskColor(projectId: string): string {
  const p = get(projects).find(x => x.id === projectId);
  const count = p?.tasks.length ?? 0;
  return TASK_COLORS[count % TASK_COLORS.length];
}

export function updateTask(projectId: string, taskId: string, data: Partial<Task>): boolean {
  const project = get(projects).find(p => p.id === projectId);
  const existing = project?.tasks.find(t => t.id === taskId);
  if (!project || !existing) return false;
  if (data.task_group_id && data.task_group_id !== existing.task_group_id) return false;

  const currentStatus = getTaskStatus(project, existing);
  const targetStatus = data.status_id
    ? getTaskGroup(project, existing)?.statuses.find(status => status.id === data.status_id)
    : currentStatus;
  if (!targetStatus) return false;
  const entersDone = targetStatus.category === 'done' && currentStatus?.category !== 'done';
  if (entersDone && !canCompleteTask(projectId, taskId)) return false;
  if (entersDone && existing.recurrence) {
    completeRecurringTask(projectId, taskId);
    return true;
  }

  const statusChanged = targetStatus.id !== existing.status_id;
  const normalized: Partial<Task> = { ...data };
  if (statusChanged) {
    normalized.completed_at = targetStatus.category === 'done' ? now() : null;
    if ((targetStatus.category === 'done' || targetStatus.category === 'cancelled') && currentStatus && !isTaskClosed(project, existing)) {
      normalized.status_before_closed_id = existing.status_id;
    }
  }

  pushHistory();
  let prevStatusId: string | null = null;
  let nextStatusId: string | null = null;
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const task = p.tasks.find(t => t.id === taskId);
    if (task) {
      if (statusChanged) {
        prevStatusId = task.status_id;
        nextStatusId = targetStatus.id;
      }
    }
    const updated = {
      ...p,
      tasks: p.tasks.map(t => (t.id === taskId ? { ...t, ...normalized, updated_at: now() } : t)),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
  if (prevStatusId && nextStatusId && prevStatusId !== nextStatusId) {
    runHook('onTaskStatusChange', {
      projectId,
      taskId,
      taskGroupId: existing.task_group_id,
      fromStatusId: prevStatusId,
      toStatusId: nextStatusId,
      fromCategory: currentStatus?.category ?? 'todo',
      toCategory: targetStatus.category,
    });
  }
  return true;
}

/**
 * 删除任务
 *
 * @param projectId - 项目 ID
 * @param taskId - 要删除的任务 ID
 */
export function deleteTask(projectId: string, taskId: string): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = { ...p, tasks: p.tasks.filter(t => t.id !== taskId), updated_at: now() };
    persistProject(updated);
    return updated;
  }));
  activeTaskId.update(curr => (curr === taskId ? null : curr));
}

/**
 * 切换任务状态（todo ↔ done）
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 */
/**
 * 检查「能否标记为完成」（前置依赖必须全部完成）
 *
 * 抽出来给「普通完成」与「循环任务完成」共用，避免两套判断走偏。
 */
function canCompleteTask(projectId: string, taskId: string): boolean {
  const proj = get(projects).find(p => p.id === projectId);
  if (!proj) return true;
  const task = proj.tasks.find(t => t.id === taskId);
  if (!task || task.dependencies.length === 0) return true;
  const pendingDeps = task.dependencies.filter(dep => {
    const depTask = proj.tasks.find(t => t.id === dep.taskId);
    return !depTask || !satisfiesDependency(proj, depTask);
  });
  if (pendingDeps.length === 0) return true;
  const names = pendingDeps.map(d => proj.tasks.find(t => t.id === d.taskId)?.title || d.taskId);
  console.warn(`[Task] 前置任务未完成，无法标记完成: ${names.join(', ')}`);
  return false;
}

/**
 * 设置 / 清除任务的循环规则
 *
 * @param rule - 传 null 表示清除（变回普通任务）
 */
export function setRecurrence(projectId: string, taskId: string, rule: RecurrenceRule | null): void {
  pushHistory();
  const normalized = rule ? normalizeRule(rule) : null;
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => (t.id === taskId ? { ...t, recurrence: normalized, updated_at: now() } : t)),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 完成一次循环任务
 *
 * 语义（**只在这一个显式入口里生成下一个实例**，`updateTask()` 绝不偷偷生成）：
 *  1. 前置依赖检查（与普通完成一致），不通过返回 null
 *  2. 当前任务标记 `done`
 *  3. 规则未耗尽：以 `due_date`（缺失则今天）为基准算下次日期，新建**同标题/描述/标签/
 *     优先级/子任务**（子任务 done 重置为 false）的新任务：
 *     `due_date = 下次日期`、`recurrence = consumeRule(rule)` + `sourceTaskId = 原任务 id`
 *  4. 原任务的 `recurrence` 置 null（避免二次触发）
 *  5. 保留原任务历史，并持久化新实例
 *
 * 只调用**一次** `pushHistory()`（对用户而言这是一次操作）。
 *
 * @returns 新任务 id；未生成（规则用尽/依赖未满足）返回 null
 */
export function completeRecurringTask(projectId: string, taskId: string): string | null {
  if (!canCompleteTask(projectId, taskId)) return null;

  const proj = get(projects).find(p => p.id === projectId);
  const task = proj?.tasks.find(t => t.id === taskId);
  if (!proj || !task) return null;
  const group = getTaskGroup(proj, task);
  if (!group) return null;
  const completion = getCompletionStatus(group);
  const initial = getInitialStatus(group);
  const rule = task.recurrence ? normalizeRule(task.recurrence) : null;

  const baseDate = normalizeDate(task.due_date || today());
  const nextDate = rule ? nextOccurrence(rule, baseDate) : null;

  const newTask: Task | null = rule && nextDate
    ? {
        ...task,
        id: genId(),
        status_id: initial.id,
        completed_at: null,
        status_before_closed_id: undefined,
        due_date: nextDate,
        recurrence: { ...consumeRule(rule), sourceTaskId: task.id },
        subtasks: task.subtasks.map(sub => ({ ...sub, id: genId(), done: false })),
        comments: [],
        dependencies: [...task.dependencies],
        tracked_start: null,
        reminder: null,
        created_at: now(),
        updated_at: now()
      }
    : null;

  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const tasks = p.tasks.map(t => (t.id === taskId
      ? { ...t, status_before_closed_id: t.status_id, status_id: completion.id, completed_at: now(), recurrence: null, updated_at: now() }
      : t));
    if (newTask) tasks.push(newTask);
    const updated = { ...p, tasks, updated_at: now() };
    persistProject(updated);
    return updated;
  }));

  return newTask?.id ?? null;
}

export function toggleTaskStatus(projectId: string, taskId: string): boolean {
  const proj = get(projects).find(p => p.id === projectId);
  const task = proj?.tasks.find(t => t.id === taskId);
  if (!proj || !task) return false;
  return isTaskClosed(proj, task) ? reopenTask(projectId, taskId) : completeTask(projectId, taskId);
}

export function completeTask(projectId: string, taskId: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const task = project?.tasks.find(item => item.id === taskId);
  if (!project || !task) return false;
  const group = getTaskGroup(project, task);
  if (!group) return false;
  if (task.recurrence && !isTaskCompleted(project, task)) {
    if (!canCompleteTask(projectId, taskId)) return false;
    completeRecurringTask(projectId, taskId);
    return true;
  }
  return updateTask(projectId, taskId, { status_id: getCompletionStatus(group).id });
}

export function reopenTask(projectId: string, taskId: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const task = project?.tasks.find(item => item.id === taskId);
  if (!project || !task) return false;
  const group = getTaskGroup(project, task);
  if (!group) return false;
  const preferred = group.statuses.find(status => status.id === task.status_before_closed_id && !['done', 'cancelled'].includes(status.category));
  return updateTask(projectId, taskId, { status_id: preferred?.id ?? getInitialStatus(group).id, completed_at: null });
}

export function cancelTask(projectId: string, taskId: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const task = project?.tasks.find(item => item.id === taskId);
  const group = project && task ? getTaskGroup(project, task) : null;
  const cancelled = group?.statuses.find(status => status.category === 'cancelled');
  return cancelled ? updateTask(projectId, taskId, { status_id: cancelled.id }) : false;
}

export function moveTaskToGroup(projectId: string, taskId: string, targetTaskGroupId: string, targetStatusId: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const task = project?.tasks.find(item => item.id === taskId);
  const group = project?.task_groups.find(item => item.id === targetTaskGroupId && !item.archived);
  const status = group?.statuses.find(item => item.id === targetStatusId);
  if (!project || !task || !group || !status) return false;
  if (status.category === 'done' && !canCompleteTask(projectId, taskId)) return false;
  pushHistory();
  const changedAt = now();
  let nextTask: Task | null = null;
  projects.update(list => list.map(item => {
    if (item.id !== projectId) return item;
    const tasks = item.tasks.map(candidate => {
      if (candidate.id !== taskId) return candidate;
      nextTask = {
        ...candidate,
        task_group_id: group.id,
        status_id: status.id,
        status_before_closed_id: undefined,
        completed_at: status.category === 'done' ? changedAt : null,
        updated_at: changedAt,
      };
      return nextTask;
    });
    const updated = { ...item, tasks, updated_at: changedAt };
    persistProject(updated);
    return updated;
  }));
  if (task.status_id !== status.id) {
    const previousStatus = getTaskStatus(project, task);
    runHook('onTaskStatusChange', {
      projectId,
      taskId,
      taskGroupId: group.id,
      fromStatusId: task.status_id,
      toStatusId: status.id,
      fromCategory: previousStatus?.category ?? 'todo',
      toCategory: status.category,
    });
  }
  if (task.task_group_id !== group.id) {
    runHook('onTaskGroupChange', {
      projectId,
      taskId,
      fromTaskGroupId: task.task_group_id,
      toTaskGroupId: group.id,
      toStatusId: status.id,
    });
  }
  return nextTask !== null;
}

/**
 * 为任务添加依赖关系
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param dep - 依赖对象（目标任务 ID 和天数偏移）
 */
export function addDependency(projectId: string, taskId: string, dep: Dependency): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    // 检查循环依赖：从 dep.taskId 出发，沿依赖链能否回到 taskId
    const visited = new Set<string>();
    const queue = [dep.taskId];
    let circular = false;
    while (queue.length > 0) {
      const cur = queue.shift()!;
      if (cur === taskId) { circular = true; break; }
      if (visited.has(cur)) continue;
      visited.add(cur);
      const t = p.tasks.find(x => x.id === cur);
      if (t) t.dependencies.forEach(d => queue.push(d.taskId));
    }
    if (circular) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => {
        if (t.id !== taskId) return t;
        if (t.dependencies.some(d => d.taskId === dep.taskId)) return t;
        return { ...t, dependencies: [...t.dependencies, dep], updated_at: now() };
      }),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 移除任务的指定依赖
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param depTaskId - 要移除的依赖目标任务 ID
 */
export function removeDependency(projectId: string, taskId: string, depTaskId: string): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => {
        if (t.id !== taskId) return t;
        return { ...t, dependencies: t.dependencies.filter(d => d.taskId !== depTaskId), updated_at: now() };
      }),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 更新任务的开始偏移天数（用于时间线视图）
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param offset - 偏移天数，null 表示未设置
 */
export function updateStartOffset(projectId: string, taskId: string, offset: number | null): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => (t.id === taskId ? { ...t, start_offset: offset, updated_at: now() } : t)),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 为任务添加子任务
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param title - 子任务标题
 */
export function addSubtask(projectId: string, taskId: string, title: string): void {
  pushHistory();
  const sub: Subtask = { id: genId(), title, done: false };
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => {
        if (t.id !== taskId) return t;
        return { ...t, subtasks: [...t.subtasks, sub], updated_at: now() };
      }),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 切换子任务完成状态
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param subtaskId - 子任务 ID
 */
export function toggleSubtask(projectId: string, taskId: string, subtaskId: string): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => {
        if (t.id !== taskId) return t;
        return {
          ...t,
          subtasks: t.subtasks.map(s => (s.id === subtaskId ? { ...s, done: !s.done } : s)),
          updated_at: now()
        };
      }),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 删除子任务
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param subtaskId - 子任务 ID
 */
export function removeSubtask(projectId: string, taskId: string, subtaskId: string): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => {
        if (t.id !== taskId) return t;
        return { ...t, subtasks: t.subtasks.filter(s => s.id !== subtaskId), updated_at: now() };
      }),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 添加评论
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param content - 评论内容
 */
export function addComment(projectId: string, taskId: string, content: string): void {
  pushHistory();
  const comment = { id: genId(), content, created_at: now() };
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => (t.id === taskId ? { ...t, comments: [...t.comments, comment], updated_at: now() } : t)),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 编辑评论
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param commentId - 评论 ID
 * @param content - 新内容
 */
export function updateComment(projectId: string, taskId: string, commentId: string, content: string): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => {
        if (t.id !== taskId) return t;
        return {
          ...t,
          comments: t.comments.map(c => (c.id === commentId ? { ...c, content } : c)),
          updated_at: now()
        };
      }),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 删除评论
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param commentId - 评论 ID
 */
export function deleteComment(projectId: string, taskId: string, commentId: string): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => {
        if (t.id !== taskId) return t;
        return { ...t, comments: t.comments.filter(c => c.id !== commentId), updated_at: now() };
      }),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 开始任务计时
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 */
export function startTracking(projectId: string, taskId: string): void {
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => (t.id === taskId && !t.tracked_start ? { ...t, tracked_start: now(), updated_at: now() } : t)),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 设置任务提醒时间
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 * @param reminderIso - 提醒时间的 ISO 字符串
 */
export function setReminder(projectId: string, taskId: string, reminderIso: string): void {
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => (t.id === taskId ? { ...t, reminder: reminderIso, updated_at: now() } : t)),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 清除任务提醒
 *
 * @param projectId - 项目 ID
 * @param taskId - 任务 ID
 */
export function clearReminder(projectId: string, taskId: string): void {
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = {
      ...p,
      tasks: p.tasks.map(t => (t.id === taskId ? { ...t, reminder: null, updated_at: now() } : t)),
      updated_at: now()
    };
    persistProject(updated);
    return updated;
  }));
}
