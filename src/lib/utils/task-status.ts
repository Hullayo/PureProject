import type { Project, StatusCategory, Task, TaskGroup, TaskStatusDefinition } from '$lib/types';

const STATUS_COLORS: Record<StatusCategory, string> = {
  todo: '#6b7280',
  active: '#4f46e5',
  done: '#10b981',
  cancelled: '#9ca3af',
};

/** 小型、稳定的 FNV-1a 哈希。迁移在不同设备上必须生成完全相同的外键。 */
export function stableId(prefix: string, value: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < value.length; i++) {
    hash ^= value.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return `${prefix}-${(hash >>> 0).toString(16).padStart(8, '0')}`;
}

export function legacyStatusCategory(value: string): StatusCategory {
  const normalized = value.trim().toLowerCase().replace(/[\s_-]+/g, '');
  if (['done', 'complete', 'completed', 'finished', '已完成', '完成', '已关闭', '关闭'].includes(normalized)) return 'done';
  if (['cancelled', 'canceled', 'cancel', '已取消', '取消'].includes(normalized)) return 'cancelled';
  if (['inprogress', 'active', 'doing', '进行中', '处理中', '开发中', '修复中', '评审中'].includes(normalized)) return 'active';
  return 'todo';
}

function legacyStatusName(value: string): string {
  if (value === 'todo') return '待办';
  if (value === 'in_progress') return '进行中';
  if (value === 'done') return '已完成';
  if (value === 'cancelled') return '已取消';
  return value.trim() || '待办';
}

export function createLegacyTaskGroup(projectKey: string, values: string[] = []): TaskGroup {
  const rawValues = values.length ? values : ['todo', 'in_progress', 'done'];
  const unique = [...new Set(rawValues.map(v => String(v || 'todo')))];
  if (!unique.some(v => legacyStatusCategory(v) === 'done')) unique.push('done');
  const groupId = stableId('group', `v4:group:${projectKey}:default`);
  const statuses: TaskStatusDefinition[] = unique.map((value, index) => {
    const category = legacyStatusCategory(value);
    return {
      id: stableId('status', `v4:status:${projectKey}:${value}`),
      name: legacyStatusName(value),
      color: STATUS_COLORS[category],
      category,
      sort_order: (index + 1) * 1024,
    };
  });
  const initial = statuses.find(status => status.category === 'todo') ?? statuses.find(status => status.category === 'active') ?? statuses[0];
  const completion = statuses.find(status => status.category === 'done')!;
  return {
    id: groupId,
    name: '默认任务组',
    sort_order: 1024,
    archived: false,
    initial_status_id: initial.id,
    completion_status_id: completion.id,
    statuses,
  };
}

export function getTaskGroup(project: Project, task: Task): TaskGroup | null {
  return project.task_groups.find(group => group.id === task.task_group_id) ?? null;
}

export function getStatusById(project: Project, statusId: string): TaskStatusDefinition | null {
  for (const group of project.task_groups) {
    const status = group.statuses.find(item => item.id === statusId);
    if (status) return status;
  }
  return null;
}

export function getTaskStatus(project: Project, task: Task): TaskStatusDefinition | null {
  const group = getTaskGroup(project, task);
  return group?.statuses.find(status => status.id === task.status_id) ?? null;
}

export function getInitialStatus(group: TaskGroup): TaskStatusDefinition {
  return group.statuses.find(status => status.id === group.initial_status_id) ?? group.statuses[0];
}

export function getCompletionStatus(group: TaskGroup): TaskStatusDefinition {
  return group.statuses.find(status => status.id === group.completion_status_id)
    ?? group.statuses.find(status => status.category === 'done')
    ?? group.statuses[0];
}

export function isTaskCompleted(project: Project, task: Task): boolean {
  return getTaskStatus(project, task)?.category === 'done';
}

export function isTaskClosed(project: Project, task: Task): boolean {
  const category = getTaskStatus(project, task)?.category;
  return category === 'done' || category === 'cancelled';
}

export function satisfiesDependency(project: Project, task: Task): boolean {
  return getTaskStatus(project, task)?.category === 'done';
}

export function activeTaskGroups(project: Project): TaskGroup[] {
  return project.task_groups.filter(group => !group.archived).sort((a, b) => a.sort_order - b.sort_order);
}

export function selectDefaultTaskGroup(project: Project, preferredId?: string | null): TaskGroup | null {
  const groups = activeTaskGroups(project);
  return groups.find(group => group.id === preferredId)
    ?? groups.find(group => group.id === project.default_task_group_id)
    ?? groups[0]
    ?? null;
}

export function statusCategoryColor(category: StatusCategory): string {
  return STATUS_COLORS[category];
}
