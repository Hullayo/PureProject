/**
 * 流程图数据类型定义
 *
 * 定义了流程图中节点与边的数据结构，作为 Project 的扩展字段持久化。
 *
 * @module types/flowchart
 */

/** 流程图节点类型 */
export type FlowNodeType = 'rect' | 'diamond' | 'circle';

/**
 * 流程图节点
 *
 * 表示流程图中的一个步骤或任务，包含位置、尺寸、文本与颜色。
 */
export interface FlowNode {
  /** 唯一标识符 */
  id: string;
  /** 节点形状 */
  type: FlowNodeType;
  /** 左上角 x 坐标 */
  x: number;
  /** 左上角 y 坐标 */
  y: number;
  /** 宽度 */
  w: number;
  /** 高度 */
  h: number;
  /** 节点文本标签 */
  label: string;
  /** 节点颜色 */
  color: string;
}

/**
 * 流程图边
 *
 * 表示节点之间的有向连接。
 */
export interface FlowEdge {
  /** 唯一标识符 */
  id: string;
  /** 起点节点 ID */
  from: string;
  /** 终点节点 ID */
  to: string;
}

/**
 * 流程图数据
 *
 * 包含一组节点与边，可完整描述一个流程图。
 */
export interface FlowchartData {
  /** 节点列表 */
  nodes: FlowNode[];
  /** 边列表 */
  edges: FlowEdge[];
}
