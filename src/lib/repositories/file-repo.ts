/**
 * 本地文件操作仓库
 *
 * 封装 Tauri 文件系统命令的调用，提供 .pm 文件的读写能力。
 *
 * @module repositories/file-repo
 */

async function invoke<T>(cmd: string, args?: Record<string, unknown>): Promise<T> {
  const { invoke: tauriInvoke } = await import('@tauri-apps/api/core');
  return tauriInvoke<T>(cmd, args);
}

/**
 * 打开文件夹选择对话框
 * @returns 选中的目录路径，取消时返回 null
 */
export async function pickDirectory(): Promise<string | null> {
  try {
    const { open } = await import('@tauri-apps/plugin-dialog');
    const selected = await open({ directory: true, multiple: false, title: '选择项目保存目录' });
    return selected as string | null;
  } catch {
    return null;
  }
}

/**
 * 读取 .pm 文件内容
 */
export async function readPmFile(path: string): Promise<string> {
  return invoke<string>('read_pm_file', { path });
}

/**
 * 写入 .pm 文件
 */
export async function writePmFile(path: string, content: string): Promise<void> {
  return invoke<void>('write_pm_file', { path, content });
}

/**
 * 扫描目录下所有 .pm 文件
 */
export async function scanPmFiles(dir: string): Promise<string[]> {
  return invoke<string[]>('scan_pm_files', { dir });
}

/**
 * 删除 .pm 文件
 */
export async function deletePmFile(path: string): Promise<void> {
  return invoke<void>('delete_pm_file', { path });
}

export interface DirEntry {
  name: string;
  path: string;
  isDir: boolean;
}

/**
 * 扫描目录结构（仅文件/目录名，不读取内容）
 *
 * @param dir - 要扫描的目录绝对路径
 * @param maxDepth - 最大递归深度，默认 6
 * @returns 扁平化的目录条目列表（目录在前，按名称排序）
 */
export async function scanDirectory(dir: string, maxDepth?: number): Promise<DirEntry[]> {
  try {
    return invoke<DirEntry[]>('scan_directory', { dir, maxDepth });
  } catch {
    return [];
  }
}
