/**
 * 「项目外部副本」（.pm 本地文件）写入决策
 *
 * 背景（真机血泪）：项目创建时可以选「本地文件夹」存储，路径会被写进 `.pm` 并**随同步带到别的设备**。
 * 例如在 Windows 上建的 `D:\Senior\大四计划书.pm`，同步到 Android 后每次保存都会尝试写这个路径，
 * 必然失败 → 旧实现把它当成「本地数据保存失败」弹**红色告警条**，于是用户每改一次任务就被打扰一次，
 * 而真正的主存储（localStorage）其实一直是好的。
 *
 * 因此把决策抽成纯函数：
 * - `write`：桌面端，正常写外部副本
 * - `unsupported`：移动端 —— Rust 侧 `std::fs` 写不了 Android 的 content:// 或别的平台盘符，
 *   直接跳过并**按项目记录一次**「本机不支持外部文件夹」，不再当错误上报
 * - `none`：项目本来就没配置外部文件夹
 *
 * 纯函数（只有 type-only 导入）→ 可被 `scripts/test-data-safety.mjs` 直接单测。
 *
 * ─── v0.6.0：再加一层「归属设备」判定 ────────────────────────────────────────
 *
 * 即使某台设备上**存在**同名可写路径（比如两台 PC 都有 `D:\`），它也不该替别人写：
 * `.pm` 的本地写入只由「归属设备」（首次成功写入时的设备）执行，
 * 其它设备只参与服务器上传/下载。判定见 {@link planLocalFileWriteWithOwner}。
 *
 * @module utils/local-file-target
 */

import type { ProjectStorage } from '$lib/types';

export type LocalFilePlan =
  | { kind: 'none' }
  | { kind: 'write'; path: string }
  | { kind: 'unsupported'; path: string; reason: 'mobile' }
  | { kind: 'foreignDesktopPath'; path: string; reason: 'windows-drive' | 'unc' };

/** Windows 盘符路径（`D:\x` / `D:/x`） */
const WIN_DRIVE = /^[a-zA-Z]:[\\/]/;
/** UNC 路径（`\\server\share`） */
const UNC = /^\\\\/;

/**
 * 项目名 → `.pm` 文件名
 *
 * Windows 非法字符（`<>:"/\|?*`）替换为 `_`。
 */
