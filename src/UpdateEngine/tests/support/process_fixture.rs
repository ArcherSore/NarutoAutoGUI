fn main()
{
    if std::env::args().any(|arg| arg == "--wait") {
        std::thread::sleep(std::time::Duration::from_secs(90));
    } else {
        std::fs::write("relaunched", "created").unwrap();
    }
}
