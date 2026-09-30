/**
 * 撤销/重做历史管理
 *
 * 使用快照栈实现撤销/重做功能，支持最多 50 步历史。
 *
 * @module stores/history
 */

import { get } from 'svelte/store';
import type { Project } from '$lib/types';
import type { Writable } from 'svelte/store';
import { saveProjects, savePmFile } from '$lib/repositories';

// Break circular dependency: project.ts imports pushHistory from here,
// and this module needs projects/activeProjectId from project.ts.
// We resolve this by having project.ts register the stores after module init.
let _projects: Writable<Project[]> | null = null;
let _activeProjectId: Writable<string | null> | null = null;
let _activeTaskId: Writable<string | null> | null = null;
let _activeTaskGroupId: Writable<string | null> | null = null;

export function registerStores(
  projects: Writable<Project[]>,
  activeProjectId: Writable<string | null>,
  activeTaskId?: Writable<string | null>
) {
  _projects = projects;
  _activeProjectId = activeProjectId;
  if (activeTaskId) _activeTaskId = activeTaskId;
}

export function registerActiveTaskId(activeTaskId: Writable<string | null>) {
  _activeTaskId = activeTaskId;
}

export function registerActiveTaskGroupId(activeTaskGroupId: Writable<string | null>) {
  _activeTaskGroupId = activeTaskGroupId;
}

/** 历史快照条目 */
interface HistoryEntry {
  projects: Project[];
  activeProjectId: string | null;
  activeTaskGroupId: string | null;
  activeTaskId: string | null;
}

/** 撤销栈 */
const undoStack: HistoryEntry[] = [];

/** 重做栈 */
const redoStack: HistoryEntry[] = [];

/** 最大历史记录数 */
const MAX_HISTORY = 50;

/**
 * 保存当前状态到撤销栈
 *
 * 在执行任何修改操作前调用，将当前状态序列化为快照。
 * 调用后清空重做栈。
 */
export function pushHistory(): void {
  if (!_projects || !_activeProjectId || !_activeTaskId) return;
  const snapshot: HistoryEntry = {
    projects: JSON.parse(JSON.stringify(get(_projects))),
    activeProjectId: get(_activeProjectId),
    activeTaskGroupId: _activeTaskGroupId ? get(_activeTaskGroupId) : null,
    activeTaskId: get(_activeTaskId)
  };
  undoStack.push(snapshot);
  if (undoStack.length > MAX_HISTORY) undoStack.shift();
  redoStack.length = 0;
}

/**
 * 撤销上一步操作
 *
 * 将当前状态推入重做栈，从撤销栈恢复上一个状态。
 */
export function undo(): void {
  if (!_projects || !_activeProjectId || !_activeTaskId) return;
  if (undoStack.length === 0) return;
  const snapshot = undoStack.pop()!;
  redoStack.push({
    projects: JSON.parse(JSON.stringify(get(_projects))),
    activeProjectId: get(_activeProjectId),
    activeTaskGroupId: _activeTaskGroupId ? get(_activeTaskGroupId) : null,
    activeTaskId: get(_activeTaskId)
  });
  _projects.set(snapshot.projects);
  _activeProjectId.set(snapshot.activeProjectId);
  _activeTaskGroupId?.set(snapshot.activeTaskGroupId);
  _activeTaskId.set(snapshot.activeTaskId);
  persistSnapshot(snapshot.projects);
}

/**
 * 重做上一步被撤销的操作
 *
 * 将当前状态推入撤销栈，从重做栈恢复状态。
 */
export function redo(): void {
  if (!_projects || !_activeProjectId || !_activeTaskId) return;
  if (redoStack.length === 0) return;
  const snapshot = redoStack.pop()!;
  undoStack.push({
    projects: JSON.parse(JSON.stringify(get(_projects))),
    activeProjectId: get(_activeProjectId),
    activeTaskGroupId: _activeTaskGroupId ? get(_activeTaskGroupId) : null,
    activeTaskId: get(_activeTaskId)
  });
  _projects.set(snapshot.projects);
  _activeProjectId.set(snapshot.activeProjectId);
  _activeTaskGroupId?.set(snapshot.activeTaskGroupId);
  _activeTaskId.set(snapshot.activeTaskId);
  persistSnapshot(snapshot.projects);
}

function persistSnapshot(list: Project[]): void {
  saveProjects(list);
  for (const project of list) void savePmFile(project);
}
