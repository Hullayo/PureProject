/**
 * 任务领域类型定义
 *
 * 定义了任务（Task）及其相关子结构的 TypeScript 接口和类型，
 * 包括优先级、状态、子任务、依赖关系等。
 *
 * @module types/task
 */

/** 循环频率 */
export type RecurrenceFreq = 'daily' | 'weekly' | 'monthly' | 'yearly';

/**
 * 循环规则
 *
 * `Task.recurrence == null` 即普通任务（**刻意不加 `isRecurring` 冗余字段**）。
 * 每完成一次就「消费」一次规则并生成下一个实例，见 `stores/task.ts` 的
 * `completeRecurringTask()` 与 `utils/recurrence.ts`。
 */
export interface RecurrenceRule {
  /** 频率 */
  freq: RecurrenceFreq;
  /** 步长（默认 1，例如每 2 周）；始终 ≥ 1 */
  interval: number;
  /** weekly 时的星期几（0=周日…6=周六）；monthly / 其它频率不用 */
  byWeekday?: number[];
  /** 结束条件 */
  end: 'never' | 'count' | 'until';
  /** end='count' 时剩余的次数（每完成一次减 1） */
  count?: number;
  /** end='until' 时的截止日期 YYYY-MM-DD（超过则不再生成） */
  until?: string;
  /** 本实例由哪个任务触发（仅由完成触发生成的新实例上写，便于追溯） */
  sourceTaskId?: string;
}

/** 任务优先级枚举 */
export type Priority = 'high' | 'medium' | 'low';

/**
 * 子任务
 * 每个子任务有独立的完成状态，用于将大任务拆分为可管理的小步骤。
 */
export interface Subtask {
  /** 唯一标识符 */
  id: string;
  /** 子任务标题 */
  title: string;
  /** 是否已完成 */
  done: boolean;
}

/**
 * 任务依赖关系
 * 表示当前任务依赖于另一个任务，dayOffset 表示依赖任务完成后多少天开始。
 */
export interface Dependency {
  /** 被依赖的任务 ID */
  taskId: string;
  /** 依赖任务完成后的偏移天数 */
  dayOffset: number;
}

/**
 * 任务评论/留言
 * 用于记录任务的进展、反馈、讨论等内容。
 */
export interface TaskComment {
  /** 唯一标识符 */
  id: string;
  /** 评论内容（支持多行文本） */
  content: string;
  /** 创建时间（ISO 格式） */
  created_at: string;
}

/**
 * 任务（Task）
 *
 * 项目中的核心实体，包含标题、描述、状态、优先级、标签、
 * 截止日期、子任务、依赖关系、时间追踪和提醒等完整信息。
 */
export interface Task {
  /** 唯一标识符（UUID 格式） */
  id: string;
  /** 任务标题 */
  title: string;
  /** 任务颜色（十六进制颜色值，如 #4f46e5） */
  color?: string;
  /** 任务详细描述（支持 Markdown） */
  description: string;
  /** 所属任务组 ID */
  task_group_id: string;
  /** 所属任务组内的状态 ID */
  status_id: string;
  /** 真正进入 done 类别的时间；其它类别必须为 null */
  completed_at: string | null;
  /** 完成或取消前的状态，用于重新打开时恢复 */
  status_before_closed_id?: string;
  /** 优先级 */
  priority: Priority;
  /** 标签名称列表（关联到 Project.tags） */
  tags: string[];
  /** 截止日期（ISO 日期字符串，null 表示未设置） */
  due_date: string | null;
  /**
   * 截止时间（HH:mm 格式）
   * 与 due_date 组合成具体截止时刻；null 表示使用当天 23:59。
   */
  due_time: string | null;
  /**
   * 开始偏移天数
   * 相对于项目创建日期，多少天后开始此任务。
   * 用于甘特图/时间线视图计算起始位置。
   */
  start_offset: number | null;
  /** 依赖的其他任务列表 */
  dependencies: Dependency[];
  /** 子任务列表 */
  subtasks: Subtask[];
  /** 评论/留言列表 */
  comments: TaskComment[];
  /**
   * 时间追踪：开始计时的时间戳（ISO 格式）
   * null 表示尚未开始计时。
   * 一旦设置，任务不能停止计时，工时 = 完成时间 - tracked_start。
   */
  tracked_start: string | null;
  /**
   * 提醒时间（ISO 格式）
   * 当到达此时间时，系统会发送通知提醒用户。
   * null 表示未设置提醒。
   */
  reminder: string | null;
  /** 关联的 Git 仓库路径（本地路径） */
  git_repo_path?: string;
  /**
   * 循环规则；`null` / `undefined` = 普通任务
   *
   * 完成循环任务时会生成下一个实例（新的 Task），本任务的 `recurrence` 置 null。
   */
  recurrence?: RecurrenceRule | null;
  /** 创建时间（ISO 格式） */
  created_at: string;
  /** 最后更新时间（ISO 格式） */
  updated_at: string;
}
