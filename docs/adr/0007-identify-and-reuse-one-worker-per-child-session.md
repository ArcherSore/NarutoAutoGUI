# 每个 Child Session 只接纳一个具有系统验证身份的 Worker

NarutoAutoGUI 使用稳定的用户级本机 Named Pipe：主 GUI 是服务端，NarutoAutoWorker 是持续重连的客户端。Pipe DACL 仅允许当前 Windows 用户 SID 并显式拒绝 Network SID；不使用会忽略自定义 `PipeSecurity` 的 `PipeOptions.CurrentUserOnly`。正式 GUI 因 Child Session 管理以高完整性运行，Worker 因此使用现有 Task Scheduler 启动器的 Worker-specific `Highest` 路径，使 Pipe 两端完整性一致，同时满足 MaaFramework 向游戏窗口注入输入的权限需求；普通 Child Session 程序和原 PoC 仍使用不变的 LeastPrivilege 路径。Pipe connection 只是可断开、可重建的通信通道，不是 Worker identity。GUI 从 Named Pipe 连接取得真实客端 PID，再由 Windows 查询该进程的真实 SessionId，并验证它属于当前 `childSessionId` 且进程映像来自本次 NarutoAutoGUI 发布携带的 Worker；客户端载荷中自报的 PID 或 SessionId 不参与信任判断。

GUI 每次启动 Worker 时分别生成随机 Worker Instance ID 和 Launch Token，通过启动参数传入。Worker 首次登记和后续重连都通过 `connection.open` 携带二者。Worker identity 由真实 PID、真实 Windows SessionId、workerInstanceId 和 launchToken 共同确定；protocolVersion、workerVersion、MaaFrameworkVersion 只用于兼容性检查。Worker 还使用 Session-local 单实例锁，使每个 Child Session 最多存在一个可被接纳的 NarutoAutoWorker。已有有效 Worker 在线时直接复用；另一个 Worker 尝试登记时返回 `worker_already_registered`，首版不抢占、不替换也不支持多 Worker。

GUI 将当前 Worker 的 workerInstanceId、launchToken、childSessionId、可空 workerPid、runtimeProfileDigest、createdAtUtc 原子保存在 `state/worker.json`。该记录只用于身份恢复，不保存任何 Worker/Run 状态。启动前先以空 PID 写入 Pending Admission，真实 PID 可以由启动器结果或 `connection.open` 的 Pipe client PID补写。GUI 重启后先开放固定 Pipe，再验证 Child Session、PID、真实 SessionId、当前发布包 Worker 映像路径、Admission Record、`connection.open` token/instance ID 及协议/runtime profile 兼容性，接纳后立即请求完整 Snapshot。PID 只是候选定位信息，不能单独证明身份；即使 PID/session/path 均匹配，最终接纳仍要求真实 Pipe PID 与记录及 token/instance ID 全部一致。

Pending Admission 在内部 grace period 内暂未发现进程时必须保留并禁止启动第二个 Worker，以覆盖 Scheduler 已提交但 Worker 延迟启动的竞态；到期且确认无匹配进程和有效登记后才作为 abandoned launch 删除。若发现多个候选，不自动选择且禁止启动新 Worker，进入 Worker Recovery Conflict 等待人工处理。IPC 断开但已知 PID 仍存在且属于目标 Child Session 时同样保留记录并等待原 Worker重连。

Run ID 和 Run 生命周期属于 Worker，与具体 Pipe connection 无关。Launch Token 只存在于受当前用户 ACL 保护的 Admission Record 和握手路径，不进入普通日志、UI、Run Plan 或 Snapshot。Child Session 注销后，原 Worker identity、Launch Token 和 Admission Record 全部失效；迟到连接或消息必须拒绝。首版不实现复杂接管、跨 Session Worker、自动故障转移或多 Worker。
