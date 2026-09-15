use maanop_update_engine::{MAX_MESSAGE_BYTES, execute};
use serde_json::{Value, json};
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};

static NEXT: AtomicUsize = AtomicUsize::new(0);

struct Installation(PathBuf);

impl Installation
{
    fn new(version: &str) -> Self
    {
        let root = std::env::temp_dir().join(format!("maanop-check-{}-{}", std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)));
        fs::create_dir(&root).unwrap();
        fs::write(root.join("interface.json"), json!({
            "name": "MaaNOP", "version": version, "github": "https://github.com/owner/repo"
        }).to_string()).unwrap();
        Self(root)
    }

    fn request(&self) -> String
    {
        json!({"protocolVersion": 1, "operation": "check", "installation": self.0}).to_string()
    }

    fn check(&self, release: &Value) -> Value
    {
        execute(&self.request(), |url| {
            assert_eq!(url, "https://api.github.com/repos/owner/repo/releases/latest");
            Ok(release.to_string())
        })
    }
}

impl Drop for Installation
{
    fn drop(&mut self)
    {
        fs::remove_dir_all(&self.0).unwrap();
    }
}

fn release(tag: &str) -> Value
{
    json!({"tag_name": tag, "draft": false, "prerelease": false, "body": "新版说明\n第二行", "assets": [
        {"name": "MaaNOP-linux-x86_64-v2.10.0.zip"},
        {"name": format!("MaaNOP-win-x86_64-{tag}.zip"), "size": 123,
         "browser_download_url": "https://github.com/owner/repo/releases/download/test/package.zip",
         "digest": "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"}
    ]})
}

#[test]
fn check_returns_display_and_opaque_candidate_without_downloading_or_creating_cache()
{
    let installation = Installation::new("v2.9.0");
    let result = installation.check(&release("v2.10.0"));
    assert_eq!(result["type"], "result");
    assert_eq!(result["currentVersion"], "v2.9.0");
    assert_eq!(result["update"]["version"], "v2.10.0");
    assert_eq!(result["update"]["notes"], "新版说明\n第二行");
    assert!(!result["update"]["descriptor"].as_str().unwrap().is_empty());
    assert_eq!(fs::read_dir(&installation.0).unwrap().count(), 1);
}

#[test]
fn semver_precedence_is_numeric_and_unbounded_and_ignores_build_metadata()
{
    for (current, target, available) in [
        ("2.9.0", "v2.10.0", true), ("2.10.0+local", "v2.10.0", false), ("3.0.0", "2.10.0", false),
        ("1.0.0-alpha.9", "1.0.0-alpha.10", true), ("1.0.0-alpha", "1.0.0", true),
        ("1.0.0-1", "1.0.0-alpha", true), ("1.0.0--1", "1.0.0-1", false),
        ("4294967296.0.0", "184467440737095516160.0.0", true),
        ("1.0.0-184467440737095516160", "1.0.0-184467440737095516161", true)
    ] {
        let result = Installation::new(current).check(&release(target));
        assert_eq!(result["type"], "result", "{current} vs {target}: {result}");
        assert_eq!(!result["update"].is_null(), available, "{current} vs {target}");
    }
}

#[test]
fn invalid_local_versions_are_rejected_before_network_access()
{
    for version in ["2.1", "2.01.0", "2.1.0-01", "2.1.0\n", " 2.1.0", "1.0.0+", "1.0.0-a..b"] {
        let installation = Installation::new(version);
        let result = execute(&installation.request(), |_| panic!("Invalid PI must not access the network"));
        assert_eq!(result["code"], "invalid_source", "{version}");
    }
}

#[test]
fn nonstable_releases_are_not_offered()
{
    let installation = Installation::new("1.0.0");
    for field in ["draft", "prerelease"] {
        let mut value = release("2.0.0");
        value[field] = json!(true);
        assert!(installation.check(&value)["update"].is_null());
    }
}

#[test]
fn incompatible_ambiguous_or_unverified_assets_are_errors()
{
    let installation = Installation::new("1.0.0");
    let original = release("v2.0.0");
    let mut missing = original.clone();
    missing["assets"][1]["name"] = json!("MaaNOP-win-arm64-v2.0.0.zip");
    let mut duplicate = original.clone();
    duplicate["assets"].as_array_mut().unwrap().push(original["assets"][1].clone());
    let mut no_digest = original.clone();
    no_digest["assets"][1].as_object_mut().unwrap().remove("digest");
    let mut bad_digest = original.clone();
    bad_digest["assets"][1]["digest"] = json!("sha256:no");
    let mut no_size = original.clone();
    no_size["assets"][1]["size"] = json!(0);
    let mut insecure = original.clone();
    insecure["assets"][1]["browser_download_url"] = json!("http://github.com/owner/repo/package.zip");
    let mut invalid_tag = original.clone();
    invalid_tag["tag_name"] = json!("2.00.0");
    for invalid in [missing, duplicate, no_digest, bad_digest, no_size, insecure, invalid_tag] {
        assert_eq!(installation.check(&invalid)["code"], "invalid_release", "{invalid}");
    }
}

#[test]
fn invalid_source_cannot_redirect_check_to_another_host()
{
    let installation = Installation::new("1.0.0");
    for github in ["http://github.com/owner/repo", "https://example.com/owner/repo",
        "https://github.com:444/owner/repo", "https://user@github.com/owner/repo",
        "https://github.com/owner/repo?x=y", "https://github.com/owner/repo#x",
        "https://github.com/owner/repo/more", "https://github.com/owner/r%2fepo"] {
        fs::write(installation.0.join("interface.json"), json!({
            "name": "MaaNOP", "version": "1.0.0", "github": github
        }).to_string()).unwrap();
        let result = execute(&installation.request(), |_| panic!("Invalid source reached network"));
        assert_eq!(result["code"], "invalid_source", "{github}");
    }
}

#[test]
fn transport_failure_and_oversized_results_do_not_produce_a_candidate()
{
    let installation = Installation::new("1.0.0");
    let failure = execute(&installation.request(), |_| Err("offline".to_owned()));
    assert_eq!(failure["code"], "network_error");
    let mut huge = release("2.0.0");
    huge["body"] = json!("a".repeat(MAX_MESSAGE_BYTES));
    assert_eq!(installation.check(&huge)["code"], "invalid_release");
}
