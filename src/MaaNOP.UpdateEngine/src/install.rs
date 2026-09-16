use crate::{MAX_MESSAGE_BYTES, error, files};
use serde::Deserialize;
use serde_json::{Value, json};
use std::fs;
use std::path::Path;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct Request
{
    pub protocol_version: u32,
    pub operation: String,
    pub installation: String,
    pub reference: String,
    pub gui_pid: u32,
}

pub(crate) fn preflight(request: &str) -> Result<Request, String>
{
    if request.len() > MAX_MESSAGE_BYTES { return Err("安装命令过大。".into()); }
    let request: Request = serde_json::from_str(request).map_err(|e| e.to_string())?;
    if request.protocol_version != 1 || request.operation != "install" || request.gui_pid == 0 {
        return Err("安装命令无效。".into());
    }
    let root = Path::new(&request.installation);
    files::installation(root)?;
    let work = root.join("cache/updater");
    files::ancestors(&work)?;
    files::tree(&work)?;
    if request.reference.is_empty()
        || fs::read_to_string(work.join("prepared")).map_err(|e| e.to_string())? != request.reference
        || !work.join("payload/NarutoAutoGUI.exe").is_file()
        || !work.join("payload/maanop-update-engine.exe").is_file()
        || work.join("old").exists() {
        return Err("已准备更新失效，请重新下载。".into());
    }
    // Verify writable workspace before handoff without modifying installed program files.
    let probe = work.join("write-probe");
    fs::write(&probe, []).and_then(|_| fs::remove_file(probe)).map_err(|e| e.to_string())?;
    let root_probe = root.join(format!(".updater-write-probe-{}", std::process::id()));
    let file = fs::OpenOptions::new().write(true).create_new(true).open(&root_probe).map_err(|e| e.to_string())?;
    drop(file);
    fs::remove_file(root_probe).map_err(|e| e.to_string())?;
    Ok(request)
}

/// Same install command boundary, with OS process operations substituted by filesystem tests.
pub fn install(
    request: &str, ready: impl FnOnce(Value) -> Result<(), String>,
    wait_gui: impl FnOnce(u32) -> Result<(), String>, relaunch: impl FnOnce(&Path) -> Result<(), String>,
    log: &mut dyn FnMut(&str),
) -> Value
{
    let request = match preflight(request) {
        Ok(request) => request,
        Err(message) => return error("install_preflight", &message),
    };
    run(&request, ready, wait_gui, relaunch, log, files::remove)
}

fn run(
    request: &Request, ready: impl FnOnce(Value) -> Result<(), String>,
    wait_gui: impl FnOnce(u32) -> Result<(), String>, relaunch: impl FnOnce(&Path) -> Result<(), String>,
    log: &mut dyn FnMut(&str), cleanup: impl Fn(&Path) -> Result<(), String>,
) -> Value
{
    if let Err(message) = ready(json!({"protocolVersion":1,"type":"ready","operation":"install"}))
        .and_then(|_| wait_gui(request.gui_pid)) {
        return error("install_preflight", &message);
    }
    let root = Path::new(&request.installation);
    let work = root.join("cache/updater");
    let old = work.join("old");
    let payload = work.join("payload");
    let replace = (|| -> Result<(), String> {
        files::installation(root)?;
        files::tree(&payload)?;
        fs::create_dir(&old).map_err(|e| e.to_string())?;
        fs::remove_file(work.join("prepared")).map_err(|e| e.to_string())?;
        for entry in fs::read_dir(root).map_err(|e| e.to_string())? {
            let entry = entry.map_err(|e| e.to_string())?;
            if !files::preserved(&entry.file_name().to_string_lossy()) {
                fs::rename(entry.path(), old.join(entry.file_name())).map_err(|e| e.to_string())?;
            }
        }
        // Every old managed entry has left the root before the first new entry is installed.
        for entry in fs::read_dir(&payload).map_err(|e| e.to_string())? {
            let entry = entry.map_err(|e| e.to_string())?;
            fs::rename(entry.path(), root.join(entry.file_name())).map_err(|e| e.to_string())?;
        }
        Ok(())
    })();
    if let Err(message) = replace {
        return error("install_failed", &format!("安装失败：{message}。请从项目 GitHub 重新下载完整包；\
            保留 config/logs/debug/cache 四个目录，清除其余程序内容后重新解压。不要从 old 恢复。"));
    }
    for path in [&old, &payload] {
        if let Err(message) = cleanup(path) { log(&format!("安装后清理失败：{message}")); }
    }
    if let Err(message) = relaunch(&root.join("NarutoAutoGUI.exe")) {
        return error("relaunch_failed", &format!("文件安装完成但启动失败：{message}。请手动启动程序。"));
    }
    json!({"protocolVersion":1,"type":"result","operation":"install"})
}

