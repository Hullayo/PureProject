/**
 * Markdown 导出工具
 *
 * 将项目数据转换为 Markdown 格式文本，支持表格展示任务列表、
 * 里程碑、变更日志和标签信息。可用于项目文档导出。
 *
 * @module utils/markdown
 */

import type { Project, Priority } from '$lib/types';
import { fmtDate } from './date';

/** 优先级中文标签映射 */
const priorityLabel: Record<Priority, string> = { high: '高', medium: '中', low: '低' };

/**
 * 计算子任务进度文本
 *
 * @param task - 含有子任务列表的对象
 * @returns 格式化的进度字符串，如 "3/5"，无子任务时返回 "-"
 */
function subtaskProgress(task: { subtasks?: { done: boolean }[] }): string {
  if (!task.subtasks?.length) return '-';
  const done = task.subtasks.filter(s => s.done).length;
  return `${done}/${task.subtasks.length}`;
}

/**
 * 将项目数据生成 Markdown 格式字符串
 *
 * 生成的内容包含：
 * - 项目名称和描述
 * - 按状态分组的任务表格（含优先级、标签、截止日期、子任务进度）
 * - 每个任务的描述和子任务清单
 * - 里程碑列表
 * - 变更日志
 * - 标签列表
 *
 * @param project - 项目数据对象
 * @returns Markdown 格式的字符串
 */
export function generateMarkdown(project: Project): string {
  const lines: string[] = [];

  lines.push(`# ${project.name}`);
  lines.push('');
  if (project.description) {
    lines.push(`> ${project.description}`);
    lines.push('');
  }

  for (const group of project.task_groups.slice().sort((a, b) => a.sort_order - b.sort_order)) {
    lines.push(`## ${group.name}${group.archived ? '（已归档）' : ''}`);
    lines.push('');
    for (const status of group.statuses.slice().sort((a, b) => a.sort_order - b.sort_order)) {
      const tasks = project.tasks.filter(task => task.task_group_id === group.id && task.status_id === status.id);
      if (tasks.length === 0) continue;
      lines.push(`### ${status.name} (${tasks.length})`);
      lines.push('');
      lines.push('| 任务 | 优先级 | 标签 | 截止日期 | 子任务 |');
      lines.push('|------|--------|------|----------|--------|');
      for (const task of tasks) {
        const tags = task.tags.length > 0 ? task.tags.join(', ') : '-';
        lines.push(`| ${task.title} | ${priorityLabel[task.priority]} | ${tags} | ${fmtDate(task.due_date)} | ${subtaskProgress(task)} |`);
      }
      lines.push('');
      for (const task of tasks) {
        if (task.description) lines.push(`> **${task.title}**: ${task.description}`);
        for (const subtask of task.subtasks ?? []) {
          lines.push(`>   - [${subtask.done ? 'x' : ' '}] ${subtask.title}`);
        }
      }
      lines.push('');
    }
  }

  if (project.milestones?.length) {
    lines.push('## 里程碑');
    lines.push('');
    for (const m of project.milestones) {
      lines.push(`- **${m.title}** — ${m.date}${m.description ? ` — ${m.description}` : ''}`);
    }
    lines.push('');
  }

  if (project.changelog?.length) {
    lines.push('## 变更日志');
    lines.push('');
    for (const c of project.changelog) {
      lines.push(`- **${c.version}** (${c.date}) — ${c.info}`);
    }
    lines.push('');
  }

  if (project.tags?.length) {
    lines.push('## 标签');
    lines.push('');
    for (const tag of project.tags) {
      lines.push(`- \`${tag.name}\` (${tag.color})`);
    }
    lines.push('');
  }

  return lines.join('\n');
}

/**
 * 将项目导出为 Markdown Blob
 *
 * @param project - 项目数据对象
 * @returns 包含 Markdown 内容的 Blob 对象
 */
export function exportMarkdown(project: Project): Blob {
  return new Blob([generateMarkdown(project)], { type: 'text/markdown;charset=utf-8' });
}
