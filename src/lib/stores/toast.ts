/**
 * 轻量提示（Toast）
 *
 * 用于导出等异步操作的结果反馈，避免「点了没反应」。
 *
 * @module stores/toast
 */

import { writable } from 'svelte/store';

export type ToastKind = 'ok' | 'err' | 'info';

export interface ToastItem {
  id: number;
  text: string;
  kind: ToastKind;
}

export const toasts = writable<ToastItem[]>([]);

let seq = 0;

/**
 * 弹出一条提示
 *
 * @param text 文案
 * @param kind 类型
 * @param ms 自动消失时间
 */
export function toast(text: string, kind: ToastKind = 'info', ms = 6000): void {
  const id = ++seq;
  toasts.update((list) => [...list, { id, text, kind }]);
  setTimeout(() => {
    toasts.update((list) => list.filter((t) => t.id !== id));
  }, ms);
}
