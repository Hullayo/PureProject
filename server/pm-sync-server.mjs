#!/usr/bin/env node
/**
 * pm-sync-server.mjs — ProjectManager 同步服务器（Node 零依赖）
 *
 * 与「更新服务器」同机、独立端口（默认 8787）。
 *
 * 认证：Authorization: Bearer <PM_SYNC_TOKEN>（SSE 走 ?token=）
 * 存储：<PM_SYNC_DATA>/projects/<id>.json + index.json（全局单调递增 rev）
 *
 * API：
 *   GET    /api/health                     探活
 *   GET    /api/projects                   项目索引
 *   GET    /api/projects/:id               拉取项目 .pm JSON
 *   PUT    /api/projects/:id               上传/覆盖（X-Base-Rev 乐观锁，冲突返回 409）
 *   DELETE /api/projects/:id               删除
 *   GET    /api/sync/changes?since=<rev>   增量拉取（含数据）
 *   GET    /api/events?token=<token>       SSE 实时变更推送
 */

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const PORT = Number(process.env.PM_SYNC_PORT || 8787);
const HOST = process.env.PM_SYNC_HOST || '0.0.0.0';
const DATA_DIR = process.env.PM_SYNC_DATA || '<SYNC_DATA>';
const TOKEN = process.env.PM_SYNC_TOKEN || '';
const PROJECTS_DIR = path.join(DATA_DIR, 'projects');
const INDEX_FILE = path.join(DATA_DIR, 'index.json');

// ── 报告中转（把 HTML 报告托管起来，并提供 PDF 渲染）──────────────────────
const REPORTS_DIR = process.env.PM_REPORTS_DIR || '/srv/pm-reports';
const CHROME_BIN = process.env.PM_CHROME_BIN || '/opt/chrome-headless/chrome-headless-shell';
const REPORT_TTL_MS = 7 * 24 * 60 * 60 * 1000; // 7 天后自动清理

fs.mkdirSync(PROJECTS_DIR, { recursive: true });
fs.mkdirSync(REPORTS_DIR, { recursive: true });

/** 清理过期报告 */
function cleanupReports() {
  try {
    const now = Date.now();
    for (const f of fs.readdirSync(REPORTS_DIR)) {
      const p = path.join(REPORTS_DIR, f);
      if (now - fs.statSync(p).mtimeMs > REPORT_TTL_MS) fs.unlinkSync(p);
    }
  } catch { /* ignore */ }
}
cleanupReports();
setInterval(cleanupReports, 6 * 60 * 60 * 1000).unref?.();

/** 给报告 HTML 注入一个下载工具栏（浏览器里用） */
function injectToolbar(html, id) {
  const bar = `<div style="position:fixed;top:0;left:0;right:0;z-index:99999;display:flex;align-items:center;gap:8px;padding:8px 12px;background:#16161e;color:#e4e4e7;font:13px/1.6 -apple-system,BlinkMacSystemFont,'Segoe UI','Microsoft YaHei',sans-serif">`
    + `<span style="flex:1">项目报告</span>`
    + `<a href="/report/${id}.pdf" style="padding:6px 14px;border-radius:6px;background:#4f46e5;color:#fff;text-decoration:none">下载 PDF</a>`
    + `<a href="/report/${id}.html" download style="padding:6px 14px;border-radius:6px;background:#2a2a3a;color:#e4e4e7;text-decoration:none">下载 HTML</a>`
    + `</div><style>body{padding-top:56px !important}</style>`;
  return html.includes('</body>') ? html.replace('</body>', `${bar}</body>`) : html + bar;
}

/** 用无头 Chrome 渲染 PDF */
function renderPdf(htmlPath, pdfPath) {
  return new Promise((resolve, reject) => {
    const args = ['--no-sandbox', '--disable-gpu', '--no-pdf-header-footer', `--print-to-pdf=${pdfPath}`, `file://${htmlPath}`];
    const child = spawn(CHROME_BIN, args, { stdio: ['ignore', 'ignore', 'pipe'] });
    let err = '';
    let done = false;
    const timer = setTimeout(() => { try { child.kill('SIGKILL'); } catch { /* ignore */ } }, 60000);
    const settle = (fn, arg) => { if (done) return; done = true; clearTimeout(timer); fn(arg); };
    child.stderr.on('data', (d) => { err += d.toString(); });
    child.on('error', (e) => settle(reject, new Error(`无法启动 Chrome: ${e.message}`)));
    child.on('close', (code) => {
      if (code === 0 && fs.existsSync(pdfPath)) settle(resolve);
      else settle(reject, new Error(`PDF 渲染失败(code=${code}): ${err.slice(-300)}`));
    });
  });
}

