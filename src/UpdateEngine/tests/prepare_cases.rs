use update_engine::Download;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::io::{Cursor, Write};
use std::sync::atomic::{AtomicBool, Ordering};

#[test]
fn prepare_validates_payload_and_preserves_installation()
{
    let root = std::env::temp_dir().join(format!("prepare-{}", std::process::id()));
    std::fs::create_dir_all(root.join("config")).unwrap();
    std::fs::write(root.join("config/user"), "keep").unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"Sample","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    let bytes = package(&[]);
    let descriptor = json!({"schema":1,"repository":"test/product","tag":"v2.0.0",
        "name":"Sample-win-x86_64-v2.0.0.zip","downloadUrl":"https://example.com/package.zip",
        "size":bytes.len(),"sha256":format!("{:x}",Sha256::digest(&bytes))}).to_string();
    let request = json!({"protocolVersion":1,"operation":"prepare","installation":root,
        "descriptor":descriptor}).to_string();
    let mut messages: Vec<Value> = Vec::new();
    let result = prepare(&request, |_| Ok(Box::new(Cursor::new(bytes))),
        &AtomicBool::new(false), &mut |message| { messages.push(message); Ok(()) });
    assert_eq!(result["type"], "result", "{result}");
    assert!(result["reference"].is_string());
    assert!(messages.iter().any(|m| m["phase"] == "download"));
    assert_eq!(std::fs::read_to_string(root.join("config/user")).unwrap(), "keep");
    assert!(!root.join("config/default").exists());
    assert!(!root.join("cache/updater/download.zip").exists());
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn prepare_rejects_unsafe_corrupt_cancelled_and_incomplete_downloads()
{
    use std::sync::atomic::Ordering;
    let root = std::env::temp_dir().join(format!("prepare-bad-{}", std::process::id()));
    std::fs::create_dir_all(root.join("cache/other")).unwrap();
    std::fs::write(root.join("cache/other/keep"), "keep").unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"Sample","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    for name in ["../escape", "/absolute", "C:/escape", "config/../escape", "config/NUL.txt",
        "cache/COM¹.txt", "file.", "file ", "state/id", "config", "resource/a/child"] {
        let bytes = package(&[name]);
        let request = prepare_request(&root, &bytes);
        let result = prepare(&request, |_| Ok(Box::new(Cursor::new(bytes))),
            &AtomicBool::new(false), &mut |_| Ok(()));
        assert_eq!(result["type"], "error", "{name}: {result}");
        assert!(result["message"].as_str().unwrap().contains("ZIP"), "{result}");
        assert!(!root.join("cache/updater/prepared").exists());
        assert_eq!(std::fs::read_to_string(root.join("cache/other/keep")).unwrap(), "keep");
    }
    let bytes = vec![1, 2, 3];
    let request = prepare_request(&root, &bytes);
    for data in [vec![1, 2], vec![3, 2, 1], vec![1, 2, 3, 4]] {
        let result = prepare(&request, |_| Ok(Box::new(Cursor::new(data))),
            &AtomicBool::new(false), &mut |_| Ok(()));
        assert_eq!(result["type"], "error");
    }
    let signal = AtomicBool::new(false);
    let result = prepare(&request, |_| Ok(Box::new(Cursor::new(bytes))), &signal,
        &mut |_| { signal.store(true, Ordering::Relaxed); Ok(()) });
    assert_eq!(result["type"], "cancelled");
    assert!(!root.join("cache/updater/prepared").exists());
    std::fs::create_dir_all(root.join("cache/updater/run")).unwrap();
    std::fs::write(root.join("cache/updater/run/old"), "old").unwrap();
    let result = prepare(&request, |_| {
        assert!(!root.join("cache/updater/run").exists());
        Err("network failure".into())
    }, &AtomicBool::new(false), &mut |_| Ok(()));
    assert_eq!(result["type"], "error");
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        std::fs::create_dir_all(root.join("cache/updater")).unwrap();
        let held = std::fs::OpenOptions::new().write(true).create(true).truncate(true).share_mode(0)
            .open(root.join("cache/updater/held")).unwrap();
        let result = prepare(&request, |_| panic!("cleanup failure must precede network"),
            &AtomicBool::new(false), &mut |_| Ok(()));
        assert_eq!(result["type"], "error");
        drop(held);
    }
    std::fs::remove_dir_all(root).unwrap();
}

