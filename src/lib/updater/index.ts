/**
 * 统一自动更新入口
 *
 * - 桌面端（Windows / macOS / Linux）：通过 Rust 命令走 `tauri-plugin-updater`，
 *   在**运行时**指定 endpoint（自建服务器 / GitHub Release 皆可），带签名校验，
 *   下载后自动安装并重启。
 * - Android：`tauri-plugin-updater` 不支持移动端，改用 Rust 自定义命令
 *   （拉取清单 JSON，下载 APK 后交由用户安装）。
 *
 * 更新清单地址由 `$lib/updater/config` 根据「更新源」配置计算，本模块不写死地址。
 *
 * @module updater
 */

import { updateManifestUrl } from './config';

export {
  getUpdateSource,
  getUpdateServerHost,
  getUpdateServerPort,
  getUpdateGithubRepo,
  getUpdateConfigValues,
  saveUpdateConfig,
  buildManifestUrl,
  updateManifestUrl,
  describeUpdateSource,
  DEFAULT_UPDATE_HOST,
  DEFAULT_UPDATE_PORT,
  DEFAULT_GITHUB_REPO,
} from './config';
export type { UpdateSource, UpdateConfigValues } from './config';

/** 构建目标平台（windows / darwin / linux / android / ios），非 Tauri 环境为空 */
const PLATFORM =
  ((import.meta.env as unknown as Record<string, string | undefined>).TAURI_ENV_PLATFORM ?? '').toLowerCase();

/** 是否为桌面端（插件可用） */
const IS_DESKTOP = PLATFORM === 'windows' || PLATFORM === 'darwin' || PLATFORM === 'linux';

/** 可用的更新信息（前端统一结构） */
export interface AvailableUpdate {
  /** 新版本号，如 "0.8.0" */
  version: string;
  /** 更新说明（Markdown/纯文本） */
  notes: string;
  /** true = 桌面端（可下载并自动安装）；false = Android（仅下载 APK） */
  native: boolean;
}

/** Rust 命令返回的更新信息 */
interface UpdateInfo {
  latest_version: string;
  download_url: string;
  release_notes: string;
  required: boolean;
}

/** Android 当前待下载的 APK 地址 */
let androidDownloadUrl = '';

/** 当前平台是否为桌面端 */
export function isDesktopPlatform(): boolean {
  return IS_DESKTOP;
}

/**
 * 检查是否有可用更新
 *
 * @returns 有更新时返回 {@link AvailableUpdate}，否则返回 null
 */
export async function checkForUpdate(): Promise<AvailableUpdate | null> {
  const { invoke } = await import('@tauri-apps/api/core');

  if (IS_DESKTOP) {
    const endpoint = updateManifestUrl('desktop');
    if (!endpoint) throw new Error('请先在「设置 → 关于」填写更新服务器地址');
    const info = await invoke<UpdateInfo | null>('desktop_check_update', {
      endpoint,
    });
    if (!info) return null;
    return { version: info.latest_version, notes: info.release_notes, native: true };
  }

  // Android：清单式（服务器 android.json / GitHub Release 资源）
  const manifestUrl = updateManifestUrl('android');
  if (!manifestUrl) throw new Error('请先在「设置 → 关于」填写更新服务器地址');
  const info = await invoke<UpdateInfo | null>('check_for_updates', {
    manifestUrl,
  });
  if (!info) return null;
  androidDownloadUrl = info.download_url;
  return { version: info.latest_version, notes: info.release_notes, native: false };
}

/** 安装结果 */
export interface InstallResult {
  /**
   * - `installed`：桌面端已安装（Windows 由安装器接管；macOS/Linux 已触发重启）
   * - `downloaded`：Android 已下载到本地（未能拉起安装器，需手动安装）
   * - `installer-launched`：Android 已下载并拉起系统安装界面，等待用户确认
   */
  kind: 'installed' | 'downloaded' | 'installer-launched';
  /** Android 下载完成后的文件路径 */
  path?: string;
}

/**
 * 下载（桌面端并安装）更新
 *
 * @param onProgress 进度回调，参数为 0-100 的百分比
 * @returns 安装/下载结果
 */
export async function installUpdate(
  onProgress: (percent: number) => void
): Promise<InstallResult> {
  const { invoke } = await import('@tauri-apps/api/core');

  if (IS_DESKTOP) {
    onProgress(0);
    // 进度由 Rust 侧的 `update:download-progress` 事件上报
    const { listen } = await import('@tauri-apps/api/event');
    const unlisten = await listen<{ percent: number }>('update:download-progress', (e) => {
      onProgress(e.payload.percent);
    });
    try {
      const endpoint = updateManifestUrl('desktop');
      if (!endpoint) throw new Error('请先在「设置 → 关于」填写更新服务器地址');
      await invoke('desktop_install_update', { endpoint });
    } finally {
      unlisten();
    }
    // macOS / Linux 需要重启应用；Windows 安装器拉起后进程已退出，这行通常不会执行到
    try {
      const { relaunch } = await import('@tauri-apps/plugin-process');
      await relaunch();
    } catch { /* ignore */ }
    return { kind: 'installed' };
  }

  // Android：Rust 自定义命令下载 APK，随后拉起系统安装器
  const path = await invoke<string>('download_update', { url: androidDownloadUrl });

  try {
    const installer = await import('tauri-plugin-android-installer-api');
    // Android 8+ 需要用户授权“安装未知应用”
    if (!(await installer.canInstall())) {
      await installer.requestInstallPermission();
      if (!(await installer.canInstall())) {
        return { kind: 'downloaded', path };
      }
    }
    await installer.install(path);
    return { kind: 'installer-launched', path };
  } catch (e) {
    console.warn('[Updater] 拉起安装器失败，回退为手动安装:', e);
    return { kind: 'downloaded', path };
  }
}
