use maanop_update_engine::{MAX_MESSAGE_BYTES, MAX_RELEASE_BYTES, error, execute, prepare, handoff};
use std::fs::OpenOptions;
use std::io::{self, BufRead, Read, Write};
use std::time::Duration;
use std::sync::{Arc, atomic::{AtomicBool, Ordering}};
use serde_json::Value;

fn main()
{
    let mut line = String::new();
    let read = io::stdin().lock().take((MAX_MESSAGE_BYTES + 1) as u64).read_line(&mut line);
    let operation = serde_json::from_str::<Value>(&line).ok()
        .and_then(|v| v["operation"].as_str().map(str::to_owned)).unwrap_or_default();
    let response = match read {
        Ok(_) if line.ends_with('\n') && line.len() <= MAX_MESSAGE_BYTES && operation == "install" => {
            let args: Vec<_> = std::env::args().collect();
            let parent = if args.get(1).map(String::as_str) == Some("--install-copy") {
                args.get(2).and_then(|p| p.parse().ok())
            } else { None };
            handoff(&line, parent)
        }
        Ok(_) if line.ends_with('\n') && line.len() <= MAX_MESSAGE_BYTES && operation == "prepare" => {
            let cancelled = Arc::new(AtomicBool::new(false));
            let signal = cancelled.clone();
            std::thread::spawn(move || {
                let mut command = String::new();
                // EOF, malformed input and the one fixed cancel message all abandon preparation.
                let _ = io::stdin().lock().take((MAX_MESSAGE_BYTES + 1) as u64).read_line(&mut command);
                signal.store(true, Ordering::Relaxed);
            });
            prepare(&line, download, &cancelled, &mut emit)
        }
        Ok(_) if line.ends_with('\n') && line.len() <= MAX_MESSAGE_BYTES => execute(&line, fetch_release),
        _ => error("invalid_request", "更新命令无效或超出大小限制。"),
    };
    let failed = response["type"] == "error";
    if let Ok(request) = serde_json::from_str::<serde_json::Value>(&line)
        && let Some(installation) = request["installation"].as_str() {
        let path = std::path::Path::new(installation);
        let logs = path.join("logs");
        if path.is_absolute() && path.is_dir() && std::fs::create_dir_all(&logs).is_ok()
            && let Ok(mut file) = OpenOptions::new().create(true).append(true).open(logs.join("updater.log")) {
            let time = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default().as_secs();
            let _ = writeln!(file, "{time} {operation}: type={} code={} message={} current={} target={}",
                response["type"], response["code"], response["message"],
                response["currentVersion"], response["update"]["version"]);
        }
    }
    let mut output = io::stdout().lock();
    if writeln!(output, "{response}").and_then(|_| output.flush()).is_err() {
        std::process::exit(1);
    }
    std::process::exit(if failed { 1 } else { 0 });
}

fn emit(value: Value) -> Result<(), String>
{
    let mut output = io::stdout().lock();
    writeln!(output, "{value}").and_then(|_| output.flush()).map_err(|e| e.to_string())
}

fn download(url: &str) -> Result<Box<dyn Read>, String>
{
    let url = url.to_owned();
    let (sender, receiver) = std::sync::mpsc::sync_channel(2);
    std::thread::spawn(move || {
        let result = (|| -> Result<(), String> {
            let config = ureq::Agent::config_builder().timeout_global(Some(Duration::from_secs(3600)))
                .https_only(true).build();
            let agent: ureq::Agent = config.into();
            let response = agent.get(&url).header("User-Agent", "MaaNOP-UpdateEngine/2")
                .call().map_err(|e| e.to_string())?;
            let mut stream = response.into_body().into_reader();
            loop {
                let mut bytes = vec![0; 81920];
                let count = stream.read(&mut bytes).map_err(|e| e.to_string())?;
                bytes.truncate(count);
                sender.send(Ok(bytes)).map_err(|e| e.to_string())?;
                if count == 0 { return Ok(()); }
            }
        })();
        if let Err(message) = result { let _ = sender.send(Err(message)); }
    });
    Ok(Box::new(DownloadReader { receiver, pending: io::Cursor::new(Vec::new()), ended: false }))
}

struct DownloadReader
{
    receiver: std::sync::mpsc::Receiver<Result<Vec<u8>, String>>,
    pending: io::Cursor<Vec<u8>>,
    ended: bool,
}

impl Read for DownloadReader
{
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize>
    {
        if buffer.is_empty() || self.ended { return Ok(0); }
        if self.pending.position() == self.pending.get_ref().len() as u64 {
            let bytes = self.receiver.recv_timeout(Duration::from_secs(15))
                .map_err(io::Error::other)?.map_err(io::Error::other)?;
            self.ended = bytes.is_empty();
            self.pending = io::Cursor::new(bytes);
        }
        self.pending.read(buffer)
    }
}

fn fetch_release(url: &str) -> Result<String, String>
{
    let config = ureq::Agent::config_builder().timeout_global(Some(Duration::from_secs(30)))
        .https_only(true).build();
    let agent: ureq::Agent = config.into();
    let mut response = agent.get(url).header("User-Agent", "MaaNOP-UpdateEngine/2")
        .header("Accept", "application/vnd.github+json").header("X-GitHub-Api-Version", "2022-11-28")
        .call().map_err(|e| {
            eprintln!("Release request failed: {e}");
            e.to_string()
        })?;
    response.body_mut().with_config().limit(MAX_RELEASE_BYTES).read_to_string().map_err(|e| e.to_string())
}
