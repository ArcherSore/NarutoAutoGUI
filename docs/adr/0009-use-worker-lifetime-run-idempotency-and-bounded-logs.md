# Run 幂等覆盖整个 Worker 生命周期，日志使用可检测断档的有界序列

`run.start` 以 runId 作为业务幂等键，requestId 只关联一次请求和响应。Worker 对每个正式接受的 Run 记录 runId、planDigest 和已有的 terminal summary，形成仅存在于该 Worker 进程内的 accepted-run ledger。相同 runId 和相同 planDigest 的请求无论命中 activeRun、lastRun 或 ledger 历史，都返回 `disposition=already_accepted` 而不重新执行；权威 Run 状态继续由 event 和 Snapshot 提供。相同 runId 配不同 planDigest 返回 `run_id_conflict`。preflight 失败发生在正式接受之前，不写入 ledger，修复后可以用原 runId 重试。新 Run 被接受时可以清除完整 lastRun，但 ledger 中的旧 runId 仍不可复用。Worker 重启后 ledger 不恢复。

存在 activeRun 时，不同 runId 的 `run.start` 返回 `worker_busy`。`run.stop` 命中 activeRun 时发起停止，重复请求按当前状态返回相应 disposition；命中相同 runId 的 terminal Run 时正常成功、不改写终态，并返回 `disposition=already_terminal`；没有匹配 Run 时返回 `run_id_mismatch`。`worker.getSnapshot` 和 `log.getSince` 均无副作用。

首版业务错误码固定为 `invalid_request`、`protocol_version_mismatch`、`worker_identity_rejected`、`worker_already_registered`、`worker_not_ready`、`worker_faulted`、`worker_busy`、`invalid_run_plan`、`run_id_conflict`、`run_id_mismatch`、`operation_not_allowed`、`internal_error`。错误对象统一包含 code 和 human-readable message；Pipe 断开与请求超时属于 GUI Observation 或 transport failure，不伪装成 Worker 业务错误。

每个 Worker instance 维护从 1 开始、跨 Run 和 IPC 重连单调递增的日志 sequence。LogEntry 包含 sequence、timestampUtc、level、source、message、truncated、可选 originalByteLength，以及按上下文可空的 runId、planItemId、taskName。message 最多 64 KiB UTF-8 bytes，截断必须停在合法字符边界并设置 truncated，整个 LogEntry 仍须满足 frame/response budget。Worker 先将日志写入有界缓冲，再发送 `log.entry`。缓冲固定最多 5000 条或约 8 MiB，以先达到者为准，字节统计按实际保存的 UTF-8 序列化或统一估算字节，而非 .NET 字符数；Worker 的 `connection.open` 与 Snapshot 都提供 workerInstanceId，变化时 GUI 清空旧游标。

Python Agent 的 stdout 与 stderr 必须异步持续读取以避免子进程管道阻塞，并分别使用 `agent.stdout` 与 `agent.stderr` source。两者始终携带 runId；存在当前 Plan Item 时可以附带 planItemId 和 taskName，但该关联仅表示日志接收时的上下文，不是 Plan Item 状态的权威依据。写入 stderr 本身不自动判定 Run 失败，Run 结果仍由 MaaFramework、Agent 连接和任务执行结果决定。

`log.getSince(afterSequence, limit)` 要求 limit > 0，非法时返回 `invalid_request`；大于 500 时收紧为 500。响应返回 entries、effectiveLimit、firstAvailableSequence、lastLogSequence、hasMore、gap，并在断档时返回 missingRange。单次响应约束在约 1 MiB，达到字节 budget 时即使未满 effectiveLimit 也提前停止并以 hasMore 表示剩余；下一页游标取最后实际返回的 sequence，不能按请求条数推算。日志因缓冲淘汰而缺失不是请求失败；Worker 返回 `gap=true` 并从当前最早可用日志继续。GUI 对重复 sequence 去重，发现跳号时通过 `log.getSince` 补取，并在确有断档时明确提示用户日志不完整。

GUI 将协议层 `lastReceivedSequence` 与可见日志的 displayEntries/displayBaseline 分离。用户“清空显示”只清除本地可见内容并推进或重设 displayBaseline，不重置 Worker sequence，也不回退 lastReceivedSequence。后续实时事件、分页补取和重连继续沿用 transport cursor，因此不会把用户刚清掉的历史日志重新灌回界面。workerInstanceId 变化时两者都按新 Worker 的 sequence 空间重新开始。
