/**
 * Markdown 预览工具
 *
 * 提供 Markdown 转 HTML、Markdown 预览功能。
 * 预览走应用内 HtmlPreview 弹窗（而非 window.open，Tauri 下更可靠）。
 *
 * @module utils/html-markdown
 */

import type { Project } from '$lib/types';
import { generateMarkdown } from './markdown';
import { escapeHtml } from './html-shared';

/**
 * 处理 Markdown 行内格式
 */
function inlineMd(s: string): string {
  return escapeHtml(s)
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/`(.+?)`/g, '<code style="background:var(--surface);padding:1px 4px;border-radius:3px">$1</code>')
    .replace(/\[x\]/g, '☑')
    .replace(/\[ \]/g, '☐');
}

/**
 * 将 Markdown 文本转换为 HTML
 */
function mdToHtml(md: string): string {
  const lines = md.split('\n');
  const out: string[] = [];
  let inTable = false;

  for (const line of lines) {
    if (line.startsWith('| ') || line.startsWith('|')) {
      if (!inTable) { out.push('<table>'); inTable = true; }
      if (/^\|[\s\-:|]+\|$/.test(line.trim())) continue;
      const cells = line.split('|').filter(c => c.trim() !== '').map(c => c.trim());
      out.push('<tr>' + cells.map(c => `<td>${inlineMd(c)}</td>`).join('') + '</tr>');
      continue;
    }
    if (inTable) { out.push('</table>'); inTable = false; }

    if (line.startsWith('### ')) { out.push(`<h3>${inlineMd(line.slice(4))}</h3>`); continue; }
    if (line.startsWith('## ')) { out.push(`<h2>${inlineMd(line.slice(3))}</h2>`); continue; }
    if (line.startsWith('# ')) { out.push(`<h1>${inlineMd(line.slice(2))}</h1>`); continue; }
    if (line.startsWith('> ')) { out.push(`<blockquote>${inlineMd(line.slice(2))}</blockquote>`); continue; }
    if (line.startsWith('- ')) { out.push(`<li>${inlineMd(line.slice(2))}</li>`); continue; }
    if (line.trim() === '') { out.push('<br>'); continue; }
    out.push(`<p>${inlineMd(line)}</p>`);
  }
  if (inTable) out.push('</table>');
  return out.join('\n');
}

/**
 * 在应用内预览项目的 Markdown 渲染结果
 *
 * @param project - 项目数据
 * @param autoPrint - 打开后是否自动触发打印（用于「导出 PDF」）
 */
export async function previewMarkdown(project: Project, autoPrint = false): Promise<void> {
  const mdText = generateMarkdown(project);
  const body = mdToHtml(mdText);

  const html = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(project.name)} — Markdown</title>
<style>
  :root { --bg:#fff; --surface:#f8fafc; --border:#e5e7eb; --text:#111827; --text-secondary:#4b5563; --text-muted:#9ca3af; --accent:#4f46e5; }
  @media (prefers-color-scheme: dark) { :root { --bg:#0f0f14; --surface:#1a1a24; --border:#2a2a3a; --text:#e4e4e7; --text-secondary:#a1a1aa; --text-muted:#52525b; --accent:#6366f1; } }
  * { box-sizing: border-box; }
  body { font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','Microsoft YaHei','PingFang SC',sans-serif; background:var(--bg); color:var(--text); padding:24px 32px; line-height:1.7; max-width:900px; margin:0 auto; }
  h1 { font-size:24px; border-bottom:1px solid var(--border); padding-bottom:8px; }
  h2 { font-size:18px; margin-top:24px; border-bottom:1px solid var(--border); padding-bottom:6px; }
  h3 { font-size:15px; margin-top:16px; color:var(--text-secondary); }
  table { width:100%; border-collapse:collapse; margin:8px 0; }
  td { padding:6px 8px; border-bottom:1px solid var(--border); font-size:13px; }
  tr:first-child td { font-weight:600; font-size:12px; color:var(--text-muted); }
  blockquote { margin:4px 0; padding:4px 12px; border-left:3px solid var(--accent); color:var(--text-secondary); font-size:13px; }
  li { margin:2px 0; font-size:13px; list-style:disc inside; }
  code { background:var(--surface); padding:1px 4px; border-radius:3px; font-size:12px; }
  @media print { body { padding:12px; } tr { break-inside:avoid; } }
</style>
</head>
<body>
${body}
</body>
</html>`;

  const { openHtmlPreview } = await import('$lib/stores/html-preview');
  openHtmlPreview(`${project.name} — Markdown`, html, { autoPrint });
}
