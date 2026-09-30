/**
 * HTML 导出与预览工具
 *
 * 生成「项目完成情况报告」（HTML），并支持预览 / 导出 HTML / 导出 PDF。
 * 报告标签跟随应用语言（中/英）。
 *
 * @module utils/html-export
 */

import type { Project, Task, Priority } from '$lib/types';
import { fmtDate } from './date';
import { escapeHtml, safeFileName } from './html-shared';
import { get } from 'svelte/store';
import { isDark } from '$lib/stores/ui';
import { t, tf, locale } from '$lib/i18n';
import { isAndroidPlatform, isTauriEnv } from './platform';
import { getServerUrl } from '$lib/sync/config';
import { toast } from '$lib/stores/toast';
import {
  completionRate,
  statusDistribution,
  priorityDistribution,
  tagDistribution,
  timeStats,
} from './stats';
import { isTaskClosed, isTaskCompleted } from './task-status';

/** 翻译（无参） */
const T = (key: string): string => get(t)(key);
/** 翻译（带插值） */
const TF = (key: string, params: Record<string, string | number>): string => get(tf)(key, params);

const categoryColor = { todo: '#9ca3af', active: '#4f46e5', done: '#10b981', cancelled: '#94a3b8' };
/** 优先级色 */
const priorityColor: Record<Priority, string> = { high: '#ef4444', medium: '#f59e0b', low: '#10b981' };

/** 日期显示（空值用 —） */
function dueText(d: string | null | undefined): string {
  return d ? fmtDate(d) : '—';
}

/** 今天（YYYY-MM-DD） */
function todayStr(): string {
  return new Date().toISOString().slice(0, 10);
}

/** 毫秒格式化（如 2h 30m） */
function fmtMs(ms: number): string {
  if (!ms || ms < 0) return '0m';
  const totalMin = Math.floor(ms / 60000);
  const h = Math.floor(totalMin / 60);
  return h > 0 ? `${h}h ${totalMin % 60}m` : `${totalMin}m`;
}

/** 里程碑就绪状态 */
function milestoneState(m: { date: string }, project: Project, today: string): { text: string; cls: 'ok' | 'warn' | 'bad' } {
  const tasks = project.tasks;
  const pending = tasks.filter(
    (task) => !isTaskClosed(project, task) && task.due_date && task.due_date.slice(0, 10) <= m.date
  ).length;
  if (pending === 0) return { text: T('report.msReady'), cls: 'ok' };
  if (m.date < today) return { text: TF('report.msOverdue', { n: pending }), cls: 'bad' };
  return { text: TF('report.msPending', { n: pending }), cls: 'warn' };
}

/**
 * 生成项目完成情况报告（纯 HTML 字符串，无工具栏）
 *
 * @param project - 项目数据
 * @param forceTheme - 强制主题：'light' | 'dark'；不传则跟随系统 prefers-color-scheme
 */
