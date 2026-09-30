/**
 * .pm 文件格式持久化仓库
 *
 * 提供项目数据与 .pm 文件格式之间的转换和持久化功能。
 * 按项目的 storage 配置决定保存位置。
 *
 * @module repositories/pm-file-repo
 */

import type { Project } from '$lib/types';
import { writePmFile, deletePmFile } from './file-repo';
import { safeSetItem, clearExternalSyncIssue, reportExternalSyncIssue, setOwnerMark, clearOwnerMark } from '$lib/stores/storage-health';
import { planLocalFileWriteWithOwner, pmFilePath } from '$lib/utils/local-file-target';
import { isMobilePlatform, currentPlatform } from '$lib/utils/platform';
import { getDeviceId } from '$lib/utils/device';
import { projectToPm } from '$lib/utils/pm-convert';

/** 项目名 → `.pm` 文件名 / 完整路径见 `utils/local-file-target`（`pmFileName` / `pmFilePath`） */

/**
 * 保存项目
 *
 * ─── 两条通道，失败语义完全不同 ─────────────────────────────────────────────
 *
 * 1. **主存储**：`localStorage` 的 `pm_file_<id>`。同步引擎直接读它上传
 *    （`sync/auto.ts` → `getProjectPmJson`），所以失败**必须**报红——
 *    否则会出现「界面是新的，推给服务器的是旧快照」。
 * 2. **外部副本**：项目自选的本地文件夹里的 `.pm`。它只是额外一份拷贝：
 *    - 移动端根本写不了（路径可能来自桌面端，如 `D:\Senior\x.pm`）
 *    - 失败**不该**弹红条，否则每次改任务都打扰一次（真机踩坑）
 *    → 走 `externalSyncIssues` 清单，同一项目一个会话只提示一次。
 *
 * @returns `ok` = 主存储（localStorage）是否写入成功；
 *          `wroteExternal` = 本次是否真的写了外部副本（用于给项目补写「归属设备」）
 */
export interface SavePmFileResult {
  ok: boolean;
  wroteExternal: boolean;
}

export async function savePmFile(proj: Project): Promise<SavePmFileResult> {
  const pm = projectToPm(proj);
  // 不带缩进：快照体积直接省 10~20%（同步上传的也是这份）
  const json = JSON.stringify(pm);

  // ── 主存储 ───────────────────────────────────────────────────────────────
  const ok = safeSetItem(`pm_file_${proj.id}`, json, `项目「${proj.name}」快照`);

  // ── 外部副本 ─────────────────────────────────────────────────────────────
  // 只由「归属设备」写：别的设备即便本机存在同名路径也不碰（避免污染对端目录）
  const decision = planLocalFileWriteWithOwner(proj.storage, isMobilePlatform, getDeviceId(), currentPlatform);
  // ⚠️ 写的是**文件**路径，不是 storage 里的目录（`decision.path` 是目录，仅用于提示展示）
  const filePath = pmFilePath(proj.storage, proj.name, proj.id);

  if (decision.kind === 'none') {
    clearExternalSyncIssue(proj.id);
    clearOwnerMark(proj.id);
    return { ok, wroteExternal: false };
  }

  if (decision.kind === 'notOwner') {
    // 预期行为：不写、不报错、不进清单（否则又会变成「每次改任务弹提示」）。
    // 仅在 UI 上打一个**只读角标**（由别的设备管理），不打扰用户。
    clearExternalSyncIssue(proj.id);
    setOwnerMark(proj.id, decision.ownerDeviceName || decision.ownerDeviceId.slice(0, 8));
    return { ok, wroteExternal: false };
  }

  if (decision.kind === 'unsupported' || decision.kind === 'foreignDesktopPath') {
    // 本机不可写（移动端 / 别的平台的绝对路径）→ 记录但不报错、不重试
    clearOwnerMark(proj.id);
    reportExternalSyncIssue({
      projectId: proj.id,
      name: proj.name,
      path: decision.path,
      unsupported: true,
      message: decision.reason === 'mobile' ? '当前平台不支持写入外部文件夹' : '该路径属于其它操作系统',
    });
    return { ok, wroteExternal: false };
  }

  try {
    if (!filePath) throw new Error('缺少本地文件夹路径');
    await writePmFile(filePath, json);
    clearExternalSyncIssue(proj.id);
    clearOwnerMark(proj.id);
    return { ok, wroteExternal: true };
  } catch (e) {
    clearOwnerMark(proj.id);
    reportExternalSyncIssue({
      projectId: proj.id,
      name: proj.name,
      path: decision.path,
      unsupported: false,
      message: e instanceof Error ? e.message : String(e),
    });
    return { ok, wroteExternal: false };
  }
}

/**
 * 删除项目的 .pm 文件
 */
export async function removePmFile(proj: Project): Promise<void> {
  try { localStorage.removeItem(`pm_file_${proj.id}`); } catch {}
  const filePath = pmFilePath(proj.storage, proj.name, proj.id);
  if (filePath) {
    try { await deletePmFile(filePath); } catch {}
  }
}

/**
 * 获取项目的 .pm 文件 JSON 字符串
 */
export function getProjectPmJson(projectId: string): string | null {
  const key = `pm_file_${projectId}`;
  return localStorage.getItem(key);
}

export { projectToPm };
