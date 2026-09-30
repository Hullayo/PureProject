//! 自动更新（桌面 + Android）
//!
//! - **桌面端（Windows/macOS/Linux）**：用 `tauri-plugin-updater` 的 `UpdaterBuilder`
//!   在**运行时**指定 endpoint（支持「自建服务器 / GitHub Release」两种更新源），
//!   下载后用 `tauri.conf.json` 里的公钥校验签名并安装。
//! - **Android**：`tauri-plugin-updater` 不支持移动端，改为拉取清单 JSON、下载 APK，
//!   再经 `tauri-plugin-android-installer` 拉起系统安装器。
//!
//! 更新地址**不写死在本模块**：由前端 `$lib/updater/config` 依「更新源」配置计算后传入，
//! 便于切换自建服务器 / GitHub、修改地址与端口。
//!
//! 清单 JSON 格式（`latest.json` 用于桌面，`android.json` 用于 Android）：
//! ```json
//! {
//!   "version": "0.8.0",
//!   "notes": "更新说明",
//!   "url": "http://<host>/updates/ProjectManager-android-arm64-v0.8.0.apk",
//!   "required": false
//! }
//! ```

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, Manager};

/// 返回给前端的更新信息
#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct UpdateInfo {
    pub latest_version: String,
    pub download_url: String,
    pub release_notes: String,
    pub required: bool,
}

/// 下载进度事件
#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct DownloadProgress {
    pub downloaded: u64,
    pub total: u64,
    pub percent: u8,
}

/// 移动端更新清单（服务器 `android.json` / GitHub Release 资源）
#[derive(Debug, Deserialize)]
struct UpdateManifest {
    version: String,
    #[serde(default)]
    notes: String,
    #[serde(default)]
    url: String,
    #[serde(default)]
    required: bool,
}

/// 将版本号解析为数字数组，忽略 "v" 前缀与预发布后缀
fn parse_version(v: &str) -> Vec<u64> {
    v.trim()
        .trim_start_matches('v')
        .split(['.', '-', '+'])
        .map(|p| p.parse::<u64>().unwrap_or(0))
        .collect()
}

/// 语义化比较：`a > b` 返回 true（避免字符串字典序导致 "0.4.10" < "0.4.9"）
fn version_gt(a: &str, b: &str) -> bool {
    let (va, vb) = (parse_version(a), parse_version(b));
    let len = va.len().max(vb.len());
    for i in 0..len {
        let x = *va.get(i).unwrap_or(&0);
        let y = *vb.get(i).unwrap_or(&0);
        if x != y {
            return x > y;
        }
    }
    false
}

/// 检查更新（Android / 清单式）
///
/// @param manifest_url 清单地址（自建服务器 `…/updates/android.json`，
///                     或 GitHub `…/releases/latest/download/android.json`）
#[tauri::command]
pub async fn check_for_updates(manifest_url: String) -> Result<Option<UpdateInfo>, String> {
    let current_version = env!("CARGO_PKG_VERSION");

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(15))
        .build()
        .map_err(|e| format!("创建 HTTP 客户端失败: {}", e))?;

    let resp = client
        .get(&manifest_url)
        .send()
        .await
        .map_err(|e| format!("无法连接更新服务器: {}", e))?;

    if !resp.status().is_success() {
        return Err(format!("更新服务器返回 {}", resp.status()));
    }

    let manifest: UpdateManifest = resp
        .json()
        .await
        .map_err(|e| format!("解析更新信息失败: {}", e))?;

    if version_gt(&manifest.version, current_version) {
        Ok(Some(UpdateInfo {
            latest_version: manifest.version,
            download_url: manifest.url,
            release_notes: manifest.notes,
            required: manifest.required,
        }))
    } else {
        Ok(None)
    }
}

/// 检查更新（桌面端）
///
/// 运行时用 `UpdaterBuilder::endpoints()` 指定 endpoint，保留 `tauri.conf.json` 的
/// 公钥做签名校验；有新版本返回 `UpdateInfo`，否则 `None`。
#[cfg(desktop)]
#[tauri::command]
pub async fn desktop_check_update(
    app: AppHandle,
    endpoint: String,
) -> Result<Option<UpdateInfo>, String> {
    use tauri_plugin_updater::UpdaterExt;

    let url = tauri::Url::parse(&endpoint).map_err(|e| format!("更新地址无效：{}", e))?;
    let updater = app
        .updater_builder()
        .endpoints(vec![url])
        .map_err(|e| format!("配置更新地址失败：{}", e))?
        .build()
        .map_err(|e| format!("初始化更新器失败：{}", e))?;

    let update = updater
        .check()
        .await
        .map_err(|e| format!("检查更新失败：{}", e))?;

    Ok(update.map(|u| UpdateInfo {
        latest_version: u.version.clone(),
        download_url: u.download_url.to_string(),
        release_notes: u.body.clone().unwrap_or_default(),
        required: false,
    }))
}