export function generateHtml(project: Project, forceTheme?: 'light' | 'dark'): string {
  const tasks = project.tasks ?? [];
  const tags = project.tags ?? [];
  const milestones = project.milestones ?? [];
  const changelog = project.changelog ?? [];
  const today = todayStr();

  // ── 统计 ──────────────────────────────────────────────────────────────
  const rate = completionRate(project);
  const dist = statusDistribution(project);
  const prio = priorityDistribution(tasks);
  const tagDist = tagDistribution(tasks, tags);
  const time = timeStats(project);

  const overdue = tasks.filter(
    (task) => !isTaskClosed(project, task) && task.due_date && task.due_date.slice(0, 10) < today
  );

  let subDone = 0;
  let subTotal = 0;
  for (const t of tasks) {
    subTotal += t.subtasks?.length ?? 0;
    subDone += t.subtasks?.filter((s) => s.done).length ?? 0;
  }

  // ── 片段 ──────────────────────────────────────────────────────────────
  const tagChips = (names: string[]): string =>
    (names ?? [])
      .map((name) => {
        const def = tags.find((x) => x.name === name);
        const c = def?.color ?? '#6b7280';
        return `<span class="tag" style="color:${c};background:${c}1f">${escapeHtml(name)}</span>`;
      })
      .join(' ');

  const taskRows = (list: Task[]): string =>
    list
      .map((task) => {
        const sp = task.subtasks?.length
          ? { done: task.subtasks.filter((s) => s.done).length, total: task.subtasks.length }
          : null;
        const subList = sp
          ? `<ul class="subtasks">${task.subtasks
              .map((s) => `<li class="${s.done ? 'sub-done' : ''}">${s.done ? '☑' : '☐'} ${escapeHtml(s.title)}</li>`)
              .join('')}</ul>`
          : '';
        const desc = task.description ? `<div class="t-desc">${escapeHtml(task.description)}</div>` : '';
        const late = !isTaskClosed(project, task) && task.due_date && task.due_date.slice(0, 10) < today;
        return `<tr>
      <td class="c-dot"><span class="pdot" style="background:${priorityColor[task.priority] ?? '#9ca3af'}" title="${escapeHtml(T(`priority.${task.priority}`))}"></span></td>
      <td class="c-title"><div class="${isTaskCompleted(project, task) ? 't-done' : ''}">${escapeHtml(task.title)}</div>${desc}${subList}</td>
      <td class="c-tags">${tagChips(task.tags) || '—'}</td>
      <td class="c-due${late ? ' late' : ''}">${dueText(task.due_date)}${late ? ' ⚠' : ''}</td>
      <td class="c-sub">${sp ? `${sp.done}/${sp.total}` : '—'}</td>
    </tr>`;
      })
      .join('\n');

  const statusSections = project.task_groups.slice().sort((a,b) => a.sort_order - b.sort_order)
    .map(group => {
      const sections = group.statuses.slice().sort((a,b) => a.sort_order - b.sort_order).map(status => {
        const list = tasks.filter(task => task.task_group_id === group.id && task.status_id === status.id);
        if (!list.length) return '';
        return `<h3><span class="sdot" style="background:${status.color}"></span>${escapeHtml(status.name)}<span class="count">${list.length}</span></h3>
<table>
<thead><tr><th></th><th>${escapeHtml(T('report.colTask'))}</th><th>${escapeHtml(T('report.colTags'))}</th><th>${escapeHtml(T('report.colDue'))}</th><th>${escapeHtml(T('report.colSubtasks'))}</th></tr></thead>
<tbody>${taskRows(list)}</tbody>
</table>`;
      }).filter(Boolean).join('\n');
      return sections ? `<h2>${escapeHtml(group.name)}${group.archived ? '（已归档）' : ''}</h2>${sections}` : '';
    })
    .filter(Boolean)
    .join('\n');

  const milestoneCards = milestones
    .map((m) => {
      const st = milestoneState(m, project, today);
      return `<div class="ms-item"><span class="ms-dot" style="background:${escapeHtml(m.color || '#4f46e5')}"></span>
    <div><div class="ms-title">${escapeHtml(m.title)}<span class="ms-date">${escapeHtml(m.date)}</span></div>${m.description ? `<div class="ms-desc">${escapeHtml(m.description)}</div>` : ''}</div>
    <span class="chip ${st.cls}">${escapeHtml(st.text)}</span></div>`;
    })
    .join('');

  const changelogItems = changelog
    .map(
      (c) =>
        `<div class="cl-item"><span class="cl-ver">${escapeHtml(c.version)}</span><span class="cl-date">${escapeHtml(c.date)}</span> ${escapeHtml(c.info)}</div>`
    )
    .join('');

  // ── 主题变量 ──────────────────────────────────────────────────────────
  const darkVars = `--bg:#0f0f14; --surface:#1a1a24; --card:#1e1e2a; --border:#2a2a3a; --text:#e4e4e7; --text-secondary:#a1a1aa; --text-muted:#52525b; --accent:#6366f1; --green:#34d399; --amber:#fbbf24; --red:#f87171;`;
  const lightVars = `--bg:#ffffff; --surface:#f8fafc; --card:#ffffff; --border:#e5e7eb; --text:#111827; --text-secondary:#4b5563; --text-muted:#9ca3af; --accent:#4f46e5; --green:#10b981; --amber:#f59e0b; --red:#ef4444;`;
  const themeCss =
    forceTheme === 'dark'
      ? `:root { ${darkVars} }`
      : forceTheme === 'light'
        ? `:root { ${lightVars} }`
        : `:root { ${lightVars} }\n  @media (prefers-color-scheme: dark) { :root { ${darkVars} } }`;

  const accent = project.color || '#4f46e5';
  const lang = get(locale) === 'en' ? 'en' : 'zh-CN';

  return `<!DOCTYPE html>
<html lang="${lang}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(project.name)} — ${escapeHtml(T('report.title'))}</title>
<style>
  ${themeCss}
  * { margin:0; padding:0; box-sizing:border-box; }
  body { font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','Microsoft YaHei','PingFang SC',sans-serif; background:var(--bg); color:var(--text); line-height:1.6; padding:32px 28px 48px; max-width:920px; margin:0 auto; }
  h1 { font-size:26px; margin-bottom:4px; }
  h1 .dot { display:inline-block; width:12px; height:12px; border-radius:50%; background:${escapeHtml(accent)}; margin-right:10px; vertical-align:middle; }
  h2 { font-size:17px; margin:30px 0 12px; padding-bottom:8px; border-bottom:1px solid var(--border); }
  h3 { font-size:14px; margin:18px 0 8px; color:var(--text-secondary); display:flex; align-items:center; gap:6px; }
  h3 .count { font-size:11px; color:var(--text-muted); font-weight:400; background:var(--surface); border-radius:10px; padding:0 7px; }
  h3 .sdot { width:8px; height:8px; border-radius:50%; display:inline-block; }
  .desc { color:var(--text-secondary); font-size:14px; margin-bottom:6px; }
  .meta { display:flex; flex-wrap:wrap; gap:6px 18px; font-size:12px; color:var(--text-muted); margin-bottom:20px; }
  .cards { display:grid; grid-template-columns:1fr 1fr; gap:12px; }
  .card { border:1px solid var(--border); border-radius:10px; padding:14px 16px; background:var(--surface); }
  .card h4 { font-size:11px; text-transform:uppercase; letter-spacing:0; color:var(--text-muted); font-weight:600; margin-bottom:10px; }
  .card.full { grid-column:1 / -1; }
  .rate-row { display:flex; align-items:baseline; gap:10px; }
  .rate-num { font-size:34px; font-weight:700; color:var(--accent); line-height:1; }
  .rate-sub { font-size:12px; color:var(--text-muted); }
  .bar { height:10px; border-radius:6px; background:var(--border); overflow:hidden; margin-top:10px; }
  .bar > i { display:block; height:100%; border-radius:6px; background:var(--accent); }
  .tiles { display:flex; gap:10px; }
  .tile { flex:1; text-align:center; padding:8px 6px; border-radius:8px; background:var(--card); border:1px solid var(--border); }
  .tile .n { font-size:20px; font-weight:700; }
  .tile .l { font-size:11px; color:var(--text-muted); }
  .stack { display:flex; height:10px; border-radius:6px; overflow:hidden; margin-top:12px; background:var(--border); }
  .stack > i { display:block; height:100%; }
  .taglist { display:flex; flex-wrap:wrap; gap:6px; }
  .tagchip { display:inline-flex; align-items:center; gap:5px; font-size:12px; padding:2px 9px; border-radius:20px; }
  .tagchip b { font-weight:600; }
  .ms { display:flex; flex-direction:column; gap:8px; }
  .ms-item { display:flex; align-items:flex-start; gap:10px; padding:9px 12px; background:var(--card); border:1px solid var(--border); border-radius:8px; }
  .ms-dot { width:10px; height:10px; border-radius:50%; margin-top:5px; flex-shrink:0; }
  .ms-title { font-size:13px; font-weight:600; }
  .ms-date { font-size:11px; color:var(--text-muted); margin-left:6px; }
  .ms-desc { font-size:12px; color:var(--text-secondary); }
  .chip { font-size:11px; padding:1px 8px; border-radius:20px; margin-left:auto; white-space:nowrap; }
  .chip.ok { color:var(--green); background:rgba(16,185,129,.14); }
  .chip.warn { color:var(--amber); background:rgba(245,158,11,.16); }
  .chip.bad { color:var(--red); background:rgba(239,68,68,.14); }
  table { width:100%; border-collapse:collapse; }
  th { text-align:left; font-size:11px; color:var(--text-muted); text-transform:uppercase; letter-spacing:0; padding:6px 8px; border-bottom:1px solid var(--border); font-weight:600; }
  td { padding:9px 8px; border-bottom:1px solid var(--border); font-size:13px; vertical-align:top; }
  .c-dot { width:20px; } .c-due { white-space:nowrap; width:96px; } .c-sub { width:60px; color:var(--text-secondary); } .c-tags { width:150px; }
  .pdot { display:inline-block; width:8px; height:8px; border-radius:50%; }
  .t-done { text-decoration:line-through; color:var(--text-muted); }
  .t-desc { font-size:12px; color:var(--text-muted); margin-top:2px; }
  .late { color:var(--red); }
  .tag { display:inline-block; padding:1px 8px; font-size:11px; border-radius:4px; margin:1px 2px 1px 0; }
  .subtasks { list-style:none; margin-top:5px; font-size:12px; color:var(--text-secondary); }
  .subtasks li { padding:1px 0; }
  .sub-done { text-decoration:line-through; color:var(--text-muted); }
  .cl { display:flex; flex-direction:column; gap:3px; }
  .cl-item { font-size:13px; }
  .cl-ver { font-weight:600; color:var(--accent); }
  .cl-date { font-size:11px; color:var(--text-muted); margin-left:6px; }
  .footer { margin-top:36px; padding-top:14px; border-top:1px solid var(--border); font-size:11px; color:var(--text-muted); text-align:center; }
  @media print { body { padding:12px; } .card { break-inside:avoid; } h2 { break-after:avoid; } tr { break-inside:avoid; } }
</style>
</head>
<body>
<h1><span class="dot"></span>${escapeHtml(project.name)}</h1>
${project.description ? `<p class="desc">${escapeHtml(project.description)}</p>` : ''}
<div class="meta">
  <span>${escapeHtml(T('report.period'))}: ${dueText(project.start_date)} ~ ${dueText(project.end_date)}</span>
  <span>${escapeHtml(T('report.generated'))}: ${today}</span>
  <span>${escapeHtml(TF('report.summary', { t: tasks.length, m: milestones.length }))}</span>
</div>

<h2>${escapeHtml(T('report.overview'))}</h2>
<div class="cards">
  <div class="card full">
    <h4>${escapeHtml(T('report.overallRate'))}</h4>
    <div class="rate-row">
      <span class="rate-num">${rate.percent}%</span>
      <span class="rate-sub">${escapeHtml(TF('report.completedOf', { done: rate.done, total: rate.total }))}${subTotal ? ` · ${escapeHtml(TF('report.subtaskNote', { done: subDone, total: subTotal }))}` : ''}</span>
    </div>
    <div class="bar"><i style="width:${rate.percent}%;background:var(--green)"></i></div>
  </div>

  <div class="card">
    <h4>${escapeHtml(T('report.statusDist'))}</h4>
    <div class="tiles">
      <div class="tile"><div class="n" style="color:${categoryColor.active}">${dist.active}</div><div class="l">进行中</div></div>
      <div class="tile"><div class="n" style="color:${categoryColor.todo}">${dist.todo}</div><div class="l">待处理</div></div>
      <div class="tile"><div class="n" style="color:${categoryColor.done}">${dist.done}</div><div class="l">已完成</div></div>
      <div class="tile"><div class="n" style="color:${categoryColor.cancelled}">${dist.cancelled}</div><div class="l">已取消</div></div>
    </div>
    <div class="stack">${tasks.length ? `<i style="width:${(dist.active / tasks.length) * 100}%;background:${categoryColor.active}"></i><i style="width:${(dist.todo / tasks.length) * 100}%;background:${categoryColor.todo}"></i><i style="width:${(dist.done / tasks.length) * 100}%;background:${categoryColor.done}"></i><i style="width:${(dist.cancelled / tasks.length) * 100}%;background:${categoryColor.cancelled}"></i>` : ''}</div>
  </div>

  <div class="card">
    <h4>${escapeHtml(T('report.priorityDist'))}</h4>
    <div class="tiles">
      <div class="tile"><div class="n" style="color:${priorityColor.high}">${prio.high}</div><div class="l">${escapeHtml(T('priority.high'))}</div></div>
      <div class="tile"><div class="n" style="color:${priorityColor.medium}">${prio.medium}</div><div class="l">${escapeHtml(T('priority.medium'))}</div></div>
      <div class="tile"><div class="n" style="color:${priorityColor.low}">${prio.low}</div><div class="l">${escapeHtml(T('priority.low'))}</div></div>
    </div>
  </div>

  <div class="card${tagDist.length ? '' : ' full'}">
    <h4>${escapeHtml(T('report.keyMetrics'))}</h4>
    <div class="tiles">
      <div class="tile"><div class="n"${overdue.length ? ' style="color:var(--red)"' : ''}>${overdue.length}</div><div class="l">${escapeHtml(T('report.overdue'))}</div></div>
      <div class="tile"><div class="n">${subTotal ? `${Math.round((subDone / subTotal) * 100)}%` : '—'}</div><div class="l">${escapeHtml(T('report.subtaskCompletion'))}</div></div>
      <div class="tile"><div class="n" style="font-size:15px;line-height:1.9">${fmtMs(time.totalMs)}</div><div class="l">${escapeHtml(T('report.totalTime'))}</div></div>
    </div>
  </div>

  ${tagDist.length ? `<div class="card full">
    <h4>${escapeHtml(T('report.tagDist'))}</h4>
    <div class="taglist">${tagDist.map((x) => `<span class="tagchip" style="color:${x.color};background:${x.color}1f">${escapeHtml(x.name)} <b>${x.count}</b></span>`).join('')}</div>
  </div>` : ''}

  ${milestones.length ? `<div class="card full">
    <h4>${escapeHtml(T('report.milestoneStatus'))}</h4>
    <div class="ms">${milestoneCards}</div>
  </div>` : ''}
</div>

<h2>${escapeHtml(T('report.taskDetails'))}</h2>
${statusSections || `<p class="desc">${escapeHtml(T('report.noTasks'))}</p>`}

${milestones.length ? `<h2>${escapeHtml(T('report.milestones'))}</h2>
<div class="ms">${milestones
    .map(
      (m) =>
        `<div class="ms-item"><span class="ms-dot" style="background:${escapeHtml(m.color || '#4f46e5')}"></span><div><div class="ms-title">${escapeHtml(m.title)}<span class="ms-date">${escapeHtml(m.date)}</span></div>${m.description ? `<div class="ms-desc">${escapeHtml(m.description)}</div>` : ''}</div></div>`
    )
    .join('')}</div>` : ''}

${changelog.length ? `<h2>${escapeHtml(T('report.changelog'))}</h2>
<div class="cl">${changelogItems}</div>` : ''}

<div class="footer">${escapeHtml(T('report.footer'))} · ${today}</div>
</body>
</html>`;
}

