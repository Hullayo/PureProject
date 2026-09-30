import type { Project, Task, TaskGroup, TaskStatusDefinition } from '$lib/types';

export interface ProjectTaskIndex {
  groupById: Map<string, TaskGroup>;
  statusById: Map<string, TaskStatusDefinition>;
  taskById: Map<string, Task>;
}

export function buildProjectTaskIndex(project: Project): ProjectTaskIndex {
  const groupById = new Map<string, TaskGroup>();
  const statusById = new Map<string, TaskStatusDefinition>();
  const taskById = new Map<string, Task>();
  for (const group of project.task_groups) {
    groupById.set(group.id, group);
    for (const status of group.statuses) statusById.set(status.id, status);
  }
  for (const task of project.tasks) taskById.set(task.id, task);
  return { groupById, statusById, taskById };
}
