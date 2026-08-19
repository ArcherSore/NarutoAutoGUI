# Worker、Run 与 GUI Observation 使用正交状态模型

NarutoAutoGUI 将 Worker 内部健康、Run 生命周期和主 GUI 对外部身份/连接/存活事实的观察建模为三个正交状态面。Worker State 为 Starting、Ready、NotReady、Faulted、Stopping；Run State 为 Idle、Starting、Running、Stopping、Succeeded、Failed、Cancelled；GUI Observation 为 WorkerNotStarted、WorkerStarting、Connected、IpcDisconnected、WorkerExited、WorkerRecoveryConflict、ChildSessionEnded。Worker Snapshot 只包含 Worker State 和 Run State，GUI Observation 由主 GUI 根据 Admission Record、Named Pipe、真实 Worker PID/SessionId 和 WTS Child Session 状态计算。

Snapshot 明确区分 `activeRun` 与 `lastRun`。顶层 `runState` 只表示 execution slot，仅允许 Idle、Starting、Running、Stopping；`activeRun == null` 当且仅当顶层为 Idle，非空时顶层必须等于 activeRun.state。Run 进入 Succeeded、Failed 或 Cancelled 后转为 `lastRun`，`activeRun` 清空，顶层回到 Idle；三个终态只存在于 lastRun.state。Idle 只表示 Worker 当前没有活动 Run、可以接受新的 `run.start`，不表示没有历史 Run；`lastRun` 保留到新 Run 被接受或 Worker 退出。

Ready 表示 Worker 健康且具备执行能力，与 Run 是否 Running 无关。NotReady 表示 Worker 和 IPC 仍正常，但 MaaFramework、Python Agent 或 MaaNOP 项目依赖未满足，并携带如 PythonMissing、AgentImportFailed、ResourceInvalid 的结构化原因。Faulted 只表示仍存活的 Worker 发生不可继续工作的内部故障。依赖初始化失败不得表示为 Run Failed；只有 Worker 已接受 Run 后的执行失败才进入 Run Failed。`run.stop` 使 Run 进入 Stopping 时 Worker 仍可为 Ready；只有 Worker 自身准备退出时才进入 Worker Stopping。

IPC 断开、Worker 退出和 Child Session 注销均不得伪造成 Worker Faulted 或 Run Failed。GUI 保留最后一次 Worker/Run Snapshot，将其标记为 stale 并记录最后更新时间，同时通过 GUI Observation 显示外部事实。WorkerExited 时当前 Run 的最终结果显示为未知或执行已中断；ChildSessionEnded 同样不改写最后快照。重连并取得新的完整 Snapshot 后，权威状态覆盖 stale 快照，GUI Observation 回到 Connected。

GUI 在当前连接上取得完整 Snapshot，并验证 requestId 对应当前请求、workerInstanceId 匹配、snapshotVersion/protocol schema 可解析且内部状态一致后，该 Snapshot 即为 fresh。Runtime Profile Digest equality 不是 freshness 条件：admitted digest 与 GUI 当前 desired digest 不同仍可 SnapshotFresh=true，但 Start 禁用；fresh Snapshot 确认 activeRun 为 null 后可以显式替换 Worker。刚重连但尚未同步或持有 stale Snapshot 时禁止 Start。若断线前最后可信 Snapshot 的 activeRun 非空，task/option 编辑锁继续保持；只有 fresh Snapshot 明确显示 activeRun 为 null 后才解除。

Observation 判定优先级固定为 ChildSessionEnded > WorkerRecoveryConflict > WorkerStarting > Connected > IpcDisconnected > WorkerExited > WorkerNotStarted。ChildSessionEnded 表示目标 Session 已确认不存在，优先级最高。WorkerRecoveryConflict 表示多候选或 Admission Record 与真实身份事实冲突，禁止 start、stop、shutdown、runtime-profile 替换和新 Worker 启动，等待人工恢复。WorkerStarting 表示有效 Pending Admission 仍在 grace period 且未完成唯一接纳，禁止第二个 Worker 和 Run。Connected 只表示 Pipe admission 与 Worker identity 已确认，不代表 Snapshot 已同步。

Snapshot freshness 是独立本地标志，不进入 Observation 枚举。Connected 且 SnapshotFresh=false 时显示“已连接，正在同步”，不得根据旧 Snapshot 作新的执行决策；当前 workerInstanceId 的完整 Snapshot 到达并通过 Runtime Profile Digest 核对后才置 true。IpcDisconnected 只在 Worker identity 已明确、PID 仍存在于目标 Session 而 Pipe 断开时使用，并保留 stale Snapshot。WorkerExited 只用于此前正式接纳的 Worker 在 Child Session 仍存在时确认退出；从未接纳的 Pending Admission 最终失败则回到 WorkerNotStarted，不经过 WorkerExited。WorkerNotStarted 只表示 Session 存在且没有有效记录、已接纳 Worker、Pending Admission 或 recovery conflict，可以安全启动新 Worker。
