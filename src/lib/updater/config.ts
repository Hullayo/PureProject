/**
 * 更新源配置（localStorage）
 *
 * 支持两种更新源：
 * - `server`：自建更新服务器（地址与端口由用户在设置里填写，不内置），
 *   清单固定放在 `${base}/updates/latest.json`（桌面）/ `android.json`（Android）。
 * - `github`：GitHub Releases，清单作为 release 资源，地址固定为
 *   `https://github.com/<owner>/<repo>/releases/latest/download/<file>`。
 *
 * 这里只做「配置 → 清单 URL」的纯计算，具体检查/下载在 `$lib/updater` 与 Rust 侧完成。
 *
 * @module updater/config
 */

/** 更新源类型 */
export type UpdateSource = 'server' | 'github';

export const UPDATE_KEYS = {
  source: 'pm_update_source',
  host: 'pm_update_server_host',
  port: 'pm_update_server_port',
  repo: 'pm_update_github_repo',
} as const;

/** 默认自建更新服务器：**留空**。全新安装默认走 GitHub，避免内置指向某台服务器 */
export const DEFAULT_UPDATE_HOST = '';
/** 默认端口：留空（不预填） */
export const DEFAULT_UPDATE_PORT = '';
/** 默认 GitHub 仓库（owner/repo） */
export const DEFAULT_GITHUB_REPO = 'Hullayo/PureProject';

function read(key: string, fallback = ''): string {
  try { return localStorage.getItem(key) ?? fallback; } catch { return fallback; }
}

function write(key: string, val: string): void {
  try { localStorage.setItem(key, val); } catch { /* ignore */ }
}

export function getUpdateSource(): UpdateSource {
  // 未显式配置过 → 默认 GitHub；用户配置过的值原样保留
  const v = read(UPDATE_KEYS.source);
  if (v === 'github' || v === 'server') return v;
  return 'github';
}

/** 服务器地址：未配置时为空（不再内置 IP） */
export function getUpdateServerHost(): string {
  return (read(UPDATE_KEYS.host, DEFAULT_UPDATE_HOST) || DEFAULT_UPDATE_HOST).trim();
}

/** 端口：未配置时为空；填了非法值也视为空 */
export function getUpdateServerPort(): string {
  const p = (read(UPDATE_KEYS.port, DEFAULT_UPDATE_PORT) || DEFAULT_UPDATE_PORT).trim();
  if (!p) return '';
  const n = Number(p);
  return Number.isInteger(n) && n >= 1 && n <= 65535 ? String(n) : '';
}

/** GitHub 仓库：容忍用户粘贴完整 URL 或 .git 后缀 */
export function getUpdateGithubRepo(): string {
  const raw = (read(UPDATE_KEYS.repo, DEFAULT_GITHUB_REPO) || DEFAULT_GITHUB_REPO).trim()
    .replace(/^https?:\/\/github\.com\//i, '')
    .replace(/\.git$/i, '')
    .replace(/^\/+|\/+$/g, '');
  return /^[^/\s]+\/[^/\s]+$/.test(raw) ? raw : DEFAULT_GITHUB_REPO;
}

export function saveUpdateConfig(data: {
  source?: UpdateSource;
  host?: string;
  port?: string;
  repo?: string;
}): void {
  if (data.source) write(UPDATE_KEYS.source, data.source);
  if (data.host !== undefined) write(UPDATE_KEYS.host, data.host.trim());
  if (data.port !== undefined) write(UPDATE_KEYS.port, data.port.trim());
  if (data.repo !== undefined) write(UPDATE_KEYS.repo, data.repo.trim());
}

/** 一组更新配置值（供纯函数与设置页使用） */
export interface UpdateConfigValues {
  source: UpdateSource;
  host: string;
  port: string;
  repo: string;
}

/** 读取当前全部更新配置 */
export function getUpdateConfigValues(): UpdateConfigValues {
  return {
    source: getUpdateSource(),
    host: getUpdateServerHost(),
    port: getUpdateServerPort(),
    repo: getUpdateGithubRepo(),
  };
}

/** 归一化 GitHub 仓库名（去 URL/前缀/.git） */
function normalizeRepo(repo: string): string {
  return (repo ?? '').trim()
    .replace(/^https?:\/\/github\.com\//i, '')
    .replace(/\.git$/i, '')
    .replace(/^\/+|\/+$/g, '');
}

/** 把用户填写的地址拆成「协议 + host[:port]」，容忍带/不带 http(s):// 与末尾斜杠 */
function normalizeServerBase(rawHost: string, rawPort: string): string {
  let v = (rawHost ?? '').trim();
  let scheme: 'http' | 'https' = 'http';
  if (/^https:\/\//i.test(v)) { scheme = 'https'; v = v.replace(/^https:\/\//i, ''); }
  else if (/^http:\/\//i.test(v)) { v = v.replace(/^http:\/\//i, ''); }
  v = v.replace(/\/+$/, '');
  // 地址里已带端口时不再拼接
  const hasPort = /:\d+$/.test(v);
  const port = (rawPort ?? '').trim();
  const host = hasPort || !port ? v : `${v}:${port}`;
  return `${scheme}://${host}`;
}

/**
 * 纯函数：根据一组配置值计算清单 URL（便于设置页实时预览）
 *
 * @param kind - `desktop` 取 `latest.json`；`android` 取 `android.json`
 */
export function buildManifestUrl(v: UpdateConfigValues, kind: 'desktop' | 'android'): string {
  const file = kind === 'desktop' ? 'latest.json' : 'android.json';
  if (v.source === 'github') {
    const repo = normalizeRepo(v.repo) || DEFAULT_GITHUB_REPO;
    return `https://github.com/${repo}/releases/latest/download/${file}`;
  }
  const host = (v.host ?? '').trim();
  // 服务器地址未填 → 返回空串，由调用方给出「请先填写地址」的提示
  if (!host) return '';
  return `${normalizeServerBase(host, v.port)}/updates/${file}`;
}

/**
 * 计算当前配置下的更新清单 URL
 *
 * @param kind - `desktop` 取 `latest.json`；`android` 取 `android.json`
 */
export function updateManifestUrl(kind: 'desktop' | 'android'): string {
  return buildManifestUrl(getUpdateConfigValues(), kind);
}

/** 当前更新源的可读描述 */
export function describeUpdateSource(): string {
  const v = getUpdateConfigValues();
  if (v.source === 'github') {
    return `GitHub · ${normalizeRepo(v.repo) || DEFAULT_GITHUB_REPO}`;
  }
  const host = (v.host ?? '').trim();
  return host ? `服务器 · ${normalizeServerBase(host, v.port)}` : '服务器 · 未填写地址';
}
