use maanop_update_engine::install;
use serde_json::json;
use std::fs;
use std::path::{Path, PathBuf};

fn fixture(name: &str) -> PathBuf
{
    let root = std::env::temp_dir().join(format!("maanop-install-{name}-{}", std::process::id()));
    if root.exists() { fs::remove_dir_all(&root).unwrap(); }
    fs::create_dir_all(root.join("cache/updater/payload")).unwrap();
    for directory in ["config", "logs", "debug", "cache/other"] {
        fs::create_dir_all(root.join(directory)).unwrap();
        fs::write(root.join(directory).join("keep"), "user").unwrap();
    }
    fs::write(root.join("cache/updater/prepared"), "opaque").unwrap();
    fs::write(root.join("old-file"), "old").unwrap();
    fs::create_dir(root.join("state")).unwrap();
    fs::write(root.join("state/old"), "old").unwrap();
    fs::write(root.join("cache/updater/payload/NarutoAutoGUI.exe"), "new").unwrap();
    fs::write(root.join("cache/updater/payload/maanop-update-engine.exe"), "new").unwrap();
    root
}

fn request(root: &Path, pid: u32) -> String
{
    json!({"protocolVersion":1,"operation":"install","installation":root,"reference":"opaque",
        "guiPid":pid}).to_string()
}