fn prepare_request(root: &std::path::Path, bytes: &[u8]) -> String
{
    let descriptor = json!({"schema":1,"repository":"test/product","tag":"v2.0.0",
        "name":"Sample-win-x86_64-v2.0.0.zip","downloadUrl":"https://example.com/fixed.zip",
        "size":bytes.len(),"sha256":format!("{:x}",Sha256::digest(bytes))}).to_string();
    json!({"protocolVersion":1,"operation":"prepare","installation":root,"descriptor":descriptor}).to_string()
}

fn package(extra: &[&str]) -> Vec<u8>
{
    package_without(extra, "")
}

fn package_without(extra: &[&str], omitted: &str) -> Vec<u8>
{
    package_with_config(extra, omitted, r#"{"runtimeOptions":{"includedFrameworks":[]}}"#)
}

// The fixture carries no .NET runtime files: their presence is the package's choice, not an Engine rule.
fn package_with_config(extra: &[&str], omitted: &str, runtimeconfig: &str) -> Vec<u8>
{
    package_for(("Bunshin", "update-engine", "Updates", "Bunshin.Worker"), extra, omitted, runtimeconfig)
}

// (launcher, engine, updates library, worker) names of one package layout.
fn package_for(layout: (&str, &str, &str, &str), extra: &[&str], omitted: &str, runtimeconfig: &str) -> Vec<u8>
{
    let (app, engine, updates, worker) = layout;
    let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
    let files = [format!("{app}.exe"), format!("{app}.dll"), format!("{engine}.exe"),
        format!("{app}.deps.json"), format!("{app}.runtimeconfig.json"),
        "libs/Wpf.Ui.dll".into(), format!("libs/{updates}.dll"),
        format!("worker/{worker}.exe"), format!("worker/{worker}.dll"),
        format!("worker/{worker}.deps.json"), format!("worker/{worker}.runtimeconfig.json"),
        "worker/runtimes/win-x64/native/MaaFramework.dll".into(),
        "worker/runtimes/win-x64/native/MaaWin32ControlUnit.dll".into(),
        "resource/a".into(), "config/default".into()];
    for file in files {
        if file == omitted { continue; }
        zip.start_file(&file, zip::write::SimpleFileOptions::default()).unwrap();
        let body = if file == format!("{app}.runtimeconfig.json") { runtimeconfig.as_bytes() } else { b"new" };
        zip.write_all(body).unwrap();
    }
    zip.start_file("interface.json", zip::write::SimpleFileOptions::default()).unwrap();
    zip.write_all(br#"{"name":"Sample","version":"2.0.0","github":"https://github.com/test/product"}"#).unwrap();
    for name in extra {
        zip.start_file(*name, zip::write::SimpleFileOptions::default()).unwrap();
        zip.write_all(b"bad").unwrap();
    }
    zip.finish().unwrap().into_inner()
}

#[test]
fn prepare_rejects_missing_gui_and_worker_startup_dependencies()
{
    let root = std::env::temp_dir().join(format!("prepare-runtime-{}", std::process::id()));
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"Sample","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    for omitted in ["Bunshin.deps.json", "Bunshin.runtimeconfig.json",
        "libs/Wpf.Ui.dll", "libs/Updates.dll",
        "worker/Bunshin.Worker.deps.json", "worker/Bunshin.Worker.runtimeconfig.json"] {
        let bytes = package_without(&[], omitted);
        let request = prepare_request(&root, &bytes);
        let result = prepare(&request, |_| Ok(Box::new(Cursor::new(bytes))),
            &AtomicBool::new(false), &mut |_| Ok(()));
        assert_eq!(result["type"], "error", "{omitted}: {result}");
        assert!(result["message"].as_str().unwrap().contains(omitted), "{result}");
        assert!(!root.join("cache/updater/prepared").exists());
    }
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn prepare_refuses_a_package_this_machine_cannot_start_and_keeps_the_download()
{
    let root = installation("runtime-missing");
    let bytes = package_with_config(&[], "", r#"{"runtimeOptions":{"frameworks":[
        {"name":"Microsoft.WindowsDesktop.App","version":"99.0.0"}]}}"#);
    let request = prepare_request(&root, &bytes);
    let result = prepare(&request, |_| Ok(Box::new(Cursor::new(bytes))), &AtomicBool::new(false), &mut |_| Ok(()));
    assert_eq!(result["type"], "error", "{result}");
    assert!(result["message"].as_str().unwrap().contains("DependencySetup"), "{result}");
    assert!(!root.join("cache/updater/prepared").exists());
    assert!(!root.join("cache/updater/payload").exists());
    // After installing the runtime, retrying must not download the package again.
    assert!(root.join("cache/updater/download/package.part").exists());
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn prepare_rejects_duplicate_entries_and_links_in_otherwise_complete_packages()
{
    let root = std::env::temp_dir().join(format!("zip-types-{}", std::process::id()));
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"Sample","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    for mode in ["duplicate", "case", "symlink", "reparse"] {
        let mut bytes = package(&["resource/b"]);
        if mode == "duplicate" || mode == "case" {
            let replacement = if mode == "duplicate" { b"resource/a" } else { b"RESOURCE/A" };
            for offset in 0..bytes.len() - 10 {
                if &bytes[offset..offset + 10] == b"resource/b" {
                    bytes[offset..offset + 10].copy_from_slice(replacement);
                }
            }
        } else {
            let archive = zip::ZipArchive::new(Cursor::new(&bytes)).unwrap();
            let offset = archive.central_directory_start() as usize;
            drop(archive);
            if mode == "symlink" {
                bytes[offset + 5] = 3;
                bytes[offset + 38..offset + 42].copy_from_slice(&(0xa000u32 << 16).to_le_bytes());
            } else {
                bytes[offset + 38..offset + 42].copy_from_slice(&0x400u32.to_le_bytes());
            }
        }
        let request = prepare_request(&root, &bytes);
        let result = prepare(&request, |_| Ok(Box::new(Cursor::new(bytes))),
            &AtomicBool::new(false), &mut |_| Ok(()));
        assert_eq!(result["type"], "error", "{mode}: {result}");
        assert!(result["message"].as_str().unwrap().contains("ZIP"), "{result}");
    }
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn prepare_process_abandons_work_when_gui_pipe_closes()
{
    use std::process::{Command, Stdio};
    let root = std::env::temp_dir().join(format!("prepare-eof-{}", std::process::id()));
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"Sample","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    let mut child = Command::new(env!("CARGO_BIN_EXE_update-engine"))
        .stdin(Stdio::piped()).stdout(Stdio::piped()).spawn().unwrap();
    writeln!(child.stdin.take().unwrap(), "{}", prepare_request(&root, &[1])).unwrap();
    let output = child.wait_with_output().unwrap();
    let lines = String::from_utf8(output.stdout).unwrap();
    assert!(lines.lines().any(|line| serde_json::from_str::<Value>(line).unwrap()["type"] == "cancelled"), "{lines}");
    assert!(!root.join("cache/updater/prepared").exists());
    std::fs::remove_dir_all(root).unwrap();
}

fn installation(name: &str) -> std::path::PathBuf
{
    let root = std::env::temp_dir().join(format!("resume-{name}-{}", std::process::id()));
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"Sample","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    root
}

// Existing package fixtures replace only transport; cache and validation use production prepare.
fn prepare(
    request: &str, source: impl FnOnce(&str) -> Result<Box<dyn std::io::Read>, String>,
    cancelled: &AtomicBool, emit: &mut dyn FnMut(Value) -> Result<(), String>,
) -> Value
{
    update_engine::prepare(request, |download: Download<'_>| {
        let mut input = source(download.url)?;
        (download.progress)(0, 0.0)?;
        if download.cancelled.load(Ordering::Relaxed) { return Err("cancelled".into()); }
        let mut output = std::fs::File::create(download.path).map_err(|e| e.to_string())?;
        std::io::copy(&mut input, &mut output).map_err(|e| e.to_string())?;
        Ok(())
    }, cancelled, emit)
}
#[test]
fn prepare_keeps_interrupted_bytes_and_reuses_only_the_same_candidate()
{
    let root = installation("cache");
    let bytes = package(&[]);
    let request = prepare_request(&root, &bytes);
    let part = root.join("cache/updater/download/package.part");
    for cancel in [false, true] {
        let signal = AtomicBool::new(false);
        let failed = update_engine::prepare(&request, |download| {
            std::fs::write(download.path, &bytes[..100]).unwrap();
            signal.store(cancel, Ordering::Relaxed);
            Err("interrupted".into())
        }, &signal, &mut |_| Ok(()));
        assert_eq!(failed["type"], if cancel { "cancelled" } else { "error" });
        assert_eq!(std::fs::read(&part).unwrap(), bytes[..100]);
        assert!(!root.join("cache/updater/prepared").exists());
        let resumed = update_engine::prepare(&request, |download| {
            assert_eq!(std::fs::read(download.path).unwrap(), bytes[..100]);
            let mut file = std::fs::OpenOptions::new().append(true).open(download.path).unwrap();
            file.write_all(&bytes[100..]).unwrap();
            Ok(())
        }, &AtomicBool::new(false), &mut |_| Ok(()));
        assert_eq!(resumed["type"], "result", "{resumed}");
        assert!(!part.exists());
    }
    // A changed SHA256 must discard partial bytes even when version/name/size stay the same.
    let _ = update_engine::prepare(&request, |download| {
        std::fs::write(download.path, b"old candidate").unwrap();
        Err("interrupted".into())
    }, &AtomicBool::new(false), &mut |_| Ok(()));
    let mut changed: Value = serde_json::from_str(&request).unwrap();
    let mut descriptor: Value = serde_json::from_str(changed["descriptor"].as_str().unwrap()).unwrap();
    descriptor["sha256"] = json!("0".repeat(64));
    changed["descriptor"] = json!(descriptor.to_string());
    let _ = update_engine::prepare(&changed.to_string(), |download| {
        assert!(!download.path.exists());
        Err("different candidate".into())
    }, &AtomicBool::new(false), &mut |_| Ok(()));
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn complete_cache_is_verified_and_corruption_is_discarded()
{
    let root = installation("hash");
    let bytes = package(&[]);
    let request = prepare_request(&root, &bytes);
    for corrupt in [true, false] {
        let _ = update_engine::prepare(&request, |download| {
            let mut cached = bytes.clone();
            if corrupt { cached[0] ^= 1; }
            std::fs::write(download.path, cached).unwrap();
            Err("process exited after final write".into())
        }, &AtomicBool::new(false), &mut |_| Ok(()));
        // download() must use the complete file without contacting example.com.
        let result = update_engine::prepare(&request, update_engine::download,
            &AtomicBool::new(false), &mut |_| Ok(()));
        assert_eq!(result["type"], if corrupt { "error" } else { "result" }, "{result}");
        if corrupt { assert!(result["message"].as_str().unwrap().contains("SHA256")); }
        assert!(!root.join("cache/updater/download").exists());
    }
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn concurrent_prepare_cannot_replace_an_active_download()
{
    let root = installation("lock");
    std::fs::create_dir_all(root.join("cache")).unwrap();
    let lock = std::fs::File::create(root.join("cache/updater.lock")).unwrap();
    lock.lock().unwrap();
    let result = update_engine::prepare(&prepare_request(&root, &package(&[])), |_| panic!("locked"),
        &AtomicBool::new(false), &mut |_| Ok(()));
    assert_eq!(result["type"], "error");
    assert!(result["message"].as_str().unwrap().contains("已有更新准备"));
    drop(lock);
    std::fs::remove_dir_all(root).unwrap();
}

// Installed copies from before the rename to Bunshin still receive packages of either layout.
#[test]
fn prepare_accepts_the_layout_from_before_the_rename()
{
    let root = std::env::temp_dir().join(format!("prepare-legacy-{}", std::process::id()));
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"Sample","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    let legacy = ("NarutoAutoGUI", "maanop-update-engine", "NarutoAutoGUI.Updates", "NarutoAutoWorker");
    let bytes = package_for(legacy, &[], "", r#"{"runtimeOptions":{"includedFrameworks":[]}}"#);
    let request = prepare_request(&root, &bytes);
    let result = prepare(&request, |_| Ok(Box::new(Cursor::new(bytes))),
        &AtomicBool::new(false), &mut |_| Ok(()));
    assert_eq!(result["type"], "result", "{result}");
    std::fs::remove_dir_all(root).unwrap();
}

// The engine updates the installed project and nothing else.
#[test]
fn prepare_rejects_a_package_of_another_project()
{
    let root = std::env::temp_dir().join(format!("prepare-other-{}", std::process::id()));
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"Other","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    let bytes = package(&[]);
    let descriptor = json!({"schema":1,"repository":"test/product","tag":"v2.0.0",
        "name":"Other-win-x86_64-v2.0.0.zip","downloadUrl":"https://example.com/package.zip",
        "size":bytes.len(),"sha256":format!("{:x}",Sha256::digest(&bytes))}).to_string();
    let request = json!({"protocolVersion":1,"operation":"prepare","installation":root,
        "descriptor":descriptor}).to_string();
    let result = prepare(&request, |_| Ok(Box::new(Cursor::new(bytes))),
        &AtomicBool::new(false), &mut |_| Ok(()));
    assert_eq!(result["type"], "error", "{result}");
    assert!(result["message"].as_str().unwrap().contains("产品名"), "{result}");
    assert!(!root.join("cache/updater/prepared").exists());
    std::fs::remove_dir_all(root).unwrap();
}
