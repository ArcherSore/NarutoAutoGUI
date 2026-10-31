use super::*;
use std::io::{Read, BufRead, BufReader};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::AtomicUsize;
use std::thread::{self, JoinHandle};

static NEXT: AtomicUsize = AtomicUsize::new(0);
const STALL: Duration = Duration::from_secs(1);

struct Fixture(std::path::PathBuf);

impl Fixture
{
    fn new() -> Self
    {
        let path = std::env::temp_dir().join(format!("maanop-curl-{}-{}", std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)));
        std::fs::create_dir(&path).unwrap();
        Self(path.join("package.part"))
    }

    fn run(&self, url: &str, size: u64) -> Result<(), String>
    {
        run(Download { url, path: &self.0, size, cancelled: &AtomicBool::new(false), progress: &mut |_, _| Ok(()) },
            Duration::ZERO, Duration::from_secs(5), STALL, true)
    }
}

impl Drop for Fixture
{
    fn drop(&mut self)
    {
        std::fs::remove_dir_all(self.0.parent().unwrap()).unwrap();
    }
}

fn listen() -> (TcpListener, u16)
{
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let port = listener.local_addr().unwrap().port();
    (listener, port)
}

fn accept_raw(listener: &TcpListener) -> TcpStream
{
    let deadline = Instant::now() + Duration::from_secs(10);
    let stream = loop {
        match listener.accept() {
            Ok((stream, _)) => break stream,
            Err(e) if e.kind() == std::io::ErrorKind::WouldBlock && Instant::now() < deadline => {
                thread::sleep(Duration::from_millis(5));
            }
            Err(e) => panic!("accept failed: {e}"),
        }
    };
    stream.set_nonblocking(false).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    stream
}

fn accept(listener: &TcpListener, offset: Option<u64>) -> TcpStream
{
    let stream = accept_raw(listener);
    let mut request = String::new();
    let mut reader = BufReader::new(&stream);
    loop {
        let mut line = String::new();
        assert!(reader.read_line(&mut line).unwrap() > 0);
        request.push_str(&line);
        if line == "\r\n" { break; }
    }
    let range = request.lines().find(|line| line.to_ascii_lowercase().starts_with("range:"));
    assert_eq!(range.map(|s| s.trim().to_ascii_lowercase()), offset.map(|n| format!("range: bytes={n}-")));
    stream
}

fn serve(replies: Vec<(Option<u64>, String)>) -> (String, JoinHandle<Vec<Instant>>)
{
    let (listener, port) = listen();
    let task = thread::spawn(move || replies.into_iter().map(|(offset, response)| {
        let mut stream = accept(&listener, offset);
        let accepted = Instant::now();
        stream.write_all(response.as_bytes()).unwrap();
        accepted
    }).collect());
    (format!("http://127.0.0.1:{port}/package"), task)
}

fn full(body: &str) -> String
{
    truncated(body, body.len())
}

fn truncated(body: &str, length: usize) -> String
{
    format!("HTTP/1.1 200 OK\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n{body}")
}

