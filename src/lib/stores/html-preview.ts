/**
 * HTML 预览状态
 *
 * 控制 HtmlPreview 组件的显示。
 *
 * @module stores/html-preview
 */

import { writable } from 'svelte/store';

export interface HtmlPreviewState {
  show: boolean;
  title: string;
  html: string;
  /** 打开后是否自动触发一次打印 */
  autoPrint: boolean;
  /** 关联的项目 ID（弹窗里的「下载 HTML / 导出 PDF」需要它去询问保存位置） */
  projectId: string | null;
}

export const htmlPreview = writable<HtmlPreviewState>({
  show: false,
  title: '',
  html: '',
  autoPrint: false,
  projectId: null,
});

/**
 * 打开 HTML 预览弹窗
 *
 * @param title - 弹窗标题
 * @param html - 报告 HTML
 * @param opts.autoPrint - 是否自动触发打印
 * @param opts.projectId - 关联项目 ID
 */
export function openHtmlPreview(
  title: string,
  html: string,
  opts: { autoPrint?: boolean; projectId?: string } = {}
): void {
  htmlPreview.set({
    show: true,
    title,
    html,
    autoPrint: !!opts.autoPrint,
    projectId: opts.projectId ?? null,
  });
}

/**
 * 关闭 HTML 预览弹窗
 */
export function closeHtmlPreview(): void {
  htmlPreview.set({ show: false, title: '', html: '', autoPrint: false, projectId: null });
}
