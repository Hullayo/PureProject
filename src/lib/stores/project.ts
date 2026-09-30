/**
 * 项目 CRUD stores
 *
 * 管理项目列表、项目 CRUD 操作和里程碑。
 * 每个项目可按 storage 配置保存到本地文件夹或服务器。
 *
 * @module stores/project
 */

import { writable, get } from 'svelte/store';
import type { Project, TemplateName, PmFile, Milestone, ProjectStorage } from '$lib/types';
import { loadProjects, saveProjects, savePmFile, removePmFile, projectToPm } from '$lib/repositories';
import { buildBackup, mergeBackupInto, parseBackup, type RestoreReport } from '$lib/utils/backup';
import { genId } from '$lib/utils/id';
import { getDeviceId, getDeviceName } from '$lib/utils/device';
import { formatPmErrors, parsePmText, type PmParseOk } from '$lib/utils/pm-schema';
import { toast } from './toast';
import {
  ORDER_GAP,
  hasFullOrder,
  nextOrder,
  orderOf,
  renumberProjects,
  sortProjects,
} from '$lib/utils/project-order';
import { now } from '$lib/utils/date';
import { pushHistory, registerStores } from './history';
import { withInheritedStorage } from '$lib/utils/storage-inherit';
import { rememberStoragePref } from '$lib/utils/storage-prefs';
import { runHook } from '$lib/plugins';
import { t } from '$lib/i18n';
import { createLegacyTaskGroup } from '$lib/utils/task-status';
import { projectFromPm } from '$lib/utils/pm-convert';

export { projectFromPm } from '$lib/utils/pm-convert';

/** 项目列表 */
export const projects = writable<Project[]>(loadProjects());

/** 当前选中的项目 ID */
export const activeProjectId = writable<string | null>(null);

registerStores(projects, activeProjectId);

/**
 * 项目被删除时的回调（由 `sync/auto.ts` 注册）
 *
 * 用于把「用户在本地删了项目」这件事**推给服务器**（写墓碑），
 * 否则同步对账会把它当成「服务器有、本地没有」而重新下载回来。
 * 用回调而不是直接 import，避免 `project` ↔ `sync/auto` 循环依赖。
 */
type ProjectDeletedHandler = (id: string) => void;
let _onProjectDeleted: ProjectDeletedHandler | null = null;

/** 注册 / 注销项目删除回调（传 null 取消） */
export function registerProjectDeletedHandler(fn: ProjectDeletedHandler | null): void {
  _onProjectDeleted = fn;
}

// 数据变更时自动保存到 localStorage（同步后备）
projects.subscribe(list => {
  saveProjects(list);
});

/**
 * 按 storage 配置持久化（store 层统一入口）
 *
 * 除写盘外还负责一件事：**本机成功写入过外部副本后，把本机标记为该项目的「归属设备」**。
 * 这样别的设备（哪怕本机存在同名路径）就不会再替它写 `.pm`。
 *
 * @param proj - 要持久化的项目
 */
export function persistProject(proj: Project): void {
  // 记录「本机用过的本地文件夹配置」：万一 storage 丢失，设置页可据此提示 + 一键恢复
  if (proj.storage?.type === 'local') rememberStoragePref(proj.name, proj.storage);
  // 插件钩子：任何插件抛错都被隔离（见插件注册表），不影响保存
  runHook('onProjectSave', { project: proj });
  void savePmFile(proj)
    .then((res) => {
      if (res.wroteExternal && !proj.storage?.ownerDeviceId) markProjectStorageOwner(proj.id);
    })
    .catch(() => { /* 主存储失败已由 storage-health 上报 */ });
}

/**
 * 把当前设备标记为该项目本地文件夹的「归属设备」
 *
 * 触发时机：**仅在本机真实写入过 `.pm` 之后**（由 `persistProject` 调用）。
 * 不在创建项目时无条件写：用户可能在 A 机创建、在 B 机才第一次写成功，
 * 「谁的写入路径真的可用，谁才是归属」。
 *
 * 只改 `storage.ownerDeviceId / ownerDeviceName`，**不动 `updated_at`**
 * （归属是本机配置，不该触发同步）。
 */
