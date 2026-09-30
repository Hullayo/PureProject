/**
 * 项目排序（侧边栏顺序）工具
 *
 * 顺序用「排序值 `Project.sort_order`」表达，而不是数组下标。
 * 原因：项目是一个数组存在 localStorage，但**每个项目单独**同步到服务器，
 * 数组下标无法随 .pm 一起上传；`sort_order` 是项目自身字段，能跟着 .pm 走，
 * 于是别的设备拉到后按它重排即可实现跨设备顺序同步。
 *
 * 取值策略（gap / 稀疏整数）：
 * - 新项目追加：`max + ORDER_GAP`
 * - 拖拽插入：取前一个与后一个的**中点**（如 1000 与 2000 → 1500）
 *   → 只改被拖动的那一个项目 → 只推一份 .pm，同步开销最小
 * - 中点没有整数空间（间隙 < 2）或存在历史数据缺 `sort_order`：全量重排为 `i * ORDER_GAP`
 *
 * @module utils/project-order
 */

import type { Project } from '$lib/types';

/** 排序值步长 */
export const ORDER_GAP = 1000;

/** 取项目有效排序值；缺失（历史数据）时按数组下标 * GAP 兜底，保证与显式值同一量级 */
export function orderOf(project: Project, fallbackIndex = 0): number {
  const v = project.sort_order;
  return typeof v === 'number' && Number.isFinite(v) ? v : fallbackIndex * ORDER_GAP;
}

/** 是否已有显式排序值 */
export function hasOrder(project: Project): boolean {
  return typeof project.sort_order === 'number' && Number.isFinite(project.sort_order);
}

/** 是否所有项目都已有显式排序值 */
export function hasFullOrder(list: Project[]): boolean {
  return list.length > 0 && list.every(hasOrder);
}

/**
 * 按排序值稳定排序（缺失排序值时保持原有相对顺序）
 *
 * 不修改入参，返回新数组。
 */
export function sortProjects(list: Project[]): Project[] {
  return list
    .map((project, index) => ({ project, key: orderOf(project, index), index }))
    .sort((a, b) => a.key - b.key || a.index - b.index)
    .map((x) => x.project);
}

/** 追加到末尾时使用的下一个排序值 */
export function nextOrder(list: Project[]): number {
  if (list.length === 0) return ORDER_GAP;
  const sorted = sortProjects(list);
  return orderOf(sorted[sorted.length - 1], sorted.length - 1) + ORDER_GAP;
}

/**
 * 全量重排：按当前顺序赋 `0, GAP, 2*GAP, ...`
 *
 * @returns 排序值发生变化、需要持久化的项目（可能是空数组）
 */
export function renumberProjects(list: Project[]): { list: Project[]; changed: Project[] } {
  const changed: Project[] = [];
  const next = list.map((project, index) => {
    const order = index * ORDER_GAP;
    if (project.sort_order === order) return project;
    const updated = { ...project, sort_order: order };
    changed.push(updated);
    return updated;
  });
  return { list: next, changed };
}

/**
 * 让一个列表带上排序值（给缺 `sort_order` 的历史数据补值）
 *
 * 先按「显式值优先，缺值按下标兜底」稳定排序，再全量重排为 `i * GAP`，
 * 保证两个不变量：
 *  - 没有重复值；
 *  - 不改变当前可见顺序（在显式值的约束下）。
 *
 * 只补值、**不修改 `updated_at`**，避免启动时把所有项目标记为「有改动」而触发同步风暴。
 */
export function migrateProjects(list: Project[]): Project[] {
  if (list.length === 0 || hasFullOrder(list)) return list;
  return sortProjects(list).map((project, index) => {
    const order = index * ORDER_GAP;
    return project.sort_order === order ? project : { ...project, sort_order: order };
  });
}
