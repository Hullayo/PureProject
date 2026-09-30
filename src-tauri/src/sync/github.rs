use serde::{Deserialize, Serialize};
use super::SyncResult;

#[derive(Serialize)]
struct GistFile {
    content: String,
}

#[derive(Serialize)]
struct UpdateGist {
    files: std::collections::HashMap<String, GistFile>,
}

#[derive(Deserialize)]
struct GistResponse {
    id: String,
}

pub async fn push_gist(gist_id: &str, token: &str, content: &str) -> Result<SyncResult, String> {
    let client = reqwest::Client::new();

    let mut files = std::collections::HashMap::new();
    files.insert("project.pm".to_string(), GistFile { content: content.to_string() });
    let body = UpdateGist { files };

    let url = if gist_id.is_empty() {
        "https://api.github.com/gists".to_string()
    } else {
        format!("https://api.github.com/gists/{}", gist_id)
    };

    let resp = if gist_id.is_empty() {
        client.post(&url)
            .bearer_auth(token)
            .header("User-Agent", "PureProject")
            .json(&body)
            .send()
            .await
            .map_err(|e| format!("GitHub API request failed: {}", e))?
    } else {
        client.patch(&url)
            .bearer_auth(token)
            .header("User-Agent", "PureProject")
            .json(&body)
            .send()
            .await
            .map_err(|e| format!("GitHub API request failed: {}", e))?
    };

    if resp.status().is_success() {
        let gist: GistResponse = resp.json().await.map_err(|e| format!("Failed to parse response: {}", e))?;
        Ok(SyncResult { success: true, message: gist.id })
    } else {
        let status = resp.status();
        let body = resp.text().await.unwrap_or_default();
        Ok(SyncResult { success: false, message: format!("GitHub API returned {}: {}", status, body) })
    }
}

pub async fn pull_gist(gist_id: &str, token: &str) -> Result<String, String> {
    let client = reqwest::Client::new();
    let url = format!("https://api.github.com/gists/{}", gist_id);

    let resp = client.get(&url)
        .bearer_auth(token)
        .header("User-Agent", "PureProject")
        .send()
        .await
        .map_err(|e| format!("GitHub API request failed: {}", e))?;

    if !resp.status().is_success() {
        return Err(format!("GitHub API returned {}", resp.status()));
    }

    let gist: serde_json::Value = resp.json().await.map_err(|e| format!("Failed to parse response: {}", e))?;

    let content = gist
        .get("files")
        .and_then(|f| f.get("project.pm"))
        .and_then(|f| f.get("content"))
        .and_then(|c| c.as_str())
        .ok_or("project.pm not found in gist")?;

    Ok(content.to_string())
}

pub async fn test_gist(gist_id: &str, token: &str) -> Result<bool, String> {
    let client = reqwest::Client::new();
    let url = if gist_id.is_empty() {
        "https://api.github.com/gists".to_string()
    } else {
        format!("https://api.github.com/gists/{}", gist_id)
    };

    let resp = client.get(&url)
        .bearer_auth(token)
        .header("User-Agent", "PureProject")
        .send()
        .await
        .map_err(|e| format!("GitHub API request failed: {}", e))?;

    Ok(resp.status().is_success())
}
