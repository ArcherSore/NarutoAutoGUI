# IPC 使用有界完整帧，GUI 消费速度不影响 Run

Named Pipe frame 固定为 uint32 little-endian payloadLength 加 UTF-8 JSON payload，hard limit 4 MiB。payloadLength 为 0、越界、frame 未完整读取或 UTF-8 非法时关闭连接；JSON 可解析但业务 schema 无效时，只有能安全关联 requestId 才返回 `invalid_request`。读取端必须先读并校验固定 4-byte prefix，再租用有界缓冲精确读取，不能按对端声明无界分配。首版不实现压缩、通用分片、二进制 payload、截图流或用户可调限制。

规范化 Run Plan hard limit 为 1 MiB。完整 `worker.getSnapshot` response envelope + WorkerSnapshot 的 UTF-8 payload implementation budget 为 3 MiB，不含 length prefix；Run 接受条件以真实 response schema 序列化 candidate base Snapshot，并要求加上 512 KiB terminal diagnostics reserve 后仍不超过 3 MiB。该 reserve 是 Run-level 128 KiB、所有 Plan Item 共享 320 KiB、stop/Agent/execution-context 64 KiB 的总 hard budget。Worker 在接受前保证后续 lastRun 增加终态诊断仍可恢复；超限使用 `invalid_run_plan`，只返回 actualBytes、limitBytes 和可合理确定的 offendingPath。

Snapshot 可变诊断按 UTF-8 JSON serialized bytes 有界并确定性压缩，核心 id/state/code/time/digest 永不裁剪。发送前执行最终 <=3 MiB guard；意外超限先压缩低优先级诊断，禁止发送 >4 MiB frame，并在开发/测试环境记录 CRITICAL invariant violation。自动化测试必须覆盖 Run Plan 和 Snapshot budget 两侧边界、Run/Plan Item/Agent/dependency 各诊断池超限、中文和 emoji 截断、裁剪后反序列化，以及所有 terminal Snapshot <=3 MiB、所有合法 frame <=4 MiB。

单条 LogEntry message 最多 64 KiB UTF-8 bytes，截断保持合法 UTF-8，可记录 originalByteLength 并设置 truncated。`log.getSince` 的有效 limit 为 1..500，超过时收紧并返回 effectiveLimit；单次响应约 1 MiB，达到字节预算时提前分页。Worker ring buffer 仍为最多 5000 条或约 8 MiB，以实际 UTF-8 保存成本计量并淘汰最旧条目。

Worker 的业务状态提交和日志缓冲不得等待 Pipe 写入。实时 log.entry 是 best effort，发送队列拥塞时可丢通知但不丢 ring buffer。stateChanged 可在 coalescing slot 或有界 latest-state queue 中合并旧 revision，但必须保留最新尚未发送状态；GUI 收到跳号后以 Snapshot 恢复。Pipe 断开时不保存无限 Event backlog，丢弃待发实时事件，重连后使用 fresh Snapshot 和 log.getSince。

实际 Pipe writer 必须唯一，并以 request/response > stateChanged > log.entry 的优先级选择下一完整 frame。一旦开始写某帧就完整写完，不能在 JSON 中途插入高优先级帧；日志分页上限用于减少 head-of-line blocking。Pipe write timeout、read failure、慢消费者、队列拥塞或断开只影响 GUI Observation/transport，不使 Worker Faulted，不改变 Run 状态，也不中断 MaaFramework execution context。