#[test]
fn install_waits_then_replaces_all_managed_entries_and_cleans_before_relaunch()
{
    let root = fixture("success");
    let result = install(&request(&root, 123), |message| {
        assert_eq!(message["type"], "ready");
        assert!(root.join("old-file").exists());
        Ok(())
    }, |_| {
        assert!(!root.join("cache/updater/old").exists());
        Ok(())
    }, |exe| {
        assert_eq!(exe, root.join("NarutoAutoGUI.exe"));
        assert!(!root.join("old-file").exists());
        assert!(!root.join("state").exists());
        assert!(!root.join("cache/updater/old").exists());
        assert!(!root.join("cache/updater/payload").exists());
        for directory in ["config", "logs", "debug", "cache/other"] {
            assert_eq!(fs::read_to_string(root.join(directory).join("keep")).unwrap(), "user");
        }
        Ok(())
    }, &mut |_| {});
    assert_eq!(result["type"], "result", "{result}");
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn preflight_and_wait_failures_leave_programs_untouched()
{
    for mode in ["reference", "disconnect", "timeout", "preserved-file"] {
        let root = fixture(mode);
        if mode == "reference" { fs::write(root.join("cache/updater/prepared"), "wrong").unwrap(); }
        if mode == "preserved-file" {
            fs::remove_dir_all(root.join("config")).unwrap();
            fs::write(root.join("config"), "file").unwrap();
        }
        let result = install(&request(&root, 123), |_| {
            if mode == "disconnect" { Err("disconnected".into()) } else { Ok(()) }
        }, |_| Err("timeout".into()), |_| panic!("must not relaunch"), &mut |_| {});
        assert_eq!(result["code"], "install_preflight", "{result}");
        assert_eq!(fs::read_to_string(root.join("old-file")).unwrap(), "old");
        assert!(!root.join("NarutoAutoGUI.exe").exists());
        fs::remove_dir_all(root).unwrap();
    }
}

#[test]
fn relaunch_failure_does_not_undo_installed_files()
{
    let root = fixture("relaunch-failure");
    let result = install(&request(&root, 123), |_| Ok(()), |_| Ok(()),
        |_| Err("cannot create process".into()), &mut |_| {});
    assert_eq!(result["code"], "relaunch_failed");
    assert_eq!(fs::read_to_string(root.join("NarutoAutoGUI.exe")).unwrap(), "new");
    assert!(!root.join("old-file").exists());
    fs::remove_dir_all(root).unwrap();
}

#[cfg(windows)]
#[test]
fn move_failure_stops_without_relaunch_or_rollback()
{
    use std::os::windows::fs::OpenOptionsExt;
    let root = fixture("move-failure");
    let held = fs::OpenOptions::new().read(true).share_mode(0).open(root.join("old-file")).unwrap();
    let result = install(&request(&root, 123), |_| Ok(()), |_| Ok(()),
        |_| panic!("failed install must not relaunch"), &mut |_| {});
    assert_eq!(result["code"], "install_failed");
    assert!(!root.join("NarutoAutoGUI.exe").exists());
    assert!(root.join("cache/updater/old").exists());
    drop(held);
    fs::remove_dir_all(root).unwrap();
}

#[cfg(windows)]
#[test]
fn actual_copy_handoff_waits_for_gui_pid_and_updates_itself()
{
    use std::io::{BufRead, BufReader, Write};
    use std::process::{Command, Stdio};
    use std::time::{Duration, Instant};
    let root = fixture("process");
    let helper = root.join("cache/other/gui-fixture.exe");
    assert!(Command::new("rustc").arg("--edition=2024").arg("--crate-name=process_fixture")
        .arg(Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/support/process_fixture.rs"))
        .arg("-o").arg(&helper).status().unwrap().success());
    fs::copy(&helper, root.join("cache/updater/payload/NarutoAutoGUI.exe")).unwrap();
    fs::copy(env!("CARGO_BIN_EXE_maanop-update-engine"), root.join("maanop-update-engine.exe")).unwrap();
    let mut gui = Command::new(&helper).arg("--wait").spawn().unwrap();
    let mut engine = Command::new(root.join("maanop-update-engine.exe"))
        .stdin(Stdio::piped()).stdout(Stdio::piped()).spawn().unwrap();
    writeln!(engine.stdin.take().unwrap(), "{}", request(&root, gui.id())).unwrap();
    let mut output = BufReader::new(engine.stdout.take().unwrap());
    let mut message = String::new();
    output.read_line(&mut message).unwrap();
    assert_eq!(serde_json::from_str::<serde_json::Value>(&message).unwrap()["type"], "ready", "{message}");
    assert!(engine.wait().unwrap().success());
    assert_eq!(fs::read_to_string(root.join("old-file")).unwrap(), "old");
    assert!(!root.join("cache/updater/old").exists());
    drop(output); // GUI stdout may disappear after ready; the copy must continue independently.
    gui.kill().unwrap();
    gui.wait().unwrap();
    let clock = Instant::now();
    while !root.join("relaunched").exists() && clock.elapsed() < Duration::from_secs(10) {
        std::thread::sleep(Duration::from_millis(50));
    }
    assert!(root.join("relaunched").exists());
    assert_eq!(fs::read_to_string(root.join("maanop-update-engine.exe")).unwrap(), "new");
    assert!(!root.join("old-file").exists());
    // The running copy is allowed to remain; wait for its image to be released before test cleanup.
    for _ in 0..100 {
        if fs::remove_dir_all(&root).is_ok() { return; }
        std::thread::sleep(Duration::from_millis(50));
    }
    panic!("Engine did not exit after relaunch");
}

#[cfg(windows)]
#[test]
fn payload_write_failure_keeps_old_isolated_and_does_not_launch()
{
    use std::os::windows::fs::OpenOptionsExt;
    let root = fixture("write-failure");
    let held = fs::OpenOptions::new().read(true).share_mode(0)
        .open(root.join("cache/updater/payload/NarutoAutoGUI.exe")).unwrap();
    let result = install(&request(&root, 123), |_| Ok(()), |_| Ok(()),
        |_| panic!("partial install cannot launch"), &mut |_| {});
    assert_eq!(result["code"], "install_failed");
    assert!(!root.join("old-file").exists());
    assert!(root.join("cache/updater/old/old-file").exists());
    drop(held);
    fs::remove_dir_all(root).unwrap();
}

#[cfg(windows)]
#[test]
fn writable_cache_cannot_hide_root_permission_failure_before_ready()
{
    use std::process::Command;
    let root = fixture("root-acl");
    let path = root.to_str().unwrap().replace('\'', "''");
    let acl = format!(
        "$p='{path}'; $a=Get-Acl -LiteralPath $p; \
        $r=[System.Security.AccessControl.FileSystemAccessRule]::new(\
        [System.Security.Principal.WindowsIdentity]::GetCurrent().User,\
        [System.Security.AccessControl.FileSystemRights]::CreateFiles,\
        [System.Security.AccessControl.AccessControlType]::Deny); ");
    let apply = format!("{acl} $a.AddAccessRule($r); Set-Acl -LiteralPath $p -AclObject $a");
    assert!(Command::new("pwsh").args(["-NoProfile", "-Command", &apply]).status().unwrap().success());
    let result = install(&request(&root, 123), |_| panic!("unwritable root cannot become ready"),
        |_| Ok(()), |_| panic!("cannot launch"), &mut |_| {});
    let restore = format!("{acl} $a.RemoveAccessRuleSpecific($r); Set-Acl -LiteralPath $p -AclObject $a");
    assert!(Command::new("pwsh").args(["-NoProfile", "-Command", &restore]).status().unwrap().success());
    assert_eq!(result["code"], "install_preflight");
    assert!(root.join("old-file").exists());
    fs::remove_dir_all(root).unwrap();
}
