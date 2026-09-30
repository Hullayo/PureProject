/**
 * 全量备份 / 恢复
 *
 * 「单项目导出 .pm」只能救一个项目；换设备、清数据、灾后恢复都需要**一次性带走全部**。
 *
 * 备份包结构（单文件 JSON，便于放网盘/微信传输）：
 * ```jsonc
 * {
 *   "kind": "projectmanager-backup",
 *   "schema_version": 2,
 *   "app_version": "0.5.4",
 *   "exported_at": "2026-09-15T…",
 *   "projects": [ { ...PmFile }, … ],     // 每个项目的完整 .pm
 *   "settings": { "pm_locale": "zh", … }, // 应用设置（不含同步 token，见下）
 *   "sync":     { "pm_sync_backend": "server", "pm_sync_server_url": "…" } // 只带非敏感项
 * }
 * ```
 *
 * ⚠️ 安全：**不导出任何凭据**（同步 token / WebDAV 密码 / Gist token），
 * 避免备份文件泄露即等于泄露账号。恢复后需要用户重新填一次。
 *
 * @module utils/backup
 */

import type { Project, PmFile } from '$lib/types';
import { CURRENT_PM_SCHEMA, formatPmErrors, parsePmText } from './pm-schema';

/** 备份包标识 */
export const BACKUP_KIND = 'projectmanager-backup';

/** 应用设置键（可安全备份的） */
const SETTINGS_KEYS = [
  'pm_locale',
  'pm_theme',
  'pm_animation',
  'pm_show_sidebar',
  'pm_color_scheme',
] as const;

/** 同步配置里**非敏感**的键（凭据一律不带） */
const SYNC_SAFE_KEYS = [
  'pm_sync_backend',
  'pm_sync_mode',
  'pm_sync_interval',
  'pm_sync_server_url',
  'pm_sync_url',
  'pm_sync_user',
  'pm_sync_gist',
] as const;

export interface BackupFile {
  kind: typeof BACKUP_KIND;
  schema_version: number;
  app_version: string;
  exported_at: string;
  projects: PmFile[];
  settings: Record<string, string>;
  sync: Record<string, string>;
}

/** 版本号（从 package.json 注入失败时回退 unknown） */
function appVersion(): string {
  try {
    return (import.meta.env?.VITE_APP_VERSION as string | undefined) ?? 'unknown';
  } catch {
    return 'unknown';
  }
}

/** 收集可备份的设置 */
function collectKeys(keys: readonly string[]): Record<string, string> {
  const out: Record<string, string> = {};
  for (const k of keys) {
    try {
      const v = localStorage.getItem(k);
      if (v !== null) out[k] = v;
    } catch { /* ignore */ }
  }
  return out;
}

/**
 * 构造备份包内容
 *
 * @param projects - 当前项目列表（含完整数据）
 * @param toPm - 项目 → PmFile 的转换函数（复用 repositories 里那份，避免两套逻辑）
 */
export function buildBackup(projects: Project[], toPm: (p: Project) => PmFile): string {
  const backup: BackupFile = {
    kind: BACKUP_KIND,
    schema_version: CURRENT_PM_SCHEMA,
    app_version: appVersion(),
    exported_at: new Date().toISOString(),
    projects: projects.map((p) => {
      const pm = toPm(p);
      return { ...pm, schema_version: CURRENT_PM_SCHEMA };
    }),
    settings: collectKeys(SETTINGS_KEYS),
    sync: collectKeys(SYNC_SAFE_KEYS),
  };
  return JSON.stringify(backup, null, 2);
}

