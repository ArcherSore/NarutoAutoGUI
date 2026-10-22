# GUI–Worker 协议

这是 GUI 与 Child Session Worker 的稳定边界摘要。字段定义和数值常量以 src/NarutoAutoGUI.Protocol 中的 ProtocolModels、PipeProtocol 和 CanonicalDigest 为准；修改协议时两端应同版发布。

## 控制通道

协议版本为 5，Run Plan 与 Snapshot 版本均为 2；Worker Launch Context 保持版本 1。GUI 是本机 Named Pipe 服务端，Worker 是客户端。每条消息以 4 字节 little-endian 无符号长度开头，后接 UTF-8 JSON；payload 上限 4 MiB。Envelope 包含 protocolVersion、messageType、operation、data；请求与响应由 requestId 配对，响应包含 success 或结构化 error。版本或帧非法时拒绝连接，不做降级协商。

| 操作 | 用途 |
| --- | --- |
| connection.open | Worker 登记或重连 |
| worker.getSnapshot | 取得完整权威状态 |
| run.start / run.stop | 提交不可变计划 / 请求停止当前 Run |
| log.getSince | 按 sequence 补取有界日志 |

Worker 的 worker.stateChanged、run.stateChanged 和 log.entry 是可丢失的实时事件。重连后必须重新获取 Snapshot；不能用事件重建权威状态。run.stop 的成功响应表示请求已接受，不表示 Run 已终结。GUI 需要观察后续 Snapshot 或事件所指向的终态。

## 身份与持久边界

GUI 使用 Pipe 对端真实 PID、Windows Session ID、Worker 映像和 Admission Record 核验连接，并核对 Worker Instance ID 与 Launch Token。Pipe 连接本身不是 Worker 身份。GUI 只持久化最小接纳信息；Run 和日志保留在 Worker 进程内，不在 GUI 重启时重放计划。

Worker Launch Context 在单个 Worker 实例内固定；GUI 为其写入有界 Launch Manifest。Run Plan 描述一次任务执行，不重复环境配置。双方核对版本 1 的 Runtime Profile Digest；Worker 对 Run Plan 自行计算版本 2 的 Plan Digest，摘要覆盖 Plan Item 的类型与程序参数。同一 Run ID 的传输重试必须使用原计划，避免重复执行。具体 canonical digest 规则由共享实现定义，改变规则需提升版本。

Plan Item 通过 Kind 区分 MaaTask 与 Pretask；Pretask 的 Program 保存已解析的可执行程序和参数，选项 JSON 已按需追加到参数末尾。Worker 使用 Launch Manifest 的 ProjectRoot 作为工作目录，不重新解释选项。前置项按项目顺序排在所有普通任务之前。Snapshot 保留完整 Run Plan 和 Plan Item 类型，重连后可恢复前置任务的程序、参数与执行状态；具体字段以共享模型为准。

pretask 是 Active Run 中的普通受控阶段，先于正式 Controller 创建。非零退出、启动失败或 180 秒超时终止后续项；停止或超时后最多等待 30 秒确认清理。若程序退出未确认，Worker 进入 Faulted 并保留 Active Run，GUI 通过结束桌面分身恢复。只有 pretask 的计划无需创建 Controller；成功退出不清理已经启动的游戏。

Launch Manifest 的 frameworkOptions 保存 SaveOnError、SaveDraw 与 DebugMode，缺省值分别为 true、false、false。
这些诊断偏好不参与项目 Runtime Profile Digest；更改后在新 Worker 实例中生效。
Worker 在进入 Ready 前设置全局选项与绝对 LogDir（项目根目录下的 debug），检查返回值并记录日志；
设置失败保持 NotReady，不执行任务。

## 预算

Launch Manifest 上限 256 KiB，Run Plan 上限 1 MiB，Snapshot payload 上限 3 MiB；日志消息和补取响应分别受 64 KiB 与 1 MiB 上限约束。这些限制低于传输帧上限，供终态诊断和响应 Envelope 留出空间。
