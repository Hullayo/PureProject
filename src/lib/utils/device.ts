/**
 * 本机设备标识
 *
 * 用途：项目选择「本地文件夹」存储时，`.pm` 的本地写入**只由归属设备执行**
 * （见 `utils/local-file-target.ts` 与 docs/DATA-FORMAT.md）。
 * 归属需要跨会话稳定，所以在 localStorage 里持久化一个随机 ID。
 *
 * 注意：设备 ID **仅供本机判定**；由于 `project.storage` 不参与同步（v0.5.5），
 * 它也不会被带到别的设备上。
 *
 * @module utils/device
 */

import { genId } from './id';
import { safeGetRaw, safeSetItem } from '$lib/stores/storage-health';

/** localStorage 键 */
export const DEVICE_ID_KEY = 'pm_device_id';

let cached: string | null = null;

/**
 * 本机设备 ID（首次调用时生成并持久化）
 *
 * 若 localStorage 不可写（隐私模式等），退化为「本会话内稳定的内存 ID」——
 * 宁可本会话判定为「归属」，也不阻塞用户。
 */
export function getDeviceId(): string {
  if (cached) return cached;
  const stored = safeGetRaw(DEVICE_ID_KEY);
  if (stored && stored.trim()) {
    cached = stored.trim();
    return cached;
  }
  const fresh = genId();
  cached = fresh;
  // notify=false：设备 ID 写失败不值得打扰用户（且这里可能在启动早期调用）
  safeSetItem(DEVICE_ID_KEY, fresh, '设备标识', false);
  return fresh;
}

/** 仅供测试/调试：清掉内存缓存，下次重新读取 */
export function resetDeviceIdCache(): void {
  cached = null;
}

/**
 * 设备可读名（展示用，例如「Windows」「Android」）
 *
 * 不要求用户命名，按平台/UA 粗粒度推断即可。
 */
export function getDeviceName(): string {
  try {
    const ua = typeof navigator !== 'undefined' ? navigator.userAgent : '';
    if (/Android/i.test(ua)) return 'Android';
    if (/iPhone|iPad|iPod/i.test(ua)) return 'iOS';
    if (/Windows/i.test(ua)) return 'Windows';
    if (/Macintosh|Mac OS X/i.test(ua)) return 'macOS';
    if (/Linux/i.test(ua)) return 'Linux';
  } catch { /* 非浏览器环境 */ }
  return 'Unknown';
}

/** 设备描述（ID 前 8 位 + 名称），用于日志与提示 */
export function getDeviceLabel(): string {
  return `${getDeviceName()} · ${getDeviceId().slice(0, 8)}`;
}
