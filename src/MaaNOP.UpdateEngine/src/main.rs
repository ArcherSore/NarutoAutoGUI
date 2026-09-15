use maanop_update_engine::{MAX_MESSAGE_BYTES, MAX_RELEASE_BYTES, error, execute};
use std::fs::OpenOptions;
use std::io::{self, BufRead, Read, Write};
use std::time::Duration;

fn main()
{
    let mut line = String::new();
    let read = io::stdin().lock().take((MAX_MESSAGE_BYTES + 1) as u64).read_line(&mut line);
    let response = match read {
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
            let _ = writeln!(file, "{time} check: type={} code={} current={} target={}",
                response["type"], response["code"], response["currentVersion"], response["update"]["version"]);
        }
    }
    let mut output = io::stdout().lock();
    if writeln!(output, "{response}").and_then(|_| output.flush()).is_err() {
        std::process::exit(1);
    }
    std::process::exit(if failed { 1 } else { 0 });
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
