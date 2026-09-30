/**
 * 文件下载工具
 *
 * @module utils/download
 */

/**
 * 触发浏览器文件下载
 *
 * 通过创建临时 `<a>` 元素并模拟点击来触发文件下载，
 * 下载完成后自动释放 Blob URL。
 *
 * @param blob - 要下载的文件内容
 * @param filename - 下载的文件名
 */
export function downloadBlob(blob: Blob, filename: string): void {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = filename;
  a.click();
  URL.revokeObjectURL(a.href);
}
