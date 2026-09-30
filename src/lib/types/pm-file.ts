/**
 * .pm 文件格式类型定义
 *
 * 定义了 .pm 文件（项目导出格式）的 TypeScript 接口。
 * .pm 文件是 JSON 格式，包含完整的项目数据和模板文件内容。
 *
 * @module types/pm-file
 */

import type { Task } from './task';
import type { Tag, ChangelogEntry, Milestone, TemplateName, TaskGroup } from './project';
import type { FlowchartData } from './flowchart';

/**
 * 旧项目模板（序列化格式）
 *
 * V1.1 只为无损兼容旧 `.pm` 文件而保留，不再用于生成新文件。
 * file_contents 中存储的是已生成的文件内容字符串。
 */
export interface ProjectTemplate {
  /** 目录列表 */
  dirs: string[];
  /** 文件列表 */
  files: string[];
  /** 文件路径 → 文件内容的映射 */
  file_contents: Record<string, string>;
}

/**
 * .pm 文件结构
 *
 * 项目导出的完整数据格式，包含项目元数据、任务、标签、
 * 变更日志、里程碑和模板文件内容。
 * 可用于项目备份、分享和导入。
 */
export interface PmFile {
  /** 文件格式版本号（人类可读，历史值固定 '1.0'） */
  version: string;
  /**
   * 数据结构版本（机器用，见 utils/pm-schema.ts 的 CURRENT_PM_SCHEMA）
   *
   * 缺失 = v1（历史文件）；导入时由 `parsePmText()` 迁移到当前版本。
   */
  schema_version?: number;
  /** 项目元数据 */
  project: {
    id?: string;
    name: string;
    description: string;
    template: TemplateName;
    color: string;
    created_at: string;
    updated_at: string;
    archived?: boolean;
    start_date?: string;
    end_date?: string;
    kanban_columns?: string[];
    /** 历史兼容字段；当前版本导出时固定为 true，由全局同步模式统一控制。 */
    sync_enabled?: boolean;
    /** 项目排序值（跨设备同步顺序用，见 Project.sort_order） */
    sort_order?: number;
    default_task_group_id: string;
    storage?: { type: 'local' | 'server'; path?: string; url?: string };
  };
  /** v4：项目下完整的任务组和状态定义 */
  task_groups: TaskGroup[];
  /** README 文件路径 */
  readme_file?: string;
  /** 旧项目模板结构和文件内容；V1.1 仅透传 */
  template?: ProjectTemplate;
  /** 任务列表 */
  tasks: Task[];
  /** 标签列表 */
  tags: Tag[];
  /** 变更日志 */
  changelog: ChangelogEntry[];
  /** 里程碑列表 */
  milestones: Milestone[];
  /** 流程图数据 */
  flowchart?: FlowchartData;
}
