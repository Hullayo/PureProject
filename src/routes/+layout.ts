/**
 * SvelteKit 布局配置
 *
 * 禁用 SSR（服务端渲染），使用 SPA 模式运行。
 * Tauri 环境没有 Node.js 服务器，需要 adapter-static
 * 配合 SPA fallback 到 index.html。
 *
 * @see https://svelte.dev/docs/kit/single-page-apps
 * @see https://v2.tauri.app/start/frontend/sveltekit/
 */

export const ssr = false;