export function pmFileName(projectName: string, projectId?: string): string {
  const safeName = (projectName ?? '').replace(/[<>:"/\\|?*]/g, '_');
  const suffix = (projectId ?? '').replace(/[^a-zA-Z0-9]/g, '').slice(0, 8);
  return `${safeName}${suffix ? `-${suffix}` : ''}.pm`;
}

/**
 * storage 目录 + 项目名 → 完整的 `.pm` 文件路径
 *
 * ⚠️ `storage.path` 是**目录**；写外部副本时**必须**用本函数拼出文件路径，
 * 绝不能把 `storage.path` 直接当目标路径（否则 Rust 侧会把目录当文件写：
 * `rename(dir.tmp, dir)` → Windows `拒绝访问 (os error 5)`）。
 *
 * @returns 完整文件路径；非 local / 无路径时返回 `null`
 */
export function pmFilePath(storage: ProjectStorage | undefined | null, projectName: string, projectId?: string): string | null {
  if (!storage || storage.type !== 'local') return null;
  const dir = (storage.path ?? '').trim().replace(/[\\/]+$/, '');
  if (!dir) return null;
  return `${dir}/${pmFileName(projectName, projectId)}`;
}

/**
 * 判断一个路径是否明显属于「另一个平台」（桌面端也可能命中，比如 Linux 上同步到了 Windows 路径）
 *
 * 只做高置信度判断，不用 heuristics 猜 Unix 路径（`/home/x` 在 Linux 上是合法的）。
 *
 * ⚠️ 必须传入**当前主机 OS**：Windows 盘符 / UNC 路径在 Windows 本机上是合法的，不能当成
 * 「异平台路径」拒写（否则 Windows 用户选了 `E:\...` 也会被跳过外部拷贝）。
 * 不传 / 传空时按「非 Windows」处理，保持历史上「Windows 路径在非 Windows 上不可写」的语义。
 *
 * @param p - 目标路径
 * @param hostOs - 当前主机平台（`'windows'` / `'darwin'` / `'linux'` / …），缺省不区分
 */
export function isForeignDesktopPath(p: string, hostOs: string = ''): 'windows-drive' | 'unc' | null {
  const path = (p ?? '').trim();
  const onWindows = String(hostOs ?? '').trim().toLowerCase() === 'windows';
  if (WIN_DRIVE.test(path)) return onWindows ? null : 'windows-drive';
  if (UNC.test(path)) return onWindows ? null : 'unc';
  return null;
}

/**
 * 决定「外部副本」这次该怎么处理
 *
 * @param filePath - 已经拼好的目标路径（`getPmFilePath()` 的结果；null 表示项目没配外部文件夹）
 * @param isMobile - 是否移动端（Android/iOS）
 */
export function planLocalFileWrite(filePath: string | null, isMobile: boolean, hostOs: string = ''): LocalFilePlan {
  const path = (filePath ?? '').trim();
  if (!path) return { kind: 'none' };
  // 委托给「带归属判定」的新函数：不传 owner / 设备 ID 为空 → 归属检查必然通过，
  // 行为与 v0.5.5 完全一致（保留此函数是为了兼容既有调用与单测）。
  const decision = planLocalFileWriteWithOwner({ type: 'local', path }, isMobile, '', hostOs);
  switch (decision.kind) {
    case 'unsupported': return { kind: 'unsupported', path, reason: 'mobile' };
    case 'foreignDesktopPath': return { kind: 'foreignDesktopPath', path, reason: decision.reason };
    case 'write': return { kind: 'write', path };
    default: return { kind: 'none' };
  }
}

/** 判断这次决策是否需要向用户提示（`none` 与正常写入不需要） */
export function planNeedsNotice(plan: LocalFilePlan): boolean {
  return plan.kind === 'unsupported' || plan.kind === 'foreignDesktopPath';
}


// ─── 归属设备判定（v0.6.0）────────────────────────────────────────────────────

export type LocalWriteDecision =
  | { kind: 'none' }
  | { kind: 'write'; path: string }
  | { kind: 'unsupported'; path: string; reason: 'mobile' }
  | { kind: 'foreignDesktopPath'; path: string; reason: 'windows-drive' | 'unc' }
  | { kind: 'notOwner'; path: string; ownerDeviceId: string; ownerDeviceName?: string };

/**
 * 判定「是否应该由本机执行 `.pm` 本地写入」
 *
 * 判定顺序（每一步都有明确理由）：
 *  1. 没配本地文件夹 → `none`（顺手清掉历史问题记录）
 *  2. **移动端** → `unsupported`：Rust `std::fs` 写不了 `content://`，
 *     路径也常来自桌面端；放在归属判定之前，因为「平台写不了」比「不是归属」更根本
 *  3. **非归属设备** → `notOwner`：不写、不报错、不进清单（这是预期行为，不是故障）
 *  4. 桌面端遇到异平台盘符/UNC → `foreignDesktopPath`：不盲目尝试
 *  5. 其余 → `write`
 *
 * @param storage - 项目的 storage 配置
 * @param isMobile - 是否移动端
 * @param currentDeviceId - 本机设备 ID（`getDeviceId()`）
 * @param hostOs - 当前主机平台（`'windows'` / `'darwin'` / `'linux'` / …）；Windows 本机不再把盘符/UNC 当成异平台路径
 */
export function planLocalFileWriteWithOwner(
  storage: ProjectStorage | undefined,
  isMobile: boolean,
  currentDeviceId: string,
  hostOs: string = ''
): LocalWriteDecision {
  const path = (storage?.path ?? '').trim();
  if (!storage || storage.type !== 'local' || !path) return { kind: 'none' };

  if (isMobile) return { kind: 'unsupported', path, reason: 'mobile' };

  const owner = (storage.ownerDeviceId ?? '').trim();
  if (owner && owner !== currentDeviceId) {
    return { kind: 'notOwner', path, ownerDeviceId: owner, ownerDeviceName: storage.ownerDeviceName };
  }

  const foreign = isForeignDesktopPath(path, hostOs);
  if (foreign) return { kind: 'foreignDesktopPath', path, reason: foreign };

  return { kind: 'write', path };
}

/** 是否需要向用户提示（`none` / `write` / `notOwner` 都不需要） */
export function decisionNeedsNotice(d: LocalWriteDecision): boolean {
  return d.kind === 'unsupported' || d.kind === 'foreignDesktopPath';
}