/**
 * 导出结果
 */
export type ExportFormat = 'html' | 'pdf';
export type ExportLocation = 'picker' | 'downloads';

export type ExportOutcome =
  | { kind: 'saved'; path: string; uri?: unknown }
  | { kind: 'cancelled' }
  | { kind: 'error'; message: string };

/** 纯浏览器（非 Tauri）调试用：Blob 下载（浏览器自身会显示下载条） */
function browserDownload(html: string, filename: string): void {
  const blob = new Blob([html], { type: 'text/html;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Android：写入公共下载目录（文件管理器可见） */
async function androidSaveToDownloads(
  name: string,
  mime: string,
  payload: { text?: string; bytes?: Uint8Array }
): Promise<{ path: string; uri: unknown }> {
  const A = await import('tauri-plugin-android-fs-api');
  const uri = await A.createNewPublicFile(
    A.PublicGeneralPurposeDir.Download,
    `ProjectManager/${name}`,
    mime,
    { isPending: true }
  );
  if (payload.text != null) await A.writeTextFile(uri, payload.text);
  else if (payload.bytes) await A.writeFile(uri, payload.bytes);
  await A.setPublicFilePending(uri, false);
  try { await A.scanPublicFile(uri); } catch { /* 扫描失败不影响保存 */ }
  return { path: `Download/ProjectManager/${name}`, uri };
}

/** 服务器渲染 PDF，返回可下载地址 */
async function renderPdfUrl(project: Project): Promise<string> {
  const html = generateHtml(project, get(isDark) ? 'dark' : 'light');
  const { publishReport } = await import('./report-export');
  const { pdfUrl } = await publishReport(html);
  return pdfUrl;
}

/**
 * 带超时与重试的 fetch
 *
 * - 首次拉取 `/report/<id>.pdf` 会触发服务端无头 Chrome 渲染，可能耗时十几秒；
 * - 失败（网络抖动 / 服务器刚重启）重试一次，重试时服务端已缓存，通常很快。
 */
async function fetchWithRetry(url: string, timeoutMs = 60000, retries = 1): Promise<Response> {
  let lastErr: unknown;
  for (let attempt = 0; attempt <= retries; attempt++) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const res = await fetch(url, { signal: controller.signal });
      if (!res.ok) throw new Error(`服务器返回 ${res.status}`);
      return res;
    } catch (e) {
      lastErr = e;
    } finally {
      clearTimeout(timer);
    }
  }
  throw lastErr;
}

/** 导出 HTML（按用户选择的位置） */
async function exportHtmlTo(project: Project, location: ExportLocation): Promise<ExportOutcome> {
  const html = generateHtml(project);
  const name = `${safeFileName(project.name)}.html`;

  if (!isTauriEnv) {
    browserDownload(html, name);
    return { kind: 'saved', path: name };
  }

  // Android
  if (isAndroidPlatform) {
    const A = await import('tauri-plugin-android-fs-api');
    if (location === 'downloads') {
      const saved = await androidSaveToDownloads(name, 'text/html', { text: html });
      return { kind: 'saved', ...saved };
    }
    const uri = await A.showSaveFilePicker(name, 'text/html');
    if (!uri) return { kind: 'cancelled' };
    await A.writeTextFile(uri, html);
    let path = name;
    try { path = (await A.getName(uri)) || name; } catch { /* ignore */ }
    return { kind: 'saved', path, uri };
  }

  // 桌面
  const { invoke } = await import('@tauri-apps/api/core');
  if (location === 'downloads') {
    const { downloadDir, join } = await import('@tauri-apps/api/path');
    const path = await join(await downloadDir(), name);
    await invoke('save_text_file', { path, content: html });
    return { kind: 'saved', path };
  }
  const { save } = await import('@tauri-apps/plugin-dialog');
  const picked = await save({ defaultPath: name, filters: [{ name: 'HTML', extensions: ['html', 'htm'] }] });
  if (!picked) return { kind: 'cancelled' };
  await invoke('save_text_file', { path: String(picked), content: html });
  return { kind: 'saved', path: String(picked) };
}

/** 导出 PDF（服务器渲染，保证排版与报告一致） */
async function exportPdfTo(project: Project, location: ExportLocation): Promise<ExportOutcome> {
  const name = `${safeFileName(project.name)}.pdf`;
  const pdfUrl = await renderPdfUrl(project);

  if (!isTauriEnv) {
    window.open(pdfUrl, '_blank');
    return { kind: 'saved', path: pdfUrl };
  }

  // Android
  if (isAndroidPlatform) {
    const A = await import('tauri-plugin-android-fs-api');
    let uri;
    if (location === 'downloads') {
      uri = await A.createNewPublicFile(
        A.PublicGeneralPurposeDir.Download,
        `ProjectManager/${name}`,
        'application/pdf',
        { isPending: true }
      );
    } else {
      const picked = await A.showSaveFilePicker(name, 'application/pdf');
      if (!picked) return { kind: 'cancelled' };
      uri = picked;
    }
    // 先下载再写入：失败时把刚建的文件（Downloads 的 pending 空文件 / 用户选中的空文件）清掉，
    // 否则会留下 0 字节的“导出成功”假象
    let bytes: Uint8Array;
    try {
      const res = await fetchWithRetry(pdfUrl, 90000, 1);
      bytes = new Uint8Array(await res.arrayBuffer());
    } catch (e) {
      try {
        if (location === 'downloads') await A.setPublicFilePending(uri, false);
        await A.removeFile(uri);
      } catch { /* 清理失败不掩盖原本的错误 */ }
      throw e;
    }
    await A.writeFile(uri, bytes);
    if (location === 'downloads') {
      await A.setPublicFilePending(uri, false);
      try { await A.scanPublicFile(uri); } catch { /* ignore */ }
      return { kind: 'saved', path: `Download/ProjectManager/${name}`, uri };
    }
    let path = name;
    try { path = (await A.getName(uri)) || name; } catch { /* ignore */ }
    return { kind: 'saved', path, uri };
  }

  // 桌面：由 Rust 从服务器拉取并写入指定路径
  const { invoke } = await import('@tauri-apps/api/core');
  if (location === 'downloads') {
    const { downloadDir, join } = await import('@tauri-apps/api/path');
    const path = await join(await downloadDir(), name);
    await invoke('save_url_to_file', { url: pdfUrl, path });
    return { kind: 'saved', path };
  }
  const { save } = await import('@tauri-apps/plugin-dialog');
  const picked = await save({ defaultPath: name, filters: [{ name: 'PDF', extensions: ['pdf'] }] });
  if (!picked) return { kind: 'cancelled' };
  await invoke('save_url_to_file', { url: pdfUrl, path: String(picked) });
  return { kind: 'saved', path: String(picked) };
}

/**
 * 把底层异常（尤其是 fetch 的 `TypeError: Failed to fetch`）转成可读文案
 *
 * 说明：Android WebView 里跨源 fetch /report/<id>.pdf 曾因服务器缺 CORS 头而失败，
 * 浏览器只会给出 “Failed to fetch”，对用户毫无信息量。
 */
function friendlyError(e: unknown): string {
  const raw = e instanceof Error ? e.message : String(e);
  const isNetwork =
    e instanceof TypeError ||
    /failed to fetch|networkerror|load failed|network request failed|fetch failed/i.test(raw) ||
    (e instanceof DOMException && e.name === 'AbortError');
  if (!isNetwork) return raw;
  let base = '';
  try { base = getServerUrl(); } catch { /* ignore */ }
  return TF('export.errNetwork', { url: base || '-' });
}

/**
 * 导出报告（**唯一入口**）
 *
 * 必须先由用户选择格式与位置；内部不做任何静默下载/兜底。
 */
export async function exportReport(
  project: Project,
  format: ExportFormat,
  location: ExportLocation
): Promise<ExportOutcome> {
  try {
    return format === 'html' ? await exportHtmlTo(project, location) : await exportPdfTo(project, location);
  } catch (e) {
    return { kind: 'error', message: friendlyError(e) };
  }
}

/**
 * 在应用内预览报告
 *
 * @param project - 项目
 * @param autoPrint - 保留参数（兼容旧调用；当前不再自动打印）
 */
export async function previewHtml(project: Project, autoPrint = false): Promise<void> {
  const forceTheme: 'light' | 'dark' = get(isDark) ? 'dark' : 'light';
  const html = generateHtml(project, forceTheme);
  const { openHtmlPreview } = await import('$lib/stores/html-preview');
  openHtmlPreview(project.name, html, { autoPrint, projectId: project.id });
}

/** 兼容旧调用：导出 HTML（默认让用户选位置） */
export async function exportHtml(project: Project): Promise<void> {
  await exportReport(project, 'html', 'picker');
}

/** 兼容旧调用：导出 PDF（默认让用户选位置） */
export async function exportPdf(project: Project): Promise<void> {
  await exportReport(project, 'pdf', 'picker');
}
