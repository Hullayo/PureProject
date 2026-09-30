/**
 * 颜色方案类型定义
 *
 * 定义了可命名的颜色项，用于项目、任务、里程碑、标签等颜色选择。
 *
 * @module types/color
 */

/**
 * 颜色项
 *
 * 每个颜色包含语义名称和十六进制色值，可被用户自定义。
 */
export interface ColorItem {
  /** 唯一标识符 */
  id: string;
  /** 语义名称（如“紧急”、“进行中”） */
  name: string;
  /** 十六进制色值（如 #4f46e5） */
  value: string;
  /** 是否为内置颜色（不可删除） */
  builtin: boolean;
}
