/**
 * 同步配置读写（localStorage）
 *
 * auto.ts 与设置界面共用，保证读到同一份配置。
 *
 * @module sync/config
 */

import type { SyncBackend, SyncConfig, SyncMode } from './types';
import { DEFAULT_SERVER_URL, DEFAULT_SYNC_INTERVAL } from './types';

export const SYNC_KEYS = {
  backend: 'pm_sync_backend',
  mode: 'pm_sync_mode',
  interval: 'pm_sync_interval',
  serverUrl: 'pm_sync_server_url',
  token: 'pm_sync_token',
  webdavUrl: 'pm_sync_url',
  webdavUser: 'pm_sync_user',
  webdavPass: 'pm_sync_pass',
  gistId: 'pm_sync_gist',
  lastSync: 'pm_sync_last',
  state: 'pm_sync_state',
} as const;

function read(key: string, fallback = ''): string {
  try { return localStorage.getItem(key) ?? fallback; } catch { return fallback; }
}

export function getSyncBackend(): SyncBackend {
  const v = read(SYNC_KEYS.backend);
  return v === 'github_gist' || v === 'server' ? v : 'webdav';
}

export function getSyncMode(): SyncMode {
  const mode = read(SYNC_KEYS.mode);
  return mode === 'manual' ? 'manual' : 'auto';
}

export function getSyncInterval(): number {
  const n = Number(read(SYNC_KEYS.interval, String(DEFAULT_SYNC_INTERVAL)));
  if (!Number.isFinite(n)) return DEFAULT_SYNC_INTERVAL;
  return Math.min(300, Math.max(3, Math.round(n)));
}

export function getServerUrl(): string {
  return read(SYNC_KEYS.serverUrl, DEFAULT_SERVER_URL) || DEFAULT_SERVER_URL;
}

export function getServerToken(): string {
  return read(SYNC_KEYS.token);
}

/** 汇总当前同步配置 */
export function getSyncConfig(): SyncConfig {
  const backend = getSyncBackend();
  if (backend === 'server') {
    return { backend, url: getServerUrl(), username: '', password: getServerToken() };
  }
  return {
    backend,
    url: read(SYNC_KEYS.webdavUrl),
    username: read(SYNC_KEYS.webdavUser) || 'github',
    password: read(SYNC_KEYS.webdavPass),
  };
}

/** 后端是否已配置必要凭据 */
export function isSyncConfigured(cfg: SyncConfig = getSyncConfig()): boolean {
  if (cfg.backend === 'server') return !!cfg.url && !!cfg.password;
  if (cfg.backend === 'github_gist') return !!cfg.password;
  return !!cfg.url && !!cfg.password;
}
