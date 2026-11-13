use std::path::Path;
use windows_sys::Win32::Foundation::{CloseHandle, HWND, LPARAM};
use windows_sys::Win32::System::Threading::{
    OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    EnumChildWindows, EnumWindows, GetWindowTextW, GetWindowThreadProcessId, PostMessageW, WM_CLOSE,
};

pub fn windows(executable: &Path) -> Vec<(HWND, String)>
{
    let mut context = (executable, Vec::new());
    unsafe { EnumWindows(Some(collect_window), &mut context as *mut _ as LPARAM); }
    context.1
}

unsafe extern "system" fn collect_window(hwnd: HWND, data: LPARAM) -> i32
{
    unsafe {
        let (executable, windows) = &mut *(data as *mut (&Path, Vec<(HWND, String)>));
        let mut pid = 0;
        GetWindowThreadProcessId(hwnd, &mut pid);
        let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if process.is_null() { return 1; }
        let mut path = [0u16; 32768];
        let mut length = path.len() as u32;
        let queried = QueryFullProcessImageNameW(process, 0, path.as_mut_ptr(), &mut length);
        CloseHandle(process);
        if queried != 0 && String::from_utf16_lossy(&path[..length as usize])
            .eq_ignore_ascii_case(&executable.to_string_lossy().replace('/', "\\")) {
            let mut text = title(hwnd);
            EnumChildWindows(hwnd, Some(collect_text), &mut text as *mut _ as LPARAM);
            windows.push((hwnd, text));
        }
        1
    }
}

unsafe extern "system" fn collect_text(hwnd: HWND, data: LPARAM) -> i32
{
    unsafe {
        let text = &mut *(data as *mut String);
        text.push('\n');
        text.push_str(&title(hwnd));
        1
    }
}

fn title(hwnd: HWND) -> String
{
    let mut text = [0u16; 4096];
    let length = unsafe { GetWindowTextW(hwnd, text.as_mut_ptr(), text.len() as i32) };
    String::from_utf16_lossy(&text[..length as usize])
}

pub fn dismiss(hwnd: HWND)
{
    assert_ne!(unsafe { PostMessageW(hwnd, WM_CLOSE, 0, 0) }, 0);
}
