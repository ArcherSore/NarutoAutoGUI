# 用系统身份接纳 Worker，并用完整 Snapshot 恢复观察

同一 Child Session 只接纳一个 Worker。GUI 依据 Pipe 对端的真实进程与 Windows Session 身份，再结合启动凭据核验 Worker；不能把可重连的 Pipe 连接或客户端自报 PID 当作身份。重连后以完整 Snapshot 恢复权威运行观察，而非重放可能丢失的事件；这增加接纳和同步逻辑，但避免错误接管另一个进程或凭不完整事件推测 Run 结果。
