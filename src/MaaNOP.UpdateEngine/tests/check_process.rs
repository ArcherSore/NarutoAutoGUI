use std::io::Write;
use std::process::{Command, Stdio};

#[test]
fn invalid_request_returns_one_structured_error_and_failure_exit()
{
    let mut child = Command::new(env!("CARGO_BIN_EXE_maanop-update-engine"))
        .stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped()).spawn().unwrap();
    child.stdin.take().unwrap().write_all(b"{\"operation\":\"unknown\"}\n").unwrap();
    let output = child.wait_with_output().unwrap();
    assert!(!output.status.success());
    let text = String::from_utf8(output.stdout).unwrap();
    assert_eq!(text.lines().count(), 1);
    let message: serde_json::Value = serde_json::from_str(text.trim()).unwrap();
    assert_eq!(message["type"], "error");
    assert_eq!(message["code"], "invalid_request");
}
