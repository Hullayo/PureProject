/**
 * 报告中转（走自建服务器）
 *
 * 移动端无法把文件写到用户可见目录（Android 10+ 分区存储 + SAF 返回 content:// URI），
 * 因此改为「上传报告 → 服务器托管 → 用系统浏览器打开下载」。
 *
 * 服务器端（pm-sync-server.mjs）提供：
 *   POST /api/report   → { id, pageUrl, htmlUrl, pdfUrl }
 *   GET  /report/:id       带「下载 PDF / 下载 HTML」工具栏的页面
 *   GET  /report/:id.pdf   用无头 Chrome 渲染的 PDF
 *   GET  /report/:id.html  原始 HTML（附件下载）
 *
 * @module utils/report-export
 */

import { getServerToken, getServerUrl } from '$lib/sync/config';

export interface PublishedReport {
  id: string;
  pageUrl: string;
  htmlUrl: string;
  pdfUrl: string;
}

/** 把报告 HTML 上传到服务器，返回可分享链接 */
export async function publishReport(html: string): Promise<PublishedReport> {
  const base = getServerUrl().replace(/\/+$/, '');
  const token = getServerToken();
  if (!base || !token) {
    throw new Error('未配置服务器（请到「设置 → 云同步 → 服务器」填写地址与 Token）');
  }

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 20000);
  try {
    const res = await fetch(`${base}/api/report`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify({ html }),
      signal: controller.signal,
    });
    if (!res.ok) throw new Error(`服务器返回 ${res.status}`);
    return (await res.json()) as PublishedReport;
  } finally {
    clearTimeout(timer);
  }
}
