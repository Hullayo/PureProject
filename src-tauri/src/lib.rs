//! Tauri 应用主库
//!
//! 配置 Tauri 应用的插件和命令处理器。
//! 功能包括：
//! - 系统托盘（最小化到托盘、托盘菜单、左键恢复窗口）
//! - 窗口关闭拦截（关闭时隐藏到托盘而非退出）
//! - `tauri-plugin-opener` — 文件/URL 打开
//! - `tauri-plugin-notification` — 桌面通知

mod sync;
mod ai;
mod project_file;
mod atomic_file;
mod updater;

#[cfg(desktop)]
use tauri::menu::{MenuBuilder, MenuItemBuilder};
#[cfg(desktop)]
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
// `Manager` 只被 #[cfg(desktop)] 的托盘/窗口代码用到；Android 上不引入，避免 unused import 警告
#[cfg(desktop)]
use tauri::Manager;

#[tauri::command]
fn greet(name: &str) -> String {
    format!("Hello, {}! You've been greeted from Rust!", name)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let mut builder = tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_process::init());

    // 自动更新（仅桌面端）：tauri-plugin-updater 不支持 Android/iOS。
    // 端点与公钥在 tauri.conf.json 的 plugins.updater 中配置，用户无需任何设置。
    #[cfg(desktop)]
    {
        builder = builder.plugin(tauri_plugin_updater::Builder::new().build());
    }

    // Android：下载完成后拉起系统安装器安装 APK（Android 不支持静默安装，必须用户确认一次）
    #[cfg(target_os = "android")]
    {
        builder = builder.plugin(tauri_plugin_android_installer::init());
    }

    // Android：SAF 文件保存（系统「保存到」对话框 + 按 content:// URI 写入）
    #[cfg(target_os = "android")]
    {
        builder = builder.plugin(tauri_plugin_android_fs::init());
    }

    // The standalone V1.2 package uses its own app identifier and intentionally
    // allows the original application and this versioned snapshot to run together.

    #[cfg(desktop)]
    {
        builder = builder.setup(|app| {
            // 创建托盘右键菜单
            let show_item = MenuItemBuilder::with_id("show", "显示窗口").build(app)?;
            let quit_item = MenuItemBuilder::with_id("quit", "退出").build(app)?;
            let menu = MenuBuilder::new(app)
                .items(&[&show_item, &quit_item])
                .build()?;

            // DevTools（仅调试模式自动打开）
            #[cfg(debug_assertions)]
            if let Some(window) = app.get_webview_window("main") {
                window.open_devtools();
                std::thread::spawn(move || {
                    std::thread::sleep(std::time::Duration::from_millis(500));
                    let _ = window.close_devtools();
                });
            }

            // 创建系统托盘图标
            let _tray = TrayIconBuilder::new()
                .icon(app.default_window_icon().unwrap().clone())
                .menu(&menu)
                .on_menu_event(move |app, event| match event.id().as_ref() {
                    "show" => {
                        if let Some(window) = app.get_webview_window("main") {
                            let _ = window.show();
                            let _ = window.set_focus();
                        }
                    }
                    "quit" => {
                        app.exit(0);
                    }
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        let app = tray.app_handle();
                        if let Some(window) = app.get_webview_window("main") {
                            let _ = window.show();
                            let _ = window.set_focus();
                        }
                    }
                })
                .build(app)?;

            Ok(())
        })
        .on_window_event(|window, event| {
            // 拦截窗口关闭事件，隐藏到托盘而非退出
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        });
    }

    builder
        .invoke_handler(tauri::generate_handler![
            greet,
            sync::sync_push,
            sync::sync_pull,
            sync::sync_test,
            sync::save_credential,
            sync::load_credential,
            ai::ai_chat,
            ai::save_text_file,
            ai::get_documents_dir,
            project_file::read_pm_file,
            project_file::write_pm_file,
            project_file::scan_pm_files,
            project_file::delete_pm_file,
            project_file::scan_directory,
            project_file::save_url_to_file,
            updater::check_for_updates,
            updater::desktop_check_update,
            updater::desktop_install_update,
            updater::download_update,
            updater::get_current_version
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
