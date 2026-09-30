/**
 * 更新通知状态
 *
 * 管理自动更新的显示状态、进度与结果。
 * 具体的检查/安装逻辑见 `$lib/updater`（桌面插件 / Android 自定义命令）。
 */

import { writable } from 'svelte/store';
import type { AvailableUpdate } from '$lib/updater';

export type { AvailableUpdate };

/** 是否显示更新通知弹窗 */
export const showUpdateDialog = writable(false);

/** 当前可用更新信息 */
export const updateInfo = writable<AvailableUpdate | null>(null);

/** 是否正在下载/安装 */
export const downloading = writable(false);

/** 下载进度（0-100） */
export const downloadProgress = writable(0);

/** Android 下载完成后的文件路径 */
export const downloadPath = writable('');

/** Android 是否已拉起系统安装界面 */
export const installerLaunched = writable(false);

/** 错误信息 */
export const updateError = writable('');
