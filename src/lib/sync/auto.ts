/**
 * 自动同步调度器
 *
 * 语义（对齐滴答清单的体验）：
 *  - **同步 = 以服务器为准的下载**：持续比对，服务器与本地不一致就下载。
 *  - **只有本地改动才上传**：没改过的项目绝不上传。
 *  - **对账**（reconcile）：
 *      1. 先推本地「有改动 / 从未同步」的项目
 *      2. 服务器有、本地没有   → 下载（**沿用服务器 ID**，绝不产生副本）
 *      3. 服务器没有、本地没有改动 → 删除本地（镜像服务器的删除）
 *      4. 同名不同 ID 的历史副本 → 合并为一个
 *
 * WebDAV / GitHub Gist 不支持 SSE，退回「变更即推 + 定时拉取」。
 *
 * @module sync/auto
 */

import { get, writable } from 'svelte/store';
import { deleteProject, importProjectWithId, projects, registerProjectDeletedHandler } from '$lib/stores/project';
import { getProjectPmJson, projectToPm } from '$lib/repositories';
import { safeSetItem } from '$lib/stores/storage-health';
import { CURRENT_PM_SCHEMA, detectSchemaVersion } from '$lib/utils/pm-schema';
import { SYNC_KEYS, getSyncConfig, getSyncInterval, getSyncMode, isSyncConfigured } from './config';
import {
  deleteRemoteProject,
  listRemoteProjects,
  pullChanges,
  pullProject,
  pushProject,
  subscribeChanges,
  supportsRealtime,
} from './client';
import type { ServerConflict, SyncConfig } from './types';

const DEBOUNCE_MS = 1200;

// ─── 运行时状态 ─────────────────────────────────────────────────────────────
export type SyncState = 'stopped' | 'idle' | 'syncing' | 'error' | 'offline';

export interface SyncRuntime {
  running: boolean;
  state: SyncState;
  lastSync: string;
  lastError: string;
  pending: number;
}

export const syncRuntime = writable<SyncRuntime>({
  running: false,
  state: 'stopped',
  lastSync: '',
  lastError: '',
  pending: 0,
});

export interface SyncConflictItem {
  id: string;
  name: string;
  localUpdatedAt: string;
  serverUpdatedAt: string | null;
  serverData: string | null;
  localData: string;
  baseRev: number;
  reason: 'revision_conflict' | 'schema_downgrade';
  localSchemaVersion: number;
  serverSchemaVersion: number;
  /** 服务器端已删除该项目（墓碑）；此时「采用服务器版本」= 删除本地 */
  serverDeleted?: boolean;
}

/** 待用户裁决的冲突 */
export const syncConflicts = writable<SyncConflictItem[]>([]);

// ─── 每项目同步状态（服务器 rev + 上次同步时的本地时间戳） ──────────────────
interface ProjectSyncState {
  rev: number;
  syncedAt: string;
  /** 上次同步完成时该项目的 updated_at，用于判断「本地是否改动过」 */
  syncedUpdatedAt: string;
}

type SyncStateMap = Record<string, ProjectSyncState>;

function loadStateMap(): SyncStateMap {
  try { return JSON.parse(localStorage.getItem(SYNC_KEYS.state) || '{}'); } catch { return {}; }
}

let stateMap: SyncStateMap = loadStateMap();

/**
 * 待推送的「本地删除」墓碑（持久化）
 *
 * 用户删掉一个已同步的项目后，必须在服务器上也删掉它；否则下一次对账会把它当成
 * 「服务器有、本地没有」而重新下载回来（真机踩坑）。删除请求可能是离线时发出的，
 * 所以先记在这里，联网后由 {@link flushPendingDeletes} 重试；在此期间对账会**跳过**这些 id，
 * 避免被服务器版本“复活”。
 */
const PENDING_DELETES_KEY = 'pm_sync_pending_deletes';

function loadPendingDeletes(): Set<string> {
  try {
    const raw = JSON.parse(localStorage.getItem(PENDING_DELETES_KEY) || '[]');
    return new Set(Array.isArray(raw) ? raw.filter((x): x is string => typeof x === 'string') : []);
  } catch { return new Set(); }
}

let pendingDeletes: Set<string> = loadPendingDeletes();

function persistPendingDeletes() {
  try { localStorage.setItem(PENDING_DELETES_KEY, JSON.stringify([...pendingDeletes])); } catch { /* ignore */ }
}

