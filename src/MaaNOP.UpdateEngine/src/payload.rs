use crate::{MAX_MESSAGE_BYTES, Project, error, files, repository, version::Version};
use serde::Deserialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use std::fs::{self, File};
use std::io::{Read, Write, Seek, SeekFrom};
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use crate::download::{Download, interrupted};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request
{
    protocol_version: u32,
    operation: String,
    installation: String,
    descriptor: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Descriptor
{
    schema: u32,
    repository: String,
    tag: String,
    name: String,
    download_url: String,
    size: u64,
    sha256: String,
}

pub fn prepare(
    request: &str, download: impl FnOnce(Download<'_>) -> Result<(), String>,
    cancelled: &AtomicBool, emit: &mut dyn FnMut(Value) -> Result<(), String>,
) -> Value
{
    match run(request, download, cancelled, emit) {
        Ok(value) => value,
        Err(message) if cancelled.load(Ordering::Relaxed) => {
            json!({"protocolVersion":1,"type":"cancelled","operation":"prepare","message":message})
        }
        Err(message) => error("prepare_failed", &message),
    }
}

fn run(
    request: &str, download: impl FnOnce(Download<'_>) -> Result<(), String>,
    cancelled: &AtomicBool, emit: &mut dyn FnMut(Value) -> Result<(), String>,
) -> Result<Value, String>
{
    if request.len() > MAX_MESSAGE_BYTES { return Err("更新命令过大。".into()); }
    let request: Request = serde_json::from_str(request).map_err(|e| e.to_string())?;
    if request.protocol_version != 1 || request.operation != "prepare" { return Err("无效准备命令。".into()); }
    let descriptor: Descriptor = serde_json::from_str(&request.descriptor).map_err(|e| e.to_string())?;
    let root = Path::new(&request.installation);
    files::installation(root)?;
    let project: Project = serde_json::from_str(fs::read_to_string(root.join("interface.json"))
        .map_err(|e| e.to_string())?.trim_start_matches('\u{feff}')).map_err(|e| e.to_string())?;
    let url = url::Url::parse(&descriptor.download_url).map_err(|e| e.to_string())?;
    let target = Version::parse(&descriptor.tag).ok_or("候选版本无效。")?;
    if descriptor.schema != 1 || project.name != "MaaNOP"
        || repository(&project.github).as_deref() != Some(&descriptor.repository)
        || target <= Version::parse(&project.version).ok_or("当前版本无效。")?
        || descriptor.name != format!("MaaNOP-win-x86_64-{}.zip", descriptor.tag)
        || descriptor.size == 0 || descriptor.size > i64::MAX as u64
        || descriptor.sha256.len() != 64 || !descriptor.sha256.bytes().all(|b| b.is_ascii_hexdigit())
        || url.scheme() != "https" || url.host_str().is_none()
        || !url.username().is_empty() || url.password().is_some() {
        return Err("更新候选失效，请重新检查。".into());
    }
    interrupted(cancelled)?;
    let cache = root.join("cache");
    fs::create_dir_all(&cache).map_err(|e| e.to_string())?;
    files::ancestors(&cache)?;
    let lock_path = cache.join("updater.lock");
    if fs::symlink_metadata(&lock_path).is_ok() { files::reject_link(&lock_path)?; }
    let lock = File::options().write(true).create(true).truncate(false).open(lock_path)
        .map_err(|e| e.to_string())?;
    lock.try_lock().map_err(|_| "已有更新准备正在进行。".to_string())?;
    let work = cache.join("updater");
    // clean_preparation checks every entry it removes; only the kept download cache needs its own walk.
    if work.exists() { files::reject_link(&work)?; }
    fs::create_dir_all(&work).map_err(|e| e.to_string())?;
    clean_preparation(&work)?;
    let saved = work.join("download");
    if saved.exists() { files::tree(&saved)?; }
    let identity = json!([descriptor.repository, descriptor.tag, descriptor.name, descriptor.size,
        descriptor.sha256.to_ascii_lowercase()]).to_string();
    if saved.exists() && fs::read_to_string(saved.join("identity")).ok().as_deref() != Some(&identity) {
        files::remove(&saved)?;
    }
    if !saved.exists() {
        fs::create_dir(&saved).map_err(|e| e.to_string())?;
        // Write identity before any bytes; never truncate matching metadata during a resumed attempt.
        fs::write(saved.join("identity"), identity).map_err(|e| e.to_string())?;
    }
    let outcome = (|| {
        let zip_path = saved.join("package.part");
        let mut progress = |bytes, speed| emit(json!({"protocolVersion":1,"type":"progress",
            "phase":"download","bytes":bytes,"total":descriptor.size,"bytesPerSecond":speed}));
        download(Download { url: &descriptor.download_url, path: &zip_path, size: descriptor.size,
            cancelled, progress: &mut progress })?;
        interrupted(cancelled)?;
        let mut input = File::open(&zip_path).map_err(|e| e.to_string())?;
        let mut hash = Sha256::new();
        let mut buffer = [0u8; 81920];
        loop {
            interrupted(cancelled)?;
            let count = input.read(&mut buffer).map_err(|e| e.to_string())?;
            if count == 0 { break; }
            hash.update(&buffer[..count]);
        }
        let size = input.metadata().map_err(|e| e.to_string())?.len();
        drop(input);
        if size != descriptor.size || !format!("{:x}", hash.finalize()).eq_ignore_ascii_case(&descriptor.sha256) {
            files::remove(&saved)?;
            return Err("更新包长度或 SHA256 不符，请重新检查并下载。".into());
        }
        emit(json!({"protocolVersion":1,"type":"progress","phase":"validate"}))?;
        let payload = work.join("payload");
        extract(&zip_path, &payload, cancelled)?;
        validate(&payload, &descriptor.tag)?;
        interrupted(cancelled)?;
        files::remove(&saved)?;
        let token = format!("{}-{}", std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos());
        // Ephemeral capability, never read by GUI and never recovered on GUI startup.
        fs::write(work.join("prepared"), &token).map_err(|e| e.to_string())?;
        Ok(json!({"protocolVersion":1,"type":"result","operation":"prepare","reference":token}))
    })();
    if outcome.is_err() { let _ = clean_preparation(&work); }
    outcome
}

fn clean_preparation(work: &Path) -> Result<(), String>
{
    for entry in fs::read_dir(work).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        if entry.file_name() != "download" { files::remove(&entry.path())?; }
    }
    Ok(())
}

fn safe_part(part: &str) -> bool
{
    let stem = part.split('.').next().unwrap_or("").to_uppercase();
    let device = matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$")
        || ["COM", "LPT"].iter().any(|prefix| stem.strip_prefix(prefix)
            .is_some_and(|s| s.chars().count() == 1 && "0123456789¹²³".contains(s)));
    !part.is_empty() && part != "." && part != ".." && !part.ends_with(['.', ' ']) && !device
        && !part.chars().any(|c| c < ' ' || "<>:\"|?*".contains(c))
}

fn extract(zip: &Path, payload: &Path, cancelled: &AtomicBool) -> Result<(), String>
{
    let mut headers = File::open(zip).map_err(|e| e.to_string())?;
    let mut archive = zip::ZipArchive::new(File::open(zip).map_err(|e| e.to_string())?)
        .map_err(|e| e.to_string())?;
    headers.seek(SeekFrom::Start(archive.central_directory_start())).map_err(|e| e.to_string())?;
    let mut count = 0;
    loop {
        let mut signature = [0u8; 4];
        headers.read_exact(&mut signature).map_err(|e| e.to_string())?;
        if signature != *b"PK\x01\x02" { break; }
        let mut header = [0u8; 42];
        headers.read_exact(&mut header).map_err(|e| e.to_string())?;
        let length = [24, 26, 28].iter().map(|i| u16::from_le_bytes([header[*i], header[*i + 1]]) as i64)
            .sum::<i64>();
        headers.seek(SeekFrom::Current(length)).map_err(|e| e.to_string())?;
        count += 1;
    }
    // ZipArchive indexes by name and hides exact duplicates; reject those before using that index.
    if count != archive.len() { return Err("ZIP 含重复条目。".into()); }
    let mut seen: HashMap<String, bool> = HashMap::new();
    let mut entries = Vec::new();
    for index in 0..archive.len() {
        interrupted(cancelled)?;
        let file = archive.by_index(index).map_err(|e| e.to_string())?;
        let name = file.name().replace('\\', "/");
        let directory = name.ends_with('/');
        let relative = name.strip_suffix('/').unwrap_or(&name);
        let parts: Vec<_> = relative.split('/').collect();
        let kind = file.unix_mode().unwrap_or(0) & 0xf000;
        // DOS reparse attributes are independent of Unix mode in the central directory.
        headers.seek(SeekFrom::Start(file.central_header_start() + 38)).map_err(|e| e.to_string())?;
        let mut attributes = [0u8; 4];
        headers.read_exact(&mut attributes).map_err(|e| e.to_string())?;
        if parts.iter().any(|p| !safe_part(p)) || !matches!(kind, 0 | 0x8000 | 0x4000)
            || u32::from_le_bytes(attributes) & 0x400 != 0
            || (parts.len() == 1 && files::preserved(parts[0]) && !directory)
            || (kind == 0x4000 && !directory) || parts[0].eq_ignore_ascii_case("state")
            || seen.insert(relative.to_lowercase(), directory).is_some() {
            return Err("ZIP 含不安全路径、链接、运行态或重复条目。".into());
        }
        entries.push((relative.to_owned(), directory));
    }
    for (name, _) in &entries {
        let mut parent = name.as_str();
        while let Some((prefix, _)) = parent.rsplit_once('/') {
            if seen.get(&prefix.to_lowercase()) == Some(&false) { return Err("ZIP 文件/目录冲突。".into()); }
            parent = prefix;
        }
    }
    fs::create_dir(payload).map_err(|e| e.to_string())?;
    for (index, (name, directory)) in entries.iter().enumerate() {
        interrupted(cancelled)?;
        if files::preserved(name.split('/').next().unwrap()) { continue; }
        let destination = payload.join(name);
        if *directory {
            fs::create_dir_all(&destination).map_err(|e| e.to_string())?;
        } else {
            fs::create_dir_all(destination.parent().unwrap()).map_err(|e| e.to_string())?;
            let mut source = archive.by_index(index).map_err(|e| e.to_string())?;
            let mut output = File::create(&destination).map_err(|e| e.to_string())?;
            let mut buffer = [0u8; 81920];
            loop {
                interrupted(cancelled)?;
                let count = source.read(&mut buffer).map_err(|e| e.to_string())?;
                if count == 0 { break; }
                output.write_all(&buffer[..count]).map_err(|e| e.to_string())?;
            }
        }
    }
    Ok(())
}

fn validate(payload: &Path, tag: &str) -> Result<(), String>
{
    for file in ["NarutoAutoGUI.exe", "NarutoAutoGUI.dll", "maanop-update-engine.exe",
        "NarutoAutoGUI.deps.json", "NarutoAutoGUI.runtimeconfig.json",
        "libs/Wpf.Ui.dll", "libs/NarutoAutoGUI.Updates.dll", "worker/NarutoAutoWorker.exe",
        "worker/NarutoAutoWorker.dll",
        "worker/NarutoAutoWorker.deps.json", "worker/NarutoAutoWorker.runtimeconfig.json",
        "worker/runtimes/win-x64/native/MaaFramework.dll",
        "worker/runtimes/win-x64/native/MaaWin32ControlUnit.dll", "python/python.exe"] {
        if !payload.join(file).is_file() { return Err(format!("完整包缺少必需文件：{file}")); }
    }
    for directory in ["resource", "agent"] {
        if !payload.join(directory).is_dir() { return Err(format!("完整包缺少目录：{directory}")); }
    }
    let source = fs::read_to_string(payload.join("interface.json")).map_err(|e| e.to_string())?;
    let project: Project = serde_json::from_str(source.trim_start_matches('\u{feff}')).map_err(|e| e.to_string())?;
    if project.name != "MaaNOP" || Version::parse(&project.version).is_none()
        || Version::parse(&project.version) != Version::parse(tag) {
        return Err("完整包产品名或版本与候选不一致。".into());
    }
    let config = fs::read_to_string(payload.join("NarutoAutoGUI.runtimeconfig.json")).map_err(|e| e.to_string())?;
    let program_files = std::env::var_os("ProgramFiles").unwrap_or_else(|| "C:\\Program Files".into());
    require_frameworks(&config, &Path::new(&program_files).join("dotnet"))
}

// Installing cannot be undone, so a package this machine could not start is refused while the old version still
// works. Only framework-dependent packages list frameworks; a failure here keeps the download for the next attempt.
fn require_frameworks(runtimeconfig: &str, dotnet: &Path) -> Result<(), String>
{
    let config: Value = serde_json::from_str(runtimeconfig.trim_start_matches('\u{feff}')).map_err(|e| e.to_string())?;
    let Some(frameworks) = config["runtimeOptions"]["frameworks"].as_array() else { return Ok(()); };
    for framework in frameworks {
        let name = framework["name"].as_str().ok_or("完整包运行配置无效。")?;
        let version = framework["version"].as_str().ok_or("完整包运行配置无效。")?;
        let wanted = Version::parse(version).ok_or("完整包运行配置无效。")?;
        let major = version.split('.').next().unwrap_or_default();
        // The host rolls forward within a major version and does not pick a prerelease for a stable request.
        let installed = fs::read_dir(dotnet.join("shared").join(name)).into_iter().flatten().flatten().any(|entry| {
            let candidate = entry.file_name().to_string_lossy().into_owned();
            candidate.split('.').next() == Some(major) && Version::parse(&candidate).is_some_and(|v| v >= wanted)
        });
        if !installed {
            return Err(format!("新版本需要 .NET {major} 运行库（{name} {version}），这台电脑尚未安装。\
                请运行安装目录中的 DependencySetup_依赖库安装.bat，完成后重新下载更新，不会重复下载。"));
        }
    }
    Ok(())
}

#[cfg(test)]
#[test]
fn require_frameworks_follows_host_roll_forward()
{
    let dotnet = std::env::temp_dir().join(format!("maanop-frameworks-{}", std::process::id()));
    let config = |version: &str| json!({"runtimeOptions":{"frameworks":[
        {"name":"Microsoft.WindowsDesktop.App","version":version}]}}).to_string();
    assert!(require_frameworks(&config("10.0.0"), &dotnet).is_err(), "nothing installed");
    let shared = dotnet.join("shared/Microsoft.WindowsDesktop.App");
    fs::create_dir_all(shared.join("8.0.28")).unwrap();
    fs::create_dir_all(shared.join("10.0.0-rc.2.25502.107")).unwrap();
    assert!(require_frameworks(&config("10.0.0"), &dotnet).is_err(), "other major and prerelease do not count");
    fs::create_dir_all(shared.join("10.0.11")).unwrap();
    assert!(require_frameworks(&config("10.0.0"), &dotnet).is_ok());
    assert!(require_frameworks(&config("10.0.11"), &dotnet).is_ok());
    assert!(require_frameworks(&config("10.0.12"), &dotnet).is_err(), "older patch does not satisfy");
    assert!(require_frameworks(&config("11.0.0"), &dotnet).is_err());
    assert!(require_frameworks(r#"{"runtimeOptions":{"includedFrameworks":[]}}"#, &dotnet).is_ok(), "self-contained");
    assert!(require_frameworks("not json", &dotnet).is_err());
    fs::remove_dir_all(dotnet).unwrap();
}
