# GUI–Worker 协议

这是 GUI 与 Child Session Worker 的稳定边界摘要。字段定义和数值常量以 src/NarutoAutoGUI.Protocol 中的 ProtocolModels、PreviewModels、PipeProtocol、PreviewBuffer 和 CanonicalDigest 为准；修改协议时两端应同版发布。

## 控制通道

协议版本为 3。GUI 是本机 Named Pipe 服务端，Worker 是客户端。每条消息以 4 字节 little-endian 无符号长度开头，后接 UTF-8 JSON；payload 上限 4 MiB。Envelope 包含 protocolVersion、messageType、operation、data；请求与响应由 requestId 配对，响应包含 success 或结构化 error。版本或帧非法时拒绝连接，不做降级协商。

| 操作 | 用途 |
| --- | --- |
| connection.open | Worker 登记或重连 |
| worker.getSnapshot | 取得完整权威状态 |
| run.start / run.stop | 提交不可变计划 / 请求停止当前 Run |
| log.getSince | 按 sequence 补取有界日志 |
| preview.start / preview.renew / preview.stop | 管理只读预览订阅 |

Worker 的 worker.stateChanged、run.stateChanged 和 log.entry 是可丢失的实时事件。重连后必须重新获取 Snapshot；不能用事件重建权威状态。run.stop 的成功响应表示请求已接受，不表示 Run 已终结。GUI 需要观察后续 Snapshot 或事件所指向的终态。

## 身份与持久边界

GUI 使用 Pipe 对端真实 PID、Windows Session ID、Worker 映像和 Admission Record 核验连接，并核对 Worker Instance ID 与 Launch Token。Pipe 连接本身不是 Worker 身份。GUI 只持久化最小接纳信息；Run 和日志保留在 Worker 进程内，不在 GUI 重启时重放计划。

Worker Launch Context 在单个 Worker 实例内固定；GUI 为其写入有界 Launch Manifest。Run Plan 描述一次任务执行，不重复环境配置。双方核对 Runtime Profile Digest；Worker 对 Run Plan 自行计算 Plan Digest。同一 Run ID 的传输重试必须使用原计划，避免重复执行。具体 canonical digest 规则由共享实现定义，改变规则需提升版本。

## 预算与预览

Launch Manifest 上限 256 KiB，Run Plan 上限 1 MiB，Snapshot payload 上限 3 MiB；日志消息和补取响应分别受 64 KiB 与 1 MiB 上限约束。这些限制低于传输帧上限，供终态诊断和响应 Envelope 留出空间。

预览控制在 Pipe 上协商并续订租约，像素不进入 JSON。Worker 以文件映射发布最新 BGR32 帧，GUI 只读消费，画面最大 640×360。订阅与帧身份包含 Worker、Session 和目标代次；旧帧不得在新目标上显示。慢消费者可跳帧，预览故障不阻塞 Run。
