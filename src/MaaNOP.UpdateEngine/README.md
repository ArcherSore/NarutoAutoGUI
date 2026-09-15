# MaaNOP Update Engine V2

独立 Windows x64 Rust 进程，拥有 check、prepare、install 的更新规则。GUI 负责 UI、确认、进程通信和运行环境生命周期。
Rust 1.98.1 MSVC 与 Cargo.lock 固定工具链/依赖，Windows CRT 静态链接，不复制 GUI 的 .NET runtime。

## 构建与验证

- 正式 baseline：`src/NarutoAutoGUI/scripts/build.ps1 -Locked`。
- 开发 GUI：`src/NarutoAutoGUI/scripts/build-development.ps1 -Configuration Release`。
- 自动化：`src/NarutoAutoGUI/scripts/test-automated.ps1`，含 GUI、Worker、进程适配、Rust tests 和 Clippy。
- 完整包集成与实机边界见 `docs/UPDATER-V2-VALIDATION.md`。baseline 不包含 MaaNOP/Python。

## JSONL v1

每次启动提交一个 UTF-8 JSONL 请求，包含 `protocolVersion: 1`、`operation` 和绝对路径 `installation`。

| 操作 | 额外输入 | 输出 |
| --- | --- | --- |
| check | 无 | result：currentVersion、update（null 或 version/notes/descriptor） |
| prepare | 原样 descriptor | progress、最终 result(reference) 或 cancelled/error |
| install | 原样 reference、guiPid | 实际安装副本的 ready，或修改前 error |

展示字段与 opaque descriptor/reference 分离，GUI 不解析 opaque 内容、不管理缓存。
错误统一为 type=error、code、message，退出码 1；成功退出码 0。stdout 不混入诊断，GUI 持续排空 stderr。
check/install 请求写完关闭 stdin。prepare 保持输入打开；固定取消消息为 protocolVersion=1、operation=cancel。
prepare 输入断开也取消，尽力清理后退出；关闭 Drawer 不取消。GUI 重启不恢复 prepared reference。

单条消息上限 1 MiB，check 的 PI 上限 1 MiB、GitHub 元数据上限 4 MiB。
check HTTP 最长 30 秒，GUI 等待 45 秒。prepare HTTP 总预算 1 小时，下载流闲置 15 秒超时，GUI 取消宽限 20 秒。
实际安装副本等待根入口释放最长 10 秒；ready 后等待 GUI PID 结束最长 30 秒，之后不再依赖 stdout。
`--install-copy` 是内部转交参数，不是第四种公开操作；安装正常 UI 只显示状态和错误。

## 文件与失败

prepare 清理 cache/updater 的废弃内容，下载后校验长度/SHA256、全部 ZIP 路径与完整包布局，再形成 Payload。
包内 config/logs/debug/cache 内容忽略，保留名称类型冲突拒绝；不复制旧用户数据，不接受 state 或缺少内置 Python 的包。
install 只做前置检查，不重复整包验证；GUI 退出后先移走全部受管理根内容，再写入新版。
先尽力清理 old/Payload，再 relaunch。失败不回滚；文件失败停止，启动失败可手动启动，清理失败仅记日志。
运行副本留到下次 prepare；无安装锁、目录 swap、长期备份、恢复 journal 或健康确认。

## 验收工具

`examples/prepare_local_package.rs` 仅用于本地完整包验收：复用生产 check/prepare，HTTP 用指定 ZIP 替代。
运行方式：`cargo run --release --locked --example prepare_local_package -- INSTALLATION ZIP TAG`（crate 目录内）。
它不发布、不进入产品命令，不等同于 GUI 从 GitHub 端到端下载验收。
