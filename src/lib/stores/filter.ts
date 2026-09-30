/** Filtering and selection state shared by all project views. */
import { writable, derived, get } from 'svelte/store';
import type { StatusCategory } from '$lib/types';
import { projects, activeProjectId } from './project';
import { registerActiveTaskId, registerActiveTaskGroupId } from './history';
import { getTaskStatus, selectDefaultTaskGroup } from '$lib/utils/task-status';

const LAST_GROUP_KEY = 'pm_last_task_group_by_project';

function readLastGroups(): Record<string, string> {
  try { return JSON.parse(localStorage.getItem(LAST_GROUP_KEY) || '{}'); } catch { return {}; }
}

function rememberGroup(projectId: string, taskGroupId: string): void {
  try {
    const value = readLastGroups();
    value[projectId] = taskGroupId;
    localStorage.setItem(LAST_GROUP_KEY, JSON.stringify(value));
  } catch { /* UI preference failure must not block project writes. */ }
}

export const activeProject = derived(
  [projects, activeProjectId],
  ([$projects, $activeProjectId]) => $projects.find(project => project.id === $activeProjectId) ?? null,
);

/** Board/detail navigation group. This is a local UI preference, never exported. */
export const activeTaskGroupId = writable<string | null>(null);
/** List/calendar/timeline/graph group filter. */
export const taskGroupFilter = writable<string | 'all'>('all');
/** A status ID when a group is selected, otherwise a semantic category. */
export const statusFilter = writable<string | StatusCategory | 'all'>('all');
export const searchQuery = writable('');
export const priorityFilter = writable<'all' | 'high' | 'medium' | 'low'>('all');
export const tagFilter = writable('all');
export const activeTaskId = writable<string | null>(null);

registerActiveTaskId(activeTaskId);
registerActiveTaskGroupId(activeTaskGroupId);

export function setActiveTaskGroup(projectId: string, taskGroupId: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const group = project?.task_groups.find(item => item.id === taskGroupId && !item.archived);
  if (!project || !group) return false;
  activeTaskGroupId.set(group.id);
  rememberGroup(project.id, group.id);
  const selectedTaskId = get(activeTaskId);
  if (selectedTaskId) {
    const selected = project.tasks.find(task => task.id === selectedTaskId);
    if (selected && selected.task_group_id !== group.id) activeTaskId.set(null);
  }
  return true;
}

export function selectTask(projectId: string, taskId: string): boolean {
  const project = get(projects).find(item => item.id === projectId);
  const task = project?.tasks.find(item => item.id === taskId);
  if (!project || !task) return false;
  activeProjectId.set(project.id);
  setActiveTaskGroup(project.id, task.task_group_id);
  activeTaskId.set(task.id);
  return true;
}

/** Repair group selection when projects load, switch, archive, or migrate. */
derived([activeProject, activeTaskGroupId], ([$project, $groupId]) => ({ project: $project, groupId: $groupId }))
  .subscribe(({ project, groupId }) => {
    if (!project) {
      if (groupId !== null) activeTaskGroupId.set(null);
      return;
    }
    const preferred = groupId ?? readLastGroups()[project.id];
    const selected = selectDefaultTaskGroup(project, preferred);
    if (selected && selected.id !== groupId) {
      activeTaskGroupId.set(selected.id);
      rememberGroup(project.id, selected.id);
    }
  });

export const activeTaskGroup = derived(
  [activeProject, activeTaskGroupId],
  ([$project, $groupId]) => $project?.task_groups.find(group => group.id === $groupId) ?? null,
);

export const filteredTasks = derived(
  [activeProject, taskGroupFilter, statusFilter, searchQuery, priorityFilter, tagFilter],
  ([$project, $group, $status, $search, $priority, $tag]) => {
    if (!$project) return [];
    let tasks = $project.tasks.filter(task => {
      const group = $project.task_groups.find(item => item.id === task.task_group_id);
      return !!group && !group.archived;
    });
    if ($group !== 'all') tasks = tasks.filter(task => task.task_group_id === $group);
    if ($status !== 'all') {
      tasks = tasks.filter(task => {
        const status = getTaskStatus($project, task);
        return $group === 'all' ? status?.category === $status : task.status_id === $status;
      });
    }
    if ($priority !== 'all') tasks = tasks.filter(task => task.priority === $priority);
    if ($tag !== 'all') tasks = tasks.filter(task => task.tags.includes($tag));
    if ($search.trim()) {
      const query = $search.trim().toLowerCase();
      tasks = tasks.filter(task => task.title.toLowerCase().includes(query) || task.description.toLowerCase().includes(query));
    }
    return tasks;
  },
);

export const activeTask = derived(
  [activeProject, activeTaskId],
  ([$project, $taskId]) => $project?.tasks.find(task => task.id === $taskId) ?? null,
);
