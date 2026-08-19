# MaaAgentClient 拥有每个 Run 的 Python Agent 连接生命周期

NarutoAutoGUI 首版只支持当前 MaaNOP 的单对象 Agent 配置，执行字段仅限 `child_exec` 和 `child_args`。Agent 数组、多 Agent、显式 `identifier` 以及其他影响执行但未实现的字段在启动 Worker 前 fail closed；纯展示字段可以 WARN 后忽略。GUI 将这份配置放入 Worker Launch Context，但不使用主 Session PATH 预解析 child_exec。首版不提供 Python 路径覆盖设置，也不实现通用 `PI_*` Agent 环境变量；若 MaaNOP 以后实际使用这些语义，再扩展 PI 子集并纳入 Runtime Profile Digest。

Worker 初始化时在 Child Session 实际环境中执行短生命周期 Agent Probe。探测使用 Launch Context 的 child_exec，以 MaaNOP 项目根目录即 interface.json 所在目录为 working directory，使用 ProcessStartInfo.ArgumentList、`UseShellExecute=false` 和与正式 Agent 相同的环境，不经过 cmd、PowerShell 或其他 shell。探测具有明确超时并在超时后结束进程，至少报告 sys.executable 绝对路径、Python 版本、`maa`、AgentServer、Toolkit 的导入状态和 Agent 入口脚本是否存在；条件允许时还验证入口顶层模块导入，但不得调用 `AgentServer.join()` 或启动长期 AgentServer。失败使 Worker 进入带 PythonMissing、AgentModuleMissing、AgentEntryInvalid 等结构化原因的 NotReady，不搜索其他 Python、不修改 PATH、不自动安装或 pip install。

每个 Run 使用独立的 MaaTasker、MaaAgentClient 和 Python Agent，不跨 Run 复用。初始化顺序为创建 Win32 controller、按顺序加载 MaaResource、创建并绑定 MaaTasker、调用 `MaaAgentClient.Create(tasker)`，再调用 LinkStart。Agent identifier 由 MaaAgentClient 的 LinkStart 提供；Worker 只在 LinkStart 启动回调中将 identifier 作为 ProcessStartInfo.ArgumentList 的最后一个参数启动 Python Agent，GUI 和 Worker 均不得脱离 MaaFramework 自行生成。回调同时提供的 nativeAssemblyDirectory 不加入当前 MaaNOP 的 child_args。当前 `agent/main.py` 通过 `sys.argv[-1]` 取得 identifier，因此最后一个参数的位置是协议约束。

Run 成功、失败或取消后释放 MaaAgentClient 和 MaaTasker。取消时必须先确认 MaaFramework 已停止，再调用 MaaAgentClient.LinkStop，并给 AgentServer 一个固定短暂 grace period 自行退出。Agent 已退出时才正常 Dispose；仍未退出时记录 `forcedAgentTermination=true`，再通过 Dispose 或显式终止已知进程树完成强制清理。Dispose 可能直接执行整树终止，因此它属于最终资源释放或强制兜底阶段，不能放在优雅退出等待之前。该过程不关闭游戏、不退出 Worker、不注销 Child Session。

Worker 必须异步持续读取 Python Agent stdout 和 stderr，分别转为 source 为 `agent.stdout` 和 `agent.stderr` 的 LogEntry，并始终携带 runId。接收时若存在当前 Plan Item，可以附带 planItemId 和 taskName，但这只是日志上下文，不是任务状态依据。stderr 内容本身不使 Run 失败；Run 结果由 MaaFramework、Agent 连接状态和任务执行结果确定。
