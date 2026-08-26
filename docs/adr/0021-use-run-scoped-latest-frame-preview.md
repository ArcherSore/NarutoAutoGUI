# Preview 使用 Run-scoped latest frame，不参与执行状态

Home 游戏画面 Preview V1 是纯 QoL 功能。每个 `WorkerRuntimeExecution` 在现有
`MaaWin32Controller` 创建成功后拥有一个 `LatestFramePreview`，并在 Run 停止或 cleanup 时先停止它，再按原顺序释放
MaaFramework execution context。Preview 不创建第二个 Controller，不把 Controller 生命周期扩展到 Worker，也不改变
Run、Worker admission、Child Session、RDP、WTS、Task Scheduler 或 cleanup 的既有状态机。

`WorkerRuntimeExecution` 持有唯一的后台 producer task，从 Controller 创建成功起约每 200 ms 采样一次，并在释放
Controller 前停止和等待 producer 结束。采样 adapter 只调用现有 Controller 的 `GetCachedImage` 复制 MaaFramework
cached image，不提交额外 `Screencap()`，随后按比例缩小到不超过 640×360 并编码为 PNG。采样、缩放、编码或诊断日志
失败均被 Preview 模块吞掉；失败不清除仍有效的旧帧，也不得改变 Run outcome。停止请求会原子禁止新发布并清空缓存，
因此在途采样不能在 Stopping 后复活旧帧。

`GetCachedImage` 是 MaaFramework 对最近 cached image 的同步复制接口，不提交截图 job，也没有 cancellation/timeout API。
cleanup 必须等待当前调用返回后再释放同一个 Controller；不得用 timeout 脱离 producer 后并发释放 Controller，也不得为
此保留旧 Controller 或允许下一 Run 创建第二个 Controller。真实 Running→Stopping 和自然终态的及时结束由交互式回归确认。

Worker 只保存一个不可变 latest frame，不建立队列或历史。缓存包含 `runId`、从 1 开始且仅在 PNG 内容变化时递增的
`revision`、`sampledAtUtc`、像素宽高和 PNG bytes。`sampledAtUtc` 表示 Worker 成功复制和编码 cached image 的观察时间，
不是 MaaFramework 的物理截图时间。新帧直接替换旧帧；重复内容不推进 revision。

GUI 通过现有 Named Pipe JSON operation `preview.getLatest(runId, afterRevision)` 单飞轮询。Worker 返回 `frame`、
`not_modified` 或 `unavailable`；只有 `frame` 携带 `image/png` bytes，JSON 序列化自动将其表示为 base64。响应同时携带
`workerInstanceId` 和 `runId`，Coordinator 严格校验 schema、身份、revision 和大小，Home 在显示前再次确认当前可见页面、
Worker Instance、Run 和 generation 仍匹配。GUI 只在主窗口可见、Home 可见、连接和 Snapshot fresh，且 active Run 为
Starting 或 Running 时约每 200 ms 请求一次；Stopping、终态、断线、Worker replacement、隐藏窗口或离开 Home 都取消轮询
并恢复 Placeholder；窗口最小化同样停止请求。请求已经写入 Pipe 后发生正常取消时，Coordinator 保留有界的 requestId
tombstone 以消费并丢弃迟到 response，不能把它解释为协议损坏或因此断开 Worker IPC。

PNG hard budget 为 1400 KiB，完整 Preview response envelope implementation budget 为 2 MiB，现有 transport frame hard
limit 仍为 4 MiB。最大 PNG 经 base64 膨胀后仍在 2 MiB 预算内；Worker 在发送前按真实 JSON envelope 序列化大小执行最终
guard。V1 不增加二进制通道、压缩、分片、帧队列、录制、截图保存、点击控制、独立 Preview Window 或可配置 FPS，也不为
30/60 FPS 提前优化。

GUI 与 Worker 始终由同一版本一起构建、打包、发布和退出，因此协议版本保持 1，不增加 capability negotiation 或旧 Worker
fallback。`preview.getLatest` 的 unknown operation 继续作为普通 `invalid_request` 实现错误处理，不解释为“不支持
Preview”。本 ADR 只废止 ADR 0008 和 ADR 0016 中“首版不提供截图”的范围排除；它们的 framing、大小限制、身份、
Snapshot 权威性和“GUI/IPC 不得阻塞或改变 Run”不变量继续有效。

自动测试通过 `IPreviewFrameSource` seam 使用脚本化 adapter，验证 200 ms tick、latest-only 替换、内容去重、revision、
`sampledAtUtc`、失败隔离/限频和停止清空；协议测试覆盖 PNG/base64 预算、严格字段名、afterRevision cursor、
`not_modified` 和 stale Worker Instance 拒绝。真实 Maa cached image 与 Home 显示仍需要交互式 Run 回归。
