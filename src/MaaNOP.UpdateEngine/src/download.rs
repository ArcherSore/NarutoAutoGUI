use curl::easy::{Easy2, Handler, List, SslOpt, WriteError};
use std::fs::{File, OpenOptions};
use std::io::{Seek, SeekFrom, Write};
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};
use url::Url;

pub struct Download<'a>
{
    pub url: &'a str,
    pub path: &'a Path,
    pub size: u64,
    pub cancelled: &'a AtomicBool,
    pub progress: &'a mut dyn FnMut(u64, f64) -> Result<(), String>,
}

pub fn download(request: Download<'_>) -> Result<(), String>
{
    run(request, Duration::from_secs(1), Duration::from_secs(3600), Duration::from_secs(15), false)
}

fn run(
    mut request: Download<'_>, retry_delay: Duration, budget: Duration, stall: Duration, allow_http: bool,
) -> Result<(), String>
{
    let url = Url::parse(request.url).map_err(|e| e.to_string())?;
    if !allowed(&url, allow_http) { return Err("更新下载只允许 HTTPS。".into()); }
    let mut file = OpenOptions::new().create(true).truncate(false).read(true).write(true)
        .open(request.path).map_err(|e| e.to_string())?;
    if file.metadata().map_err(|e| e.to_string())?.len() > request.size {
        file.set_len(0).map_err(|e| e.to_string())?;
    }
    let started = Instant::now();
    let mut retries = 0;
    loop {
        interrupted(request.cancelled)?;
        let remaining = budget.checked_sub(started.elapsed()).filter(|d| !d.is_zero())
            .ok_or("更新下载超时，请重试。")?;
        let offset = file.seek(SeekFrom::End(0)).map_err(|e| e.to_string())?;
        (request.progress)(offset, 0.0)?;
        interrupted(request.cancelled)?;
        if offset == request.size { return Ok(()); }
        let target = request.url;
        let mut easy = Easy2::new(Transfer { file: &mut file, request: &mut request, base: &url, offset,
            bytes: offset, status: 0, range_ok: false, resume_rejected: false, error: None,
            started: Instant::now(), last: Instant::now() - Duration::from_millis(100), allow_http });
        configure(&mut easy, target, remaining, stall, offset).map_err(|e| e.to_string())?;
        let result = easy.perform();
        let status = easy.response_code().map_err(|e| e.to_string())?;
        let handler = easy.get_mut();
        handler.file.flush().map_err(|e| e.to_string())?;
        interrupted(handler.request.cancelled)?;
        // A server may ignore Range, reject the offset or return another range. Never append its response;
        // stop and clear the cache so the next prepare starts from zero instead of failing the same way.
        if handler.resume_rejected || (offset > 0 && (status == 200 || status == 416
            || result.as_ref().is_err_and(|e| e.is_range_error()))) {
            drop(easy);
            file.set_len(0).map_err(|e| e.to_string())?;
            return Err("更新服务器不支持续传，已清除下载缓存，请重新准备。".into());
        }
        if let Some(message) = handler.error.take() { return Err(message); }
        let bytes = handler.bytes;
        // Transfer::write already reported the final progress when bytes reached size.
        if result.is_ok() && bytes == handler.request.size { return Ok(()); }
        drop(easy);
        let transient = matches!(status, 408 | 429 | 500 | 502 | 503 | 504)
            || result.as_ref().is_err_and(|e| e.is_couldnt_resolve_proxy() || e.is_couldnt_resolve_host()
                || e.is_couldnt_connect() || e.is_ssl_connect_error() || e.is_partial_file()
                || e.is_operation_timedout() || e.is_send_error() || e.is_recv_error() || e.is_got_nothing())
            || (result.is_ok() && (status == 200 || status == 206) && bytes < request.size);
        if !transient || retries == 5 {
            return Err(format!("更新下载失败（HTTP {status}）：{}", result.err()
                .map(|e| e.to_string()).unwrap_or_else(|| "更新包长度不符。".into())));
        }
        let delay = retry_delay.saturating_mul(1 << retries);
        retries += 1;
        let until = Instant::now() + delay.min(budget.saturating_sub(started.elapsed()));
        while Instant::now() < until {
            interrupted(request.cancelled)?;
            std::thread::sleep(Duration::from_millis(50).min(until.saturating_duration_since(Instant::now())));
        }
    }
}

pub(crate) fn interrupted(cancelled: &AtomicBool) -> Result<(), String>
{
    if cancelled.load(Ordering::Relaxed) { Err("已取消更新准备。".into()) } else { Ok(()) }
}

fn allowed(url: &Url, allow_http: bool) -> bool
{
    url.scheme() == "https" || (allow_http && url.scheme() == "http")
}

fn status_code(line: &str) -> u32
{
    line.split_whitespace().nth(1).and_then(|s| s.parse().ok()).unwrap_or(0)
}

/// The binding lacks protocol setters. Reject unsafe redirects before libcurl follows them.
fn unsafe_redirect(base: &Url, allow_http: bool, status: u32, name: &str, value: &str) -> bool
{
    name.eq_ignore_ascii_case("Location") && (300..400).contains(&status)
        && !base.join(value).is_ok_and(|target| allowed(&target, allow_http))
}

/// Options shared by check and download.
fn common<H: Handler>(easy: &mut Easy2<H>, url: &str, timeout: Duration) -> Result<(), curl::Error>
{
    easy.url(url)?;
    easy.useragent(crate::USER_AGENT)?;
    easy.follow_location(true)?;
    easy.max_redirections(10)?;
    easy.fail_on_error(true)?;
    easy.timeout(timeout)?;
    // Schannel fails when revocation servers are unreachable; keep chain and host checks, tolerate only that.
    easy.ssl_options(SslOpt::new().revoke_best_effort(true))
}

fn configure(
    easy: &mut Easy2<Transfer<'_, '_>>, url: &str, timeout: Duration, stall: Duration, offset: u64,
) -> Result<(), curl::Error>
{
    common(easy, url, timeout)?;
    easy.resume_from(offset)?;
    easy.connect_timeout(Duration::from_secs(15).min(timeout))?;
    easy.low_speed_limit(1)?;
    easy.low_speed_time(stall)?;
    easy.http_content_decoding(false)?;
    easy.progress(true)
}

/// GET a small text resource, such as check metadata, under the same TLS and redirect rules as downloads.
pub fn fetch(url: &str, headers: &[&str], limit: u64, timeout: Duration) -> Result<String, String>
{
    fetch_with(url, headers, limit, timeout, false)
}

fn fetch_with(url: &str, headers: &[&str], limit: u64, timeout: Duration, allow_http: bool) -> Result<String, String>
{
    let base = Url::parse(url).map_err(|e| e.to_string())?;
    if !allowed(&base, allow_http) { return Err("更新检查只允许 HTTPS。".into()); }
    let mut list = List::new();
    for header in headers { list.append(header).map_err(|e| e.to_string())?; }
    let mut easy = Easy2::new(Fetch { base, allow_http, limit, status: 0, body: Vec::new(), error: None });
    let configured = (|| {
        common(&mut easy, url, timeout)?;
        easy.http_headers(list)?;
        easy.accept_encoding("")
    })();
    configured.map_err(|e| e.to_string())?;
    let result = easy.perform();
    let fetch = easy.get_mut();
    if let Some(message) = fetch.error.take() { return Err(message); }
    result.map_err(|e| e.to_string())?;
    String::from_utf8(std::mem::take(&mut fetch.body)).map_err(|e| e.to_string())
}

struct Fetch
{
    base: Url,
    allow_http: bool,
    limit: u64,
    status: u32,
    body: Vec<u8>,
    error: Option<String>,
}

impl Handler for Fetch
{
    fn header(&mut self, data: &[u8]) -> bool
    {
        let line = String::from_utf8_lossy(data);
        if line.starts_with("HTTP/") {
            self.status = status_code(&line);
        } else if let Some((name, value)) = line.split_once(':')
            && unsafe_redirect(&self.base, self.allow_http, self.status, name, value.trim()) {
            self.error = Some("更新检查重定向必须使用 HTTPS。".into());
            return false;
        }
        true
    }

    fn write(&mut self, data: &[u8]) -> Result<usize, WriteError>
    {
        if (300..400).contains(&self.status) { return Ok(data.len()); }
        if (self.body.len() + data.len()) as u64 > self.limit {
            self.error = Some("更新信息超出大小限制。".into());
            return Ok(0);
        }
        self.body.extend_from_slice(data);
        Ok(data.len())
    }
}

struct Transfer<'a, 'b>
{
    file: &'a mut File,
    request: &'a mut Download<'b>,
    base: &'a Url,
    offset: u64,
    bytes: u64,
    status: u32,
    range_ok: bool,
    resume_rejected: bool,
    error: Option<String>,
    started: Instant,
    last: Instant,
    allow_http: bool,
}

impl Handler for Transfer<'_, '_>
{
    fn header(&mut self, data: &[u8]) -> bool
    {
        let line = String::from_utf8_lossy(data);
        if line.starts_with("HTTP/") {
            self.status = status_code(&line);
            self.range_ok = false;
        } else if let Some((name, value)) = line.split_once(':') {
            let value = value.trim();
            if unsafe_redirect(self.base, self.allow_http, self.status, name, value) {
                self.error = Some("更新下载重定向必须使用 HTTPS。".into());
                return false;
            } else if name.eq_ignore_ascii_case("Content-Range") && self.status == 206 {
                self.range_ok = value == format!("bytes {}-{}/{}", self.offset,
                    self.request.size - 1, self.request.size);
            } else if name.eq_ignore_ascii_case("Content-Encoding") && (200..300).contains(&self.status)
                && !value.eq_ignore_ascii_case("identity") {
                self.error = Some("更新包使用了不支持的传输编码。".into());
                return false;
            }
        }
        true
    }

    fn write(&mut self, data: &[u8]) -> Result<usize, WriteError>
    {
        if (300..400).contains(&self.status) { return Ok(data.len()); }
        if (self.status == 200 && self.offset > 0) || (self.status == 206 && !self.range_ok) {
            self.resume_rejected = true;
            return Ok(0);
        }
        if self.status != 200 && self.status != 206 {
            self.error = Some("更新下载返回了无效的响应。".into());
            return Ok(0);
        }
        if self.request.cancelled.load(Ordering::Relaxed) { return Ok(0); }
        match self.store(data) {
            Ok(()) => Ok(data.len()),
            Err(error) => { self.error = Some(error); Ok(0) }
        }
    }

    fn progress(&mut self, _: f64, _: f64, _: f64, _: f64) -> bool
    {
        !self.request.cancelled.load(Ordering::Relaxed)
    }
}

impl Transfer<'_, '_>
{
    fn store(&mut self, data: &[u8]) -> Result<(), String>
    {
        if data.len() as u64 > self.request.size.saturating_sub(self.bytes) { return Err("更新包长度不符。".into()); }
        self.file.write_all(data).map_err(|e| e.to_string())?;
        self.bytes += data.len() as u64;
        if self.last.elapsed() >= Duration::from_millis(100) || self.bytes == self.request.size {
            let speed = (self.bytes - self.offset) as f64 / self.started.elapsed().as_secs_f64();
            (self.request.progress)(self.bytes, speed)?;
            self.last = Instant::now();
        }
        Ok(())
    }
}
#[cfg(test)]
#[path = "download_tests.rs"]
mod tests;
