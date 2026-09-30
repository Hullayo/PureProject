/**
 * 全局搜索 stores
 *
 * 跨项目搜索：**项目名/描述、任务标题/描述、子任务标题**。
 * 与 `CommandPalette`（面向命令，职责不同）完全独立，这里只做「关键词 → 结果」的过滤，
 * 不做任何排序以外的业务逻辑，也不触碰持久化。
 *
 * 设计要点：
 * - `searchResults` 从 `projects` 派生 → 项目增删改后**自动**跟随，无需手动刷新；
 * - 关键词匹配**大小写不敏感**（统一转小写后比较）；
 * - 结果分三类（project / task / subtask）并按此顺序排列，组件据此分组展示；
 * - 上限 {@link SEARCH_RESULT_LIMIT} 条，避免大项目（几千任务）每次输入都卡顿。
 *
 * @module stores/search
 *
 * @example
 * ```svelte
 * <script>
 *   import { openSearch, closeSearch, searchKeyword, searchResults } from '$lib/stores/search';
 * </script>
 * <button onclick={openSearch}>搜索</button>
 * <input bind:value={$searchKeyword} />
 * {#each $searchResults as r (r.id)}<span>{r.title}</span>{/each}
 * ```
 */

import { writable, derived } from 'svelte/store';
import type { Project } from '$lib/types';
import { projects } from './project';
import { getTaskStatus, isTaskCompleted } from '$lib/utils/task-status';

/** 单次搜索返回的结果上限（防止大项目卡顿） */
export const SEARCH_RESULT_LIMIT = 200;

/** 搜索命中的对象类型 */
export type SearchResultType = 'project' | 'task_group' | 'task' | 'subtask';

/** 一条搜索结果（已归一化，组件无需再关心来源结构） */
export interface SearchResult {
  /** 稳定且唯一的键（用于 `{#each ... (r.id)}` 与选中态） */
  id: string;
  /** 命中类型：项目 / 任务 / 子任务 */
  type: SearchResultType;
  /** 所属项目 ID（点击结果时写入 `activeProjectId`） */
  projectId: string;
  /** 所属项目名（列表右侧展示） */
  projectName: string;
  /** 所属项目主题色（色点） */
  projectColor: string;
  /** 任务 ID（任务/子任务结果才有，点击时写入 `activeTaskId`） */
  taskId?: string;
  taskGroupId?: string;
  taskGroupName?: string;
  statusName?: string;
  /** 主标题（项目名 / 任务标题 / 子任务标题） */
  title: string;
  /** 辅助说明（项目/任务描述、子任务的父任务名） */
  context?: string;
  /** 是否已完成（任务 status='done' / 子任务 done）→ 组件加删除线 */
  done: boolean;
}

/** 搜索面板是否打开 */
export const searchOpen = writable(false);

/** 当前搜索关键词（原样保留，匹配时再归一化） */
export const searchKeyword = writable('');

/** 打开搜索面板（不清空上次关键词，方便反复使用） */
export function openSearch(): void {
  searchOpen.set(true);
}

/** 关闭搜索面板并清空关键词，避免下次打开残留旧结果 */
export function closeSearch(): void {
  searchOpen.set(false);
  searchKeyword.set('');
}

/** 归一化关键词：去首尾空白 + 转小写 */
function normalize(s: string): string {
  return (s ?? '').trim().toLowerCase();
}

/** 截断过长的辅助说明，避免 UI 撑爆 */
function clip(s: string | undefined, max = 120): string | undefined {
  const v = (s ?? '').trim();
  if (!v) return undefined;
  return v.length > max ? `${v.slice(0, max)}…` : v;
}

/**
 * 收集全部匹配项（纯函数，便于单测）
 *
 * 分组顺序固定为**项目 → 任务 → 子任务**（先分别收集再拼接），
 * 这样组件按 type 连续渲染分组标题即可，不会出现「任务/子任务」交错。
 * 不依赖 store，也不做上限截断（由调用方处理）。
 *
 * @param projectList - 待搜索的项目列表
 * @param keyword - 原始关键词（内部会归一化）
 */
export function collectSearchResults(projectList: Project[], keyword: string): SearchResult[] {
  const q = normalize(keyword);
  if (!q) return [];

  const projectHits: SearchResult[] = [];
  const taskHits: SearchResult[] = [];
  const subtaskHits: SearchResult[] = [];
  const groupHits: SearchResult[] = [];

  for (const project of projectList) {
    const base = {
      projectId: project.id,
      projectName: project.name,
      projectColor: project.color,
    };

    // 1) 项目名 / 项目描述
    if (normalize(project.name).includes(q) || normalize(project.description).includes(q)) {
      projectHits.push({
        ...base,
        id: `project:${project.id}`,
        type: 'project',
        title: project.name,
        context: clip(project.description),
        done: false,
      });
    }

    for (const group of project.task_groups ?? []) {
      if (normalize(group.name).includes(q)) {
        groupHits.push({ ...base, id: `task_group:${project.id}:${group.id}`, type: 'task_group', taskGroupId: group.id, taskGroupName: group.name, title: group.name, context: project.name, done: false });
      }
    }

    // 2) 任务标题 / 任务描述 → 3) 子任务标题
    for (const task of project.tasks ?? []) {
      const group = project.task_groups.find(item => item.id === task.task_group_id);
      const status = getTaskStatus(project, task);
      if (normalize(task.title).includes(q) || normalize(task.description).includes(q)) {
        taskHits.push({
          ...base,
          id: `task:${project.id}:${task.id}`,
          type: 'task',
          taskId: task.id,
          taskGroupId: task.task_group_id,
          taskGroupName: group?.name,
          statusName: status?.name,
          title: task.title,
          context: clip(task.description),
          done: isTaskCompleted(project, task),
        });
      }
      for (const sub of task.subtasks ?? []) {
        if (normalize(sub.title).includes(q)) {
          subtaskHits.push({
            ...base,
            id: `subtask:${project.id}:${task.id}:${sub.id}`,
            type: 'subtask',
            taskId: task.id,
            taskGroupId: task.task_group_id,
            taskGroupName: group?.name,
            statusName: status?.name,
            title: sub.title,
            context: clip(task.title),
            done: !!sub.done,
          });
        }
      }
    }
  }

  return [...projectHits, ...groupHits, ...taskHits, ...subtaskHits];
}

/**
 * 全局搜索结果（派生）
 *
 * 自动响应 `projects` 的变化；空关键词返回空数组（由组件展示提示文案）。
 * 顺序固定为「项目 → 任务 → 子任务」，总条数不超过 {@link SEARCH_RESULT_LIMIT}。
 */
export const searchResults = derived(
  [projects, searchKeyword],
  ([$projects, $keyword]): SearchResult[] =>
    collectSearchResults($projects, $keyword).slice(0, SEARCH_RESULT_LIMIT)
);