/** 重试把本地删除推给服务器；失败的保留到下次 */
async function flushPendingDeletes(): Promise<void> {
  if (pendingDeletes.size === 0 || !isSyncConfigured()) return;
  const cfg = getSyncConfig();
  // 只有服务器后端支持按 id 删除；WebDAV/Gist 是单文件后端，不在这里处理
  if (!supportsRealtime(cfg)) return;
  for (const id of [...pendingDeletes]) {
    try {
      await deleteRemoteProject(cfg, id);
      pendingDeletes.delete(id);
      // 极端竞态：删除过程中若有并发拉取把它又写回本地，这里再清一次
      if (get(projects).some((p) => p.id === id)) removeLocal(id);
    } catch { /* 离线/服务器不可达 → 保留重试 */ }
  }
  persistPendingDeletes();
}

/**
 * 用户在本地删除项目时的处理
 *
 * - 清掉该项目的同步状态；
 * - 若它曾经参与过同步，则记下墓碑并尝试删除服务器副本。
 *
 * 由 `stores/project.ts` 的回调触发；同步内部自己调用的 `removeLocal()`（服务器删→本地删）
 * 已在 `applyingRemote` 保护下，不会再次触发这里。
 */
function handleLocalDelete(id: string) {
  // 同步内部自己触发的删除（服务器删→本地删 / 去重 / 合并）不再回推服务器
  if (applyingRemote) return;
  const hadSyncState = !!stateMap[id];
  delete stateMap[id];
  delete snapshot[id];
  persistState();
  if (!hadSyncState || !isSyncConfigured()) return;
  pendingDeletes.add(id);
  persistPendingDeletes();
  void flushPendingDeletes();
}

// 注册删除回调（模块加载即生效，手动同步模式也能推送墓碑）
registerProjectDeletedHandler(handleLocalDelete);

function persistState() {
  safeSetItem(SYNC_KEYS.state, JSON.stringify(stateMap), '同步状态', false);
}

// ─── 内部变量 ───────────────────────────────────────────────────────────────
let running = false;
let reconciling = false;
let flushing = false;
let lastRev = 0;
let snapshot: Record<string, string> = {};
let pendingIds = new Set<string>();
let debounceTimer: ReturnType<typeof setTimeout> | null = null;
let pollTimer: ReturnType<typeof setTimeout> | null = null;
let closeStream: (() => void) | null = null;
let unsubscribeStore: (() => void) | null = null;

// 远端写入重入计数：期间的 store 变更不应触发「本地上传」
let remoteDepth = 0;
let applyingRemote = false;
function enterRemote() { remoteDepth++; applyingRemote = true; }
function exitRemote() { remoteDepth = Math.max(0, remoteDepth - 1); applyingRemote = remoteDepth > 0; }

function setRuntime(patch: Partial<SyncRuntime>) {
  syncRuntime.update((r) => ({ ...r, ...patch }));
}

