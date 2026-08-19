# Snapshot 使用原子 stateRevision，并区分 freshness 与 Runtime Profile 一致性

WorkerSnapshot 包含 snapshotVersion、capturedAtUtc、stateRevision、workerInstanceId；Worker 的真实 PID/SessionId、Worker/协议/MaaFramework 版本、Worker State/reason/startedAtUtc；runtimeProfileDigest、项目与 interface 元数据、项目根目录、controller/resource/Agent 诊断摘要和 dependencyStatus；顶层 runState、activeRun、lastRun；以及 firstAvailableLogSequence 和 lastLogSequence。dependencyStatus 至少报告 resolvedPythonExecutablePath、pythonVersion、maaImport、agentServerImport、toolkitImport、agentEntryCheck、checkedAtUtc、reason、details。Worker NotReady 时 worker.reason 提供顶层结构化原因，dependencyStatus 提供检查细节。Snapshot 明确不包含 Launch Token、accepted-run ledger 全量历史、历史 Run 列表、日志正文或 MaaNOP Config 原文。

Snapshot freshness 只验证响应 requestId 对应当前请求、workerInstanceId 等于当前已接纳 Worker、snapshotVersion/protocol schema 可解析，以及 Snapshot 内部状态一致；通过后即可 SnapshotFresh=true。Worker Snapshot 报告的是 Admitted Runtime Profile Digest，GUI 根据当前项目解析 Desired Runtime Profile Digest。二者不一致不使 Snapshot stale，但会禁止 Start；fresh Snapshot 确认 activeRun 为 null 后，GUI才可显式替换 Worker。这样避免 runtime 变化导致无法取得可用于安全替换的 fresh 状态。

stateRevision 在单个 workerInstanceId 内从 1 开始，每次对 Worker、Run或 Plan Item 权威状态做一次原子提交时加一。一次提交即使修改多个字段也只增加一次；Snapshot 在同一状态机锁下构造并只对应一个完整 revision。每个 revision 必须产生恰好一个逻辑 state-change notification：Worker-only mutation 使用 `worker.stateChanged`，Run/Plan Item mutation 使用 `run.stateChanged`；需要分别改变两个 scope 时拆成两个有序提交和两个 revision。日志 sequence 与 stateRevision 完全独立，二者不互相推导。

GUI 对当前 workerInstanceId 只顺序应用 revision。等于 currentRevision+1 时正常应用；小于等于当前值时作为重复或迟到忽略；大于 currentRevision+1 时将 SnapshotFresh 设为 false，不猜测缺失状态并重新调用 `worker.getSnapshot`。取得 revision=N 的 fresh Snapshot 后将 currentRevision 设为 N，之后只接受连续事件。IPC 重连后无论 revision 是否看似连续，都先读取完整 Snapshot，再恢复增量事件。

顶层 runState 只允许 Idle、Starting、Running、Stopping。`activeRun == null` 当且仅当顶层为 Idle；非空时必须等于 activeRun.state。Succeeded、Failed、Cancelled 只允许出现在 lastRun.state。RunSnapshot 包含 runId、planDigest、state、创建/开始/停止请求/结束时间、可空 currentPlanItemId/currentPlanItemIndex、完整 plan、result 和 error。currentPlanItemIndex 为 0-based；Run 尚未进入具体 Item 或没有当前项时 ID/index 均为 null，非空时必须指向 plan.items 中同一项。lastRun 的 current ID/index 始终为 null。

PlanItemSnapshot 包含 planItemId、taskName、taskLabel、entry、resolvedOptions、pipelineOverride、state、开始/结束时间、reason、result、error。planItemId 是权威身份，index 仅用于展示和一致性检查。Worker 不重新解释 plan 内的 PI option 数据。

完整 Run Plan 可以进入 Snapshot，但受 IPC payload hard limit 约束。新 Run 接受时清除旧完整 lastRun，因此正常 Snapshot 最多包含一个 activeRun 或一个 lastRun 的完整 plan，不同时保存两份。规范化 Run Plan hard limit 为 1 MiB UTF-8 serialized bytes。完整 `worker.getSnapshot` response envelope + WorkerSnapshot 的 UTF-8 JSON payload 必须不超过 3 MiB，不含 4-byte length prefix；4 MiB 只是 transport hard limit并保留约 1 MiB schema/serializer/防御余量。

Run 正式接受前必须使用真实 response envelope/schema 序列化 candidate base Snapshot，并验证 `base response + 512 KiB terminal diagnostics reserve <= 3 MiB`，不能只计算 Run Plan 字符串。512 KiB 是整个 Run 的共享 hard budget：Run-level result+error 不超过 128 KiB；所有 Plan Item 的 result+error+reason 合计不超过 320 KiB；stop、Agent cleanup、execution-context 等附加诊断合计不超过 64 KiB。各预算按对应 JSON subtree 的实际 UTF-8 serialized bytes 计算。只有通过检查才写入 accepted-run ledger，以保证任何成功、失败或取消终态都能恢复。超限以 `invalid_run_plan` 拒绝，details 只返回 actualBytes、limitBytes 和能够合理确定的 offendingPath。

320 KiB Plan Item 诊断使用全局池而非 per-item 配额。确定性压缩先为每个 Item 保留 planItemId、state、reason code、error code、short message、startedAtUtc、endedAtUtc，再保留 result/error details、stack summary 和扩展诊断；接近总上限时截断或省略后者，并设置 truncated、可确定时的 originalByteLength 和 omittedCount。首个大异常不得挤掉后续 Item 的核心错误信息。

Worker reason/details 独立限制为 64 KiB，dependencyStatus 为 128 KiB，单个 error message/stack summary 为 16 KiB；它们不计入 terminal 512 KiB，但计入完整 3 MiB response。所有可变长字段按 UTF-8 JSON serialized bytes 确定性裁剪，保持合法 JSON/UTF-8。裁剪顺序为结构化 code/state/id/time、short message、stack、details、最低优先级扩展字段；runId、planItemId、state、error code、timestamps、plan identity/digest 绝不裁剪。Snapshot 只保存有界摘要，较完整异常进入同样有界的 LogEntry。

发送 `worker.getSnapshot` 前必须执行最终 UTF-8 serialized size guard。正常 invariant 是 payload <=3 MiB；若实现 bug或 schema 演进导致超限，先进一步压缩可丢弃诊断，绝不能删除核心状态或尝试发送超过 4 MiB 的 frame。开发和测试环境将该情况视为 invariant violation 并记录 CRITICAL。
