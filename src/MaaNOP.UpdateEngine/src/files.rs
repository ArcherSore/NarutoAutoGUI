use std::fs;
use std::path::Path;

pub const PRESERVED: [&str; 4] = ["config", "logs", "debug", "cache"];

pub fn preserved(name: &str) -> bool
{
    PRESERVED.iter().any(|p| name.eq_ignore_ascii_case(p))
}

pub fn reject_link(path: &Path) -> Result<(), String>
{
    let metadata = fs::symlink_metadata(path).map_err(|e| e.to_string())?;
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if metadata.file_attributes() & 0x400 != 0 {
            return Err(format!("路径含重解析点：{}", path.display()));
        }
    }
    if metadata.file_type().is_symlink() {
        return Err(format!("路径含链接：{}", path.display()));
    }
    Ok(())
}

pub fn ancestors(path: &Path) -> Result<(), String>
{
    if !path.is_absolute() || path.components().any(|c| matches!(c, std::path::Component::ParentDir)) {
        return Err("必须使用无逃逸的绝对安装路径。".into());
    }
    for parent in path.ancestors() {
        reject_link(parent)?;
    }
    Ok(())
}

pub fn tree(path: &Path) -> Result<(), String>
{
    reject_link(path)?;
    if path.is_dir() {
        for entry in fs::read_dir(path).map_err(|e| e.to_string())? {
            tree(&entry.map_err(|e| e.to_string())?.path())?;
        }
    }
    Ok(())
}

pub fn installation(root: &Path) -> Result<(), String>
{
    ancestors(root)?;
    for entry in fs::read_dir(root).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        let path = entry.path();
        reject_link(&path)?;
        if preserved(&entry.file_name().to_string_lossy()) {
            if !path.is_dir() {
                return Err("保留目录名称与文件冲突。".into());
            }
        } else {
            tree(&path)?;
        }
    }
    Ok(())
}

pub fn remove(path: &Path) -> Result<(), String>
{
    tree(path)?;
    if path.is_dir() { fs::remove_dir_all(path) } else { fs::remove_file(path) }
        .map_err(|e| e.to_string())
}
