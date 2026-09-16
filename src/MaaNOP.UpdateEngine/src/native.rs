use std::path::Path;
use std::process::{Command, Stdio};
use std::io::Write;
use std::fs;
use std::os::windows::process::CommandExt;
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT};
use windows_sys::Win32::System::Threading::{OpenProcess, WaitForSingleObject, PROCESS_SYNCHRONIZE};
use windows_sys::Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_OK, MB_ICONERROR, MB_SETFOREGROUND};

pub struct Process(HANDLE);

impl Process
{
    pub fn open(pid: u32) -> Result<Self, String>
    {
        let handle = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, 0, pid) };
        if handle.is_null() { return Err(format!("无法确认进程 {pid}：{}", std::io::Error::last_os_error())); }
        Ok(Self(handle))
    }

    pub fn running(&self) -> bool
    {
        unsafe { WaitForSingleObject(self.0, 0) == WAIT_TIMEOUT }
    }

    pub fn wait(&self, timeout: u32) -> Result<(), String>
    {
        if unsafe { WaitForSingleObject(self.0, timeout) } != WAIT_OBJECT_0 {
            return Err("等待进程退出超时或失败，未安装更新。".into());
        }
        Ok(())
    }
}

impl Drop for Process
{
    fn drop(&mut self)
    {
        unsafe { CloseHandle(self.0); }
    }
}

pub fn copy_and_launch(request: &str, root: &Path) -> Result<(), String>
{
    let run = root.join("cache/updater/run");
    fs::create_dir(&run).map_err(|e| e.to_string())?;
    let exe = run.join("maanop-update-engine.exe");
    fs::copy(std::env::current_exe().map_err(|e| e.to_string())?, &exe).map_err(|e| e.to_string())?;
    let mut child = Command::new(exe).arg("--install-copy").arg(std::process::id().to_string())
        .creation_flags(0x08000000) // CREATE_NO_WINDOW; normal installation is silent.
        .current_dir(run).stdin(Stdio::piped()).stdout(Stdio::inherit()).stderr(Stdio::null())
        .spawn().map_err(|e| e.to_string())?;
    child.stdin.take().unwrap().write_all(format!("{request}\n").as_bytes()).map_err(|e| e.to_string())
}

pub fn relaunch(executable: &Path) -> Result<(), String>
{
    Command::new(executable).current_dir(executable.parent().unwrap()).stdin(Stdio::null())
        .stdout(Stdio::null()).stderr(Stdio::null()).spawn().map(|_| ()).map_err(|e| e.to_string())
}

fn wide(value: &str) -> Vec<u16>
{
    value.encode_utf16().chain(Some(0)).collect()
}

pub fn failure(message: &str)
{
    unsafe {
        MessageBoxW(std::ptr::null_mut(), wide(message).as_ptr(), wide("MaaNOP 更新失败").as_ptr(),
            MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
    }
}