/// 非桌面端占位：前端统一调用同名命令，移动端直接返回无更新
#[cfg(not(desktop))]
#[tauri::command]
pub async fn desktop_check_update(
    _app: AppHandle,
    _endpoint: String,
) -> Result<Option<UpdateInfo>, String> {
    Ok(None)
}

/// 下载并安装更新（桌面端）
///
/// Windows：安装器以 passive 模式拉起后**应用会退出**（由插件负责），前端无需再 relaunch。
/// macOS / Linux：安装完成后由前端 `relaunch()` 重启。
#[cfg(desktop)]
#[tauri::command]
pub async fn desktop_install_update(app: AppHandle, endpoint: String) -> Result<(), String> {
    use std::sync::{Arc, Mutex};
    use tauri_plugin_updater::UpdaterExt;

    let url = tauri::Url::parse(&endpoint).map_err(|e| format!("更新地址无效：{}", e))?;
    let updater = app
        .updater_builder()
        .endpoints(vec![url])
        .map_err(|e| format!("配置更新地址失败：{}", e))?
        .build()
        .map_err(|e| format!("初始化更新器失败：{}", e))?;

    let update = updater
        .check()
        .await
        .map_err(|e| format!("检查更新失败：{}", e))?
        .ok_or_else(|| "没有可用更新".to_string())?;

    let downloaded = Arc::new(Mutex::new(0u64));
    let total = Arc::new(Mutex::new(0u64));
    let app_handle = app.clone();
    let (d, t) = (downloaded.clone(), total.clone());

    update
        .download_and_install(
            move |chunk: usize, content_length: Option<u64>| {
                let mut done = d.lock().unwrap();
                *done += chunk as u64;
                if let Some(len) = content_length {
                    *t.lock().unwrap() = len;
                }
                let total_bytes = *t.lock().unwrap();
                let percent = if total_bytes > 0 {
                    ((*done as f64 / total_bytes as f64) * 100.0).min(100.0) as u8
                } else {
                    0
                };
                let _ = app_handle.emit(
                    "update:download-progress",
                    DownloadProgress {
                        downloaded: *done,
                        total: total_bytes,
                        percent,
                    },
                );
            },
            || {},
        )
        .await
        .map_err(|e| format!("下载/安装更新失败：{}", e))?;

    Ok(())
}

/// 非桌面端占位
#[cfg(not(desktop))]
#[tauri::command]
pub async fn desktop_install_update(_app: AppHandle, _endpoint: String) -> Result<(), String> {
    Ok(())
}

/// 下载更新文件（Android APK）
///
/// 从给定 URL 流式下载到应用缓存目录，并通过
/// `update:download-progress` 事件上报进度。
#[tauri::command]
pub async fn download_update(app: AppHandle, url: String) -> Result<String, String> {
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(300))
        .build()
        .map_err(|e| format!("创建 HTTP 客户端失败: {}", e))?;

    let resp = client
        .get(&url)
        .send()
        .await
        .map_err(|e| format!("无法下载更新文件: {}", e))?;

    let total = resp.content_length().unwrap_or(0);
    let mut downloaded: u64 = 0;

    // 保存到 cache 目录：tauri-plugin-android-installer 的 FileProvider 只覆盖
    // cache / files / external-cache / external-files，appCacheDir 最可靠。
    let app_data_dir = app
        .path()
        .app_cache_dir()
        .map_err(|e| format!("无法获取应用缓存目录: {}", e))?;

    std::fs::create_dir_all(&app_data_dir)
        .map_err(|e| format!("无法创建应用缓存目录: {}", e))?;

    // 只保留最新一份：下载前清掉历史更新包，避免缓存目录无限堆积。
    // 注意：不清刚下载的那一个（本次会覆盖写）。
    if let Ok(entries) = std::fs::read_dir(&app_data_dir) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            if name.starts_with("ProjectManager") && name.ends_with(".apk") {
                let _ = std::fs::remove_file(entry.path());
            }
        }
    }

    let file_name = url.split('/').last().unwrap_or("update.apk");
    let file_path = app_data_dir.join(file_name);

    let mut file =
        std::fs::File::create(&file_path).map_err(|e| format!("无法创建下载文件: {}", e))?;

    let mut stream = resp.bytes_stream();

    use futures_util::StreamExt;
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|e| format!("下载中断: {}", e))?;
        use std::io::Write;
        file.write_all(&chunk)
            .map_err(|e| format!("写入文件失败: {}", e))?;

        downloaded += chunk.len() as u64;

        if total > 0 {
            let percent = ((downloaded as f64 / total as f64) * 100.0) as u8;
            let _ = app.emit(
                "update:download-progress",
                DownloadProgress {
                    downloaded,
                    total,
                    percent,
                },
            );
        }
    }

    Ok(file_path.to_string_lossy().to_string())
}

/// 获取当前版本号
#[tauri::command]
pub fn get_current_version() -> String {
    env!("CARGO_PKG_VERSION").to_string()
}
