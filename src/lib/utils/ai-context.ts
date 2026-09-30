import type { Project } from '$lib/types';
import { getTaskStatus } from './task-status';

export function buildProjectAiTaskContext(project: Project): string {
  return project.tasks.map(task => {
    const group = project.task_groups.find(item => item.id === task.task_group_id);
    const status = getTaskStatus(project, task);
    return `- [${group?.name ?? '未知任务组'} / ${status?.name ?? '未知状态'} / ${status?.category ?? 'todo'}] ${task.title}${task.description ? `: ${task.description}` : ''}`;
  }).join('\n');
}
