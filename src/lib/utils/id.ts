/**
 * ID 生成工具
 *
 * @module utils/id
 */

/**
 * 生成唯一 ID
 *
 * 优先使用 crypto.randomUUID()（现代浏览器标准），
 * 在不支持的环境中回退到 Date.now + Math.random 组合。
 *
 * @returns UUID 格式的唯一标识符
 */
export function genId(): string {
  return crypto.randomUUID?.() ?? Date.now().toString(36) + Math.random().toString(36).slice(2);
}