#[cfg(test)]
#[test]
fn cleanup_failure_does_not_prevent_relaunch()
{
    let root = std::env::temp_dir().join(format!("maanop-cleanup-failure-{}", std::process::id()));
    fs::create_dir_all(root.join("cache/updater/payload")).unwrap();
    fs::write(root.join("cache/updater/prepared"), "opaque").unwrap();
    fs::write(root.join("cache/updater/payload/NarutoAutoGUI.exe"), "new").unwrap();
    fs::write(root.join("cache/updater/payload/maanop-update-engine.exe"), "new").unwrap();
    let request = json!({"protocolVersion":1,"operation":"install","installation":root,
        "reference":"opaque","guiPid":123}).to_string();
    let mut logged = Vec::new();
    let result = run(&preflight(&request).unwrap(), |_| Ok(()), |_| Ok(()), |exe| {
        assert!(exe.exists());
        Ok(())
    }, &mut |message| logged.push(message.to_owned()), |_| Err("injected cleanup failure".into()));
    assert_eq!(result["type"], "result");
    assert_eq!(logged.len(), 2);
    fs::remove_dir_all(root).unwrap();
}

#[cfg(windows)]
pub fn handoff(request: &str, copy_parent: Option<u32>) -> Value
{
    use crate::native;
    use std::io::Write;
    if let Some(pid) = copy_parent {
        let parent = match native::Process::open(pid) {
            Ok(parent) => parent,
            Err(message) => return error("install_preflight", &message),
        };
        if let Err(message) = parent.wait(10000) { return error("install_preflight", &message); }
    }
    let parsed = match preflight(request) {
        Ok(parsed) => parsed,
        Err(message) => return error("install_preflight", &message),
    };
    let root = Path::new(&parsed.installation);
    if copy_parent.is_none() {
        return match native::copy_and_launch(request, root) {
            Ok(()) => std::process::exit(0),
            Err(message) => error("install_preflight", &message),
        };
    }
    let expected = root.join("cache/updater/run/maanop-update-engine.exe");
    if std::env::current_exe().ok().as_deref() != Some(expected.as_path()) {
        return error("install_preflight", "安装副本位置无效。");
    }
    let gui = match native::Process::open(parsed.gui_pid) {
        Ok(gui) => gui,
        Err(message) => return error("install_preflight", &message),
    };
    let logs = root.join("logs");
    let _ = fs::create_dir_all(&logs);
    let log_path = logs.join("updater.log");
    let mut log = |message: &str| {
        if let Ok(mut file) = fs::OpenOptions::new().create(true).append(true).open(&log_path) {
            let _ = writeln!(file, "install: {message}");
        }
    };
    let mut sent_ready = false;
    let response = run(&parsed, |value| {
        if !gui.running() { return Err("GUI 已在接管前退出。".into()); }
        let mut output = std::io::stdout().lock();
        writeln!(output, "{value}").and_then(|_| output.flush()).map_err(|e| e.to_string())?;
        sent_ready = true;
        Ok(())
    }, |_| gui.wait(30000), native::relaunch, &mut log, files::remove);
    if sent_ready && response["type"] == "error" {
        native::failure(&format!("{}\n日志：{}", response["message"].as_str().unwrap_or("安装失败"), log_path.display()));
    }
    response
}
