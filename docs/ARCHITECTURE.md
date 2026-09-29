# 当前架构

NarutoAutoGUI 是面向 MaaNOP 的 Windows x64 前端。主桌面负责配置与观察；游戏、自动化框架和 Worker 在 Child Session 中运行。发布包包含 GUI、Worker、固定 MaaFramework runtime 和 Update Engine；MaaNOP 项目内容由完整包提供。

## 组件与边界

| 组件 | 职责 |
| --- | --- |
| NarutoAutoGUI（.NET 10 WPF） | 用户界面、托盘、设置、任务配置、Child Session 生命周期、Worker 接纳和运行观察 |
| ChildSession 模块 | 通过 Windows WTS、RDP ActiveX 和 Task Scheduler 管理隔离会话与程序启动 |
| ProjectModel | 读取 MaaNOP Project Interface，保存用户意图，解析本次 Run Plan |
| Protocol | GUI 与 Worker 共享的消息、运行模型及预览帧契约 |
| NarutoAutoWorker | 在 Child Session 中持有 MaaFramework、MaaNOP Agent、运行状态、日志和只读预览采集 |
| MaaNOP.UpdateEngine（Rust） | 完整包更新的检查、准备、校验、安装和重启规则 |

GUI 不加载 MaaFramework 执行任务。Worker 不读取或修改用户任务配置，也不重新解释 Project Interface 的选项。MFAAvalonia 仅用于人工对照，不参与正式运行链路。

## 运行环境生命周期

GUI 启动时检查已有 Child Session，必要时恢复 RDP 预览宿主。创建或恢复后，程序在独立 Windows Session 中运行。隐藏桌面分身只隐藏宿主窗口，保留 RDP 连接及其中程序；结束分身或退出时通过统一入口停止相关操作并注销 Session。

GUI 以真实 Windows 进程和 Session 身份核验 Worker，再通过受限的本机 Named Pipe 接纳连接。同一 Child Session 只接纳一个 Worker。GUI 只在能证明 Worker 已不存在或 Child Session 已确认注销时丢弃 Admission，无法确认时拒绝准备；每次启动 Worker 前还会确认目标 Child Session 中没有 Worker 进程。断线时，已接纳 Worker 可继续运行；GUI 重连后取得新的完整 Snapshot 才能重新作出执行决策。Worker 不具备跨进程退出恢复 Run 的能力。

这些路径依赖管理员权限、交互式 Windows 桌面、RDP ActiveX、WTS、Task Scheduler COM 和 WMI。Child Session 的显示分辨率和缩放固定为 1920×1080、100%；预览缩放不改变隔离桌面的实际设置。

## 任务与状态流

MaaNOP Config 保存多份独立任务配置及激活项；它是用户意图，不是执行结果。ProjectModel 按当前 Project Interface 校验激活配置，开始时生成不可变、有序的 Run Plan。Worker Launch Context 独立确定项目与框架环境；Run Plan 只描述本次任务与参数。

Worker 同时只执行一个 Run，逐项处理 Plan Item。失败终止后续项，停止请求先进入 Stopping，最终结果由 Worker 和 MaaFramework 的执行结果决定。Worker 是 Worker State、Run State 和 Plan Item 状态的权威来源。GUI 把连接、进程与 Session 状况作为独立的 GUI Observation；断线不伪造 Run 失败。

控制和状态通过 Named Pipe 传递；完整 Snapshot 用于初次同步和重连，事件用于加快显示，日志另按 sequence 补取。稳定的传输契约见 [Worker IPC](WORKER-IPC.md)。

## 游戏画面预览

Preview 独立于 Active Run：Worker 使用专用只截图实例采集目标游戏窗口，GUI 只读显示。控制消息走原有 Pipe，最新像素帧经跨 Session 文件映射传递。预览不可见时暂停或释放采集；预览失败不能改变 Run、Worker 接纳或 Child Session 生命周期。

## 更新

GUI 负责更新界面、用户确认和运行环境关闭；Rust Update Engine 负责 Release 选择、完整包下载与验证、缓存和安装。GUI 将 Engine 给出的 descriptor/reference 原样交回，不解释更新包。安装前必须停止任务并确认运行环境退出；Engine 接管安装后等待 GUI 进程退出，再替换完整包内容。更新契约见 [Update Engine 文档](../src/MaaNOP.UpdateEngine/README.md)。

## 当前支持边界

支持按顺序执行不重复的顶层 MaaNOP task、多份独立配置、只读连续预览和完整包更新。当前不支持同一 task 的多实例独立参数、依赖图、条件或并行调度，也不支持自动登录、游戏画面输入控制、录制、可调预览帧率或可调 Child Session 分辨率/DPI。

设计取舍见 [ADR](adr/)；领域术语见 [CONTEXT](../CONTEXT.md)。
