/**
 * 数据统计工具函数
 *
 * 提供任务完成率、状态分布、优先级分布、标签分布、
 * 周趋势和工时统计等计算函数，用于统计报表展示。
 *
 * @module utils/stats
 */

import type { Project, Task, Tag } from '$lib/types';
import { formatMs } from './date';
import { getTaskStatus, isTaskCompleted } from './task-status';

/** 完成率统计数据 */
export interface CompletionRate {
  /** 已完成任务数 */
  done: number;
  /** 总任务数 */
  total: number;
  /** 完成百分比（0-100） */
  percent: number;
}

/** 状态分布统计数据 */
export interface StatusDist {
  /** 待办任务数 */
  todo: number;
  /** 进行中任务数 */
  active: number;
  /** 已完成任务数 */
  done: number;
  cancelled: number;
}

/** 优先级分布统计数据 */
export interface PriorityDist {
  /** 高优先级任务数 */
  high: number;
  /** 中优先级任务数 */
  medium: number;
  /** 低优先级任务数 */
  low: number;
}

/** 标签分布统计数据 */
export interface TagDist {
  /** 标签名称 */
  name: string;
  /** 标签颜色 */
  color: string;
  /** 使用该标签的任务数 */
  count: number;
}

/** 周完成趋势数据 */
export interface WeekCompletion {
  /** 周标签（格式 "M/DD"） */
  label: string;
  /** 该周完成的任务数 */
  count: number;
}

/** 工时统计数据 */
export interface TimeStats {
  /** 总工时（毫秒） */
  totalMs: number;
  /** 平均工时（毫秒） */
  avgMs: number;
  /** 耗时最长的任务 */
  longestTask: { title: string; ms: number } | null;
  /** 格式化后的工时数据 */
  formatted: {
    /** 总工时（如 "12h 30m"） */
    total: string;
    /** 平均工时（如 "2h 15m"） */
    avg: string;
    /** 最长工时（如 "5h 45m"） */
    longest: string;
  };
}

/**
 * 计算任务完成率
 *
 * @param tasks - 任务列表
 * @returns 包含已完成数、总数和百分比的统计对象
 */
export function completionRate(project: Project): CompletionRate {
  const tasks = project.tasks;
  const total = tasks.length;
  const done = tasks.filter(task => isTaskCompleted(project, task)).length;
  return { done, total, percent: total > 0 ? Math.round((done / total) * 100) : 0 };
}

/**
 * 计算任务状态分布
 *
 * @param tasks - 任务列表
 * @returns 各状态的任务数量
 */
export function statusDistribution(project: Project): StatusDist {
  const tasks = project.tasks;
  return {
    todo: tasks.filter(task => getTaskStatus(project, task)?.category === 'todo').length,
    active: tasks.filter(task => getTaskStatus(project, task)?.category === 'active').length,
    done: tasks.filter(task => getTaskStatus(project, task)?.category === 'done').length,
    cancelled: tasks.filter(task => getTaskStatus(project, task)?.category === 'cancelled').length,
  };
}

/**
 * 计算任务优先级分布
 *
 * @param tasks - 任务列表
 * @returns 各优先级的任务数量
 */
export function priorityDistribution(tasks: Task[]): PriorityDist {
  return {
    high: tasks.filter(t => t.priority === 'high').length,
    medium: tasks.filter(t => t.priority === 'medium').length,
    low: tasks.filter(t => t.priority === 'low').length
  };
}

/**
 * 计算标签使用分布
 *
 * 统计每个标签被多少任务使用，按使用量降序排列，
 * 仅返回有任务使用的标签。
 *
 * @param tasks - 任务列表
 * @param tags - 标签定义列表
 * @returns 标签分布数组，按使用量降序
 */
export function tagDistribution(tasks: Task[], tags: Tag[]): TagDist[] {
  const counts = new Map<string, number>();
  for (const t of tasks) {
    for (const tag of t.tags) {
      counts.set(tag, (counts.get(tag) ?? 0) + 1);
    }
  }
  return tags
    .map(t => ({ name: t.name, color: t.color, count: counts.get(t.name) ?? 0 }))
    .filter(t => t.count > 0)
    .sort((a, b) => b.count - a.count);
}

/**
 * 计算最近几周的任务完成趋势
 *
 * 从当前日期往前推算指定周数，统计每周完成的任务数量。
 * 周起始日为周日。
 *
 * @param tasks - 任务列表
 * @param weeks - 往前推算的周数，默认 4 周
 * @returns 每周完成数数组，从最早一周到最近一周
 */
export function weeklyCompletions(project: Project, weeks = 4): WeekCompletion[] {
  const tasks = project.tasks;
  const now = new Date();
  const result: WeekCompletion[] = [];
  for (let i = weeks - 1; i >= 0; i--) {
    const weekStart = new Date(now);
    weekStart.setDate(now.getDate() - now.getDay() - i * 7);
    weekStart.setHours(0, 0, 0, 0);
    const weekEnd = new Date(weekStart);
    weekEnd.setDate(weekStart.getDate() + 7);
    const count = tasks.filter(t => {
      if (!isTaskCompleted(project, t) || !t.completed_at) return false;
      const d = new Date(t.completed_at);
      return d >= weekStart && d < weekEnd;
    }).length;
    const m = weekStart.getMonth() + 1;
    const d = weekStart.getDate();
    result.push({ label: `${m}/${d}`, count });
  }
  return result;
}

/**
 * 计算工时统计
 *
 * 基于任务的 tracked_start 和完成时间（updated_at）计算总工时、
 * 平均工时和耗时最长的任务。仅统计已开启计时且已完成的任务。
 *
 * @param tasks - 任务列表
 * @returns 工时统计数据，包含原始毫秒值和格式化字符串
 */
export function timeStats(project: Project): TimeStats {
  const tasks = project.tasks;
  const tracked = tasks.filter(t => t.tracked_start);
  const doneTracked = tracked.filter(t => isTaskCompleted(project, t) && t.completed_at);

  let totalMs = 0;
  let longestTask: { title: string; ms: number } | null = null;

  for (const t of doneTracked) {
    const ms = new Date(t.completed_at!).getTime() - new Date(t.tracked_start!).getTime();
    totalMs += ms;
    if (!longestTask || ms > longestTask.ms) {
      longestTask = { title: t.title, ms };
    }
  }

  const avgMs = doneTracked.length > 0 ? totalMs / doneTracked.length : 0;

  return {
    totalMs,
    avgMs,
    longestTask,
    formatted: {
      total: formatMs(totalMs),
      avg: formatMs(avgMs),
      longest: longestTask ? formatMs(longestTask.ms) : '0m'
    }
  };
}
