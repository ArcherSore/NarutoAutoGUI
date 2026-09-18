# activeRun 锁定配置，Start 依赖当前 Worker 的 fresh Snapshot

2026-09-18 起 MaaNOP Config 可保存多份独立 Task Configuration，但只激活一份供编辑及下次启动。
不支持运行配置 A 时浏览或编辑配置 B，也不维护下一次 Run 草稿。Start 请求在途同样锁住配置工作区。只要 Worker Snapshot 的 `activeRun != null`，即 Starting、Running 或 Stopping，GUI 就禁用全部配置操作，包括 Tab 切换、新建、删除、重命名、任务增删/排序和参数编辑。IPC 断开后若最后一次可信 Snapshot 仍显示 activeRun，编辑锁继续保持；只有重新 Connected、针对当前 workerInstanceId 取得新的完整 Snapshot，并明确看到 activeRun 为 null 后才能解除。GUI 不因失联推测 Run 已结束。

Start 只有在 Child Session 存在、Observation 为 Connected、SnapshotFresh=true、Worker 为 Ready、activeRun 为 null 且 runState 为 Idle、配置合法、至少选择一个 task、admitted 与 desired Runtime Profile Digest 一致时才启用。fresh 只要求 Snapshot requestId、workerInstanceId、schema 和内部一致性校验通过；digest 不一致时 Snapshot 仍可 fresh，以便确认 Idle 后显式替换 Worker。stale Snapshot或刚重连尚未完成同步时均禁止 Start。WorkerStarting 和 WorkerRecoveryConflict 明确禁止 Start；后者还禁止普通 Stop、Shutdown、Worker 替换和新 Worker 启动。

Stop 只在 Connected 且 activeRun 为 Starting/Running 时可用。Run 为 Stopping 时按钮禁用并显示“正在停止”；IpcDisconnected 时禁用并显示“恢复连接后可停止”；WorkerExited 或 ChildSessionEnded 不显示为可停止。`run.stop` ACK 不表示已停止，GUI 只有通过后续 `run.stateChanged` 或 fresh Snapshot 确认 Run Cancelled 后才显示“已停止”。

状态区独立显示 GUI Observation、Worker State/诊断、Run State/当前 Plan Item，并在 stale 时显示最后更新时间；Idle 与 lastRun 摘要同时展示。日志区显示全局 `log.entry`，支持实时追加、断线补取、gap 提示、级别筛选和自动滚动。清空显示只影响 GUI 的 displayEntries/displayBaseline，不清空 Worker 缓冲、不重置 transport cursor。首版继续保留 Child Session 创建、进入、隐藏、游戏启动和注销，登录与验证码由用户在 Child Session 手工完成；MFAAvalonia 不进入正常执行链路。

首版不实现游戏截图/远程控制、pause/resume、队列、并行、Run 历史持久化、依赖自动更新、多 Worker/controller/resource、通用 Maa 项目或完整 MFAAvalonia 复刻。
