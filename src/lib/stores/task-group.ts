import { get } from 'svelte/store';
import type { StatusCategory, TaskGroup, TaskStatusDefinition } from '$lib/types';
import { genId } from '$lib/utils/id';
import { now } from '$lib/utils/date';
import { statusCategoryColor } from '$lib/utils/task-status';
import { projects, persistProject } from './project';
import { activeTaskGroupId, setActiveTaskGroup } from './filter';
import { pushHistory } from './history';
import { runHook } from '$lib/plugins';
import { t } from '$lib/i18n';

function mutateProject(projectId: string, mutate: (groupProject: import('$lib/types').Project) => import('$lib/types').Project): boolean {
  let changed = false;
  projects.update(list => list.map(project => {
    if (project.id !== projectId) return project;
    const updated = { ...mutate(project), updated_at: now() };
    changed = updated !== project;
    if (changed) persistProject(updated);
    return updated;
  }));
  return changed;
}

function newStatus(name: string, category: StatusCategory, order: number): TaskStatusDefinition {
  return { id: genId(), name, category, color: statusCategoryColor(category), sort_order: order };
}

export function createTaskGroup(projectId: string, name: string): string | null {
  const project = get(projects).find(item => item.id === projectId);
  if (!project || !name.trim()) return null;
  const translate = t.get();
  const initial = newStatus(translate('status.todo'), 'todo', 1024);
  const completion = newStatus(translate('status.done'), 'done', 2048);
  const group: TaskGroup = {
    id: genId(),
    name: name.trim(),
    sort_order: Math.max(0, ...project.task_groups.map(item => item.sort_order)) + 1024,
    archived: false,
    initial_status_id: initial.id,
    completion_status_id: completion.id,
    statuses: [initial, completion],
  };
  pushHistory();
  mutateProject(projectId, item => ({ ...item, task_groups: [...item.task_groups, group] }));
  setActiveTaskGroup(projectId, group.id);
  runHook('onTaskGroupCreate', { projectId, taskGroup: group });
  return group.id;
}

export function renameTaskGroup(projectId: string, groupId: string, name: string): boolean {
  const group = get(projects).find(item => item.id === projectId)?.task_groups.find(item => item.id === groupId);
  if (!group || !name.trim()) return false;
  pushHistory();
  const changed = mutateProject(projectId, project => ({
    ...project,
    task_groups: project.task_groups.map(group => group.id === groupId ? { ...group, name: name.trim() } : group),
  }));
  if (changed) runHook('onTaskGroupUpdate', { projectId, taskGroup: { ...group, name: name.trim() } });
  return changed;
}

export function setDefaultTaskGroup(projectId: string, groupId: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  if (!project?.task_groups.some(group => group.id === groupId && !group.archived)) return false;
  pushHistory();
  const changed = mutateProject(projectId, item => ({ ...item, default_task_group_id: groupId }));
  const group = project.task_groups.find(item => item.id === groupId)!;
  if (changed) runHook('onTaskGroupUpdate', { projectId, taskGroup: group });
  return changed;
}

export function archiveTaskGroup(projectId: string, groupId: string, replacementDefaultId?: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const active = project?.task_groups.filter(group => !group.archived) ?? [];
  const target = active.find(group => group.id === groupId);
  if (!project || !target || active.length <= 1) return false;
  const replacement = project.task_groups.find(group => group.id === replacementDefaultId && !group.archived && group.id !== groupId)
    ?? active.find(group => group.id !== groupId);
  if (!replacement) return false;
  pushHistory();
  const changed = mutateProject(projectId, item => ({
    ...item,
    default_task_group_id: item.default_task_group_id === groupId ? replacement.id : item.default_task_group_id,
    task_groups: item.task_groups.map(group => group.id === groupId ? { ...group, archived: true } : group),
  }));
  if (get(activeTaskGroupId) === groupId) setActiveTaskGroup(projectId, replacement.id);
  if (changed) runHook('onTaskGroupUpdate', { projectId, taskGroup: { ...target, archived: true } });
  return changed;
}

export function restoreTaskGroup(projectId: string, groupId: string): boolean {
  const group = get(projects).find(item => item.id === projectId)?.task_groups.find(item => item.id === groupId);
  if (!group || !group.archived) return false;
  pushHistory();
  const changed = mutateProject(projectId, item => ({
    ...item,
    task_groups: item.task_groups.map(group => group.id === groupId ? { ...group, archived: false } : group),
  }));
  if (changed) runHook('onTaskGroupUpdate', { projectId, taskGroup: { ...group, archived: false } });
  return changed;
}