export function markProjectStorageOwner(projectId: string): void {
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    if (!p.storage || p.storage.type !== 'local') return p;
    const deviceId = getDeviceId();
    if (p.storage.ownerDeviceId === deviceId) return p;
    return { ...p, storage: { ...p.storage, ownerDeviceId: deviceId, ownerDeviceName: getDeviceName() } };
  }));
}

/** 按 storage 配置删除 */
function unpersistProject(proj: Project): void {
  removePmFile(proj).catch(() => {});
}

// ─── CRUD 操作 ─────────────────────────────────────────────────────────────

export function createProject(
  name: string,
  description: string,
  color: string,
  template: TemplateName = 'default',
  storage?: ProjectStorage,
  start_date?: string,
  end_date?: string
): void {
  pushHistory();
  const _t = t.get();
  const id = genId();
  const defaultGroup = createLegacyTaskGroup(id);
  defaultGroup.name = _t('taskGroup.default');
  defaultGroup.statuses = defaultGroup.statuses.map(status => ({
    ...status,
    name: status.category === 'active'
      ? _t('status.in_progress')
      : _t(`status.${status.category}`),
  }));
  const p: Project = {
    id, name, description, color, template,
    created_at: now(), updated_at: now(), archived: false,
    start_date, end_date,
    sync_enabled: true,
    sort_order: nextOrder(get(projects)),
    tasks: [], task_groups: [defaultGroup], default_task_group_id: defaultGroup.id, tags: [],
    changelog: [],
    milestones: [],
    readme: '',
    storage
  };

  projects.update(list => [...list, p]);
  activeProjectId.set(p.id);
  persistProject(p);
}

export function deleteProject(id: string): void {
  pushHistory();
  let deletedProj: Project | null = null;
  projects.update(list => {
    deletedProj = list.find(p => p.id === id) || null;
    return list.filter(p => p.id !== id);
  });
  activeProjectId.update(curr => (curr === id ? null : curr));
  if (deletedProj) unpersistProject(deletedProj);
  if (deletedProj) _onProjectDeleted?.(id);
}

export function updateProject(id: string, data: Partial<Project>): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== id) return p;
    const updated = { ...p, ...data, updated_at: now() };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 拖动排序：把项目从 `fromIndex` 移到 `toIndex`（两者都是当前显示顺序的下标）
 *
 * 顺序值写入 `sort_order` 并随 .pm 一起上传，其它设备拉取后按它重排 → 跨设备顺序同步。
 *
 * - 常规情况只改被拖动的项目（取前后邻居的中点）→ 只推一份 .pm；
 * - 间隙耗尽（相邻差值 ≤ 2）或缺历史排序值时全量重排（`i * ORDER_GAP`）→ 推送所有变化项。
 *
 * 只改顺序、不改项目内容，所以不需要写 changelog。
 */
export function reorderProject(fromIndex: number, toIndex: number): void {
  const list = get(projects);
  if (list.length < 2 || fromIndex < 0 || fromIndex >= list.length) return;
  const target = Math.max(0, Math.min(toIndex, list.length - 1));
  if (target === fromIndex) return;

  pushHistory();

  const ordered = [...list];
  const [moved] = ordered.splice(fromIndex, 1);
  ordered.splice(target, 0, moved);

  const prev = ordered[target - 1];
  const next = ordered[target + 1];
  const prevOrder = prev ? orderOf(prev, target - 1) : null;
  const nextOrder = next ? orderOf(next, target + 1) : null;
  const canSplit =
    hasFullOrder(ordered) &&
    (prevOrder === null || nextOrder === null || nextOrder - prevOrder >= 2);

  let result: Project[];
  let changed: Project[];

  if (canSplit) {
    const order =
      prevOrder === null && nextOrder === null
        ? ORDER_GAP
        : prevOrder === null
          ? nextOrder! - ORDER_GAP
          : nextOrder === null
            ? prevOrder + ORDER_GAP
            : Math.floor((prevOrder + nextOrder) / 2);
    const updated: Project = { ...moved, sort_order: order, updated_at: now() };
    changed = [updated];
    result = ordered.map(p => (p.id === moved.id ? updated : p));
  } else {
    const renumbered = renumberProjects(ordered);
    const byId = new Map<string, Project>();
    for (const p of renumbered.changed) {
      byId.set(p.id, { ...p, updated_at: now() });
    }
    result = renumbered.list.map(p => byId.get(p.id) ?? p);
    changed = [...byId.values()];
  }

  projects.set(sortProjects(result));
  for (const p of changed) persistProject(p);
}

