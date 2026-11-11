# MaaNOP Update Engine V2

独立 Windows x64 Rust 进程，拥有 check、prepare、install 的更新规则。GUI 负责 UI、确认、进程通信和运行环境生命周期。
Rust 1.98.1 MSVC 与 Cargo.lock 固定工具链/依赖，Windows CRT 静态链接，不复制 GUI 的 .NET runtime。

## 构建与验证

- 正式 baseline：`src/Gui/scripts/build.ps1 -Locked`。
- 开发 GUI：`src/Gui/scripts/build-development.ps1 -Configuration Release`。
- 自动化：`src/Gui/scripts/test-automated.ps1`，含 GUI、Worker、进程适配、Rust tests 和 Clippy。
- 完整包集成和真实 Windows 验收条件见 [GitHub Issues](https://github.com/ArcherSore/Bunshin/issues)；
  本仓库的前端 baseline 不包含 MaaNOP/Python 项目内容。

## JSONL v1

每次启动提交一个 UTF-8 JSONL 请求，包含 `protocolVersion: 1`、`operation` 和绝对路径 `installation`。

| 操作 | 额外输入 | 输出 |
| --- | --- | --- |
| check | 无 | result：currentVersion、update（null 或 version/notes/releaseUrl/descriptor） |
| prepare | 原样 descriptor | progress、最终 result(reference) 或 cancelled/error |
| install | 原样 reference、guiPid | 实际安装副本的 ready，或修改前 error |

展示字段与 opaque descriptor/reference 分离，GUI 不解析 opaque 内容、不管理缓存。
releaseUrl 为发布说明页面的展示链接；GUI 可消费没有该字段的 V2 返回，此时在弹窗内查看说明。
错误统一为 type=error、code、message，退出码 1；成功退出码 0。stdout 不混入诊断，GUI 持续排空 stderr。
check/install 请求写完关闭 stdin。prepare 保持输入打开；固定取消消息为 protocolVersion=1、operation=cancel。
prepare 输入断开也取消，保留下载缓存并清理未完成的解压内容；关闭 Drawer 不取消。
GUI 重启不恢复 prepared reference，再次准备同一候选时复用下载缓存。

单条消息上限 1 MiB，check 的 PI 上限 1 MiB、GitHub 元数据上限 4 MiB。
check HTTP 最长 30 秒，GUI 等待 45 秒。prepare 下载总预算（含重试）1 小时，连接超时 15 秒，连续 15 秒低于 1 byte/s 时中断重试，
GUI 取消宽限 20 秒。暂时性网络错误最多重试 5 次，等待 1/2/4/8/16 秒；取消不重试。
实际安装副本等待根入口释放最长 10 秒；ready 后等待 GUI PID 结束最长 30 秒，之后不再依赖 stdout。
`--install-copy` 是内部转交参数，不是第四种公开操作；安装正常 UI 只显示状态和错误。

## 文件与失败

prepare 清理 cache/updater 的废弃准备内容，保留 download/ 下的候选身份与 package.part。
候选身份包含仓库、版本、文件名、大小和 SHA256；身份不一致时清理旧下载。
libcurl 通过 Rust curl 绑定执行 HTTPS 下载、重定向和按文件长度续传；服务器忽略 Range、返回 416 或范围不符时停止并清除下载缓存，下次准备从头下载。
网络失败、取消、进程退出后保留已写入文件；缓存长度达到目标后仍须校验整个文件的 SHA256。
下载后校验长度/SHA256、全部 ZIP 路径与完整包布局，再形成 Payload；成功准备或哈希不符时清理下载缓存。
候选包的 runtimeconfig 声明 frameworks 时，要求系统 .NET 目录已安装满足版本的框架；未安装时拒绝准备并保留下载缓存，安装后重试不重新下载。
cache/updater.lock 的操作系统文件锁防止多个 prepare 同时写缓存，进程退出释放锁。
check 与下载均使用静态链接 libcurl 与 Windows Schannel，无需用户安装 curl，也不依赖外部 curl.exe；重定向只允许 HTTPS。
吊销信息无法获取时继续（revoke best effort），证书链与主机名校验不变；下载的 TLS 握手失败按暂时性错误重试。
包内 config/logs/debug/cache 内容忽略，保留名称类型冲突拒绝；不复制旧用户数据，不接受 state 或缺少内置 Python 的包。
install 只做前置检查，不重复整包验证；GUI 退出后先移走全部受管理根内容，再写入新版。
先尽力清理 old/Payload，再 relaunch。失败不回滚；文件失败停止，启动失败可手动启动，清理失败仅记日志。
运行副本留到下次 prepare；无安装锁、目录 swap、长期备份、恢复 journal 或健康确认。

## 验收工具

`examples/prepare_local_package.rs` 仅用于本地完整包验收：复用生产 check/prepare，HTTP 用指定 ZIP 替代。
运行方式：`cargo run --release --locked --example prepare_local_package -- INSTALLATION ZIP TAG`（crate 目录内）。
它不发布、不进入产品命令，不等同于 GUI 从 GitHub 端到端下载验收。
