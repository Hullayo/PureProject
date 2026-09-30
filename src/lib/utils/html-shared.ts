/**
 * HTML 共享工具函数
 *
 * @module utils/html-shared
 */

/**
 * 转义 HTML 特殊字符
 */
export function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/**
 * 生成安全的文件名
 *
 * 去掉文件系统非法字符与控制字符，压缩连续空白，去除首尾点/空格。
 * 不改变中文等 Unicode 字符。
 *
 * @param name - 原始名称（如项目名）
 * @param fallback - 清洗后为空时的兜底名
 */
export function safeFileName(name: string, fallback = 'project'): string {
  const cleaned = String(name ?? '')
    // eslint-disable-next-line no-control-regex
    .replace(/[\u0000-\u001f<>:"/\\|?*]/g, '_')
    .replace(/\s+/g, ' ')
    .replace(/^[.\s]+|[.\s]+$/g, '')
    .slice(0, 120);
  return cleaned || fallback;
}
