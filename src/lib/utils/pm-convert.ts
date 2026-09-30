/** Pure conversion helpers between the in-memory project model and schema v4 `.pm`. */
import type { PmFile, Project } from '$lib/types';
import { now } from '$lib/utils/date';
import { CURRENT_PM_SCHEMA } from '$lib/utils/pm-schema';

export function projectToPm(proj: Project): PmFile {
  const pm: PmFile = {
    version: '1.0',
    schema_version: CURRENT_PM_SCHEMA,
    project: {
      id: proj.id,
      name: proj.name,
      description: proj.description,
      template: proj.template,
      color: proj.color,
      created_at: proj.created_at,
      updated_at: proj.updated_at,
      archived: proj.archived,
      start_date: proj.start_date,
      end_date: proj.end_date,
      sync_enabled: true,
      sort_order: proj.sort_order,
      default_task_group_id: proj.default_task_group_id,
      // `storage` is device-local and must never cross machines through `.pm`.
    },
    task_groups: proj.task_groups,
    tasks: proj.tasks,
    tags: proj.tags,
    changelog: proj.changelog,
    milestones: proj.milestones,
    flowchart: proj.flowchart,
  };

  if (proj.legacy_template) {
    pm.readme_file = proj.legacy_readme_file;
    pm.template = {
      dirs: [...proj.legacy_template.dirs],
      files: [...proj.legacy_template.files],
      file_contents: { ...proj.legacy_template.file_contents },
    };
  } else if (proj.readme) {
    // Old internal storage kept README as a string. Preserve that string only;
    // V1.1 must not regenerate the removed template's other files.
    const readmeFile = proj.legacy_readme_file || 'README.md';
    pm.readme_file = readmeFile;
    pm.template = { dirs: [], files: [readmeFile], file_contents: { [readmeFile]: proj.readme } };
  }

  return pm;
}

export function projectFromPm(pm: PmFile, id: string): Project | null {
  if (!pm.version || !pm.project) return null;
  const readmeFile = pm.readme_file || 'README.md';
  const readmeContent = pm.template?.file_contents?.[readmeFile] || '';
  const legacyFileContents = pm.template?.file_contents ?? {};

  return {
    id,
    name: pm.project.name,
    description: pm.project.description || '',
    color: pm.project.color || '#a33b32',
    template: pm.project.template || 'default',
    created_at: pm.project.created_at || now(),
    updated_at: now(),
    archived: pm.project.archived ?? false,
    start_date: pm.project.start_date,
    end_date: pm.project.end_date,
    kanban_columns: pm.project.kanban_columns,
    sync_enabled: true,
    sort_order: typeof pm.project.sort_order === 'number' ? pm.project.sort_order : undefined,
    default_task_group_id: pm.project.default_task_group_id,
    task_groups: pm.task_groups,
    storage: undefined,
    tasks: (pm.tasks || []).map(task => ({
      ...task,
      due_time: task.due_time ?? null,
      start_offset: task.start_offset ?? null,
      dependencies: task.dependencies ?? [],
      subtasks: task.subtasks ?? [],
      tracked_start: task.tracked_start ?? null,
      reminder: task.reminder ?? null,
      recurrence: task.recurrence ?? null,
      completed_at: task.completed_at ?? null,
    })),
    tags: pm.tags || [],
    changelog: pm.changelog || [],
    milestones: pm.milestones || [],
    readme: readmeContent,
    legacy_template: pm.template ? {
      dirs: [...(pm.template.dirs ?? [])],
      files: [...(pm.template.files ?? Object.keys(legacyFileContents))],
      file_contents: { ...legacyFileContents },
    } : undefined,
    legacy_readme_file: pm.readme_file,
    flowchart: pm.flowchart,
  };
}
