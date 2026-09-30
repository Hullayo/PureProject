/**
 * 本地存储健康状态
 *
 * 背景：`localStorage` 有配额（WebView ≈ 5MB）。旧实现里
 * `saveProjects()` / `savePmFile()` 都是 `try { setItem } catch {}` —— 一旦写满，
 * **数据静默丢失**，而且 `.pm` 快照会停留在旧版本 → 同步把旧数据推给服务器。
 *
 * 这里做三件事：
 *   1. 任何一次写入失败都记录并在界面上**可见**（顶栏告警条 + Toast）；
 *   2. 估算占用，接近阈值时提前预警；
 *   3. 提供「导出备份」引导，避免用户只能干瞪眼。
 *
 * ─── 两条通道，别混在一起 ───────────────────────────────────────────────────
 *
 * | 通道 | 写什么 | 失败后果 | 展示 |
 * |---|---|---|---|
 * | **主存储**（本节上面的 `storageHealth`） | localStorage 的 `pm_projects` / `pm_file_<id>` | **真丢数据**，同步会推旧快照 | 顶栏**红条** + Toast |
 * | **外部副本**（本节下面的 `externalSyncIssues`） | 项目自选的本地文件夹里的 `.pm` | 只是少了一份外部拷贝 | 「设置 → 数据」里的清单（不打扰） |
 *
 * 真机教训：把「外部副本失败」当成主存储失败弹红条，会让每次改任务都弹一次（因为项目里
 * 存的是别的平台的路径），用户被反复打扰却并没有丢数据。
 *
 * @module stores/storage-health
 */

import { writable, get } from 'svelte/store';
import { toast } from './toast';

/** localStorage 软预算（WebView 一般 5MB，留 20% 余量） */
export const STORAGE_BUDGET_BYTES = 4 * 1024 * 1024;

/** 预警阈值：占预算 80% */
const WARN_RATIO = 0.8;

export type StorageProblem = 'quota' | 'write' | 'unavailable';

export interface StorageHealth {
  /** 是否处于异常状态（界面据此显示告警条） */
  error: boolean;
  /** 问题类型 */
  problem: StorageProblem | null;
  /** 触发时的细节（异常信息 / 键名） */
  detail: string;
  /** 最近一次估算的占用字节数 */
  usedBytes: number;
  /** 是否接近配额（>= 80%） */
  nearLimit: boolean;
  /** 最近更新时间（ISO） */
  at: string;
}

const initial: StorageHealth = {
  error: false,
  problem: null,
  detail: '',
  usedBytes: 0,
  nearLimit: false,
  at: '',
};

export const storageHealth = writable<StorageHealth>(initial);

/** 估算 localStorage 已用字节数（key + value 的 UTF-16 长度 ×2） */
export function estimateLocalStorageBytes(): number {
  try {
    let total = 0;
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key === null) continue;
      const value = localStorage.getItem(key) ?? '';
      total += (key.length + value.length) * 2;
    }
    return total;
  } catch {
    return 0;
  }
}

/** 刷新占用估算，返回是否接近上限 */
export function refreshStorageUsage(): { usedBytes: number; nearLimit: boolean } {
  const usedBytes = estimateLocalStorageBytes();
  const nearLimit = usedBytes >= STORAGE_BUDGET_BYTES * WARN_RATIO;
  storageHealth.update((h) => ({ ...h, usedBytes, nearLimit, at: new Date().toISOString() }));
  return { usedBytes, nearLimit };
}

/** 判断异常是否属于「配额写满」 */
function isQuotaError(e: unknown): boolean {
  if (!(e instanceof Error)) return false;
  return /quota|exceeded|storage is full|quotaexceeded/i.test(`${e.name} ${e.message}`);
}