function errMsg(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

function markSynced() {
  const stamp = new Date().toLocaleString();
  try { localStorage.setItem(SYNC_KEYS.lastSync, stamp); } catch { /* ignore */ }
  setRuntime({ lastSync: stamp, lastError: '', state: 'idle' });
}

function rebuildSnapshot() {
  snapshot = {};
  for (const p of get(projects)) snapshot[p.id] = p.updated_at;
}

/** 本地是否相对上次同步有改动（从未同步视为有改动） */
function isDirty(p: { id: string; updated_at: string }): boolean {
  const st = stateMap[p.id];
  return !st || st.syncedUpdatedAt !== p.updated_at;
}

function normalizeName(s: string): string {
  return (s || '').trim().toLowerCase();
}

function projectName(id: string): string {
  return get(projects).find((p) => p.id === id)?.name ?? id;
}

// ─── 远端应用 ───────────────────────────────────────────────────────────────
/**
 * 应用服务器数据
 *
 * 关键：用 `importProjectWithId(id, data)` 写入，**沿用服务器 ID**，
 * 避免旧实现用 genId() 造成「本地/服务器 ID 不一致 → 重复上传 → 副本」。
 */
/**
 * 应用服务器数据
 *
 * ⚠️ 这里**只写主存储**（`importProjectWithId` → localStorage 快照）。
 * 项目本地文件夹（外部副本）的写入**永远**由 `repositories/pm-file-repo.ts` 的
 * `savePmFile()` 依据「归属设备」判定后执行——**不要在同步流程里直接调 `writePmFile`**，
 * 否则非归属设备会污染对端同名目录（v0.6.0 修的就是这个）。
 */
function schemaVersionFromJson(data: string): number {
  try { return detectSchemaVersion(JSON.parse(data)); } catch { return 1; }
}

function applyRemote(id: string, data: string, rev?: number, updatedAt?: string): boolean {
  enterRemote();
  try {
    const ok = importProjectWithId(data, id);
    if (!ok) {
      // 导入失败就不写同步状态，否则会被当成“已同步”而永远不再下载
      const schema = schemaVersionFromJson(data);
      const detail = schema > CURRENT_PM_SCHEMA
        ? `远端项目使用 schema v${schema}，当前客户端仅支持 v${CURRENT_PM_SCHEMA}，请升级客户端`
        : `导入项目失败：${id}`;
      setRuntime({ state: 'error', lastError: detail });
      return false;
    }
    const local = get(projects).find((p) => p.id === id);
    stateMap[id] = {
      rev: typeof rev === 'number' ? rev : stateMap[id]?.rev ?? 0,
      syncedAt: new Date().toISOString(),
      syncedUpdatedAt: local?.updated_at ?? updatedAt ?? '',
    };
    persistState();
    if (local) snapshot[id] = local.updated_at;
    return true;
  } finally {
    exitRemote();
  }
}

/** 删除前的本地备份键（可恢复） */
const BACKUP_KEY = 'pm_projects_backup';

/** 删除本地项目前先备份其 .pm 数据，保留最近 20 条 */
function backupBeforeDelete(id: string) {
  try {
    const json = getProjectPmJson(id);
    if (!json) return;
    const raw = localStorage.getItem(BACKUP_KEY);
    const map: Record<string, { name: string; json: string; at: string }> = raw ? JSON.parse(raw) : {};
    map[id] = { name: projectName(id), json, at: new Date().toISOString() };
    const entries = Object.entries(map)
      .sort((a, b) => String(b[1].at).localeCompare(String(a[1].at)))
      .slice(0, 20);
    safeSetItem(BACKUP_KEY, JSON.stringify(Object.fromEntries(entries)), '删除前备份', false);
  } catch { /* 备份失败不阻断 */ }
}

/** 本地删除（仅用于「服务器明确删除」或「同名重复副本清理」） */
function removeLocal(id: string) {
  backupBeforeDelete(id);
  enterRemote();
  try {
    deleteProject(id);
    delete stateMap[id];
    persistState();
    delete snapshot[id];
  } finally {
    exitRemote();
  }
  // 该项目已不存在，待裁决的冲突条目也没有意义了
  syncConflicts.update((list) => list.filter((c) => c.id !== id));
}

// ─── 推送 ───────────────────────────────────────────────────────────────────
function schedulePush(ids: string[]) {
  for (const id of ids) pendingIds.add(id);
  setRuntime({ pending: pendingIds.size });
  if (debounceTimer) clearTimeout(debounceTimer);
  debounceTimer = setTimeout(() => { void flushPush(); }, DEBOUNCE_MS);
}

function handleConflict(cfg: SyncConfig, id: string, conflict: ServerConflict, localData: string, localUpdatedAt: string) {
  const lastSynced = stateMap[id]?.syncedUpdatedAt;
  const localChanged = !lastSynced || lastSynced !== localUpdatedAt;
  const localSchemaVersion = conflict.incomingSchemaVersion ?? schemaVersionFromJson(localData);
  const serverSchemaVersion = conflict.serverSchemaVersion
    ?? (conflict.serverData ? schemaVersionFromJson(conflict.serverData) : localSchemaVersion);
  const reason = conflict.reason ?? 'revision_conflict';

  if (serverSchemaVersion > CURRENT_PM_SCHEMA) {
    setRuntime({
      state: 'error',
      lastError: `服务器项目「${projectName(id)}」使用 schema v${serverSchemaVersion}，当前客户端仅支持 v${CURRENT_PM_SCHEMA}，请升级客户端`,
    });
    return;
  }

  if (!localChanged) {
    // 本地未改动 → 服务器优先，直接采用服务器版本
    if (conflict.serverData) {
      if (!applyRemote(id, conflict.serverData, conflict.serverRev, conflict.serverUpdatedAt ?? undefined)) return;
    } else if (conflict.deleted) {
      // 服务器已删除该项目 → 本地也删除（removeLocal 会先写入删除前备份）
      removeLocal(id);
    }
    markSynced();
    return;
  }

  syncConflicts.update((list) => [
    ...list.filter((c) => c.id !== id),
    {
      id,
      name: projectName(id),
      localUpdatedAt,
      serverUpdatedAt: conflict.serverUpdatedAt,
      serverData: conflict.serverData,
      localData,
      baseRev: conflict.serverRev,
      reason,
      localSchemaVersion,
      serverSchemaVersion,
      serverDeleted: conflict.deleted,
    },
  ]);
  setRuntime({ state: 'idle', lastError: `项目「${projectName(id)}」存在同步冲突，请处理` });
}

/** 推送单个项目的结果 */
type PushResult = 'ok' | 'conflict' | 'error';

/** 推送单个项目 */
async function pushOne(cfg: SyncConfig, id: string): Promise<PushResult> {
  const proj = get(projects).find((p) => p.id === id);
  if (!proj) return 'error';
  const json = JSON.stringify(projectToPm(proj));
  const storedSnapshot = getProjectPmJson(id);
  if (storedSnapshot !== json && !safeSetItem(`pm_file_${id}`, json, `项目「${proj.name}」同步快照`)) {
    setRuntime({ state: 'error', lastError: `无法更新项目「${proj.name}」的 v4 同步快照` });
    return 'error';
  }

  try {
    const outcome = await pushProject(cfg, id, json, stateMap[id]?.rev);
    if ('conflict' in outcome) {
      handleConflict(cfg, id, outcome, json, proj.updated_at);
      return 'conflict';
    }
    const rev = outcome.rev ?? stateMap[id]?.rev ?? 0;
    stateMap[id] = { rev, syncedAt: new Date().toISOString(), syncedUpdatedAt: proj.updated_at };
    persistState();
    snapshot[id] = proj.updated_at;
    lastRev = Math.max(lastRev, rev);
    return 'ok';
  } catch (e) {
    // 超时 / 断网属可恢复：项目会重新进 pending，下一次对账自动重试
    pendingIds.add(id);
    setRuntime({ state: 'error', lastError: `推送「${proj.name}」失败（将自动重试）：${errMsg(e)}` });
    return 'error';
  }
}

/** 推送所有待推项目 */
async function flushPush(force = false) {
  if ((!running && !force) || flushing) return;
  if (!isSyncConfigured()) { setRuntime({ state: 'error', lastError: '同步未配置（缺少地址或 Token）' }); return; }
  if (pendingIds.size === 0) return;

  flushing = true;
  const ids = Array.from(pendingIds);
  pendingIds.clear();
  setRuntime({ state: 'syncing', pending: 0 });

  for (const id of ids) await pushOne(getSyncConfig(), id);

  flushing = false;
  setRuntime({ pending: pendingIds.size, state: pendingIds.size ? 'error' : 'idle' });
}

// ─── 拉取 ───────────────────────────────────────────────────────────────────
/**
 * 下载单个项目
 *
 * @param force 为 true 时忽略「回声抑制」（本地缺该项目时必须强制下载）
 */
async function pullOne(cfg: SyncConfig, id: string, rev?: number, updatedAt?: string, force = false): Promise<boolean> {
  // 本地已主动删除、墓碑还没推成功 → 不要被服务器版本“复活”
  if (pendingDeletes.has(id)) return true;
  // 回声抑制：自己刚推的 rev 不再拉回（但 force=true 时强制拉）
  if (!force && rev !== undefined && stateMap[id]?.rev !== undefined && stateMap[id].rev >= rev) return true;
  try {
    const res = await pullProject(cfg, id);
    if (!applyRemote(id, res.data, res.rev ?? rev, res.updated_at ?? updatedAt)) return false;
    if (res.rev) lastRev = Math.max(lastRev, res.rev);
    markSynced();
    return true;
  } catch (e) {
    setRuntime({ state: 'error', lastError: errMsg(e) });
    return false;
  }
}

/** 强制全量下载服务器上的所有项目（忽略同步记录，用于「下载」按钮与灾后恢复） */
async function forcePullAll(cfg: SyncConfig) {
  const list = await listRemoteProjects(cfg);
  let complete = true;
  for (const meta of list.projects) {
    if (pendingDeletes.has(meta.id)) continue;
    if (!await pullOne(cfg, meta.id, meta.rev, meta.updated_at, true)) complete = false;
    lastRev = Math.max(lastRev, meta.rev);
  }
  rebuildSnapshot();
  if (complete) markSynced();
}

/** 本地同名副本清理：仅当分组里存在「已同步过」的项目时才清理，保留已同步的那个 */
function dedupeLocalByName() {
  const groups = new Map<string, { id: string; name: string; created_at: string }[]>();
  for (const p of get(projects)) {
    const k = normalizeName(p.name);
    const arr = groups.get(k) ?? [];
    arr.push({ id: p.id, name: p.name, created_at: p.created_at });
    groups.set(k, arr);
  }
  for (const arr of groups.values()) {
    if (arr.length < 2) continue;
    // 没有已同步过的 → 不动（可能是用户有意创建的同名项目）
    if (!arr.some((p) => stateMap[p.id])) continue;
    const keep = arr.find((p) => stateMap[p.id]) ?? arr[0];
    for (const p of arr) if (p.id !== keep.id) removeLocal(p.id);
  }
}

/**
 * 本地与服务器同名但 ID 不同（历史副本）→ 合并
 *
 * - 本地副本能推送成功 → 以本地 ID 为准，删掉服务器旧 ID（原行为）
 * - 本地副本推送**冲突**（服务器上同名项目已更新，或本项目在服务器已被删除产生墓碑）
 *   → **服务器优先**：下载服务器版本并移除本地同名副本（`removeLocal` 会先写入删除前备份）。
 *   旧实现 `pushOne` 失败后什么都不做，会与服务器同名项形成**永久 409 死循环**，
 *   且服务器的同名新项目永远下载不下来。
 * - 推送因网络/写盘等**非冲突**原因失败 → 不动本地，交给下一轮重试。
 */
async function mergeLocalWithServer(cfg: SyncConfig, localId: string, serverId: string) {
  const res = await pushOne(cfg, localId);
  if (res === 'ok') {
    try { await deleteRemoteProject(cfg, serverId); } catch { /* 忽略 */ }
    return;
  }
  if (res === 'error') return;

  // res === 'conflict' → 服务器优先
  await pullOne(cfg, serverId, undefined, undefined, true);
  if (get(projects).some((p) => p.id === serverId)) {
    removeLocal(localId);
  }
}

// ─── 对账（核心） ───────────────────────────────────────────────────────────
async function reconcile(cfg: SyncConfig) {
  if (!supportsRealtime(cfg) || reconciling) return;
  reconciling = true;
  setRuntime({ state: 'syncing' });

  try {
    // 0) 本地同名副本清理（**全新客户端不做**，避免误删）
    if (Object.keys(stateMap).length > 0) dedupeLocalByName();

    // 1) 先推本地「有改动 / 从未同步」的项目
    const dirtyIds = get(projects)
      .filter((p) => isDirty(p))
      .map((p) => p.id);
    if (dirtyIds.length) {
      pendingIds = new Set(dirtyIds);
      await flushPush(true);
    }

    // 2) 拉服务器清单
    let list: { rev: number; projects: { id: string; name: string; rev: number; updated_at: string }[] };
    try {
      list = await listRemoteProjects(cfg);
    } catch (e) {
      setRuntime({ state: 'offline', lastError: errMsg(e) });
      return;
    }

    // 2.5) 删除同步：**只认服务器明确的“删除墓碑”**
    //     绝不把「不在服务器列表里」当作删除（否则服务器一旦数据丢失，本地会被全部清空）。
    try {
      const changes = await pullChanges(cfg, lastRev);
      for (const ch of changes) {
        lastRev = Math.max(lastRev, ch.rev);
        if (!ch.deleted) continue;
        const st = stateMap[ch.id];
        const p = get(projects).find((x) => x.id === ch.id);
        // 需同时满足：曾同步过、删除发生在上次同步之后、本地无改动
        if (p && st && ch.rev > st.rev && !isDirty(p)) {
          removeLocal(ch.id);
        }
      }
    } catch { /* 墓碑拉取失败不影响主流程 */ }

    // 2.6) 本地主动删除 → 重试推送墓碑；推成功前不下载这些项目
    await flushPendingDeletes();

    // 3) 服务器 → 本地：没有则下载；有但服务器更新且本地无改动也下载
    for (const meta of list.projects) {
      lastRev = Math.max(lastRev, meta.rev);
      // 本地已删、墓碑还没推成功 → 跳过，否则会被重新下载回来
      if (pendingDeletes.has(meta.id)) continue;

      const local = get(projects).find((p) => p.id === meta.id);
      if (local) {
        const st = stateMap[meta.id];
        // 本地未改动 && 服务器版本更新 → 下载（补齐 SSE 可能漏掉的事件）
        if (!isDirty(local) && (!st || meta.rev > st.rev)) {
          await pullOne(cfg, meta.id, meta.rev, meta.updated_at);
        }
        continue;
      }

      const twin = get(projects).find((p) => normalizeName(p.name) === normalizeName(meta.name));
      if (twin) {
        await mergeLocalWithServer(cfg, twin.id, meta.id);
        continue;
      }
      // 本地没有该项目 → 强制下载（不受同步记录 rev 影响）
      await pullOne(cfg, meta.id, meta.rev, meta.updated_at, true);
    }

    // 4) 本地有 → 服务器没有：**一律上传，绝不删除**
    //    （服务器数据丢失/重置时，自动把本地项目补回服务器；这是最安全的行为）
    const serverIds = new Set(list.projects.map((p) => p.id));
    for (const p of get(projects).slice()) {
      if (serverIds.has(p.id)) continue;

      // 同名重复副本（同名项目已在服务器上）→ 清理本副本
      const twin = get(projects).find(
        (q) => q.id !== p.id && serverIds.has(q.id) && normalizeName(q.name) === normalizeName(p.name)
      );
      if (twin) { removeLocal(p.id); continue; }

      // 其余一律上传（含「本地无改动但服务器没有」→ 相当于把数据补回服务器）
      await pushOne(cfg, p.id);
    }

    rebuildSnapshot();
    markSynced();
  } finally {
    reconciling = false;
  }
}

// ─── 轮询 / SSE ─────────────────────────────────────────────────────────────
function schedulePoll(cfg: SyncConfig) {
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = setTimeout(async () => {
    if (!running) return;
    if (supportsRealtime(cfg)) {
      await reconcile(cfg);
    } else {
      // WebDAV / Gist：单文件，拉取后按 project.id 匹配应用
      try {
        const res = await pullProject(cfg, '');
        const parsed = JSON.parse(res.data);
        const id = parsed?.project?.id;
        if (id) applyRemote(id, res.data);
      } catch (e) {
        setRuntime({ state: 'offline', lastError: errMsg(e) });
      }
    }
    schedulePoll(cfg);
  }, getSyncInterval() * 1000);
}

function openStream(cfg: SyncConfig) {
  closeStream?.();
  closeStream = null;
  if (!supportsRealtime(cfg)) return;
  closeStream = subscribeChanges(
    cfg,
    (e) => {
      if (e.rev) lastRev = Math.max(lastRev, e.rev);
      if (!e.id) return;
      // 本地已主动删除、墓碑还没推成功 → 忽略服务器的“复活”事件
      if (pendingDeletes.has(e.id)) return;
      if (e.type === 'delete') {
        // 服务器删除 → 本地也删（仅当曾同步过、删除在上次同步之后、且本地无改动）
        const p = get(projects).find((x) => x.id === e.id);
        const st = stateMap[e.id];
        if (p && st && (!e.rev || e.rev > st.rev) && !isDirty(p)) {
          removeLocal(e.id);
        }
        return;
      }
      // 本地有改动 → 不直接覆盖（避免丢本地修改），交给对账走冲突提示
      const local = get(projects).find((x) => x.id === e.id);
      if (local && isDirty(local)) return;
      void pullOne(cfg, e.id, e.rev, e.updated_at);
    },
    (s) => setRuntime({ state: s === 'open' ? 'idle' : 'offline' })
  );
}

// ─── 启停 ───────────────────────────────────────────────────────────────────
function teardown() {
  if (debounceTimer) { clearTimeout(debounceTimer); debounceTimer = null; }
  if (pollTimer) { clearTimeout(pollTimer); pollTimer = null; }
  closeStream?.();
  closeStream = null;
  unsubscribeStore?.();
  unsubscribeStore = null;
  pendingIds.clear();
  remoteDepth = 0;
  applyingRemote = false;
}

/** 启动自动同步（仅当同步模式为 auto 时生效；可重复调用） */
export function startAutoSync() {
  teardown();
  const cfg = getSyncConfig();
  const auto = getSyncMode() === 'auto' && isSyncConfigured(cfg);

  if (!auto) {
    running = false;
    setRuntime({ running: false, state: 'stopped', pending: 0 });
    return;
  }

  running = true;
  rebuildSnapshot();
  setRuntime({ running: true, state: 'idle', lastError: '' });

  unsubscribeStore = projects.subscribe(() => {
    if (!running || applyingRemote) return;
    const dirty: string[] = [];
    for (const p of get(projects)) {
      if (snapshot[p.id] !== p.updated_at) dirty.push(p.id);
    }
    if (dirty.length) schedulePush(dirty);
  });

  openStream(cfg);
  void reconcile(cfg);
  schedulePoll(cfg);
}

/** 停止自动同步 */
export function stopAutoSync() {
  running = false;
  teardown();
  setRuntime({ running: false, state: 'stopped', pending: 0 });
}

/** 配置变更后重启调度器（设置界面调用） */
export function restartAutoSync() {
  startAutoSync();
}

/** 手动「立即同步」：推本地改动 + 与服务器对账 */
export async function syncNow(): Promise<void> {
  const cfg = getSyncConfig();
  if (!isSyncConfigured(cfg)) throw new Error('同步未配置（缺少地址或 Token）');
  if (supportsRealtime(cfg)) {
    await reconcile(cfg);
    return;
  }
  // WebDAV / Gist：推全部 + 拉一次
  rebuildSnapshot();
  pendingIds = new Set(get(projects).map((p) => p.id));
  await flushPush(true);
  markSynced();
}

/** 手动：仅上传所有项目 */
export async function pushAllNow(): Promise<void> {
  const cfg = getSyncConfig();
  if (!isSyncConfigured(cfg)) throw new Error('同步未配置（缺少地址或 Token）');
  rebuildSnapshot();
  pendingIds = new Set(get(projects).map((p) => p.id));
  await flushPush(true);
}

/** 手动：仅下载（强制全量拉取，忽略同步记录） */
export async function pullAllNow(): Promise<void> {
  const cfg = getSyncConfig();
  if (!isSyncConfigured(cfg)) throw new Error('同步未配置（缺少地址或 Token）');
  setRuntime({ state: 'syncing' });
  try {
    if (supportsRealtime(cfg)) {
      lastRev = 0;
      await forcePullAll(cfg);
      return;
    }
    const res = await pullProject(cfg, '');
    const parsed = JSON.parse(res.data);
    const id = parsed?.project?.id;
    if (!id || !applyRemote(id, res.data)) throw new Error('远端项目无法导入');
    markSynced();
  } catch (e) {
    setRuntime({ state: 'error', lastError: errMsg(e) });
    throw e;
  }
}

/** 处理冲突：server = 采用服务器版本；local = 用本地覆盖服务器 */
export async function resolveConflict(id: string, choice: 'server' | 'local'): Promise<void> {
  const item = get(syncConflicts).find((c) => c.id === id);
  if (!item) return;
  const cfg = getSyncConfig();

  let resolved = false;
  try {
    if (choice === 'server') {
      if (item.serverData) {
        resolved = applyRemote(id, item.serverData, item.baseRev, item.serverUpdatedAt ?? undefined);
      } else {
        // 服务器版本 = 该项目已被删除（墓碑）→ 本地也删除
        removeLocal(id);
        resolved = true;
      }
    } else {
      if (item.localSchemaVersion < item.serverSchemaVersion) {
        throw new Error(`不能用 schema v${item.localSchemaVersion} 覆盖服务器的 v${item.serverSchemaVersion} 项目`);
      }
      const outcome = await pushProject(cfg, id, item.localData, item.baseRev);
      if ('conflict' in outcome) {
        handleConflict(cfg, id, outcome, item.localData, item.localUpdatedAt);
      } else {
        const rev = outcome.rev ?? item.baseRev;
        const local = get(projects).find((p) => p.id === id);
        stateMap[id] = { rev, syncedAt: new Date().toISOString(), syncedUpdatedAt: local?.updated_at ?? '' };
        persistState();
        lastRev = Math.max(lastRev, rev);
        resolved = true;
      }
    }
    if (resolved) markSynced();
  } finally {
    if (resolved) syncConflicts.update((list) => list.filter((c) => c.id !== id));
  }
}
