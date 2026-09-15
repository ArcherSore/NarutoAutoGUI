# MaaNOP Update Engine（V2 工单 01）

目前只实现 check。prepare/install 由后续工单实现；GUI 暂时关闭下载/安装入口，不回退到 V1。
独立 Rust 进程不依赖 .NET；支持 Windows x64，开发需要 Rust MSVC 工具链与 Visual Studio C++ build tools。

## 开发构建

在仓库根目录运行 `src/NarutoAutoGUI/scripts/build-development.ps1 -Configuration Release`。
脚本构建 Engine 和 GUI，将 Engine 复制到 GUI 开发输出同级；不改变现有正式发布脚本，也不组装完整 MaaNOP 包。
Cargo.lock 固定依赖解析；Cargo target 和依赖缓存不提交。

Rust 验证：`cargo test --locked --manifest-path src/MaaNOP.UpdateEngine/Cargo.toml`。
GUI 进程适配验证：`dotnet run --project src/NarutoAutoUpdater.Tests -- --engine-tests`。
既有 GUI/Worker/Updater 全套验证继续由 `test-automated.ps1` 执行；其中 V1 安装用例保留到安装迁移工单替换。

## JSONL seam v1

启动 `maanop-update-engine.exe`，stdin 写一条 UTF-8（无 BOM）JSON，换行后关闭输入。
请求包含 `protocolVersion: 1`、`operation: "check"` 和存在的绝对路径 `installation`。
Engine 自己读取根 interface.json，并获取 GitHub 最新正式 Release；不下载包。

stdout 恰好返回一条 UTF-8 JSONL。成功返回 `protocolVersion: 1`、`type: "result"`、`operation: "check"`、
`currentVersion` 和 `update`；无更新时 update 为 null，否则包含 `version`、`notes`、`descriptor` 字符串。
descriptor 对 GUI 不透明，后续 prepare 负责消费；GUI 不解释内容、不从中提取展示字段。

错误返回 `protocolVersion: 1`、`type: "error"`、`code`、`message`，退出码为 1；成功退出码为 0。
当前错误类别为 invalid_request、invalid_source、network_error、invalid_release。
GUI 同时验证退出码、协议版本、消息类型及必需字段，不把日志当作协议，也不接受多个结果。

单条请求/响应上限为 1 MiB（含输出换行），PI 上限 1 MiB，GitHub 原始 Release 响应上限 4 MiB。
HTTP 全局超时 30 秒，GUI check 总超时 45 秒；GUI 取消/超时会终止自己的 check 进程。
诊断写入 logs/updater.log；输出失败不得在 stdout 混入诊断。GUI 始终排空 stderr 且不积累无界日志缓冲。

测试通过 JSON 命令 seam 使用可控 HTTP 响应与真实隔离目录；真实进程测试验证 framing 和退出结果。
没有测试专用产品参数、更新源覆盖设置或通用 RPC。当前不声称 prepared/install 的消息已实现。

prepare 输入 descriptor，返回 progress(download: bytes/total/bytesPerSecond；validate) 和 result(reference)。stdin 保持打开；固定取消消息为 protocolVersion=1、operation=cancel，EOF 同样取消。单消息上限 1 MiB，下载最长 1 小时，阻塞读取最长 15 秒；GUI 取消宽限 20 秒。reference 仅当前 GUI 内存持有，下次 prepare 删除此前全部 updater 工作内容。

install 输入 reference、guiPid，只有实际缓存副本检查完成后返回 ready。GUI 等待最长45秒，副本等待根入口10秒、GUI退出30秒。ready后不依赖stdout；原地替换成功先清理old/Payload，再创建新版GUI进程。失败通过logs/updater.log及最小Windows提示呈现。--install-copy仅为Engine内部转交参数，不是公开操作。
