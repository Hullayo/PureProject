//! 原子文件写入
//!
//! 单独成模块的意义：**不依赖 tauri**，所以可以脱离整个依赖树直接跑单测：
//! ```bash
//! pnpm test:rust      # = rustc --test src-tauri/src/atomic_file.rs && 运行
//! ```
//! 本机（4GB 内存）跑完整的 `cargo test` 需要给 linux 目标编译整棵依赖树，
//! 这个模块让「断电不损坏文件」这条最重要的一致性保证随时可验证。

use std::io::Write;

/// 原子写文件
///
/// 流程：写 `<path>.tmp` → `fsync` → 把旧文件复制为 `<path>.bak` → `rename(tmp, path)`。
/// 任意时刻断电/崩溃，`path` 要么是完整的旧版本、要么是完整的新版本，
/// 不会出现「写了一半」的截断文件（直接 `fs::write` 会）。
///
/// `.bak` 只保留最近一次写入前的版本，供人工/程序回退。
pub fn write_file_atomic(path: &str, bytes: &[u8]) -> std::io::Result<()> {
    let target = std::path::Path::new(path);
    if let Some(parent) = target.parent() {
        std::fs::create_dir_all(parent)
            .map_err(|e| ctx("创建目录", &parent.to_string_lossy(), e))?;
    }

    // 1) 先落到同目录的临时文件：同目录才能保证 rename 是原子替换（不会跨设备）
    let tmp = format!("{}.tmp", path);
    {
        let mut file = std::fs::File::create(&tmp).map_err(|e| ctx("创建临时文件", &tmp, e))?;
        file.write_all(bytes).map_err(|e| ctx("写入临时文件", &tmp, e))?;
        file.flush().map_err(|e| ctx("刷新临时文件", &tmp, e))?;
        file.sync_all().map_err(|e| ctx("落盘临时文件", &tmp, e))?; // 内容+元数据落盘，避免断电后只剩个空文件
    }

    // 2) 旧文件备份一份（.bak）。用 copy 而非 rename：
    //    即便紧接着崩溃，原文件仍在原位，不会出现「两个都没了」
    if target.exists() {
        let bak = format!("{}.bak", path);
        let _ = std::fs::copy(target, &bak); // 备份失败不阻断主流程
    }

    // 3) 原子替换
    match std::fs::rename(&tmp, target) {
        Ok(()) => Ok(()),
        Err(e) => {
            let _ = std::fs::remove_file(&tmp);
            Err(ctx("替换目标文件", path, e))
        }
    }
}

/// 给 IO 错误补上「哪一步 + 哪个路径」，便于定位（如 `os error 5` 拒绝访问）
fn ctx(step: &str, path: &str, e: std::io::Error) -> std::io::Error {
    std::io::Error::new(e.kind(), format!("{step}失败 «{path}»: {e}"))
}

/// 读取文件；主文件缺失时回退到 `<path>.bak`（极端断电场景的自愈）
pub fn read_file_with_fallback(path: &str) -> std::io::Result<String> {
    match std::fs::read_to_string(path) {
        Ok(s) => Ok(s),
        Err(primary) => {
            let bak = format!("{}.bak", path);
            if std::path::Path::new(&bak).exists() {
                std::fs::read_to_string(&bak)
            } else {
                Err(primary)
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmpdir(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("pm-atomic-{}-{}", tag, std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn p(dir: &std::path::Path, name: &str) -> String {
        dir.join(name).to_string_lossy().to_string()
    }

    #[test]
    fn writes_new_file_and_cleans_tmp() {
        let dir = tmpdir("new");
        let path = p(&dir, "a.pm");
        let _ = std::fs::remove_file(&path);
        write_file_atomic(&path, b"v1").unwrap();
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "v1");
        assert!(!std::path::Path::new(&format!("{}.tmp", path)).exists(), "临时文件应已被 rename");
        assert!(!std::path::Path::new(&format!("{}.bak", path)).exists(), "首次写入无旧文件可备份");
    }

    #[test]
    fn keeps_previous_version_as_bak() {
        let dir = tmpdir("bak");
        let path = p(&dir, "b.pm");
        let _ = std::fs::remove_file(&path);
        let _ = std::fs::remove_file(format!("{}.bak", path));
        write_file_atomic(&path, b"old").unwrap();
        write_file_atomic(&path, b"new").unwrap();
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "new");
        assert_eq!(std::fs::read_to_string(format!("{}.bak", path)).unwrap(), "old", "上一版应留在 .bak");
    }

    #[test]
    fn read_falls_back_to_bak() {
        let dir = tmpdir("fallback");
        let path = p(&dir, "c.pm");
        let _ = std::fs::remove_file(&path);
        write_file_atomic(&path, b"only").unwrap();
        // 模拟「新文件丢失、但备份还在」
        std::fs::rename(&path, format!("{}.bak", path)).unwrap();
        assert_eq!(read_file_with_fallback(&path).unwrap(), "only");
    }

    #[test]
    fn overwrite_is_not_partial() {
        let dir = tmpdir("partial");
        let path = p(&dir, "d.pm");
        let _ = std::fs::remove_file(&path);
        let big = vec![b'x'; 512 * 1024];
        write_file_atomic(&path, &big).unwrap();
        write_file_atomic(&path, b"small").unwrap();
        // 覆盖后长度必须是新内容的长度，不能是旧内容的残影
        assert_eq!(std::fs::metadata(&path).unwrap().len(), 5);
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "small");
    }

    #[test]
    fn creates_missing_parent_dirs() {
        let dir = tmpdir("mkdir");
        let path = p(&dir.join("deep").join("nested"), "e.pm");
        write_file_atomic(&path, b"ok").unwrap();
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "ok");
    }
}