export function createMilestone(projectId: string, title: string, date: string, color: string): void {
  pushHistory();
  const m: Milestone = {
    id: genId(), title, date, color, description: ''
  };
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = { ...p, milestones: [...p.milestones, m], updated_at: now() };
    persistProject(updated);
    return updated;
  }));
}

export function updateMilestone(projectId: string, milestoneId: string, data: Partial<Milestone>): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = { ...p, milestones: p.milestones.map(m => (m.id === milestoneId ? { ...m, ...data } : m)), updated_at: now() };
    persistProject(updated);
    return updated;
  }));
}

export function deleteMilestone(projectId: string, milestoneId: string): void {
  pushHistory();
  projects.update(list => list.map(p => {
    if (p.id !== projectId) return p;
    const updated = { ...p, milestones: p.milestones.filter(m => m.id !== milestoneId), updated_at: now() };
    persistProject(updated);
    return updated;
  }));
}

/**
 * 统一解析 `.pm` 文本（体积检查 → JSON.parse → 迁移 → 校验）
 *
 * 所有导入入口都必须走这里：非法文件给出**带字段路径**的错误并弹 Toast，
 * 绝不落地半截数据。
 *
 * @param jsonStr - 文件内容
 * @param source - 来源描述（文件名 / 「服务器 xxx」），用于错误信息
 */
function parseIncomingPm(jsonStr: string, source: string): PmParseOk | null {
  const result = parsePmText(jsonStr, source);
  if (!result.ok) {
    const msg = formatPmErrors(result.errors);
    console.error('[Import] 拒绝非法 .pm：', result.errors);
    toast(`导入失败：${msg}`, 'err', 12000);
    return null;
  }
  if (result.warnings?.length) {
    console.warn('[Import] 迁移/校验警告：', result.warnings);
  }
  if (result.migratedFrom != null) {
    console.info(`[Import] ${source} 由 v${result.migratedFrom} 迁移到当前格式`);
  }
  return { ok: true, pm: result.pm, migratedFrom: result.migratedFrom ?? null, warnings: result.warnings ?? [] };
}

/**
 * 把「本机配置」从本地已有项目里带回来（三级兜底，见 utils/storage-inherit.ts）
 *
 * `storage`（这台机器上的文件夹路径）不参与同步：同步/备份导入时沿用本地既有值：
 *   1. 同 ID → 直接沿用；
 *   2. 同名（不同 ID，历史副本/跨设备恢复）→ 沿用；
 *   3. 都没有 → 保留传入值（新项目为 undefined）。
 *
 * @param project - 从 `.pm` 重建出的项目
 * @param local - 本机当前项目列表
 * @param byId - 可选：已按 ID 找到的本地项目（避免重复查找）
 */
function withLocalStorage(project: Project, local: Project[], byId?: Project): Project {
  return withInheritedStorage(project, local, byId);
}

export function importProject(jsonStr: string, source = '.pm 文件'): boolean {
  const parsedOk = parseIncomingPm(jsonStr, source);
  if (!parsedOk) return false;
  pushHistory();
  try {
    const pm: PmFile = parsedOk.pm;
    const p = projectFromPm(pm, genId());
    if (!p) {
      toast('导入失败：文件结构不完整（缺少项目信息）', 'err', 10000);
      return false;
    }
    if (p.sort_order === undefined) p.sort_order = nextOrder(get(projects));
    projects.update(list => sortProjects([...list, p]));
    activeProjectId.set(p.id);
    persistProject(p);
    return true;
  } catch (e) {
    console.error('[Import] 写入失败', e);
    toast(`导入失败：${e instanceof Error ? e.message : String(e)}`, 'err', 10000);
    return false;
  }
}

/**
 * 导出全量备份（含所有项目 + 非敏感设置）
 *
 * 返回 JSON 文本，由调用方决定存到哪里（桌面另存为 / Android「保存到」/ 浏览器下载）。
 * **不包含任何凭据**（token、密码），见 utils/backup.ts。
 */
export function exportBackupJson(): string {
  return buildBackup(get(projects), projectToPm);
}