fn status(code: u16) -> String
{
    format!("HTTP/1.1 {code} Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
}

fn partial(body: &str, start: u64, total: u64) -> String
{
    format!("HTTP/1.1 206 Partial Content\r\nContent-Length: {}\r\nContent-Range: bytes {start}-{}/{}\r\n\
        Connection: close\r\n\r\n{body}", body.len(), total - 1, total)
}

#[test]
fn dropped_body_retries_from_written_offset()
{
    let fixture = Fixture::new();
    let (url, server) = serve(vec![(None, truncated("abc", 10)),
        (Some(3), partial("defghij", 3, 10))]);
    fixture.run(&url, 10).unwrap();
    server.join().unwrap();
    assert_eq!(std::fs::read(&fixture.0).unwrap(), b"abcdefghij");
}

#[test]
fn stalled_body_times_out_and_resumes_from_written_offset()
{
    let fixture = Fixture::new();
    let (listener, port) = listen();
    let server = thread::spawn(move || {
        // Keep the first connection open without sending the rest, so only the low-speed limit can end it.
        let mut stalled = accept(&listener, None);
        stalled.write_all(truncated("abc", 10).as_bytes()).unwrap();
        accept(&listener, Some(3)).write_all(partial("defghij", 3, 10).as_bytes()).unwrap();
        drop(stalled);
    });
    let started = Instant::now();
    fixture.run(&format!("http://127.0.0.1:{port}/package"), 10).unwrap();
    assert!(started.elapsed() >= STALL);
    server.join().unwrap();
    assert_eq!(std::fs::read(&fixture.0).unwrap(), b"abcdefghij");
}

#[test]
fn transient_failures_retry_five_times_with_doubling_delays()
{
    let fixture = Fixture::new();
    std::fs::write(&fixture.0, b"abc").unwrap();
    let (url, server) = serve(vec![(Some(3), status(503)); 6]);
    let base = Duration::from_millis(50);
    assert!(run(Download { url: &url, path: &fixture.0, size: 10, cancelled: &AtomicBool::new(false),
        progress: &mut |_, _| Ok(()) }, base, Duration::from_secs(10), STALL, true).is_err());
    let accepted = server.join().unwrap();
    let gaps: Vec<_> = accepted.windows(2).map(|pair| pair[1] - pair[0]).collect();
    // Each wait is at least base * 2^n; strictly growing gaps rule out a constant delay.
    assert!(gaps.iter().enumerate().all(|(n, gap)| *gap >= base * (1 << n)), "{gaps:?}");
    assert!(gaps.windows(2).all(|pair| pair[1] > pair[0]), "{gaps:?}");
}

#[test]
fn unsupported_resume_stops_and_clears_cache()
{
    for reply in [full("abcdefghij"), "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\n\r\n".into(),
        partial("defghij", 2, 10), partial("defghij", 3, 11)] {
        let fixture = Fixture::new();
        std::fs::write(&fixture.0, b"abc").unwrap();
        let (url, server) = serve(vec![(Some(3), reply)]);
        let error = fixture.run(&url, 10).unwrap_err();
        assert!(error.contains("不支持续传"), "{error}");
        server.join().unwrap();
        assert!(std::fs::read(&fixture.0).unwrap().is_empty());
    }
}

#[test]
fn encoded_response_does_not_corrupt_cache()
{
    let fixture = Fixture::new();
    std::fs::write(&fixture.0, b"abc").unwrap();
    let reply = partial("defghij", 3, 10).replace("Connection: close", "Content-Encoding: gzip");
    let (url, server) = serve(vec![(Some(3), reply)]);
    assert!(fixture.run(&url, 10).is_err());
    server.join().unwrap();
    assert_eq!(std::fs::read(&fixture.0).unwrap(), b"abc");
}

#[test]
fn retry_exhaustion_preserves_bytes_and_404_is_not_retried()
{
    for statuses in [vec![503; 6], vec![404]] {
        let fixture = Fixture::new();
        std::fs::write(&fixture.0, b"abc").unwrap();
        let (url, server) = serve(statuses.into_iter().map(|code| (Some(3), status(code))).collect());
        assert!(fixture.run(&url, 10).is_err());
        server.join().unwrap();
        assert_eq!(std::fs::read(&fixture.0).unwrap(), b"abc");
    }
}

#[test]
fn cancellation_keeps_bytes_and_a_new_process_resumes()
{
    let fixture = Fixture::new();
    let body = "a".repeat(128 * 1024);
    let (url, server) = serve(vec![(None, full(&body))]);
    let cancelled = AtomicBool::new(false);
    let result = run(Download { url: &url, path: &fixture.0, size: body.len() as u64, cancelled: &cancelled,
        progress: &mut |bytes, _| {
            if bytes > 0 { cancelled.store(true, Ordering::Relaxed); }
            Ok(())
        } }, Duration::ZERO, Duration::from_secs(5), STALL, true);
    assert!(result.is_err());
    server.join().unwrap();
    let offset = std::fs::metadata(&fixture.0).unwrap().len();
    assert!(offset > 0);
    assert!(offset < body.len() as u64);
    let (url, server) = serve(vec![(Some(offset), partial(&body[offset as usize..], offset, body.len() as u64))]);
    let child = std::process::Command::new(std::env::current_exe().unwrap())
        .args(["--exact", "download::tests::resume_process_helper", "--ignored"])
        .env("MAANOP_TEST_DOWNLOAD_PATH", &fixture.0).env("MAANOP_TEST_DOWNLOAD_URL", url)
        .env("MAANOP_TEST_DOWNLOAD_SIZE", body.len().to_string()).output().unwrap();
    assert!(child.status.success(), "{}", String::from_utf8_lossy(&child.stdout));
    server.join().unwrap();
    assert_eq!(std::fs::read(&fixture.0).unwrap(), body.as_bytes());
}

#[test]
#[ignore = "invoked as a separate process by cancellation_keeps_bytes_and_a_new_process_resumes"]
fn resume_process_helper()
{
    let path = std::path::PathBuf::from(std::env::var_os("MAANOP_TEST_DOWNLOAD_PATH").unwrap());
    let url = std::env::var("MAANOP_TEST_DOWNLOAD_URL").unwrap();
    let size = std::env::var("MAANOP_TEST_DOWNLOAD_SIZE").unwrap().parse().unwrap();
    run(Download { url: &url, path: &path, size, cancelled: &AtomicBool::new(false), progress: &mut |_, _| Ok(()) },
        Duration::ZERO, Duration::from_secs(5), STALL, true).unwrap();
}

#[test]
fn completed_cache_skips_network_and_oversized_cache_restarts()
{
    let fixture = Fixture::new();
    std::fs::write(&fixture.0, b"abc").unwrap();
    fixture.run("http://127.0.0.1:1/unreachable", 3).unwrap();
    let (url, server) = serve(vec![(None, full("xy"))]);
    fixture.run(&url, 2).unwrap();
    server.join().unwrap();
    assert_eq!(std::fs::read(&fixture.0).unwrap(), b"xy");
}

#[test]
fn https_only_and_redirect_protocols_are_enforced()
{
    let fixture = Fixture::new();
    assert!(download(Download { url: "http://127.0.0.1:1/package", path: &fixture.0, size: 10,
        cancelled: &AtomicBool::new(false), progress: &mut |_, _| Ok(()) }).is_err());
    let (url, server) = serve(vec![(None, "HTTP/1.1 302 Found\r\nLocation: file:///test\r\n\
        Content-Length: 0\r\n\r\n".into())]);
    assert!(fixture.run(&url, 10).is_err());
    server.join().unwrap();
    assert!(std::fs::read(&fixture.0).unwrap().is_empty());
}
#[test]
fn cancellation_interrupts_retry_wait_and_budget_bounds_retries()
{
    for cancel in [true, false] {
        let fixture = Fixture::new();
        std::fs::write(&fixture.0, b"abc").unwrap();
        let (url, server) = serve(vec![(Some(3), status(503))]);
        let signal = std::sync::Arc::new(AtomicBool::new(false));
        let signal_copy = signal.clone();
        let canceller = thread::spawn(move || {
            thread::sleep(Duration::from_millis(100));
            signal_copy.store(cancel, Ordering::Relaxed);
        });
        let started = Instant::now();
        let result = run(Download { url: &url, path: &fixture.0, size: 10, cancelled: &signal,
            progress: &mut |_, _| Ok(()) }, Duration::from_secs(30), Duration::from_millis(300), STALL, true);
        assert!(result.is_err());
        assert!(started.elapsed() < Duration::from_secs(3));
        if cancel { assert!(result.unwrap_err().contains("取消")); }
        canceller.join().unwrap();
        server.join().unwrap();
        assert_eq!(std::fs::read(&fixture.0).unwrap(), b"abc");
    }
}

#[test]
fn interrupted_tls_handshake_is_retried()
{
    let fixture = Fixture::new();
    let (listener, port) = listen();
    let server = thread::spawn(move || {
        for _ in 0..6 {
            let mut stream = accept_raw(&listener);
            let _ = stream.read(&mut [0; 1024]);
            stream.write_all(b"HTTP/1.1 400 Bad Request\r\n\r\n").unwrap();
        }
    });
    assert!(fixture.run(&format!("https://127.0.0.1:{port}/package"), 10).is_err());
    server.join().unwrap();
}

#[test]
fn reconnect_follows_original_url_redirect_again()
{
    let fixture = Fixture::new();
    let (target, target_server) = serve(vec![(None, truncated("abc", 10)),
        (Some(3), partial("defghij", 3, 10))]);
    let redirect = format!("HTTP/1.1 302 Found\r\nLocation: {target}\r\nContent-Length: 0\r\n\
        Connection: close\r\n\r\n");
    let (url, source_server) = serve(vec![(None, redirect.clone()), (Some(3), redirect)]);
    fixture.run(&url, 10).unwrap();
    source_server.join().unwrap();
    target_server.join().unwrap();
    assert_eq!(std::fs::read(&fixture.0).unwrap(), b"abcdefghij");
}