export function deleteTaskGroup(projectId: string, groupId: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  if (!project || project.tasks.some(task => task.task_group_id === groupId)) return false;
  if (project.task_groups.filter(group => !group.archived && group.id !== groupId).length === 0) return false;
  const fallback = project.task_groups.find(group => !group.archived && group.id !== groupId)!;
  pushHistory();
  const changed = mutateProject(projectId, item => ({
    ...item,
    default_task_group_id: item.default_task_group_id === groupId ? fallback.id : item.default_task_group_id,
    task_groups: item.task_groups.filter(group => group.id !== groupId),
  }));
  if (get(activeTaskGroupId) === groupId) setActiveTaskGroup(projectId, fallback.id);
  if (changed) runHook('onTaskGroupDelete', { projectId, taskGroupId: groupId });
  return changed;
}

export function reorderTaskGroups(projectId: string, orderedIds: string[]): boolean {
  pushHistory();
  const changed = mutateProject(projectId, item => ({
    ...item,
    task_groups: item.task_groups.map(group => {
      const index = orderedIds.indexOf(group.id);
      return index < 0 ? group : { ...group, sort_order: (index + 1) * 1024 };
    }),
  }));
  if (changed) {
    const project = get(projects).find(item => item.id === projectId);
    for (const group of project?.task_groups ?? []) runHook('onTaskGroupUpdate', { projectId, taskGroup: group });
  }
  return changed;
}

export function createTaskStatus(projectId: string, groupId: string, name: string, category: StatusCategory): string | null {
  const project = get(projects).find(item => item.id === projectId);
  const group = project?.task_groups.find(item => item.id === groupId);
  if (!project || !group || !name.trim()) return null;
  const status = newStatus(name.trim(), category, Math.max(0, ...group.statuses.map(item => item.sort_order)) + 1024);
  pushHistory();
  mutateProject(projectId, item => ({
    ...item,
    task_groups: item.task_groups.map(candidate => candidate.id === groupId
      ? { ...candidate, statuses: [...candidate.statuses, status] }
      : candidate),
  }));
  runHook('onStatusDefinitionCreate', { projectId, taskGroupId: groupId, status });
  return status.id;
}

export function updateTaskStatusDefinition(
  projectId: string,
  groupId: string,
  statusId: string,
  data: Partial<Pick<TaskStatusDefinition, 'name' | 'color' | 'category' | 'sort_order'>>,
): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const group = project?.task_groups.find(item => item.id === groupId);
  const current = group?.statuses.find(item => item.id === statusId);
  if (!project || !group || !current || (data.name !== undefined && !data.name.trim())) return false;
  if (statusId === group.completion_status_id && data.category && data.category !== 'done') return false;
  const categoryChanged = data.category !== undefined && data.category !== current.category;
  const changedAt = now();
  pushHistory();
  const changed = mutateProject(projectId, item => ({
    ...item,
    task_groups: item.task_groups.map(candidate => candidate.id === groupId
      ? { ...candidate, statuses: candidate.statuses.map(status => status.id === statusId ? { ...status, ...data, name: data.name?.trim() ?? status.name } : status) }
      : candidate),
    tasks: categoryChanged
      ? item.tasks.map(task => task.task_group_id === groupId && task.status_id === statusId
        ? { ...task, completed_at: data.category === 'done' ? (task.completed_at ?? changedAt) : null, updated_at: changedAt }
        : task)
      : item.tasks,
  }));
  if (changed) runHook('onStatusDefinitionUpdate', {
    projectId,
    taskGroupId: groupId,
    status: { ...current, ...data, name: data.name?.trim() ?? current.name },
  });
  return changed;
}

export function setGroupInitialStatus(projectId: string, groupId: string, statusId: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const group = project?.task_groups.find(item => item.id === groupId);
  if (!group?.statuses.some(status => status.id === statusId)) return false;
  pushHistory();
  const changed = mutateProject(projectId, item => ({ ...item, task_groups: item.task_groups.map(candidate => candidate.id === groupId ? { ...candidate, initial_status_id: statusId } : candidate) }));
  if (changed) runHook('onTaskGroupUpdate', { projectId, taskGroup: { ...group, initial_status_id: statusId } });
  return changed;
}