/** 人类可读的体积 */
export function fmtBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(2)} MB`;
}

/**
 * 上报一次本地存储写入失败
 *
 * @param e - 捕获到的异常
 * @param what - 写的是什么（用于提示，如 `项目列表` / `项目 xxx 的快照`）
 * @param notify - 是否弹 Toast（默认 true；批量写入时只弹一次）
 */
export function reportStorageFailure(e: unknown, what: string, notify = true): void {
  const quota = isQuotaError(e);
  const problem: StorageProblem = quota ? 'quota' : 'write';
  const detail = `${what}：${e instanceof Error ? e.message : String(e)}`;
  const { usedBytes } = refreshStorageUsage();

  storageHealth.update((h) => ({ ...h, error: true, problem, detail }));
  console.error('[Storage] 本地存储写入失败', detail, e);

  if (notify) {
    // 文案直接内联：这里可能在 i18n 初始化前被调用（如启动时加载数据）
    toast(
      quota
        ? `本地存储已满（已用 ${fmtBytes(usedBytes)}），数据可能无法保存。请到「设置 → 数据」导出备份并清理旧项目。`
        : `本地数据保存失败（${what}），请到「设置 → 数据」检查并导出备份。`,
      'err',
      10000
    );
  }
}

/** 清除告警（用户已处理 / 下次写入成功） */
export function clearStorageFailure(): void {
  storageHealth.update((h) => ({ ...h, error: false, problem: null, detail: '' }));
}

/**
 * 安全读取 localStorage（读失败返回 null，不抛）
 *
 * 与 `safeSetItem` 配对，避免各处再写 `try { getItem } catch {}`。
 */
export function safeGetRaw(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

/**
 * 包装一次 localStorage 写入
 *
 * @returns 是否写入成功（调用方据此决定是否继续/提示）
 */
export function safeSetItem(key: string, value: string, what: string, notify = true): boolean {
  try {
    localStorage.setItem(key, value);
    // 写成功即视为恢复：顺手刷新占用，若已接近上限仍留预警
    const { nearLimit, usedBytes } = refreshStorageUsage();
    if (get(storageHealth).error) clearStorageFailure();
    if (nearLimit) {
      toast(`本地存储已用 ${fmtBytes(usedBytes)}，接近上限，建议到「设置 → 数据」导出备份。`, 'info', 8000);
    }
    return true;
  } catch (e) {
    reportStorageFailure(e, what, notify);
    return false;
  }
}


// ─── 外部副本（项目自选的本地文件夹）─────────────────────────────────────────

/** 外部副本写入问题（按项目去重，不重复打扰） */
export interface ExternalSyncIssue {
  projectId: string;
  /** 项目名（展示用） */
  name: string;
  /** 目标路径 */
  path: string;
  /** 人类可读原因 */
  message: string;
  /** 是否属于「本平台不支持」而不是真失败（UI 文案不同，严重度更低） */
  unsupported: boolean;
  /** 最近一次发生时间（ISO） */
  at: string;
}

/** projectId → 问题（同一项目重复失败只保留一条，避免无限增长与反复弹窗） */
export const externalSyncIssues = writable<Record<string, ExternalSyncIssue>>({});

/** 本会话已提示过的项目（同一个项目只弹一次 Toast，避免每次保存都打扰） */
const notifiedProjects = new Set<string>();

/**
 * 记录一次外部副本问题
 *
 * - 按 `projectId` 去重（后写覆盖前写）
 * - 同一项目**一个会话只弹一次** Toast（`unsupported` 用 info，真失败用 err）
 */
export function reportExternalSyncIssue(issue: Omit<ExternalSyncIssue, 'at'>): void {
  const first = !notifiedProjects.has(issue.projectId);
  externalSyncIssues.update((map) => ({
    ...map,
    [issue.projectId]: { ...issue, at: new Date().toISOString() },
  }));
  if (first) {
    notifiedProjects.add(issue.projectId);
    toast(
      issue.unsupported
        ? `项目「${issue.name}」的外部文件夹在本机不可用（${issue.path}），已跳过外部拷贝；数据仍保存在应用内部。可在「设置 → 数据」改为应用内部存储。`
        : `项目「${issue.name}」写入外部文件夹失败：${issue.message}。数据已保存在应用内部，可在「设置 → 数据」查看。`,
      issue.unsupported ? 'info' : 'err',
      12000
    );
  }
  console.warn('[Storage] 外部副本问题', issue);
}

/** 外部副本恢复正常（写成功 / 项目改为应用内部存储） */
export function clearExternalSyncIssue(projectId: string): void {
  externalSyncIssues.update((map) => {
    if (!(projectId in map)) return map;
    const next = { ...map };
    delete next[projectId];
    return next;
  });
  notifiedProjects.delete(projectId);
}

/** 全部清除（用户手动「忽略」） */
export function clearAllExternalSyncIssues(): void {
  externalSyncIssues.set({});
  notifiedProjects.clear();
}

// ─── 只读「归属设备」角标（非归属设备打开项目时用）────────────────────────

/**
 * projectId → 归属设备名。
 *
 * 与 `externalSyncIssues` 不同：这是**正常状态**的只读标注，
 * **绝不**弹 Toast、**不**进「外部副本问题」清单、**不**置红条。
 * 非归属设备（`planLocalFileWriteWithOwner` 返回 `notOwner`）保存项目时由
 * `pm-file-repo.savePmFile()` 写入，供侧边栏 / 任务列表渲染 `🔒 由 <设备> 管理`。
 */
export const ownerMarks = writable<Record<string, { ownerName: string }>>({});

/** 标记某项目由「别的设备」管理（只读角标用） */
export function setOwnerMark(projectId: string, ownerName: string): void {
  const name = ownerName?.trim() || '?';
  ownerMarks.update((m) => (m[projectId]?.ownerName === name ? m : { ...m, [projectId]: { ownerName: name } }));
}

/** 清除某项目的归属角标（本机成为归属 / 改为内部存储 / 项目删除时） */
export function clearOwnerMark(projectId: string): void {
  ownerMarks.update((m) => {
    if (!(projectId in m)) return m;
    const next = { ...m };
    delete next[projectId];
    return next;
  });
}

/** 全部清除 */
export function clearAllOwnerMarks(): void {
  ownerMarks.set({});
}