/** 备份包文件名：ProjectManager-backup-20260915-1304.json */
export function backupFileName(d = new Date()): string {
  const p = (n: number) => String(n).padStart(2, '0');
  const ts = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}`;
  return `ProjectManager-backup-${ts}.json`;
}

export interface BackupParseOk {
  ok: true;
  projects: PmFile[];
  /** 逐个项目校验失败的条目（跳过，不阻断其它项目） */
  skipped: { name: string; reason: string }[];
  settings: Record<string, string>;
  sync: Record<string, string>;
  exportedAt: string;
  appVersion: string;
}

export type BackupParseResult = BackupParseOk | { ok: false; error: string };

/**
 * 解析并校验备份包
 *
 * 逐个项目走 `parsePmText()`（自动迁移旧版本格式），坏掉的项目**跳过并记录**，
 * 避免一个损坏条目导致整个备份不可用。
 */
export function parseBackup(text: string): BackupParseResult {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch (e) {
    return { ok: false, error: `不是合法 JSON：${e instanceof Error ? e.message : String(e)}` };
  }
  if (typeof raw !== 'object' || raw === null) return { ok: false, error: '备份内容不是对象' };

  const obj = raw as Record<string, unknown>;

  // 兼容：也允许直接丢一个 .pm 文件（单项目）进来
  if (obj.kind !== BACKUP_KIND) {
    const single = parsePmText(text, '备份文件');
    if (single.ok) {
      return {
        ok: true,
        projects: [single.pm],
        skipped: [],
        settings: {},
        sync: {},
        exportedAt: '',
        appVersion: '',
      };
    }
    return { ok: false, error: `不是 ProjectManager 备份包（缺少 kind=${BACKUP_KIND}），且作为单个 .pm 也解析失败：${formatPmErrors(single.errors)}` };
  }

  if (!Array.isArray(obj.projects)) return { ok: false, error: '备份包缺少 projects 数组' };

  const projects: PmFile[] = [];
  const skipped: { name: string; reason: string }[] = [];
  obj.projects.forEach((p, i) => {
    const label = (typeof p === 'object' && p !== null && typeof (p as Record<string, unknown>).project === 'object')
      ? String(((p as Record<string, unknown>).project as Record<string, unknown>).name ?? `#${i + 1}`)
      : `#${i + 1}`;
    const res = parsePmText(JSON.stringify(p), `备份中的「${label}」`);
    if (res.ok) projects.push(res.pm);
    else skipped.push({ name: label, reason: formatPmErrors(res.errors, 2) });
  });

  if (projects.length === 0 && skipped.length > 0) {
    return { ok: false, error: `备份包里 ${skipped.length} 个项目全部校验失败：${skipped[0].reason}` };
  }

  const strMap = (v: unknown): Record<string, string> => {
    if (typeof v !== 'object' || v === null) return {};
    const out: Record<string, string> = {};
    for (const [k, val] of Object.entries(v as Record<string, unknown>)) {
      if (typeof val === 'string') out[k] = val;
    }
    return out;
  };

  return {
    ok: true,
    projects,
    skipped,
    settings: strMap(obj.settings),
    sync: strMap(obj.sync),
    exportedAt: typeof obj.exported_at === 'string' ? obj.exported_at : '',
    appVersion: typeof obj.app_version === 'string' ? obj.app_version : '',
  };
}

/** 恢复结果统计 */
export interface RestoreReport {
  added: number;
  replaced: number;
  skipped: number;
  /** 恢复后建议的设置项（由调用方决定是否写回，默认不覆盖用户当前设置） */
  settings: Record<string, string>;
  sync: Record<string, string>;
  skippedProjects: { name: string; reason: string }[];
}

/**
 * 把备份包里的项目合并进当前项目列表
 *
 * 合并策略（保守优先，绝不静默丢数据）：
 * - 备份里有、本地没有 → **新增**
 * - 两边都有（同 id）→ 比较 `updated_at`，**新的赢**；本地较新则跳过（计 skipped）
 *
 * 不会删除本地任何项目。
 */
export function mergeBackupInto(
  backupProjects: PmFile[],
  current: Project[],
  fromPm: (pm: PmFile, id: string) => Project | null
): { projects: Project[]; report: Omit<RestoreReport, 'settings' | 'sync' | 'skippedProjects'> } {
  const next = [...current];
  let added = 0;
  let replaced = 0;
  let skipped = 0;

  for (const pm of backupProjects) {
    const incoming = fromPm(pm, pm.project.id ?? '');
    if (!incoming) { skipped++; continue; }
    const idx = next.findIndex((p) => p.id === incoming.id);
    if (idx === -1) {
      next.push(incoming);
      added++;
      continue;
    }
    const localTime = Date.parse(next[idx].updated_at || '') || 0;
    const incomingTime = Date.parse(incoming.updated_at || '') || 0;
    if (incomingTime > localTime) {
      next[idx] = incoming;
      replaced++;
    } else {
      skipped++;
    }
  }
  return { projects: next, report: { added, replaced, skipped } };
}