/** 同一份报告并发请求时复用同一次渲染（防止多个 Chrome 写同一文件） */
const renderingPdfs = new Map();
function renderPdfOnce(id, htmlPath, pdfPath) {
  const running = renderingPdfs.get(id);
  if (running) return running;
  const task = renderPdf(htmlPath, pdfPath).finally(() => renderingPdfs.delete(id));
  renderingPdfs.set(id, task);
  return task;
}

// ─── 索引（全局 rev + 每项目元数据） ────────────────────────────────────────
/** @type {{ rev: number, projects: Record<string, {name:string, rev:number, updated_at:string, schema_version:number, deleted?:boolean}> }} */
let index = loadIndex();

function schemaVersionOf(value) {
  const raw = value && typeof value === 'object' ? value.schema_version : undefined;
  const version = typeof raw === 'number' ? raw : Number.parseInt(String(raw ?? ''), 10);
  return Number.isInteger(version) && version > 0 ? version : 1;
}

function loadIndex() {
  try {
    const parsed = JSON.parse(fs.readFileSync(INDEX_FILE, 'utf8'));
    if (parsed && typeof parsed.rev === 'number' && parsed.projects) {
      for (const [id, meta] of Object.entries(parsed.projects)) {
        if (Number.isInteger(meta?.schema_version) && meta.schema_version > 0) continue;
        let schema_version = 1;
        try { schema_version = schemaVersionOf(JSON.parse(fs.readFileSync(projectFile(id), 'utf8'))); } catch { /* legacy tombstone or missing file */ }
        parsed.projects[id] = { ...meta, schema_version };
      }
      return parsed;
    }
  } catch { /* 首次运行 */ }
  return { rev: 0, projects: {} };
}

function saveIndex() {
  const tmp = `${INDEX_FILE}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(index, null, 2));
  fs.renameSync(tmp, INDEX_FILE);
}

function projectFile(id) {
  return path.join(PROJECTS_DIR, `${id.replace(/[^a-zA-Z0-9._-]/g, '_')}.json`);
}

function writeProjectFileAtomic(id, body) {
  const target = projectFile(id);
  const tmp = `${target}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(tmp, body);
  try {
    fs.renameSync(tmp, target);
  } catch (error) {
    try { fs.unlinkSync(tmp); } catch { /* ignore cleanup failure */ }
    throw error;
  }
}

const nextRev = () => ++index.rev;

// ─── SSE 客户端 ─────────────────────────────────────────────────────────────
/** @type {Set<import('node:http').ServerResponse>} */
const sseClients = new Set();

function broadcast(event) {
  const payload = `data: ${JSON.stringify(event)}\n\n`;
  for (const res of sseClients) {
    try { res.write(payload); } catch { /* 客户端已断开，稍后由 close 清理 */ }
  }
}

// ─── HTTP 工具 ──────────────────────────────────────────────────────────────
const CORS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'GET, PUT, DELETE, OPTIONS',
  'Access-Control-Allow-Headers': 'Authorization, Content-Type, X-Base-Rev',
  'Access-Control-Max-Age': '86400',
};

function sendJson(res, code, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(code, { ...CORS, 'Content-Type': 'application/json; charset=utf-8' });
  res.end(body);
}

function sendText(res, code, text, extra = {}) {
  res.writeHead(code, { ...CORS, 'Content-Type': 'text/plain; charset=utf-8', ...extra });
  res.end(text);
}

