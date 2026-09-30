/**
 * 向后兼容的类型导出桥接模块
 *
 * 保持旧的导入路径 `from '$lib/types'` 可用。
 * 新类型定义位于 `src/lib/types/` 目录。
 */

export type {
  Priority,
  Subtask,
  Dependency,
  TaskComment,
  Task,
  RecurrenceFreq,
  RecurrenceRule,
  TemplateName,
  Tag,
  ChangelogEntry,
  Milestone,
  Project,
  ProjectStorage,
  FileTreeEntry,
  ProjectTemplate,
  PmFile,
  ColorItem,
  FlowNodeType,
  FlowNode,
  FlowEdge,
  FlowchartData,
  StatusCategory,
  TaskStatusDefinition,
  TaskGroup
} from './types/index';
