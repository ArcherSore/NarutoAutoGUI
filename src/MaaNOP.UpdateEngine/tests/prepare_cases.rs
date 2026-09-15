use maanop_update_engine::prepare;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::io::{Cursor, Write};
use std::sync::atomic::AtomicBool;

#[test]
fn prepare_validates_payload_and_preserves_installation()
{
    let root = std::env::temp_dir().join(format!("maanop-prepare-{}", std::process::id()));
    std::fs::create_dir_all(root.join("config")).unwrap();
    std::fs::write(root.join("config/user"), "keep").unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"MaaNOP","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    let bytes = package(&[]);
    let descriptor = json!({"schema":1,"repository":"test/product","tag":"v2.0.0",
        "name":"MaaNOP-win-x86_64-v2.0.0.zip","downloadUrl":"https://example.com/package.zip",
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
    let root = std::env::temp_dir().join(format!("maanop-prepare-bad-{}", std::process::id()));
    std::fs::create_dir_all(root.join("cache/other")).unwrap();
    std::fs::write(root.join("cache/other/keep"), "keep").unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"MaaNOP","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
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
    assert!(!root.join("cache/updater").exists());
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
        "name":"MaaNOP-win-x86_64-v2.0.0.zip","downloadUrl":"https://example.com/fixed.zip",
        "size":bytes.len(),"sha256":format!("{:x}",Sha256::digest(bytes))}).to_string();
    json!({"protocolVersion":1,"operation":"prepare","installation":root,"descriptor":descriptor}).to_string()
}

fn package(extra: &[&str]) -> Vec<u8>
{
    package_without(extra, "")
}

fn package_without(extra: &[&str], omitted: &str) -> Vec<u8>
{
    let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
    let files = ["NarutoAutoGUI.exe", "NarutoAutoGUI.dll", "maanop-update-engine.exe",
        "NarutoAutoGUI.deps.json", "NarutoAutoGUI.runtimeconfig.json",
        "hostfxr.dll", "hostpolicy.dll", "libs/coreclr.dll", "libs/System.Private.CoreLib.dll",
        "libs/PresentationFramework.dll", "libs/Wpf.Ui.dll", "libs/NarutoAutoGUI.Updates.dll",
        "worker/NarutoAutoWorker.exe", "worker/NarutoAutoWorker.dll",
        "worker/NarutoAutoWorker.deps.json", "worker/NarutoAutoWorker.runtimeconfig.json",
        "worker/hostfxr.dll", "worker/hostpolicy.dll", "worker/coreclr.dll", "worker/System.Private.CoreLib.dll",
        "worker/runtimes/win-x64/native/MaaFramework.dll",
        "worker/runtimes/win-x64/native/MaaWin32ControlUnit.dll", "python/python.exe",
        "resource/a", "agent/a", "config/default"];
    for file in files {
        if file == omitted { continue; }
        zip.start_file(file, zip::write::SimpleFileOptions::default()).unwrap();
        zip.write_all(b"new").unwrap();
    }
    zip.start_file("interface.json", zip::write::SimpleFileOptions::default()).unwrap();
    zip.write_all(br#"{"name":"MaaNOP","version":"2.0.0","github":"https://github.com/test/product"}"#).unwrap();
    for name in extra {
        zip.start_file(*name, zip::write::SimpleFileOptions::default()).unwrap();
        zip.write_all(b"bad").unwrap();
    }
    zip.finish().unwrap().into_inner()
}

#[test]
fn prepare_rejects_missing_gui_and_worker_startup_dependencies()
{
    let root = std::env::temp_dir().join(format!("maanop-prepare-runtime-{}", std::process::id()));
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"MaaNOP","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    for omitted in ["NarutoAutoGUI.deps.json", "NarutoAutoGUI.runtimeconfig.json",
        "libs/PresentationFramework.dll", "libs/Wpf.Ui.dll", "libs/NarutoAutoGUI.Updates.dll",
        "worker/NarutoAutoWorker.deps.json", "worker/NarutoAutoWorker.runtimeconfig.json",
        "worker/hostfxr.dll", "worker/hostpolicy.dll", "worker/coreclr.dll", "worker/System.Private.CoreLib.dll"] {
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
fn prepare_rejects_duplicate_entries_and_links_in_otherwise_complete_packages()
{
    let root = std::env::temp_dir().join(format!("maanop-zip-types-{}", std::process::id()));
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"MaaNOP","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
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
    let root = std::env::temp_dir().join(format!("maanop-prepare-eof-{}", std::process::id()));
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("interface.json"),
        r#"{"name":"MaaNOP","version":"1.0.0","github":"https://github.com/test/product"}"#).unwrap();
    let mut child = Command::new(env!("CARGO_BIN_EXE_maanop-update-engine"))
        .stdin(Stdio::piped()).stdout(Stdio::piped()).spawn().unwrap();
    writeln!(child.stdin.take().unwrap(), "{}", prepare_request(&root, &[1])).unwrap();
    let output = child.wait_with_output().unwrap();
    let lines = String::from_utf8(output.stdout).unwrap();
    assert!(lines.lines().any(|line| serde_json::from_str::<Value>(line).unwrap()["type"] == "cancelled"), "{lines}");
    assert!(!root.join("cache/updater/prepared").exists());
    std::fs::remove_dir_all(root).unwrap();
}
