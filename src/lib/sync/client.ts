/**
 * 统一同步客户端
 *
 * 对外暴露与后端无关的接口：
 * - 服务器后端：走 fetch + SSE（见 ./server）
 * - WebDAV / GitHub Gist：走 Rust 命令（见 ./rust）
 *
 * @module sync/client
 */

import type { ServerChange, ServerConflict, ServerEvent, ServerProjectMeta, SyncConfig, SyncResult } from './types';
import { rustSyncPull, rustSyncPush, rustSyncTest } from './rust';
import {
  isServerConflict,
  serverChanges,
  serverDelete,
  serverEvents,
  serverHealth,
  serverList,
  serverPull,
  serverPush,
} from './server';

/** 推送结果：成功，或服务器返回乐观锁冲突 */
export type PushOutcome = { ok: true; rev?: number } | ServerConflict;

/** 拉取结果 */
export interface PullOutcome {
  data: string;
  rev?: number;
  updated_at?: string;
  schema_version?: number;
}

/** 该后端是否支持 SSE 实时推送 */
export function supportsRealtime(cfg: SyncConfig): boolean {
  return cfg.backend === 'server';
}

/** 测试连接 */
export async function testConnection(cfg: SyncConfig): Promise<boolean> {
  if (cfg.backend === 'server') {
    const health = await serverHealth(cfg);
    return !!health.ok;
  }
  return rustSyncTest(cfg);
}

/**
 * 推送单个项目
 *
 * @param baseRev 服务器后端用于乐观锁；WebDAV/Gist 忽略
 */
export async function pushProject(
  cfg: SyncConfig,
  projectId: string,
  json: string,
  baseRev?: number
): Promise<PushOutcome> {
  if (cfg.backend === 'server') {
    const res = await serverPush(cfg, projectId, json, baseRev);
    return isServerConflict(res) ? res : { ok: true, rev: res.rev };
  }
  const res: SyncResult = await rustSyncPush(cfg, json);
  if (!res.success) throw new Error(res.message);
  return { ok: true };
}

/** 拉取单个项目 */
export async function pullProject(cfg: SyncConfig, projectId: string): Promise<PullOutcome> {
  if (cfg.backend === 'server') {
    const res = await serverPull(cfg, projectId);
    return { data: res.data, rev: res.rev, updated_at: res.updated_at, schema_version: res.schema_version };
  }
  const data = await rustSyncPull(cfg);
  return { data };
}

/** 增量变更（仅服务器后端支持） */
export async function pullChanges(cfg: SyncConfig, since: number): Promise<ServerChange[]> {
  if (cfg.backend !== 'server') return [];
  const res = await serverChanges(cfg, since);
  return res.changes;
}

/** 订阅服务器实时变更（仅服务器后端；其他后端返回空取消函数） */
export function subscribeChanges(
  cfg: SyncConfig,
  onEvent: (e: ServerEvent) => void,
  onState?: (s: 'open' | 'error') => void
): () => void {
  if (cfg.backend !== 'server') return () => {};
  return serverEvents(cfg, onEvent, onState);
}

/** 列出服务器上的项目（仅服务器后端） */
export async function listRemoteProjects(
  cfg: SyncConfig
): Promise<{ rev: number; projects: ServerProjectMeta[] }> {
  if (cfg.backend !== 'server') return { rev: 0, projects: [] };
  return serverList(cfg);
}

/** 删除服务器上的项目（仅服务器后端） */
export async function deleteRemoteProject(cfg: SyncConfig, id: string): Promise<void> {
  if (cfg.backend !== 'server') return;
  await serverDelete(cfg, id);
}
