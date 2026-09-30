/**
 * 工具函数统一导出
 *
 * 将所有工具函数集中导出，方便其他模块统一引入。
 *
 * @example
 * import { genId, now, fmtDate, downloadBlob } from '$lib/utils';
 *
 * @module utils
 */

export { genId } from './id';
export { now, today, fmtDate } from './date';
export { generateMarkdown, exportMarkdown } from './markdown';
export { escapeHtml, exportHtml, previewMarkdown, previewHtml, exportPdf } from './html';
export { downloadBlob } from './download';
