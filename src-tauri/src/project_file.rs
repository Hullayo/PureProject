/// 读取 .pm 文件内容
///
/// 容错：若目标文件不存在但存在上次写入留下的 `<path>.bak`（极端断电场景），
/// 自动回退到 `.bak`，避免“文件凭空消失”。
#[tauri::command]
pub fn read_pm_file(path: String) -> Result<String, String> {
    crate::atomic_file::read_file_with_fallback(&path).map_err(|e| format!("读取文件失败: {}", e))
}

/// 写入 .pm 文件（**原子写**）
///
/// 实现见 `crate::atomic_file`（tmp → fsync → .bak → rename），
/// 保证断电/崩溃时不会留下写了一半的文件。
#[tauri::command]
pub fn write_pm_file(path: String, content: String) -> Result<(), String> {
    crate::atomic_file::write_file_atomic(&path, content.as_bytes())
        .map_err(|e| format!("写入文件失败: {}", e))
}

/// 扫描目录下所有 .pm 文件，返回文件路径列表
#[tauri::command]
pub fn scan_pm_files(dir: String) -> Result<Vec<String>, String> {
    let mut files = Vec::new();
    let dir_path = std::path::Path::new(&dir);

    if !dir_path.exists() {
        return Ok(files);
    }

    let entries = std::fs::read_dir(dir_path)
        .map_err(|e| format!("读取目录失败: {}", e))?;

    for entry in entries {
        let entry = entry.map_err(|e| format!("读取目录条目失败: {}", e))?;
        let path = entry.path();
        if path.is_file() {
            if let Some(ext) = path.extension() {
                if ext == "pm" {
                    files.push(path.to_string_lossy().to_string());
                }
            }
        }
    }

    files.sort();
    Ok(files)
}

/// 删除 .pm 文件
#[tauri::command]
pub fn delete_pm_file(path: String) -> Result<(), String> {
    std::fs::remove_file(&path)
        .map_err(|e| format!("删除文件失败: {}", e))
}

/// 从远端 URL 下载并保存到本地路径
///
/// 用于「导出 PDF」：前端拿不到二进制时，由 Rust 流式落盘到用户选定路径。
#[tauri::command]
pub async fn save_url_to_file(url: String, path: String) -> Result<(), String> {
    if let Some(parent) = std::path::Path::new(&path).parent() {
        std::fs::create_dir_all(parent).map_err(|e| format!("创建目录失败: {}", e))?;
    }
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(180))
        .build()
        .map_err(|e| format!("创建 HTTP 客户端失败: {}", e))?;

    let resp = client
        .get(&url)
        .send()
        .await
        .map_err(|e| format!("下载失败: {}", e))?;

    if !resp.status().is_success() {
        return Err(format!("服务器返回 {}", resp.status()));
    }

    let bytes = resp.bytes().await.map_err(|e| format!("读取响应失败: {}", e))?;
    std::fs::write(&path, &bytes).map_err(|e| format!("写入文件失败: {}", e))
}

/// 目录条目（用于前端渲染文件树）
#[derive(serde::Serialize, Clone)]
pub struct DirEntry {
    /// 文件/目录名
    name: String,
    /// 相对于扫描根目录的路径
    path: String,
    /// 是否为目录
    #[serde(rename = "isDir")]
    is_dir: bool,
}

/// 递归扫描目录，返回扁平化的文件/目录列表（不读取文件内容）
///
/// 隐藏文件和 node_modules 等常见忽略目录会被跳过。
/// 最大深度 6 层，防止过深目录导致性能问题。
#[tauri::command]
pub fn scan_directory(dir: String, max_depth: Option<usize>) -> Result<Vec<DirEntry>, String> {
    let max_depth = max_depth.unwrap_or(6);
    let mut entries = Vec::new();
    scan_dir_recursive(&dir, "", max_depth, &mut entries)
        .map_err(|e| format!("扫描目录失败: {}", e))?;
    Ok(entries)
}

fn scan_dir_recursive(
    base: &str,
    relative: &str,
    max_depth: usize,
    entries: &mut Vec<DirEntry>,
) -> Result<(), Box<dyn std::error::Error>> {
    if max_depth == 0 {
        return Ok(());
    }

    let current_path = std::path::Path::new(base).join(relative);
    if !current_path.exists() {
        return Ok(());
    }

    let dir_entries = std::fs::read_dir(&current_path)?;
    let mut children: Vec<std::fs::DirEntry> = Vec::new();

    for entry in dir_entries {
        let entry = entry?;
        let name = entry.file_name().to_string_lossy().to_string();

        // 跳过隐藏文件和常见忽略目录
        if name.starts_with('.') || name == "node_modules" || name == "target" || name == "build" || name == "dist" {
            continue;
        }

        children.push(entry);
    }

    // 排序：目录在前，然后按名称字母序
    children.sort_by(|a, b| {
        let a_is_dir = a.path().is_dir();
        let b_is_dir = b.path().is_dir();
        match (a_is_dir, b_is_dir) {
            (true, false) => std::cmp::Ordering::Less,
            (false, true) => std::cmp::Ordering::Greater,
            _ => a.file_name().cmp(&b.file_name()),
        }
    });

    for entry in children {
        let name = entry.file_name().to_string_lossy().to_string();
        let rel = if relative.is_empty() {
            name.clone()
        } else {
            format!("{}/{}", relative, name)
        };
        let is_dir = entry.path().is_dir();

        entries.push(DirEntry {
            name,
            path: rel.clone(),
            is_dir,
        });

        if is_dir {
            scan_dir_recursive(base, &rel, max_depth - 1, entries)?;
        }
    }

    Ok(())
}
