namespace NarutoAutoGUI.Updates;

// Legacy download input until ticket 02 removes the old downloader. Release discovery lives in Rust.
public sealed record UpdateRelease(string Tag, string Notes, string Name, Uri DownloadUrl, long Size, string Sha256);
