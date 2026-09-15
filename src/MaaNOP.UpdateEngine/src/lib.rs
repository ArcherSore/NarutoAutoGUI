mod version;

use serde::Deserialize;
use serde_json::{Value, json};
use std::fs::File;
use std::io::Read;
use std::path::Path;
use url::Url;
use version::Version;

pub const MAX_MESSAGE_BYTES: usize = 1024 * 1024;
pub const MAX_RELEASE_BYTES: u64 = 4 * 1024 * 1024;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request
{
    protocol_version: u32,
    operation: String,
    installation: String,
}

#[derive(Deserialize)]
struct Project
{
    name: String,
    version: String,
    github: String,
}

#[derive(Deserialize)]
struct Release
{
    tag_name: String,
    draft: bool,
    prerelease: bool,
    body: Option<String>,
    assets: Vec<Value>,
}

#[derive(Deserialize)]
struct Asset
{
    name: String,
    browser_download_url: String,
    size: u64,
    digest: String,
}

/// The JSON command seam. Only the external HTTP operation is substituted in tests.
pub fn execute(request: &str, fetch_release: impl FnOnce(&str) -> Result<String, String>) -> Value
{
    let result = check(request, fetch_release);
    match result {
        Ok(value) if value.to_string().len() < MAX_MESSAGE_BYTES => value,
        Ok(_) => error("invalid_release", "更新说明超出消息大小限制。"),
        Err((code, message)) => error(code, &message),
    }
}

pub fn error(code: &str, message: &str) -> Value
{
    json!({"protocolVersion": 1, "type": "error", "code": code, "message": message})
}

type CheckError = (&'static str, String);

fn check(request: &str, fetch_release: impl FnOnce(&str) -> Result<String, String>) -> Result<Value, CheckError>
{
    let invalid_request = || ("invalid_request", "更新命令无效。".to_owned());
    if request.len() > MAX_MESSAGE_BYTES {
        return Err(invalid_request());
    }
    let request: Request = serde_json::from_str(request).map_err(|_| invalid_request())?;
    if request.protocol_version != 1 || request.operation != "check" {
        return Err(invalid_request());
    }
    let installation = Path::new(&request.installation);
    if !installation.is_absolute() || !installation.is_dir() {
        return Err(("invalid_source", "安装目录必须为存在的绝对路径。".to_owned()));
    }
    let invalid_source = || ("invalid_source", "Project Interface 未提供有效的 MaaNOP 更新来源。".to_owned());
    let mut source = String::new();
    File::open(installation.join("interface.json")).map_err(|_| invalid_source())?
        .take((MAX_MESSAGE_BYTES + 1) as u64).read_to_string(&mut source).map_err(|_| invalid_source())?;
    if source.len() > MAX_MESSAGE_BYTES {
        return Err(invalid_source());
    }
    let project: Project = serde_json::from_str(source.trim_start_matches('\u{feff}'))
        .map_err(|_| invalid_source())?;
    if project.name != "MaaNOP" {
        return Err(invalid_source());
    }
    let current = Version::parse(&project.version).ok_or_else(invalid_source)?;
    let repository = repository(&project.github).ok_or_else(invalid_source)?;
    let release_json = fetch_release(&format!("https://api.github.com/repos/{repository}/releases/latest"))
        .map_err(|_| ("network_error", "检查更新失败，请稍后重试。".to_owned()))?;
    let invalid_release = || ("invalid_release", "GitHub Release 更新信息无效。".to_owned());
    if release_json.len() as u64 > MAX_RELEASE_BYTES {
        return Err(invalid_release());
    }
    let release: Release = serde_json::from_str(&release_json).map_err(|_| invalid_release())?;
    let mut result = json!({
        "protocolVersion": 1, "type": "result", "operation": "check",
        "currentVersion": project.version, "update": null
    });
    if release.draft || release.prerelease {
        return Ok(result);
    }
    let target = Version::parse(&release.tag_name).ok_or_else(invalid_release)?;
    if target <= current {
        return Ok(result);
    }
    let name = format!("MaaNOP-win-x86_64-{}.zip", release.tag_name);
    let mut matches = release.assets.iter().filter(|asset| asset["name"].as_str() == Some(&name));
    let asset = matches.next().ok_or_else(|| {
        ("invalid_release", "当前 Release 不包含唯一兼容的 Windows x64 更新包。".to_owned())
    })?;
    if matches.next().is_some() {
        return Err(("invalid_release", "当前 Release 存在重复的 Windows x64 更新包。".to_owned()));
    }
    let asset: Asset = serde_json::from_value(asset.clone()).map_err(|_| invalid_release())?;
    let digest = asset.digest.strip_prefix("sha256:").ok_or_else(|| {
        ("invalid_release", "更新包缺少有效的 SHA256 摘要。".to_owned())
    })?;
    if digest.len() != 64 || !digest.bytes().all(|c| c.is_ascii_hexdigit()) {
        return Err(("invalid_release", "更新包缺少有效的 SHA256 摘要。".to_owned()));
    }
    let download = Url::parse(&asset.browser_download_url).map_err(|_| invalid_release())?;
    if download.scheme() != "https" || download.host_str().is_none() || !download.username().is_empty()
        || download.password().is_some() || asset.size == 0 || asset.size > i64::MAX as u64 {
        return Err(invalid_release());
    }
    // The caller stores this string verbatim. Its contents belong exclusively to the Engine.
    let descriptor = json!({
        "schema": 1, "repository": repository, "tag": release.tag_name, "name": asset.name,
        "downloadUrl": download.as_str(), "size": asset.size, "sha256": digest.to_ascii_lowercase()
    }).to_string();
    result["update"] = json!({"version": release.tag_name, "notes": release.body.unwrap_or_default(),
        "descriptor": descriptor});
    Ok(result)
}

fn repository(value: &str) -> Option<String>
{
    let url = Url::parse(value).ok()?;
    if url.scheme() != "https" || url.host_str() != Some("github.com") || url.port().is_some()
        || !url.username().is_empty() || url.password().is_some() || url.query().is_some()
        || url.fragment().is_some() {
        return None;
    }
    let path = url.path().trim_matches('/');
    let parts: Vec<_> = path.split('/').collect();
    if parts.len() != 2 || parts.iter().any(|p| p.is_empty() || *p == "." || *p == "..") {
        return None;
    }
    if !parts[0].bytes().all(|c| c.is_ascii_alphanumeric() || c == b'_' || c == b'-')
        || !parts[1].bytes().all(|c| c.is_ascii_alphanumeric() || b"_.-".contains(&c)) {
        return None;
    }
    Some(path.to_owned())
}
