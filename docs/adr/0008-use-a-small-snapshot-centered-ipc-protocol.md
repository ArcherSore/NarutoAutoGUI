# 首版 IPC 使用最小且以 Snapshot 为中心的协议

NarutoAutoGUI 与 NarutoAutoWorker 通过带长度前缀的本机 Named Pipe JSON envelope 通信。每帧前缀是 uint32 little-endian payloadLength，表示紧随其后的 UTF-8 JSON 字节数，hard limit 为 4 MiB且不可由用户配置。读取端先精确读取 4 bytes、校验长度，再租用或分配有界缓冲并精确读取 payloadLength bytes；不能按对端声明无界分配。Envelope 固定包含协议版本、消息类型、operation 和 data；请求/响应使用 requestId 关联，响应返回 success 或结构化错误。首版固定一个兼容协议版本，不做协商降级，也不把 requestId 当作 runId。

payloadLength 为 0、超过 4 MiB、frame 截断或 UTF-8 非法属于 framing/protocol corruption，关闭当前 Pipe。JSON 可解析但业务 schema 非法时，能安全取得 requestId 才返回 `invalid_request`，否则关闭连接。首版不实现压缩、通用 fragmentation、binary payload、截图/frame streaming 或 per-user IPC size tuning。

首版只提供七个请求/响应 operation：Worker 用 `connection.open` 完成首次登记和重连握手；双方使用 `ping` 检测连接活性；GUI 用 `worker.getSnapshot` 取得完整权威状态；用 `worker.shutdown` 在没有 activeRun 时优雅退出 Worker；用 `run.start` 提交完整不可变 Run Plan；用 `run.stop` 停止匹配 runId 的活动 Run；用 `log.getSince` 按递增日志序号补取有界缓冲。首版不提供 pause/resume、队列、单独跳过 Plan Item、动态修改 Run Plan、通用远程调用或单独的 task 操作。

`worker.shutdown` 仅在 activeRun 为 null 时允许，否则返回 `operation_not_allowed`。接受后 Worker 进入 Stopping，停止接受新请求，flush 必要日志、关闭 IPC 并正常退出。GUI 等待旧 PID 在固定 timeout 内退出；超时后只可针对已验证 PID 强制结束。确认旧 PID 退出后才能删除旧 Admission Record 并启动新 Worker。

Worker 只发送 `worker.stateChanged`、`run.stateChanged` 和 `log.entry` 三类 Event。`run.stateChanged` 同时覆盖 Run 与 Plan Item 的状态变化，不再增加 task.started、task.completed 等重复事件。`log.entry` 同时承载 Worker 初始化、依赖检查、NotReady/Faulted 诊断、IPC 生命周期和 Run 执行日志，其 runId、planItemId、taskName 按上下文允许为空。Event 只用于降低实时显示延迟，允许因断线丢失；Snapshot 始终是 Worker/Run 状态的权威来源。GUI 重连后必须重新调用 `worker.getSnapshot`，不能依赖补齐所有 Event 重建状态；日志则通过 `log.getSince` 独立补取。

每个 stateRevision 对应一次原子权威状态提交和恰好一个逻辑 state-change notification。仅改变 Worker scope 时发送 `worker.stateChanged`；改变 Run 或 Plan Item 时发送 `run.stateChanged`。若一个业务流程需要分别通知 Worker 和 Run 的变化，必须拆成两个有序的原子提交和两个 revision，不能让一个 revision 依赖两个独立 Event 才能完整表达。Event 携带 workerInstanceId、stateRevision 和对应 scope 的最新状态摘要。

`run.stop` 的正常 ACK 只表示 Worker 已接受停止请求并把 Run 置为 Stopping，不表示 MaaFramework 已经停止。GUI 必须等待后续 `run.stateChanged`，或主动读取 `worker.getSnapshot`，观察到 Run 的 Cancelled 终态后才能显示“已停止”。

Run 进入 Succeeded、Failed 或 Cancelled 后，Worker 将完整终态保存为 `lastRun`，清空 `activeRun`，并将顶层 `runState` 设回 Idle。Idle 与历史终态并存，表示执行槽当前空闲且可以接受新的 `run.start`。
