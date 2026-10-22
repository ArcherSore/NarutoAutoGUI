# 当前架构

NarutoAutoGUI 是面向 MaaNOP 的 Windows x64 前端。主桌面负责配置与观察；游戏、自动化框架和 Worker 在 Child Session 中运行。发布包包含 GUI、Worker、固定 MaaFramework runtime 和 Update Engine；MaaNOP 项目内容由完整包提供。

## 组件与边界

| 组件 | 职责 |
| --- | --- |
| NarutoAutoGUI（.NET 10 WPF） | 用户界面、托盘、设置、任务配置、Child Session 生命周期、Worker 接纳和运行观察 |
| ChildSession 模块 | 通过 Windows WTS、RDP ActiveX 和 Task Scheduler 管理隔离会话与程序启动 |
| ProjectModel | 读取 MaaNOP Project Interface，保存用户意图，解析本次 Run Plan |
| Protocol | GUI 与 Worker 共享的消息与运行模型 |
| NarutoAutoWorker | 在 Child Session 中持有 MaaFramework、MaaNOP Agent、运行状态和日志 |
| MaaNOP.UpdateEngine（Rust） | 完整包更新的检查、准备、校验、安装和重启规则 |

GUI 不加载 MaaFramework 执行任务。Worker 不读取或修改用户任务配置，也不重新解释 Project Interface 的选项。MFAAvalonia 仅用于人工对照，不参与正式运行链路。

## 运行环境生命周期

GUI 启动时检查已有 Child Session，必要时恢复 RDP 预览宿主。创建或恢复后，程序在独立 Windows Session 中运行。隐藏桌面分身只隐藏宿主窗口，保留 RDP 连接及其中程序。

用户可在设置中保存用于 Child Session 登录的 Windows 账户密码。凭据只存于 Windows 凭据管理器，建立 RDP 连接时读取并提交给 ActiveX；设置页也会读取凭据以显示保存状态和账户。账户和密码不进入配置、日志或诊断包；登录是否成功以 RDP 登录完成为准。未保存或无法读取时保持 Windows 原有的密码提示。

RuntimeEnvironment 是运行环境生命周期的唯一所有者：持有应用操作闸门，负责准备与结束运行环境。准备只创建或恢复 Child Session、连接 RDP 并接纳 Worker，不自动启动游戏。游戏可在分身中手动启动，也可由开始任务时执行的 Project Interface pretask 启动。退出、托盘、主窗口恢复操作与安装更新都以同一顺序结束运行环境：先在时限内正常停止 Active Run（失败时以注销恢复），再注销 Child Session，并在时限内确认 Child Session 已结束、已跟踪的 Worker 已退出，之后才丢弃 Admission。无法确认时如实报告"结束未确认"并保留 Admission，可再次结束。结束期间主窗口显示进度并暂停其他命令。

GUI 以真实 Windows 进程和 Session 身份核验 Worker，再通过受限的本机 Named Pipe 接纳连接。同一 Child Session 只接纳一个 Worker。GUI 只在能证明 Worker 已不存在或 Child Session 已确认注销时丢弃 Admission，无法确认时拒绝准备；每次启动 Worker 前还会确认目标 Child Session 中没有 Worker 进程。断线时，已接纳 Worker 可继续运行；GUI 重连后取得新的完整 Snapshot 才能重新作出执行决策。Worker 不具备跨进程退出恢复 Run 的能力。

这些路径依赖管理员权限、交互式 Windows 桌面、RDP ActiveX、WTS、Task Scheduler COM 和 WMI。Child Session 的显示分辨率和缩放固定为 1920×1080、100%；预览缩放不改变隔离桌面的实际设置。

## 任务与状态流

MaaNOP Config 保存多份独立任务配置及激活项；它是用户意图，不是执行结果。ProjectModel 按当前 Project Interface 校验激活配置，开始时生成不可变、有序的 Run Plan。Worker Launch Context 独立确定项目与框架环境；Run Plan 只描述本次任务与参数。

每份 Task Configuration 可添加或移除 Project Interface 声明的 pretask 并设置选项。ProjectModel 按项目声明顺序把选中的 pretask 放在普通任务之前；pretask 的选项只生成程序参数，不合并到 Pipeline Override。Worker 在 Child Session 中执行程序，工作目录为项目根目录；成功后才为普通任务创建 MaaFramework Controller。仅含 pretask 的 Run 不创建 Controller，成功启动的游戏进程保留。

Worker 同时只执行一个 Run，逐项处理 Plan Item。pretask 非零退出、启动失败或超时均终止后续项；每项外层超时为 180 秒。停止或超时后，Worker 最多等待 30 秒确认程序及其受控后代已结束；无法确认时进入 Faulted 并保留 Active Run，不报告执行终态。停止请求先进入 Stopping，最终结果由 Worker、程序退出状态和 MaaFramework 的执行结果决定。Worker 是 Worker State、Run State 和 Plan Item 状态的权威来源。GUI 把连接、进程与 Session 状况作为独立的 GUI Observation；断线不伪造 Run 失败。

停止未确认或 Run 后台循环出现意外异常时，Worker 进入 Faulted，Active Run 保持原状态、不终结。此时执行终止未确认；Worker 失联且进程未被证明已退出，或上次结束运行环境未确认，也是如此。执行终止未确认期间，GUI 不提供重试、开始或准备，只提供结束桌面分身作为恢复方式；Worker 重新连接或其进程确认退出后恢复正常控制。

控制和状态通过 Named Pipe 传递；完整 Snapshot 用于初次同步和重连，事件用于加快显示，日志另按 sequence 补取。稳定的传输契约见 [Worker IPC](WORKER-IPC.md)。

## 桌面分身观看

观看复用已有 RDP 控件及独立窗口，不经 Worker 额外采集或共享像素帧。小窗默认置顶只读，大窗仍只读，用户手动解锁后才可操作。隐藏、重新打开及返回小窗恢复只读；窗口变化不重建连接、不停止任务。登录凭据输入阶段保留交互，登录完成立即锁定。任务识别所需截图仍由 MaaFramework 执行。

## 更新

GUI 负责更新界面、用户确认和运行环境关闭；Rust Update Engine 负责 Release 选择、完整包下载与验证、缓存和安装。GUI 将 Engine 给出的 descriptor/reference 原样交回，不解释更新包。安装前必须停止任务并确认运行环境退出；Engine 接管安装后等待 GUI 进程退出，再替换完整包内容。更新契约见 [Update Engine 文档](../src/MaaNOP.UpdateEngine/README.md)。

## 当前支持边界

支持按顺序执行不重复的顶层 MaaNOP task、Project Interface pretask、多份独立配置、桌面分身只读观看与手动操作和完整包更新。当前不支持同一 task 的多实例独立参数、依赖图、条件或并行调度，也不内置 QQ 登录或游戏登录资源、托管启动入口、录制或可调 Child Session 分辨率/DPI。

设计取舍见 [ADR](adr/)；领域术语见 [CONTEXT](../CONTEXT.md)。
