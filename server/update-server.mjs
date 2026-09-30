#!/usr/bin/env node
/**
 * pm-update-server —— 更新服务器（并发静态文件）
 *
 * 替代 `python3 -m http.server`：后者是**单线程**、backlog 仅 5，在被扫描器
 * 高频探测或有人下载 30MB APK 时会阻塞，导致客户端检查更新报
 * `error sending request for url (...)`。Node 的 http server 是非阻塞的，
 * 可并发处理多个连接。
 *
 * 只提供 `/updates/*`（更新清单与安装包）与 `/recover/*`（灾后恢复），
 * 其余路径（扫描器的 /index.php、/.env 等）立即 404。
 *
 * 环境变量：
 *   PM_UPDATE_ROOT  站点根目录（默认 <UPDATE_ROOT>）
 *   PM_UPDATE_PORT  端口（默认 80）
 *   PM_UPDATE_BIND  绑定地址（默认 0.0.0.0）
 */

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(process.env.PM_UPDATE_ROOT || '<UPDATE_ROOT>');
const PORT = Number(process.env.PM_UPDATE_PORT || 80);
const HOST = process.env.PM_UPDATE_BIND || '0.0.0.0';

const MIME = {
  '.json': 'application/json; charset=utf-8',
  '.txt': 'text/plain; charset=utf-8',
  '.sig': 'text/plain; charset=utf-8',
  '.exe': 'application/octet-stream',
  '.msi': 'application/octet-stream',
  '.apk': 'application/vnd.android.package-archive',
  '.zip': 'application/zip',
  '.dmg': 'application/octet-stream',
  '.appimage': 'application/octet-stream',
  '.deb': 'application/vnd.debian.binary-package',
  '.html': 'text/html; charset=utf-8',
  '.pdf': 'application/pdf',
};

/** 允许暴露的顶层目录（其它一律 404，挡住扫描器） */
const ALLOWED = ['updates', 'recover'];

/** 安全解析：必须落在 ROOT 之内，且首层目录在 ALLOWED 内 */
function resolveSafe(rawUrl) {
  let pathname;
  try { pathname = decodeURIComponent((rawUrl || '/').split('?')[0]); } catch { return null; }
  const abs = path.normalize(path.join(ROOT, pathname));
  if (abs !== ROOT && !abs.startsWith(ROOT + path.sep)) return null;
  const rel = path.relative(ROOT, abs);
  const top = rel.split(path.sep)[0];
  if (!ALLOWED.includes(top)) return null;
  return abs;
}

const server = http.createServer((req, res) => {
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    res.writeHead(405, { 'Content-Type': 'text/plain' }).end('method not allowed');
    return;
  }
  const abs = resolveSafe(req.url);
  if (!abs) { res.writeHead(404, { 'Content-Type': 'text/plain' }).end('not found'); return; }

  fs.stat(abs, (err, st) => {
    if (err || !st.isFile()) { res.writeHead(404, { 'Content-Type': 'text/plain' }).end('not found'); return; }
    res.writeHead(200, {
      'Content-Type': MIME[path.extname(abs).toLowerCase()] || 'application/octet-stream',
      'Content-Length': st.size,
      // 清单必须实时；安装包可短缓存
      'Cache-Control': path.extname(abs) === '.json' ? 'no-store' : 'public, max-age=300',
      'Access-Control-Allow-Origin': '*',
    });
    if (req.method === 'HEAD') { res.end(); return; }
    const stream = fs.createReadStream(abs);
    stream.on('error', () => res.destroy());
    stream.pipe(res);
  });
});

// 下载大文件时不因超时被中断
server.requestTimeout = 0;
server.headersTimeout = 60000;
server.keepAliveTimeout = 65000;
// 并发 backlog 比 python 的 5 大得多
server.listen({ port: PORT, host: HOST, backlog: 511 }, () => {
  console.log(`[update-server] http://${HOST}:${PORT} root=${ROOT} allowed=/updates,/recover`);
});
