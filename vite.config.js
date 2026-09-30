import { defineConfig } from "vite";
import { sveltekit } from "@sveltejs/kit/vite";
import path from "path";
import { readFileSync } from "node:fs";

const host = process.env.TAURI_DEV_HOST;
const appVersion = JSON.parse(readFileSync(new URL("./package.json", import.meta.url), "utf8")).version;

export default defineConfig(async () => ({
  plugins: [sveltekit()],
  base: "./",
  clearScreen: false,
  // 暴露 Tauri 构建期注入的 TAURI_ENV_* 变量（如 TAURI_ENV_PLATFORM）。
  // Vite 默认只暴露 VITE_ 前缀，不配这里 import.meta.env.TAURI_ENV_PLATFORM 恒为 undefined，
  // 会导致桌面端误判为 Android、走 android.json 去下载 APK。
  envPrefix: ["VITE_", "TAURI_ENV_"],
  define: {
    "import.meta.env.VITE_APP_VERSION": JSON.stringify(appVersion),
  },
  resolve: {
    alias: {
      "$debug": path.resolve("./debug"),
    },
  },
  server: {
    port: 1420,
    strictPort: true,
    host: "0.0.0.0",
    watch: {
      ignored: ["**/src-tauri/**"],
    },
  },
}));
