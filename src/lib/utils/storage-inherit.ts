/**
 * 「本机 storage 配置」继承决策（纯函数）
 *
 * 背景：`Project.storage`（本机文件夹路径）**刻意不写入 `.pm`**（见
 * `repositories/pm-file-repo.ts` 的 `projectToPm()`，防止路径随同步污染别的设备）。
 * 因此任何「`.pm` → 重建 Project」的路径都有可能丢 storage。
 *
 * 旧实现只按 **ID** 兜底（`withLocalStorage(project, local)`），一旦重建后的项目 ID
 * 与本地已有项目不同（跨设备恢复、按名合并、ID 丢失的历史数据），storage 就永久丢失。
 *
 * 这里把兜底升级为**三级**（保守优先，不猜）：
 *   1. **同 ID** 的本地项目 → 直接沿用（最常见：同步覆盖）。
 *   2. **同名**（大小写/首尾空白归一化后相同）的本地项目且带 storage → 沿用
 *      （覆盖「同名不同 ID」的历史副本 / 跨设备恢复）。
 *   3. 都没有 → 保留 `incoming.storage`（调用方一般传 `undefined`，即新项目）。
 *
 * 纯函数（只有 type-only 导入）→ 可被 `scripts/test-data-safety.mjs` 直接单测。
 *
 * @module utils/storage-inherit
 */

import type { Project, ProjectStorage } from '$lib/types';

/** 项目名归一化：去首尾空白 + 转小写（同名判定用） */
export function normalizeProjectName(name: string): string {
  return (name ?? '').trim().toLowerCase();
}

/**
 * 三级兜底：解析重建后的项目应采用的 storage
 *
 * @param incoming - 从 `.pm` 重建出的项目（`storage` 通常为 undefined）
 * @param local - 本机当前项目列表
 * @param byId - 可选：已按 ID 找到的本地项目（避免调用方重复查找）
 * @returns 应采用的 storage（找不到则为 `incoming.storage`）
 */
export function resolveInheritedStorage(
  incoming: Project,
  local: Project[],
  byId?: Project
): ProjectStorage | undefined {
  // 1) 同 ID
  const sameId = byId ?? local.find((p) => p.id === incoming.id);
  if (sameId?.storage !== undefined) return sameId.storage;

  // 2) 同名（不同 ID）
  const key = normalizeProjectName(incoming.name);
  if (key) {
    const sameName = local.find(
      (p) =>
        p.id !== incoming.id &&
        p.storage !== undefined &&
        normalizeProjectName(p.name) === key
    );
    if (sameName) return sameName.storage;
  }

  // 3) 保留现有值
  return incoming.storage;
}

/**
 * 在项目上应用三级兜底
 *
 * 仅在 storage 真的变化时返回新对象，否则原样返回（减少无谓的对象分配与写盘）。
 */
export function withInheritedStorage(
  incoming: Project,
  local: Project[],
  byId?: Project
): Project {
  const storage = resolveInheritedStorage(incoming, local, byId);
  if (storage === incoming.storage) return incoming;
  return { ...incoming, storage };
}
