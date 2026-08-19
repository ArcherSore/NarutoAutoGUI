# 只有确认 MaaFramework 停止后才完成 Run-layer Cancelled

`run.stop` 是异步控制操作。Worker 在同一串行化状态机中校验 runId、把 Run 置为 Stopping，并立即把所有尚未开始的 Plan Item 标记为 Cancelled、`startedAt=null`、`reason=user_requested`。当前 Plan Item 不增加 Stopping 枚举，暂时保留 Starting 或 Running。首次 ACK 返回 `disposition=stop_requested`，已经 Stopping 时返回 `disposition=already_stopping`；ACK 只表示请求已接受，实际停止和清理由 Worker 后台继续，GUI 不得在此时显示“已停止”。

Worker 对当前 MaaTasker 调用 `Stop()`，等待返回的 MaaTaskJob 到达确定终态，并结合 Tasker 已不再 Running/Stopping，确认 execution context 真正停止。该等待具有固定内部 timeout，具体数值由固定 MaaFramework baseline 的端到端测试确定。若期限内无法确认，Worker 进入 `Faulted / StopTimeout`，Run 和 activeRun 保持 Stopping，当前 Plan Item 保持原 Starting/Running，不创建虚假的 lastRun，不再接受新 Run。即使后来观察到迟到状态，首版也不自动恢复 Ready；用户只能显式替换 Worker 或结束 Child Session。

Cancelled 是 NarutoAutoGUI 的 Run 层语义，不是 MaaJobStatus 的直接映射。停止请求与 Plan Item 完成必须经同一串行状态机处理：停止接受前已经终态的当前项保留真实 Succeeded 或 Failed；停止接受时仍未终态的当前项在 MaaFramework 停止确认后标记 Cancelled、`reason=user_requested`，不能把因 Stop 产生的 MaaJobStatus Failed 直接映射成 Plan Item Failed。一旦 active Run 的停止请求正式接受，整个 Run 最终为 Cancelled，即使当前项随后恰好自然 Succeeded，因为剩余计划已经被取消。

MaaFramework 停止确认后，Worker 调用 MaaAgentClient.LinkStop，给 AgentServer 固定短暂 grace period 自行退出，然后才进入 Dispose/强制兜底。Agent 正常退出时正常释放对象；仍未退出时记录 WARN 和 `result.forcedAgentTermination=true`，再通过 Dispose 或显式终止进程树。只要最终确认清理完成，Run 仍为 Cancelled，activeRun 清空、完整终态进入 lastRun，Worker 保持 Ready。

若 MaaFramework 已确认停止但 Agent 进程树最终无法确认清理，Run 仍可终态为 Cancelled，activeRun 清空并进入 lastRun，但 Worker 进入 `Faulted / AgentCleanupFailed`，拒绝新 Run。Faulted Worker 继续响应 ping、worker.getSnapshot、log.getSince，以及与当前或最近 Run 对应的重复 run.stop，以保留诊断能力；它拒绝 run.start 和任何需要创建新 execution context 的操作。
