/** Versioned internal project storage. */
import type { Project } from '$lib/types';
import { migratePm, validatePm, CURRENT_PM_SCHEMA } from '$lib/utils/pm-schema';
import { migrateProjects, sortProjects } from '$lib/utils/project-order';
import { safeSetItem } from '$lib/stores/storage-health';
import { projectToPm } from './pm-file-repo';

const STORAGE_KEY = 'pm_projects';
const MIGRATION_BACKUP_KEY = 'pm_projects_pre_v4_backup';

interface StoredProjectsV4 {
  schema_version: 4;
  projects: Project[];
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null;
}

function projectFromStored(raw: unknown): Project {
  const source = asRecord(raw);
  if (!source || typeof source.id !== 'string' || typeof source.name !== 'string') {
    throw new Error('项目缺少 id 或 name');
  }
  const isV4 = Array.isArray(source.task_groups) && typeof source.default_task_group_id === 'string';
  const pmRaw: Record<string, unknown> = {
    version: '1.0',
    schema_version: isV4 ? 4 : 3,
    project: {
      id: source.id,
      name: source.name,
      description: source.description ?? '',
      template: source.template ?? 'default',
      color: source.color ?? '#a33b32',
      created_at: source.created_at ?? new Date(0).toISOString(),
      updated_at: source.updated_at ?? source.created_at ?? new Date(0).toISOString(),
      archived: source.archived ?? false,
      start_date: source.start_date,
      end_date: source.end_date,
      kanban_columns: source.kanban_columns,
      sync_enabled: source.sync_enabled,
      sort_order: source.sort_order,
      default_task_group_id: source.default_task_group_id,
    },
    task_groups: source.task_groups,
    tasks: source.tasks ?? [],
    tags: source.tags ?? [],
    changelog: source.changelog ?? [],
    milestones: source.milestones ?? [],
  };
  const migrated = migratePm(pmRaw).pm;
  const validation = validatePm(migrated, { source: `内部项目「${source.name}」` });
  if (!validation.ok) {
    const first = validation.errors[0];
    throw new Error(`${first.path || '项目'}：${first.message}`);
  }
  const pm = validation.pm;
  return {
    ...(source as unknown as Project),
    description: typeof source.description === 'string' ? source.description : '',
    color: typeof source.color === 'string' ? source.color : '#a33b32',
    template: (source.template as Project['template']) ?? 'default',
    created_at: String(source.created_at ?? new Date(0).toISOString()),
    updated_at: String(source.updated_at ?? source.created_at ?? new Date(0).toISOString()),
    archived: source.archived === true,
    sync_enabled: true,
    tasks: pm.tasks.map(task => ({
      ...task,
      description: task.description ?? '',
      tags: task.tags ?? [],
      due_date: task.due_date ?? null,
      due_time: task.due_time ?? null,
      start_offset: task.start_offset ?? null,
      dependencies: task.dependencies ?? [],
      subtasks: task.subtasks ?? [],
      comments: task.comments ?? [],
      tracked_start: task.tracked_start ?? null,
      reminder: task.reminder ?? null,
      recurrence: task.recurrence ?? null,
      completed_at: task.completed_at ?? null,
    })),
    task_groups: pm.task_groups,
    default_task_group_id: pm.project.default_task_group_id,
    tags: pm.tags ?? [],
    changelog: pm.changelog ?? [],
    milestones: pm.milestones ?? [],
    readme: typeof source.readme === 'string' ? source.readme : '',
  };
}

export function loadProjects(): Project[] {
  try {
    const text = localStorage.getItem(STORAGE_KEY);
    if (!text) return [];
    const parsed: unknown = JSON.parse(text);
    const wrapped = asRecord(parsed);
    const sourceList = Array.isArray(parsed)
      ? parsed
      : wrapped?.schema_version === CURRENT_PM_SCHEMA && Array.isArray(wrapped.projects)
        ? wrapped.projects
        : null;
    if (!sourceList) throw new Error('pm_projects 不是受支持的项目数组或 v4 包装对象');

    const migrated = sourceList.map(projectFromStored);
    const sorted = sortProjects(migrateProjects(migrated));
    if (Array.isArray(parsed)) {
      localStorage.setItem(MIGRATION_BACKUP_KEY, text);
      safeSetItem(STORAGE_KEY, JSON.stringify({ schema_version: 4, projects: sorted } satisfies StoredProjectsV4), 'v4 项目列表');
      for (const project of sorted) {
        safeSetItem(`pm_file_${project.id}`, JSON.stringify(projectToPm(project)), `项目「${project.name}」v4 快照`);
      }
    }
    return sorted;
  } catch (error) {
    console.error('[ProjectRepo] 内部项目迁移失败，原数据未覆盖', error);
    return [];
  }
}

export function saveProjects(list: Project[]): boolean {
  const payload: StoredProjectsV4 = { schema_version: 4, projects: list };
  return safeSetItem(STORAGE_KEY, JSON.stringify(payload), '项目列表');
}