/**
 * 从备份包恢复（**合并，不删除本地任何项目**）
 *
 * 策略：备份有本地没有 → 新增；同 id 比较 `updated_at`，新的赢。
 *
 * @returns 成功时给出 新增/覆盖/跳过 统计；失败时给出可读原因
 */
export function restoreBackupJson(text: string): { ok: true; report: RestoreReport } | { ok: false; error: string } {
  const parsed = parseBackup(text);
  if (!parsed.ok) return { ok: false, error: parsed.error };

  const current = get(projects);
  const { projects: merged, report } = mergeBackupInto(parsed.projects, current, projectFromPm);
  if (report.added === 0 && report.replaced === 0) {
    return {
      ok: true,
      report: { ...report, settings: parsed.settings, sync: parsed.sync, skippedProjects: parsed.skipped },
    };
  }

  pushHistory();
  const before = new Map(current.map((p) => [p.id, p]));
  // storage 是本机配置：恢复/覆盖时沿用本地值（同 ID → 同名 → 保留），别被备份包里的路径带跑
  for (const p of merged) {
    const prev = before.get(p.id);
    if (prev === p) continue; // 本地原样保留、未参与恢复的项目，无需处理
    p.storage = withLocalStorage(p, current, prev).storage;
  }
  projects.set(sortProjects(merged));

  // 只持久化新增/被覆盖的项目，避免无谓写盘（每个项目一份 .pm）
  for (const p of merged) {
    const prev = before.get(p.id);
    if (!prev || prev.updated_at !== p.updated_at) {
      persistProject(p);
    }
  }

  return {
    ok: true,
    report: { ...report, settings: parsed.settings, sync: parsed.sync, skippedProjects: parsed.skipped },
  };
}

/**
 * 按指定 ID 导入/覆盖项目（**同步专用**）
 *
 * 关键：沿用给定 ID（即服务器上的项目 ID）。
 * 旧实现用 `genId()` 生成新 ID，导致本地与服务器 ID 不一致，
 * 下次同步时又把该项目当成“新项目”推上去 → 产生副本。
 *
 * @param jsonStr - 服务器返回的 .pm JSON
 * @param id - 服务器上的项目 ID
 */
export function importProjectWithId(jsonStr: string, id: string, source = '服务器'): boolean {
  const parsedOk = parseIncomingPm(jsonStr, source);
  if (!parsedOk) return false;
  pushHistory();
  try {
    const pm: PmFile = parsedOk.pm;
    const parsed = projectFromPm(pm, id);
    if (!parsed) return false;

    // 顺序：远端带了 sort_order 就用它（跨设备同步顺序），否则保留本地现有位置
    const current = get(projects);
    const idx = current.findIndex(x => x.id === id);
    const p: Project = parsed.sort_order === undefined
      ? { ...parsed, sort_order: idx === -1 ? nextOrder(current) : orderOf(current[idx], idx) }
      : parsed;

    const stored = withLocalStorage(p, current, idx === -1 ? undefined : current[idx]);
    projects.update(list => {
      const i = list.findIndex(x => x.id === id);
      const next = i === -1 ? [...list, stored] : list.map((x, k) => (k === i ? stored : x));
      return sortProjects(next);
    });
    persistProject(stored);
    return true;
  } catch (e) {
    console.error('[Import] 同步导入失败', e);
    return false;
  }
}

export function replaceProjectFromPmJson(projectId: string, jsonStr: string, source = '冲突数据'): boolean {
  const parsedOk = parseIncomingPm(jsonStr, source);
  if (!parsedOk) return false;
  pushHistory();
  try {
    const pm: PmFile = parsedOk.pm;
    const parsed = projectFromPm(pm, projectId);
    if (!parsed) return false;

    const current = get(projects);
    const idx = current.findIndex(p => p.id === projectId);
    const replacement: Project = parsed.sort_order === undefined
      ? { ...parsed, sort_order: idx === -1 ? nextOrder(current) : orderOf(current[idx], idx) }
      : parsed;

    const stored = withLocalStorage(replacement, current, idx === -1 ? undefined : current[idx]);
    let replaced = false;
    projects.update(list => sortProjects(list.map(p => {
      if (p.id !== projectId) return p;
      replaced = true;
      return stored;
    })));

    if (!replaced) return false;
    activeProjectId.set(projectId);
    persistProject(stored);
    return true;
  } catch { return false; }
}
