//! Release acceptance harness: production check/prepare rules with only HTTP replaced by a local ZIP.
//! This example is never shipped or exposed as an Engine operation.
use maanop_update_engine::{execute, prepare};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::fs::File;
use std::io::Read;
use std::sync::atomic::AtomicBool;

fn main()
{
    let args: Vec<_> = std::env::args().collect();
    assert_eq!(args.len(), 4, "usage: prepare_local_package INSTALLATION ZIP TAG");
    let root = &args[1];
    let zip = &args[2];
    let tag = &args[3];
    let mut input = File::open(zip).unwrap();
    let size = input.metadata().unwrap().len();
    let mut hash = Sha256::new();
    let mut buffer = [0; 81920];
    loop {
        let count = input.read(&mut buffer).unwrap();
        if count == 0 { break; }
        hash.update(&buffer[..count]);
    }
    let candidate = execute(&json!({"protocolVersion":1,"operation":"check","installation":root}).to_string(),
        |_| Ok(json!({"tag_name":tag,"draft":false,"prerelease":false,"body":"Local release acceptance",
            "assets":[{"name":format!("MaaNOP-win-x86_64-{tag}.zip"),"size":size,
                "browser_download_url":"https://example.invalid/acceptance.zip",
                "digest":format!("sha256:{:x}",hash.finalize())}]}).to_string()));
    let descriptor = candidate["update"]["descriptor"].as_str().expect("check did not select local candidate");
    let result = prepare(&json!({"protocolVersion":1,"operation":"prepare","installation":root,
        "descriptor":descriptor}).to_string(), |_| Ok(Box::new(File::open(zip).unwrap())),
        &AtomicBool::new(false), &mut |message| { println!("{message}"); Ok(()) });
    println!("{result}");
    if result["type"] != "result" { std::process::exit(1); }
}
