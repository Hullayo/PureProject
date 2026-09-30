/**
 * 文件导出操作
 *
 * 提供 .pm 文件下载、Markdown/HTML/PDF 文档导出功能。
 *
 * @module stores/export
 */

import { get } from 'svelte/store';
import { projectToPm } from '$lib/repositories';
import { previewHtml, previewMarkdown } from '$lib/utils/html';
import { saveTextFile } from '$lib/utils/save-file';
import { toast } from './toast';
import { openExportDialog } from './export-dialog';
import { projects } from './project';
import { runHook } from '$lib/plugins';

/**
 * 导出单个项目的 .pm 文件
 *
 * ⚠️ 不能再直接用 `Blob + <a download>`：Android WebView 下**静默失败**（无文件、无报错）。
 * 统一走 `saveTextFile()`，由它按平台分流（Android 用系统「保存到」对话框）。
 *
 * @param projectId - 项目 ID
 */
export async function downloadPmFile(projectId: string): Promise<void> {
  const proj = get(projects).find(p => p.id === projectId);
  if (!proj) {
    console.error('[Store/Export] Project not found for PM download:', projectId);
    toast('导出失败：项目不存在', 'err');
    return;
  }
  const json = JSON.stringify({ ...projectToPm(proj) }, null, 2);
  const res = await saveTextFile(`${proj.name}.pm`, json, 'application/json');
  if (res.kind === 'saved') {
    toast(`已导出：${res.path ?? proj.name + '.pm'}`, 'ok');
    runHook('onExport', { projectId, format: 'pm' });
  }
  else if (res.kind === 'error') toast(`导出失败：${res.message}`, 'err');
}

/**
 * 导出项目为 Markdown 并在新窗口预览
 *
 * @param projectId - 项目 ID
 */
export function exportMarkdownFile(projectId: string): void {
  const list = get(projects);
  const proj = list.find(p => p.id === projectId);
  if (!proj) return;
  previewMarkdown(proj);
  runHook('onExport', { projectId, format: 'markdown' });
}

/**
 * 导出项目为 HTML 报告
 *
 * @param projectId - 项目 ID
 */
export async function exportHtmlFile(projectId: string): Promise<void> {
  console.log('[Store/Export] exportHtmlFile:', projectId);
  const list = get(projects);
  const proj = list.find(p => p.id === projectId);
  if (!proj) {
    console.error('[Store/Export] Project not found:', projectId);
    return;
  }
  await previewHtml(proj);
}

/**
 * 导出 HTML 到指定位置（**先询问用户**）
 *
 * @param projectId - 项目 ID
 */
export async function exportHtmlAsFile(projectId: string): Promise<void> {
  openExportDialog(projectId, 'html');
}

/**
 * 导出项目为 PDF（**先询问用户**）
 *
 * @param projectId - 项目 ID
 */
export async function exportPdfFile(projectId: string): Promise<void> {
  openExportDialog(projectId, 'pdf');
}
