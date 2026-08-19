# NarutoAutoWorker 自带固定 MaaFramework runtime

NarutoAutoWorker 使用经过 MaaNOP 端到端验证的精确 `Maa.Framework` 版本，并随发布物携带 C# Binding、Native runtime 和 AgentBinary，不从 MaaNOP 或 MFAAvalonia 目录动态加载框架 DLL。MaaNOP 目录仅提供 Project Interface、resource、图像/OCR、Python Agent 等项目内容，MFAAvalonia 继续使用自己的 runtime 作为人工诊断后备。Worker 还必须在 Child Session 实际环境中检查 Python、`maa.agent.agent_server`、`maa.toolkit`、Agent 和资源依赖，缺失时报告明确的未就绪诊断；首版不自动安装、修改系统环境、热切换 runtime 或提供多版本兼容层，具体支持版本只在实机验证后写入 Supported Baseline。

完整依赖检查只在 Worker 根据 Worker Launch Context 初始化时执行；GUI 或 IPC 重连不触发重新检查。Python/Agent 探测必须在 Child Session 中尽可能复用真实启动语义：使用 Launch Context 的 child_exec、以 MaaNOP 项目根目录为 working directory、使用 ProcessStartInfo.ArgumentList、`UseShellExecute=false`、不经过 shell，并设置超时后结束探测进程。探测至少报告 sys.executable 绝对路径、Python 版本、`maa`、AgentServer、Toolkit 和 Agent 入口状态；失败进入带 PythonMissing、AgentModuleMissing、AgentEntryInvalid 等结构化原因的 NotReady，不自动搜索 Python、修改 PATH 或安装包。

每次 `run.start` 接受前执行必要的轻量 preflight，并要求 Run Plan 的 Runtime Profile Digest 与 Worker Launch Context 一致。接受前发现问题时，Worker 进入 NotReady，拒绝请求且不创建活动 Run；Run 接受后才出现的执行环境问题归入该 Run 的 Failed。活动 Run 期间不重检、不热加载。用户显式变更 MaaNOP 项目目录、controller、resource 或 Agent 等 Runtime Profile 字段时，只能在没有 activeRun 时替换 Worker；首版不提供进程内重新初始化、文件 watcher、依赖自动修复或运行时热切换。