export function setGroupCompletionStatus(projectId: string, groupId: string, statusId: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const group = project?.task_groups.find(item => item.id === groupId);
  const target = group?.statuses.find(status => status.id === statusId);
  if (!project || !group || !target) return false;

  const ordered = group.statuses.slice().sort((a, b) => a.sort_order - b.sort_order);
  const orderedIds = [...ordered.filter(status => status.id !== statusId).map(status => status.id), statusId];
  const nextInitialStatusId = group.initial_status_id === statusId
    ? orderedIds.find(id => id !== statusId) ?? group.initial_status_id
    : group.initial_status_id;
  const alreadyNormalized = group.completion_status_id === statusId
    && group.initial_status_id === nextInitialStatusId
    && ordered.every((status, index) => status.id === orderedIds[index]
      && status.sort_order === (index + 1) * 1024
      && status.category === (status.id === statusId ? 'done' : status.category === 'done' ? 'active' : status.category));
  if (alreadyNormalized) return false;

  const changedAt = now();
  const updatedStatuses = group.statuses.map(status => ({
    ...status,
    category: status.id === statusId ? 'done' as const : status.category === 'done' ? 'active' as const : status.category,
    sort_order: (orderedIds.indexOf(status.id) + 1) * 1024,
  }));
  const updatedGroup = {
    ...group,
    initial_status_id: nextInitialStatusId,
    completion_status_id: statusId,
    statuses: updatedStatuses,
  };

  pushHistory();
  const changed = mutateProject(projectId, item => ({
    ...item,
    task_groups: item.task_groups.map(candidate => candidate.id === groupId ? updatedGroup : candidate),
    tasks: item.tasks.map(task => {
      if (task.task_group_id !== groupId) return task;
      const currentStatus = group.statuses.find(status => status.id === task.status_id);
      const nextStatus = updatedStatuses.find(status => status.id === task.status_id);
      if (!currentStatus || !nextStatus || currentStatus.category === nextStatus.category) return task;
      return {
        ...task,
        completed_at: nextStatus.category === 'done' ? (task.completed_at ?? changedAt) : null,
        updated_at: changedAt,
      };
    }),
  }));
  if (changed) {
    runHook('onTaskGroupUpdate', { projectId, taskGroup: updatedGroup });
    for (const status of updatedStatuses) {
      const previous = group.statuses.find(item => item.id === status.id);
      if (previous && (previous.category !== status.category || previous.sort_order !== status.sort_order)) {
        runHook('onStatusDefinitionUpdate', { projectId, taskGroupId: groupId, status });
      }
    }
  }
  return changed;
}

export function reorderTaskStatuses(projectId: string, groupId: string, orderedIds: string[]): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const group = project?.task_groups.find(item => item.id === groupId);
  if (!project || !group || orderedIds.length !== group.statuses.length) return false;
  const knownIds = new Set(group.statuses.map(status => status.id));
  if (new Set(orderedIds).size !== knownIds.size || orderedIds.some(id => !knownIds.has(id))) return false;
  if (group.statuses
    .slice()
    .sort((a, b) => a.sort_order - b.sort_order)
    .every((status, index) => status.id === orderedIds[index])) return false;

  pushHistory();
  const changed = mutateProject(projectId, item => ({
    ...item,
    task_groups: item.task_groups.map(candidate => candidate.id === groupId
      ? {
          ...candidate,
          statuses: candidate.statuses.map(status => ({
            ...status,
            sort_order: (orderedIds.indexOf(status.id) + 1) * 1024,
          })),
        }
      : candidate),
  }));
  if (changed) {
    const updatedGroup = get(projects)
      .find(item => item.id === projectId)
      ?.task_groups.find(item => item.id === groupId);
    for (const status of updatedGroup?.statuses ?? []) {
      runHook('onStatusDefinitionUpdate', { projectId, taskGroupId: groupId, status });
    }
  }
  return changed;
}

export function deleteTaskStatus(projectId: string, groupId: string, statusId: string, migrateToStatusId?: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const group = project?.task_groups.find(item => item.id === groupId);
  if (!project || !group || group.statuses.length <= 1 || statusId === group.initial_status_id || statusId === group.completion_status_id) return false;
  const references = project.tasks.filter(task => task.task_group_id === groupId && task.status_id === statusId);
  const target = migrateToStatusId ? group.statuses.find(status => status.id === migrateToStatusId && status.id !== statusId) : null;
  if (references.length > 0 && !target) return false;
  pushHistory();
  const changed = mutateProject(projectId, item => ({
    ...item,
    task_groups: item.task_groups.map(candidate => candidate.id === groupId
      ? { ...candidate, statuses: candidate.statuses.filter(status => status.id !== statusId) }
      : candidate),
    tasks: item.tasks.map(task => task.task_group_id === groupId && task.status_id === statusId && target
      ? { ...task, status_id: target.id, completed_at: target.category === 'done' ? now() : null, updated_at: now() }
      : task),
  }));
  if (changed) runHook('onStatusDefinitionDelete', { projectId, taskGroupId: groupId, statusId, migrateToStatusId: target?.id });
  return changed;
}
