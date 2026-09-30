//! Tauri 应用入口
//!
//! Windows 平台在 release 模式下隐藏控制台窗口。
//! 调用 `projectmanager_lib::run()` 启动应用。

// Prevents additional console window on Windows in release, DO NOT REMOVE!!
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    projectmanager_lib::run()
}
