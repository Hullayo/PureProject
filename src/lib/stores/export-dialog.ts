/**
 * 「导出报告」对话框状态
 *
 * 所有导出入口（侧边栏菜单、预览弹窗按钮、命令面板）统一走这里，
 * 保证「先询问用户 → 再写文件」。
 *
 * @module stores/export-dialog
 */

import { writable } from 'svelte/store';
import type { ExportFormat } from '$lib/utils/html-export';

export interface ExportDialogState {
  show: boolean;
  projectId: string | null;
  format: ExportFormat;
}

export const exportDialog = writable<ExportDialogState>({ show: false, projectId: null, format: 'html' });

/**
 * 打开导出对话框
 *
 * @param projectId - 要导出的项目
 * @param format - 预选格式
 */
export function openExportDialog(projectId: string, format: ExportFormat = 'html'): void {
  exportDialog.set({ show: true, projectId, format });
}

export function closeExportDialog(): void {
  exportDialog.set({ show: false, projectId: null, format: 'html' });
}
