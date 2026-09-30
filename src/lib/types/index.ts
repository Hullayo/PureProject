/**
 * 类型定义统一导出
 *
 * 将所有领域类型集中导出，方便其他模块统一引入。
 *
 * @example
 * import type { Task, Project, PmFile } from '$lib/types';
 *
 * @module types
 */

// 任务相关类型
export type { Priority, Subtask, Dependency, TaskComment, Task, RecurrenceFreq, RecurrenceRule } from './task';

// 项目相关类型
export type { TemplateName, Tag, ChangelogEntry, Milestone, Project, ProjectStorage, FileTreeEntry, StatusCategory, TaskStatusDefinition, TaskGroup } from './project';

// 颜色类型
export type { ColorItem } from './color';

// 流程图类型
export type { FlowNodeType, FlowNode, FlowEdge, FlowchartData } from './flowchart';

// .pm 文件格式类型
export type { ProjectTemplate, PmFile } from './pm-file';
