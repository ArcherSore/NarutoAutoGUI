# 持久化两阶段 Worker Admission 与 Child Session 生命周期

GUI 不能只在内存中保存 Worker Instance ID 和 Launch Token，否则自身重启后无法验证旧 Worker。NarutoAutoGUI 因此原子维护 `state/worker.json`，字段仅为 workerInstanceId、launchToken、childSessionId、可空 workerPid、runtimeProfileDigest、createdAtUtc。它是当前 Child Session Worker 的最小 admission journal，不是 Worker Snapshot，不保存 Worker State、Run State、Run Plan、日志或历史。GUI 是唯一 writer；Worker 不读写该文件。Launch Token 不进入普通日志、UI、Run Plan 或 Snapshot，目录、正式文件以及临时文件替换全过程的 ACL 均限制为当前 Windows 用户。

Admission Record 使用两阶段写入。GUI 在提交 Task Scheduler COM 启动前，先以 `workerPid=null` 原子写入身份和时间，表示 Pending Admission；取得真实 PID 后再原子补写，PID 可以来自启动器返回结果，也可以直接来自 `connection.open` 的真实 Named Pipe client PID，不能要求启动器先写 PID 才允许 Worker 握手。Worker 接入仍必须同时满足真实 SessionId、当前发布包映像路径、workerInstanceId、launchToken、真实 Pipe PID 和协议兼容性。

GUI 恢复 Pending Admission 时，Observation 为 WorkerStarting；恰好一个匹配 Worker 可在全部验证后接纳并补写 PID，多个候选进入 WorkerRecoveryConflict，禁止自动选择和启动。暂未发现匹配 Worker 时不能立即删除：内部 launch/recovery grace period 内保留记录、等待延迟启动或重连，并禁止第二个 Worker；只有到期且确认没有匹配进程和有效 IPC 登记后才判定 abandoned launch，删除记录并进入 WorkerNotStarted。grace period 是实现常量，不是用户设置。Task Scheduler 启动调用明确失败且确认没有产生 Worker时，可立即删除。

已有 PID 仅作为候选定位信息。GUI 必须先验证进程存在、属于记录中的 childSessionId 且映像为当前发布包 NarutoAutoWorker；明确不匹配时记录失效。即使这些都匹配，最终接纳仍要求 `connection.open` 的 workerInstanceId、launchToken、真实 Pipe PID 和 protocolVersion 一致，以防 PID reuse。PID 合法但 IPC 未连时保留记录、等待重连并禁止重复启动。

Admission Record 只在封闭条件下删除：启动明确失败且确认未产生 Worker；Pending grace period 到期且确认无匹配 Worker；已记录 PID 被确认不存在或身份明显不匹配；强制终止后确认旧 PID 已退出；Child Session 注销成功；或人工恢复明确废弃旧 identity。IPC 暂时断开、Snapshot 超时或 Worker 尚未完成启动均不是删除理由。

创建新 Session 的顺序固定为：Named Pipe server ready，创建并确认 Child Session，生成 Worker Instance ID、Launch Token 和 Worker Launch Context，原子写一次性 Launch Manifest，原子写 Pending Admission，只有两者都成功后才通过现有 Task Scheduler COM 目标 Session 启动器启动 Worker，补写 PID并完成 `connection.open` 和 fresh Snapshot，再启动游戏并让用户进入 Child Session 登录。Manifest 写成功但 Admission 写失败时不得启动 Worker且删除 Manifest；GUI 恢复时可删除没有对应有效 Admission Record 的 orphan Manifest，因为合法 Scheduler 提交一定发生在 Admission 落盘之后。Worker初始化不依赖游戏窗口，GUI重启恢复时不自动重启游戏。

结束整个桌面分身时，activeRun 且 IPC 可用时先发 `run.stop` 并有界等待；停止超时或失败时，GUI提示“停止未确认，将通过注销 Session 强制结束执行环境”，然后仍继续 WTS Logoff，不能永久阻止用户结束。只有 WTS Logoff 失败才保留明确错误状态。MFAAvalonia 不进入任何自动启动、恢复或替换链路。