function readBody(req, limit = 32 * 1024 * 1024) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on('data', (c) => {
      size += c.length;
      if (size > limit) { reject(new Error('body too large')); req.destroy(); return; }
      chunks.push(c);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

function timingSafeEqual(a, b) {
  const ba = Buffer.from(String(a));
  const bb = Buffer.from(String(b));
  if (ba.length !== bb.length) return false;
  return crypto.timingSafeEqual(ba, bb);
}

function authorized(req, url) {
  if (!TOKEN) return true; // 未配置 token 则不鉴权（不推荐）
  const header = req.headers['authorization'] || '';
  const bearer = header.startsWith('Bearer ') ? header.slice(7) : '';
  const query = url.searchParams.get('token') || '';
  return timingSafeEqual(bearer || query, TOKEN);
}

// ─── 路由 ───────────────────────────────────────────────────────────────────
const server = http.createServer(async (req, res) => {
  // access log：便于排查客户端是否连上
  const started = Date.now();
  res.on('finish', () => {
    console.log(`[access] ${req.socket.remoteAddress} ${req.method} ${req.url} -> ${res.statusCode} (${Date.now() - started}ms)`);
  });

  const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
  const { pathname } = url;

  if (req.method === 'OPTIONS') { res.writeHead(204, CORS); res.end(); return; }

  // SSE：用 query token（EventSource 不支持自定义头）
  if (pathname === '/api/events' && req.method === 'GET') {
    if (!authorized(req, url)) { sendJson(res, 401, { error: 'unauthorized' }); return; }

    res.writeHead(200, {
      ...CORS,
      'Content-Type': 'text/event-stream; charset=utf-8',
      'Cache-Control': 'no-cache, no-transform',
      Connection: 'keep-alive',
      'X-Accel-Buffering': 'no',
    });
    res.write(`retry: 3000\n`);
    res.write(`data: ${JSON.stringify({ type: 'hello', rev: index.rev })}\n\n`);
    sseClients.add(res);

    const heartbeat = setInterval(() => {
      try { res.write(`: ping\n\n`); } catch { /* ignore */ }
    }, 20000);

    req.on('close', () => { clearInterval(heartbeat); sseClients.delete(res); });
    return;
  }

  if (!pathname.startsWith('/api/')) {
    // ── 报告托管（浏览器直接访问，用不可猜测的 id 当凭证，无需 header）──
    const rm = pathname.match(/^\/report\/([a-f0-9]{24})(\.pdf|\.html)?$/);
    if (rm && req.method === 'GET') {
      const id = rm[1];
      const ext = rm[2] || '';
      const htmlPath = path.join(REPORTS_DIR, `${id}.html`);
      if (!fs.existsSync(htmlPath)) { sendText(res, 404, 'report not found or expired'); return; }

      // ⚠️ 必须带 CORS：Android WebView 里 exportPdfTo() 用 fetch(pdfUrl) 直接拉 PDF，
      // 页面 origin 是 http://tauri.localhost，缺 ACAO 会抛 TypeError: Failed to fetch。
      // 另外把 Content-Disposition 暴露给 JS，便于前端读取真实文件名。
      const reportHeaders = {
        ...CORS,
        'Access-Control-Expose-Headers': 'Content-Disposition, Content-Type',
        'Cache-Control': 'no-store',
      };

      if (ext === '.html') {
        res.writeHead(200, { ...reportHeaders, 'Content-Type': 'text/html; charset=utf-8', 'Content-Disposition': 'attachment; filename="report.html"' });
        res.end(fs.readFileSync(htmlPath));
        return;
      }
      if (ext === '.pdf') {
        const pdfPath = path.join(REPORTS_DIR, `${id}.pdf`);
        const finish = () => {
          // 先读后写头：若读取失败（如 TTL 清理刚好删掉），还能走 500 分支，
          // 不会因为 writeHead 已经发出后又 writeHead 而撞 ERR_HTTP_HEADERS_SENT。
          let body;
          try {
            body = fs.readFileSync(pdfPath);
          } catch (e) {
            sendText(res, 500, String(e.message || e));
            return;
          }
          res.writeHead(200, { ...reportHeaders, 'Content-Type': 'application/pdf', 'Content-Disposition': 'attachment; filename="report.pdf"' });
          res.end(body);
        };
        if (fs.existsSync(pdfPath)) { finish(); return; }
        renderPdfOnce(id, htmlPath, pdfPath).then(finish).catch((e) => sendText(res, 500, String(e.message || e)));
        return;
      }
      // 默认：带工具栏的页面
      res.writeHead(200, { ...reportHeaders, 'Content-Type': 'text/html; charset=utf-8' });
      res.end(injectToolbar(fs.readFileSync(htmlPath, 'utf8'), id));
      return;
    }

    sendJson(res, 404, { error: 'not found' });
    return;
  }
  if (!authorized(req, url)) { sendJson(res, 401, { error: 'unauthorized' }); return; }

  // GET /api/health
  if (pathname === '/api/health' && req.method === 'GET') {
    sendJson(res, 200, {
      ok: true,
      name: 'pm-sync-server',
      version: '1.0.0',
      rev: index.rev,
      projects: Object.keys(index.projects).length,
      time: new Date().toISOString(),
    });
    return;
  }

  // GET /api/projects
  if (pathname === '/api/projects' && req.method === 'GET') {
    const projects = Object.entries(index.projects)
      .filter(([, m]) => !m.deleted)
      .map(([id, m]) => ({ id, ...m }));
    sendJson(res, 200, { rev: index.rev, projects });
    return;
  }

  // GET /api/sync/changes?since=<rev>
  if (pathname === '/api/sync/changes' && req.method === 'GET') {
    const since = Number(url.searchParams.get('since') || 0);
    const changes = [];
    for (const [id, m] of Object.entries(index.projects)) {
      if (m.rev <= since) continue;
      if (m.deleted) { changes.push({ id, rev: m.rev, updated_at: m.updated_at, schema_version: m.schema_version, deleted: true }); continue; }
      try {
        const data = fs.readFileSync(projectFile(id), 'utf8');
        changes.push({ id, rev: m.rev, updated_at: m.updated_at, name: m.name, schema_version: m.schema_version, data });
      } catch { /* 文件缺失则跳过 */ }
    }
    sendJson(res, 200, { rev: index.rev, changes });
    return;
  }

  // /api/projects/:id
  const match = pathname.match(/^\/api\/projects\/(.+)$/);
  if (match) {
    const id = decodeURIComponent(match[1]);

    // GET 拉取
    if (req.method === 'GET') {
      const meta = index.projects[id];
      if (!meta || meta.deleted) { sendJson(res, 404, { error: 'project not found' }); return; }
      try {
        const data = fs.readFileSync(projectFile(id), 'utf8');
        sendJson(res, 200, { id, rev: meta.rev, updated_at: meta.updated_at, schema_version: meta.schema_version, data });
      } catch {
        sendJson(res, 404, { error: 'project file missing' });
      }
      return;
    }

    // PUT 上传/覆盖
    if (req.method === 'PUT') {
      let body;
      try { body = await readBody(req); } catch (e) { sendJson(res, 413, { error: String(e.message || e) }); return; }

      let parsed;
      try { parsed = JSON.parse(body); } catch { sendJson(res, 400, { error: 'invalid json' }); return; }

      const incomingSchema = schemaVersionOf(parsed);
      if (!parsed || typeof parsed !== 'object' || !parsed.project || typeof parsed.project !== 'object') {
        sendJson(res, 400, { error: 'invalid_project', message: 'project must be an object' });
        return;
      }
      if (parsed.project.id !== undefined && parsed.project.id !== id) {
        sendJson(res, 400, { error: 'project_id_mismatch', message: 'project.id must match URL id' });
        return;
      }

      const baseRev = req.headers['x-base-rev'];
      const current = index.projects[id];
      // 服务器该项目的当前版本：不存在 → 0；存在（**含墓碑**）→ 其 rev。
      // 旧实现把墓碑也当成 0，导致「已删除的项目永远无法再 PUT」——客户端带着
      // 删除前的 rev 重试，而 currentRev 恒为 0，于是永久 409 死循环。
      const currentRev = current ? current.rev : 0;
      const serverDeleted = !!current?.deleted;
      const base = (baseRev === undefined || baseRev === '') ? undefined : Number(baseRev);

      // 乐观锁：
      //   - 服务器没有该项目（currentRev === 0）→ 视为新建 / 服务器数据丢失，接受上传（自愈）
      //   - 服务器有该项目 → 客户端基线必须等于当前 rev（含墓碑）
      if (base !== undefined && currentRev !== 0 && base !== currentRev) {
        let serverData = null;
        try { serverData = fs.readFileSync(projectFile(id), 'utf8'); } catch { /* 无（含墓碑） */ }
        sendJson(res, 409, {
          conflict: true,
          reason: 'revision_conflict',
          id,
          serverRev: currentRev,
          serverUpdatedAt: current?.updated_at ?? null,
          serverData,
          serverSchemaVersion: current?.schema_version ?? 1,
          incomingSchemaVersion: incomingSchema,
          deleted: serverDeleted,
        });
        return;
      }

      const serverSchema = current?.schema_version ?? 1;
      if (current && incomingSchema < serverSchema) {
        let serverData = null;
        try { serverData = fs.readFileSync(projectFile(id), 'utf8'); } catch { /* tombstone or missing file */ }
        sendJson(res, 409, {
          conflict: true,
          reason: 'schema_downgrade',
          id,
          serverRev: currentRev,
          serverUpdatedAt: current.updated_at ?? null,
          serverData,
          serverSchemaVersion: serverSchema,
          incomingSchemaVersion: incomingSchema,
          deleted: serverDeleted,
        });
        return;
      }

      const rev = nextRev();
      const updated_at = parsed?.project?.updated_at || new Date().toISOString();
      const name = parsed?.project?.name || current?.name || id;

      writeProjectFileAtomic(id, body);
      index.projects[id] = { name, rev, updated_at, schema_version: incomingSchema };
      saveIndex();
      broadcast({ type: 'change', id, rev, updated_at, name, schema_version: incomingSchema });
      sendJson(res, 200, { ok: true, id, rev, updated_at, schema_version: incomingSchema });
      return;
    }

    // DELETE 删除（墓碑，便于增量同步感知删除）
    if (req.method === 'DELETE') {
      const rev = nextRev();
      const updated_at = new Date().toISOString();
      try { fs.unlinkSync(projectFile(id)); } catch { /* 已不存在 */ }
      index.projects[id] = {
        name: index.projects[id]?.name || id,
        rev,
        updated_at,
        schema_version: index.projects[id]?.schema_version ?? 1,
        deleted: true,
      };
      saveIndex();
      broadcast({ type: 'delete', id, rev, updated_at });
      sendJson(res, 200, { ok: true, id, rev });
      return;
    }
  }

  // POST /api/report  上传报告 HTML，返回可分享链接
  if (pathname === '/api/report' && req.method === 'POST') {
    let body;
    try { body = await readBody(req, 8 * 1024 * 1024); } catch (e) { sendJson(res, 413, { error: String(e.message || e) }); return; }
    let html = '';
    try { html = JSON.parse(body).html || ''; } catch { sendJson(res, 400, { error: 'invalid json' }); return; }
    if (!html || typeof html !== 'string') { sendJson(res, 400, { error: 'html required' }); return; }

    const id = crypto.randomBytes(12).toString('hex');
    fs.writeFileSync(path.join(REPORTS_DIR, `${id}.html`), html);
    const origin = `http://${req.headers.host || 'localhost'}`;
    sendJson(res, 200, {
      id,
      pageUrl: `${origin}/report/${id}`,
      htmlUrl: `${origin}/report/${id}.html`,
      pdfUrl: `${origin}/report/${id}.pdf`,
    });
    return;
  }

  sendJson(res, 404, { error: 'not found' });
});

server.listen(PORT, HOST, () => {
  console.log(`[pm-sync-server] listening on http://${HOST}:${PORT}`);
  console.log(`[pm-sync-server] data dir: ${DATA_DIR}`);
  console.log(`[pm-sync-server] auth: ${TOKEN ? 'token enabled' : 'DISABLED (no PM_SYNC_TOKEN)'}`);
});

for (const sig of ['SIGINT', 'SIGTERM']) {
  process.on(sig, () => {
    console.log(`[pm-sync-server] ${sig} received, shutting down`);
    for (const res of sseClients) { try { res.end(); } catch { /* ignore */ } }
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 2000);
  });
}
