use super::SyncResult;

const FILE_NAME: &str = "projectmanager_backup.json";
const JIANGUOYUN_APP_DIR: &str = "ProjectManager";

/// 确保 URL 指向具体的文件（不是目录）
fn file_url(base: &str) -> String {
    let b = base.trim_end_matches('/');
    let parsed = reqwest::Url::parse(b).ok();
    let has_file_name = parsed
        .as_ref()
        .and_then(|url| {
            url.path_segments()
                .and_then(|mut segments| segments.next_back())
                .map(|segment| segment.contains('.'))
        })
        .unwrap_or_else(|| {
            b.rsplit('/')
                .next()
                .map(|segment| segment.contains('.'))
                .unwrap_or(false)
        });

    if has_file_name {
        b.to_string()
    } else if is_jianguoyun_root(parsed.as_ref()) {
        format!("{}/{}/{}", b, JIANGUOYUN_APP_DIR, FILE_NAME)
    } else {
        format!("{}/{}", b, FILE_NAME)
    }
}

fn is_jianguoyun_root(url: Option<&reqwest::Url>) -> bool {
    url.and_then(|u| u.host_str())
        .map(|host| host.eq_ignore_ascii_case("dav.jianguoyun.com"))
        .unwrap_or(false)
        && url
            .map(|u| u.path().trim_matches('/') == "dav")
            .unwrap_or(false)
}

fn collection_url(full_url: &str) -> String {
    full_url
        .rsplit_once('/')
        .map(|(parent, _)| format!("{}/", parent))
        .unwrap_or_else(|| full_url.to_string())
}

#[cfg(test)]
mod tests {
    use super::{collection_url, file_url, propfind_succeeded, FILE_NAME};

    #[test]
    fn stores_jianguoyun_backup_in_projectmanager_directory() {
        assert_eq!(
            file_url("https://dav.jianguoyun.com/dav/"),
            format!("https://dav.jianguoyun.com/dav/ProjectManager/{}", FILE_NAME)
        );
    }

    #[test]
    fn keeps_explicit_file_url() {
        assert_eq!(
            file_url("https://dav.example.com/backups/project.pm"),
            "https://dav.example.com/backups/project.pm"
        );
    }

    #[test]
    fn returns_parent_collection_url() {
        assert_eq!(
            collection_url("https://dav.jianguoyun.com/dav/ProjectManager/projectmanager_backup.json"),
            "https://dav.jianguoyun.com/dav/ProjectManager/"
        );
    }

    #[test]
    fn accepts_webdav_propfind_success_statuses_only() {
        assert!(propfind_succeeded(reqwest::StatusCode::OK));
        assert!(propfind_succeeded(reqwest::StatusCode::MULTI_STATUS));
        assert!(!propfind_succeeded(reqwest::StatusCode::NOT_FOUND));
    }
}

pub async fn push(url: &str, username: &str, password: &str, content: &str) -> Result<SyncResult, String> {
    let full_url = file_url(url);
    let dir_url = collection_url(&full_url);
    let client = reqwest::Client::new();
    ensure_collection(&client, &dir_url, username, password).await?;
    let resp = client
        .put(&full_url)
        .basic_auth(username, Some(password))
        .header("Content-Type", "application/json")
        .body(content.to_string())
        .send()
        .await
        .map_err(|e| format!("WebDAV 请求失败: {}", e))?;

    if resp.status().is_success() || resp.status() == 201 || resp.status() == 204 {
        Ok(SyncResult { success: true, message: format!("上传成功: {}", full_url) })
    } else {
        let status_code = resp.status();
        let body = resp.text().await.unwrap_or_default();
        Ok(SyncResult { success: false, message: format!("服务器返回 {} - {}", status_code, body) })
    }
}

async fn ensure_collection(client: &reqwest::Client, url: &str, username: &str, password: &str) -> Result<(), String> {
    let resp = client
        .request(reqwest::Method::from_bytes(b"MKCOL").unwrap(), url)
        .basic_auth(username, Some(password))
        .send()
        .await
        .map_err(|e| format!("WebDAV 创建目录失败: {}", e))?;

    if resp.status().is_success()
        || resp.status() == reqwest::StatusCode::METHOD_NOT_ALLOWED
        || resp.status() == reqwest::StatusCode::CONFLICT
    {
        Ok(())
    } else {
        let status_code = resp.status();
        let body = resp.text().await.unwrap_or_default();
        Err(format!("WebDAV 创建目录失败: 服务器返回 {} - {}", status_code, body))
    }
}

fn propfind_succeeded(status: reqwest::StatusCode) -> bool {
    status.is_success() || status == reqwest::StatusCode::MULTI_STATUS
}

pub async fn pull(url: &str, username: &str, password: &str) -> Result<String, String> {
    let full_url = file_url(url);
    let client = reqwest::Client::new();
    let resp = client
        .get(&full_url)
        .basic_auth(username, Some(password))
        .send()
        .await
        .map_err(|e| format!("WebDAV 请求失败: {}", e))?;

    if resp.status().is_success() {
        resp.text().await.map_err(|e| format!("读取响应失败: {}", e))
    } else {
        Err(format!("服务器返回 {}", resp.status()))
    }
}

pub async fn test_connection(url: &str, username: &str, password: &str) -> Result<bool, String> {
    let full_url = file_url(url);
    let dir_url = collection_url(&full_url);
    let client = reqwest::Client::new();
    let resp = client
        .request(reqwest::Method::from_bytes(b"PROPFIND").unwrap(), &dir_url)
        .basic_auth(username, Some(password))
        .header("Depth", "0")
        .send()
        .await
        .map_err(|e| format!("WebDAV 请求失败: {}", e))?;

    if propfind_succeeded(resp.status()) {
        return Ok(true);
    }

    if resp.status() == reqwest::StatusCode::NOT_FOUND {
        ensure_collection(&client, &dir_url, username, password).await?;
        return Ok(true);
    }

    Ok(false)
}
