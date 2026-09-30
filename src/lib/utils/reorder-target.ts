/**
 * 拖拽落点换算
 *
 * 拖拽时 DOM 里看到的是**过滤 / 分列后**的子集，而 store 要的是**全量数组**下标，
 * 两者不能直接混用。这里统一用「目标位置后面那一个」当锚点做换算。
 *
 * 语义与 `arr.splice(from, 1); arr.splice(index, 0, moved)` 对齐：
 * 返回值的含义是「移除被拖元素之后」的插入下标。
 *
 * @module utils/reorder-target
 */

export interface HasId {
  id: string;
}

/**
 * 把「区域内的落点」换算成全量数组里的插入下标
 *
 * @param all - 全量数组（如 `project.tasks` 或 `$projects`）
 * @param area - 被拖元素当前所在 / 目标所在的可视区域（如筛选后的列表、看板的某一列）
 * @param movedId - 被拖元素 id
 * @param targetIndex - 区域内「移除被拖元素后」的插入下标（由 sortable action 给出）
 * @param fallback - 区域内没有其它元素时的行为：
 *   - `'noop'`（默认）保持原位 —— 竖列表场景（筛选后只剩一项时拖动不应把它扔到末尾）
 *   - `'append'` 追加到全量末尾 —— 看板拖进空列
 * @returns 全量数组里「移除被拖元素后」的插入下标；无法换算时返回 -1
 */
export function resolveInsertIndex<T extends HasId>(
  all: T[],
  area: T[],
  movedId: string,
  targetIndex: number,
  fallback: 'noop' | 'append' = 'noop'
): number {
  const fromAll = all.findIndex(x => x.id === movedId);
  if (fromAll === -1) return -1;

  /** 同一元素在「移除被拖项后的数组」中的下标 */
  const removedIndex = (id: string): number => {
    const i = all.findIndex(x => x.id === id);
    if (i === -1) return -1;
    return i < fromAll ? i : i - 1;
  };

  const rest = area.filter(x => x.id !== movedId);
  const after = rest[targetIndex];
  if (after) return removedIndex(after.id);

  const last = rest[rest.length - 1];
  if (last) return removedIndex(last.id) + 1;

  // 区域内没有其它元素
  return fallback === 'append' ? all.length - 1 : fromAll;
}
