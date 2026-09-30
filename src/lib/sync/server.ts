/**
 * 服务器同步客户端（REST + SSE）
 *
 * 直接使用 webview 的 fetch / EventSource。服务器已开启 CORS，
 * 无需经过 Rust，因此不引入额外的 Tauri 权限或 CORS 问题。
 *
 * @module sync/server
 */

import type {
  ServerChange,
  ServerConflict,
  ServerEvent,
  ServerProjectMeta,
  SyncConfig,
} from './types';

/** 常规请求超时（列表 / 变更 / 拉取 / 探活） */
const TIMEOUT_MS = 30000;
/** 上传（PUT）超时：链路慢或项目体积大时给更长预算 */
const UPLOAD_TIMEOUT_MS = 60000;

function base(cfg: SyncConfig): string {
  return (cfg.url || '').replace(/\/+$/, '');
}

function authHeaders(cfg: SyncConfig): Record<string, string> {
  return {
    'Content-Type': 'application/json',
    Authorization: `Bearer ${cfg.password}`,
  };
}

async function request(
  cfg: SyncConfig,
  method: string,
  p: string,
  body?: string,
  extra: Record<string, string> = {},
  timeoutMs: number = TIMEOUT_MS
): Promise<Response> {
  // GET 是幂等的 → 超时/断网时快速重试一次；PUT 不重试（避免与服务器乐观锁重复写入）
  try {
    return await fetchOnce(cfg, method, p, body, extra, timeoutMs);
  } catch (e) {
    if (method === 'GET') {
      return await fetchOnce(cfg, method, p, body, extra, timeoutMs);
    }
    throw e;
  }
}

/** 发一次请求，并把“超时/断网”翻译成人能看懂的报错 */
async function fetchOnce(
  cfg: SyncConfig,
  method: string,
  p: string,
  body: string | undefined,
  extra: Record<string, string>,
  timeoutMs: number
): Promise<Response> {
  const controller = new AbortController();
  let timedOut = false;
  const timer = setTimeout(() => { timedOut = true; controller.abort(); }, timeoutMs);
  try {
    return await fetch(`${base(cfg)}${p}`, {
      method,
      headers: { ...authHeaders(cfg), ...extra },
      body,
      signal: controller.signal,
    });
  } catch (e) {
    const aborted = timedOut || (e instanceof DOMException && e.name === 'AbortError');
    if (aborted) {
      throw new Error(`同步服务器 ${Math.round(timeoutMs / 1000)} 秒未响应（网络超时）`);
    }
    if (e instanceof TypeError) {
      // fetch 在连接失败时抛 TypeError: Failed to fetch / NetworkError
      throw new Error(`无法连接同步服务器 ${base(cfg)}：${e.message}`);
    }
    throw e;
  } finally {
    clearTimeout(timer);
  }
}

/** 探活 */
export async function serverHealth(cfg: SyncConfig): Promise<{ ok: boolean; rev: number; projects: number }> {
  const res = await request(cfg, 'GET', '/api/health');
  if (!res.ok) throw new Error(`服务器返回 ${res.status}`);
  return res.json();
}

/** 项目索引 */
export async function serverList(
  cfg: SyncConfig
): Promise<{ rev: number; projects: ServerProjectMeta[] }> {
  const res = await request(cfg, 'GET', '/api/projects');
  if (!res.ok) throw new Error(`服务器返回 ${res.status}`);
  return res.json();
}

/** 拉取单个项目 */
export async function serverPull(
  cfg: SyncConfig,
  id: string
): Promise<{ rev: number; updated_at: string; schema_version: number; data: string }> {
  const res = await request(cfg, 'GET', `/api/projects/${encodeURIComponent(id)}`);
  if (!res.ok) throw new Error(`服务器返回 ${res.status}`);
  return res.json();
}

/** 增量变更（含数据） */
export async function serverChanges(
  cfg: SyncConfig,
  since: number
): Promise<{ rev: number; changes: ServerChange[] }> {
  const res = await request(cfg, 'GET', `/api/sync/changes?since=${since}`);
  if (!res.ok) throw new Error(`服务器返回 ${res.status}`);
  return res.json();
}

/**
 * 上传/覆盖项目
 *
 * @param baseRev 客户端基于的版本号；服务器不一致时返回 {@link ServerConflict}（HTTP 409）
 */
export async function serverPush(
  cfg: SyncConfig,
  id: string,
  data: string,
  baseRev?: number
): Promise<{ rev: number; updated_at: string; schema_version: number } | ServerConflict> {
  const extra: Record<string, string> = {};
  if (typeof baseRev === 'number') extra['X-Base-Rev'] = String(baseRev);

  const res = await request(cfg, 'PUT', `/api/projects/${encodeURIComponent(id)}`, data, extra, UPLOAD_TIMEOUT_MS);
  if (res.status === 409) return res.json();
  if (!res.ok) throw new Error(`服务器返回 ${res.status}`);
  return res.json();
}

/** 删除项目 */
export async function serverDelete(cfg: SyncConfig, id: string): Promise<void> {
  const res = await request(cfg, 'DELETE', `/api/projects/${encodeURIComponent(id)}`);
  if (!res.ok) throw new Error(`服务器返回 ${res.status}`);
}

/**
 * 订阅服务器实时变更（SSE，断线由 EventSource 自动重连）
 *
 * @returns 取消订阅函数
 */
export function serverEvents(
  cfg: SyncConfig,
  onEvent: (e: ServerEvent) => void,
  onState?: (s: 'open' | 'error') => void
): () => void {
  if (typeof EventSource === 'undefined') return () => {};
  const url = `${base(cfg)}/api/events?token=${encodeURIComponent(cfg.password)}`;
  const es = new EventSource(url);
  es.onopen = () => onState?.('open');
  es.onerror = () => onState?.('error');
  es.onmessage = (ev) => {
    try {
      onEvent(JSON.parse(ev.data) as ServerEvent);
    } catch {
      /* 忽略非法消息 */
    }
  };
  return () => es.close();
}

export function isServerConflict(x: unknown): x is ServerConflict {
  return !!x && typeof x === 'object' && (x as ServerConflict).conflict === true;
}
