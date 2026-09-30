mod credentials;
mod github;
mod webdav;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub enum SyncBackend {
    #[serde(rename = "webdav")]
    WebDav,
    #[serde(rename = "github_gist")]
    GitHubGist,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncConfig {
    pub backend: SyncBackend,
    pub url: String,
    pub username: String,
    pub password: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncResult {
    pub success: bool,
    pub message: String,
}

#[tauri::command]
pub async fn sync_push(config: SyncConfig, project_json: String) -> Result<SyncResult, String> {
    match config.backend {
        SyncBackend::WebDav => webdav::push(&config.url, &config.username, &config.password, &project_json).await,
        SyncBackend::GitHubGist => github::push_gist(&config.url, &config.password, &project_json).await,
    }
}

#[tauri::command]
pub async fn sync_pull(config: SyncConfig) -> Result<String, String> {
    match config.backend {
        SyncBackend::WebDav => webdav::pull(&config.url, &config.username, &config.password).await,
        SyncBackend::GitHubGist => github::pull_gist(&config.url, &config.password).await,
    }
}

#[tauri::command]
pub async fn sync_test(config: SyncConfig) -> Result<bool, String> {
    match config.backend {
        SyncBackend::WebDav => webdav::test_connection(&config.url, &config.username, &config.password).await,
        SyncBackend::GitHubGist => github::test_gist(&config.url, &config.password).await,
    }
}

#[tauri::command]
pub async fn save_credential(service: String, key: String, value: String) -> Result<(), String> {
    credentials::save_credential(&service, &key, &value)
        .map_err(|e| format!("Failed to save credential: {}", e))
}

#[tauri::command]
pub async fn load_credential(service: String, key: String) -> Result<String, String> {
    credentials::load_credential(&service, &key)
        .map_err(|e| format!("Failed to load credential: {}", e))
}
