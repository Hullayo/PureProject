/**
 * 实时时长刷新（ticker）
 *
 * 背景：工时「正在计时」时，界面需要**每秒**更新 `H:MM:SS`。之前三处组件各自写了
 * `setInterval`，但 Svelte 5 的响应式只跟踪模板中**实际读取**的变量，写了 `tick` 却没在
 * 模板里引用 → 不会重渲染（表现为「工时一直不变，刷新页面才跳」）。
 *
 * 这里抽一个公共 ticker：
 * - 页面**可见**时每 1s 触发一次（`document.visibilityState` + `visibilitychange`）
 * - 页面**不可见**（移动端切后台）时暂停，省电、不空转
 * - `active()` 返回 false 时不发值（避免无谓的重渲染）
 *
 * 与 `utils/date.ts` 的 `formatDurationHMS(start, end, nowMs)` 配合使用：把 ticker 的
 * 值作为 `nowMs` 传入，格式化结果就会随 ticker 变化。
 *
 * @module utils/live-duration
 *
 * @example
 * ```svelte
 * <script lang="ts">
 *   import { createTicker } from '$lib/utils/live-duration';
 *   import { formatDurationHMS } from '$lib/utils/date';
 *   let { start } = $props();
 *   const ticker = createTicker(() => true);
 * </script>
 * <span>{formatDurationHMS(start, undefined, $ticker)}</span>
 * ```
 */

import { readable } from 'svelte/store';

/** 刷新间隔：1 秒 */
const TICK_MS = 1000;

/**
 * 创建一个每秒触发的时间戳 store（`Readable<number>`）
 *
 * @param active - 返回当前是否需要刷新；返回 false 时不发新值（但计时器保持运行，
 *                 以便之后变为 true 时能立即继续）
 * @returns 可订阅的 `Readable<number>`（值为 `Date.now()`）
 */
export function createTicker(active: () => boolean): { subscribe: (run: (v: number) => void) => () => void } {
  return readable(Date.now(), (set) => {
    let timer: ReturnType<typeof setInterval> | null = null;
    let disposed = false;
    const hasDocument = typeof document !== 'undefined';

    const tick = () => {
      if (active()) set(Date.now());
    };

    const start = () => {
      if (timer || disposed) return;
      if (hasDocument && document.visibilityState === 'hidden') return;
      timer = setInterval(tick, TICK_MS);
    };

    const stop = () => {
      if (timer) {
        clearInterval(timer);
        timer = null;
      }
    };

    const onVisibility = () => {
      if (document.visibilityState === 'hidden') stop();
      else start();
    };

    if (hasDocument) document.addEventListener('visibilitychange', onVisibility);
    start();

    return () => {
      disposed = true;
      stop();
      if (hasDocument) document.removeEventListener('visibilitychange', onVisibility);
    };
  });
}
