# Architecture

## 当前目录

```text
NarutoAutoGUI/
├─ docs/
│  ├─ ARCHITECTURE.md
│  ├─ STATUS.md
│  └─ ROADMAP.md
└─ src/
   ├─ NarutoAutoGUI.Protocol/        # GUI/Worker 共享 IPC schema 与 framing
   ├─ NarutoAutoGUI.ProjectModel/    # Project Interface、Config 与 Run Plan
   ├─ NarutoAutoWorker/              # Child Session Worker 与固定 runtime
   └─ NarutoAutoGUI/                 # 正式 .NET 10 WPF GUI
      ├─ App.xaml(.cs)               # 单实例、应用操作门、退出、托盘、全局异常
      ├─ Views/MainWindow.xaml(.cs)  # 首页、任务、设置与 MaaNOP focus 运行日志
      ├─ Models/                     # 配置和日志模型
      ├─ Infrastructure/             # 配置、滚动日志、自动自检
      ├─ ChildSession/               # RDP/WTS/COM 实现、状态模型、生命周期与程序启动编排
      └─ scripts/                    # build/publish 与非交互自检
```

## 正式 GUI 内部的 Child Session 模块

以下已完成实机验证的实现现在直接位于 `src/NarutoAutoGUI/ChildSession/`，由正式 GUI 按 SDK 默认规则编译：

- `ChildSessionNativeMethods.cs`
- `ChildSessionProcessLauncher.cs`
- `ChildSessionService.cs`
- `RdpActiveXHost.cs`

独立 PoC 已在 2026-08-22 删除，避免正式产品通过另一个可执行项目间接拥有核心实现。迁移只调整目录与命名空间，
不改变已经实机验证的 WTS API、RDP ActiveX、Task Scheduler COM、WMI 降级和清理流程。2026-08-19 对该实现完成两项
最小生命周期修复：`WTSGetChildSessionId` 仅将成功返回 `ULONG(-1)` 或本机实测的 `ERROR_NOT_FOUND (1168)`
识别为“无 Session”，其他原生失败保留错误码并抛出；RDP `ConnectedState` 改为读取 ActiveX 实时值，所有非主动断开
都会上报给正式 GUI。

2026-08-20 为 Worker 增加独立的 `LaunchElevatedVerifiedAsync` 强化入口：游戏和普通程序使用的
`LaunchAsync`/`LaunchElevatedAsync` 提交与清理语义保持不变；只有 Worker 路径会在 `RunEx` 后暂时保留任务，
等待枚举到新的目标 PID 并验证 Session ID，同时采集 Task State 与 `LastTaskResult` 后再删除临时任务。

## 正式 GUI 调用流程

1. `App` 先取得按当前 Windows Session 区分的命名 Mutex；同一 Session 中的第二个正式 GUI 实例提示后退出。随后初始化统一日志、便携式配置、WPF 主窗口和 WinForms 托盘图标。
2. `ChildSessionManager` 在启动时通过 WTS 探测已有 `childSessionId`；API 成功并返回 `ULONG(-1)`，或原生返回本机实测的 `ERROR_NOT_FOUND (1168)`，均表示没有 Child Session；其他原生调用失败会保留错误码并进入故障状态。如存在 Session，则自动创建预览宿主并恢复 RDP 连接。
3. 创建/恢复时，Child Session 模块将 `MsRdpClient10` 连接到 `localhost`，启用 `ConnectToChildSession`，强制
   `1920×1080`、桌面/设备缩放 `100%`；`SmartSizing` 只缩放预览。
4. 子桌面窗口 X 和“隐藏”只调用 `Hide()`，不销毁 ActiveX，因而保持 RDP 和 Child Session 内程序存活。Manager 只在 ActiveX 实时 `ConnectedState == 1` 且状态为已连接时复用宿主；任何非主动断开都会进入 `Faulted`，下一次创建/显示/启动会销毁旧宿主并重新连接。
5. `ChildSessionProgramService` 先按 exe 文件名与 Session ID 检测目标进程；已运行则记录 PID 并跳过，否则从 exe
   自动推导工作目录，并将 exe、参数和工作目录传给已验证的 Task Scheduler COM
   `RunEx(TASK_RUN_USE_SESSION_ID)`。
6. 启动后使用已验证的 WMI/托管枚举流程在 10 秒内验证 PID 与 Session ID。单个启动失败被记录和呈现，不会终止
   GUI 或自动清理仍可用的 Session。
   固定微端 profile 同时接受 `Launch.exe` 或 `QQMicroGameBox.exe`，用于兼容启动器交接后快速退出，
   启动前幂等检查采用同一规则。这里只确认进程存在，游戏窗口和登录状态仍由后续运行检查确认。
7. 主窗口和托盘的 Session/程序操作共用一个应用级操作门。退出在入口立即禁止新操作并等待在途操作完成，然后在门内重新查询 Session、按原行为确认、调用 Manager 注销，并在释放资源前再次确认 Session 已不存在。Manager 内部仍先断开 ActiveX，再同步调用 `WTSLogoffSession`；主窗口 X 默认隐藏到托盘，关闭该设置后复用同一安全退出入口。
8. Worker 启动先写入 Pending Admission，再通过 Worker 专用 Task Scheduler 路径等待新 PID/Session 验证；验证成功后将 PID 写回 Admission 并继续等待 Pipe admission 与 fresh Snapshot。`RunEx` 未真正生成进程时在 10 秒内携带 Task State/`LastTaskResult` 失败并清理；60 秒 admission 超时且没有存活的已验证 Worker 时自动回滚 `worker.json` 与 launch manifest，存活 Worker 则保留 Admission 供重连。

## 配置与日志

- 侧栏展开状态保存在 `<程序目录>\config\navigation-pane.txt`，默认展开；首次加载只读取，切换时保存，
  重建窗口时恢复。读写失败只记录日志，不阻止切换；不属于 Settings Definition 或 MaaNOP 配置。
- 配置：MaaNOP project payload 与 NarutoAutoGUI 一同打包，Project root 固定为 application base directory，
  `interface.json` 位于 `NarutoAutoGUI.exe` 同级目录。火影忍者 Online 使用固定 launch profile：
  `NarutoGameLaunchProfile` 从当前用户 `%APPDATA%\Tencent\QQMicroGameBox\Launch.exe` 推导启动器路径，AppId
  固定为 `1103286479`，参数固定为 `-/appid:1103286479`，均不由用户配置；MaaNOP 用户意图保存在
  `<程序目录>\config\maanop-config.json`。SchemaVersion 2 容器保存 ActiveConfigurationId 与有序 Configurations；
  每份配置用稳定 Id 标识，Name 允许重复，独立持有 SelectedTasks 和 ExplicitOptions。SelectedTasks 保存不重复
  task name 的实际执行顺序；ExplicitOptions 继续按 option name 保存，同配置的 global 在各任务卡共享，跨配置独立。
  GUI 通过 Tab 新建、切换、重命名和直接删除配置；删除当前项选左邻、其次右邻，最后一份不能删除。
  全部配置操作服从现有运行编辑锁；Start 只解析当前配置，不引入 TaskInstanceId、Worker 配置或协议字段。
- 配置加载：明确声明的 V1 无损包装为“配置 1”，验证结构后原子保存；不因当前 PI 无法执行而丢弃旧意图。
  Active 指针无效时选第一份，空列表生成空配置；无法读取或解析时仍提供空工作区，原文件保持不变。
  此类异常原文件仅在用户真实修改触发首次替换前按原始 bytes 保存相邻 invalid 备份，备份失败不覆盖。
  不提供恢复模式或历史管理。迁移/指针修正写回失败保留加载结果并报告错误，不能宣称持久化成功。
- 工作目录不提供配置字段，统一自动使用启动器所在目录。
- 首次配置：Store 确认原文件不存在时，由 ProjectPlanModule 预置 PI 第一项到“配置 1”并原子保存，
  ExplicitOptions 为空，GUI 首次渲染即展开。已有空配置、损坏 fallback、迁移及用户新增配置不补任务。
- 新手指引：MainWindow 内单层四步 Spotlight/Popover，设置页可 replay；真实控件定位、滚动裁剪、
  键盘限制和有限 Pulse 均由 GUI 管理。Tour 单独拦截输入，不使用 Preview/Update 的原生模态钩子，
  因而保留最小化/隐藏到托盘后的同一步恢复。启动完成信号位于 Project、更新偏好和 Session 恢复之后。
  `config/onboarding.txt` 保存处理版本；首次配置保存前创建空的 `config/onboarding-new-user.pending`，
  只保留未完成新用户的跨启动资格。老用户缺完成文件不自动弹，replay 不改这两个文件或用户配置。
- 文件日志：默认写入 `<程序目录>\logs`，记录 DEBUG+；按日期命名，单文件最大 10 MB，保留 14 天。若程序目录
  不可写，则依次回退到 LocalAppData 和临时目录并记录 WARN。
- GUI 运行日志：只显示 MaaNOP 通过字符串 `focus` 明确声明的 user-facing Run Log，保留最近 1000 条；
  主窗口可直接打开当前实际日志目录。
- Worker 在 `MaaTasker.Callback` 中将匹配的字符串 `focus` 投影为 `source=maanop.run` 的既有
  WorkerLogEntry。运行动态按接收顺序倒序呈现，最新一条高亮；自动滚动跟随顶部，翻阅旧记录时暂停并保留阅读位置。
  清空只影响当前 GUI 列表，不清除文件日志或重置 Worker sequence cursor。实时 sequence gap 通过 `log.getSince`
  补取，Worker Instance 变化时 cursor 重置。
- `WorkerPreviewService` 独立于 Run 持有一个只截图 Controller，按 MaaNOP 配置截图，最高约 30 fps，
  最多一次截图在途；游戏发现和失效检查约每 2 秒执行。窗口关闭清空，重开自动恢复；暂时截图失败保帧低频重试。
  旧截图返回后才释放 Controller，预览回收不进入任务 Stop 或 Child Session 退出的等待链。
- 协议版本为 3，Pipe 名称不变；`preview.start/renew/stop` 仅传控制信息，2 秒续订、6 秒单调时钟租约。
  `start/renew` 的 `paused` 表示保留订阅和有效 Controller、停止新截图；暂停仍续订，过期/断线仍释放。
  尚未取得像素 buffer 时 GUI 每 100 ms 查询，暂停/恢复变化立即发送 renew。
  像素使用固定 925696 字节的文件映射与受限 Global Mutex 跨 Session 传输，GUI 只读打开。
  每帧为最大 640×360、保持比例的 BGR32；非阻塞锁保证完整提交，竞争时跳帧，清空请求持续重试。
- GUI 只有在启动完成、Worker Ready/Fresh、Session 匹配且首页预览可见时订阅，与 Active Run 无关。
  最小化 GUI 时清空本地显示、暂停采集并保留实例，恢复时复用同一订阅；Worker 暂停期间继续检查窗口，
  窗口失效清空并释放实例，恢复时再次检查。隐藏到托盘、离开首页、收起预览或连接失效仍撤销并清空。
  仅隐藏 RDP 宿主继续预览。
  同 Worker 重连取得 Fresh Snapshot 后建立新订阅。取消后迟到控制响应沿用 requestId 消费，不误断主 Pipe。
- `LivePreviewClient` 后台续订和读取，`PreviewPresentation` 用两个固定像素缓冲合并最新帧，
  最多一个 Dispatcher 回调在途；显示前重新检查目标代次，显示操作不持采样锁。
  GUI 复用 WriteableBitmap，放大层共用同一图像，不另采高清。身份包含 Worker、Session、订阅和目标代次，
  `sampledAtUtc` 是截图完成时间；每个目标的 revision 独立递增，不依赖 Run 或 Plan Item。
  当前实现、已通过的运行闭环/至少 30 分钟观察及剩余专项见 [验收记录](issues/preview-live-validation.md)。
- Diagnostic log 不进入 GUI 列表，继续覆盖应用/Session/RDP 生命周期、程序路径、PID、SessionId、异常堆栈
  以及 Child Session 模块返回的 Win32/COM 错误码。Preview 采样、映射或显示失败也只写限频诊断，不能改变
  Run、Worker admission、cleanup 或 Child Session 生命周期。

## 应用设置页面

`Assets/settings.json` 是随 GUI 程序集嵌入发布的内部 Settings Definition；它不属于 MaaNOP Project Interface，
不读取或保存用户状态。`SettingsDefinition` 解析并校验 section/toggle/action/info，`SettingsRegistry` 只解析显式
注册的 setting/action/value 引用，`SettingsView` 用三种通用行模板渲染，MainWindow 只挂载 View 与接入原业务入口。
定义或绑定错误记录资产路径及异常并抛出，不静默回退到空页面。

`ApplicationSettings` 注册当前的两个开关、四个动作和一个版本值；动作自带 C# 更新的状态文本、可用性和提示。
偏好继续用 `config/update-check.txt`，指引继续使用原完成版本和首次资格文件；不恢复旧 `config/settings.json`，
不合并 MaaNOP 配置。Updater/diagnostics/onboarding 的原流程只将 Settings 控件引用改为绑定对象，详情和扩展方式见
[Settings Definition](SETTINGS.md)。

## MaaNOP 完整包更新

V2 以独立 Rust Update Engine 集中 check/prepare/install。GUI 经 JSONL 短命进程调用，只保存 opaque descriptor/reference，
负责现有 UI、配置偏好、用户确认与运行环境生命周期；不下载、不解压、不管理更新文件。
Engine 读取 PI 并选择正式 Release，prepare 固定候选、校验 SHA256 和完整包，将新内容放入 cache/updater/Payload。
包必须含 Python；config/logs/debug/cache 的包内内容忽略，既有内容保留，其他根条目全部由完整包管理。

确认安装后 GUI 持有既有操作门，停止任务/Preview、注销 Child Session 并等待已跟踪 Worker 退出。
Engine 内部复制自己，实际副本完成前置检查后发送 ready；GUI 此后退出，Engine 等待 PID 结束再开始移动旧程序内容。
旧内容仅短命隔离到 cache/updater/old，全部隔离后才写入新内容；失败不回滚、不启动混合版本。
安装完成先尽力清理 old/Payload，再启动原位置 GUI；运行副本可留到下一次 prepare 清理。
安装交接后 Engine 静默等待 GUI 退出、替换文件并重启，不创建状态或进度窗口。
ready 后等待、安装或重启失败仍通过 Win32 MessageBox 提示具体错误和 updater.log 路径。
没有目录 swap、安装锁、完成 journal、长期 backup、健康确认或 .NET Updater runtime bootstrap。

正式 build.ps1 已集成 Rust；GUI baseline 和 MaaNOP 完整包分别验证。当前自动化、本地两轮完整包及真实进程证据见
[UPDATER-V2-VALIDATION](UPDATER-V2-VALIDATION.md)；已完成两轮 GUI 更新验收，剩余边界以该记录为准。

GUI 使用设置上方的侧栏更新入口、全局居中 Modal Dialog 和 Settings 更新区；检查/下载与运行状态栏分离。
打开更新窗口保持当前页面及任务配置不变，整个主窗口内容统一轻度 Blur 并覆盖半透明暗色遮罩；
更新提示红点只在发现更新时显示，检查中显示互斥的 loading indicator。Dialog 宽 440 DIP，小窗口自动收窄，最大高度为
600 DIP 与窗口高度 80% 中的较小值；Release Notes 使用 Markdig 解析为原生 WPF 文档，独立滚动区域最大高 260 DIP。
正文左对齐，文档右侧预留滚动条间距；新版本状态以版本号旁的轻量标签呈现，底部操作固定可见。
支持基础标题、列表、任务列表、强调、删除线、引用、代码、表格和 HTTP(S) 链接；不执行 HTML 或自动加载远程图片。
关闭按钮、Esc 和遮罩共用关闭入口，安装准备期间服从现有退出操作门；原生标题栏操作复用截图模态层的拦截。
版本、说明及发布说明链接由 Engine 的展示字段提供；GUI 不解析 descriptor，不恢复 V1 的一次性完成提示。
关闭弹窗不取消 Engine prepare；“取消下载”继续使用 V2 固定取消消息。


## 明确边界

当前执行计划支持把 PI 中不重复的 top-level task 按 `SelectedTasks` 顺序组成多个 Plan Item，并由同一 Worker 逐项执行；
当前项失败或用户停止时不再启动后续项。仍不支持同一 Task 多实例独立参数、DAG、依赖、条件或并行调度。Active Run 期间
提供最高约 30 FPS、允许自然降帧的只读连续 Preview。不包含自动登录/扫码、自动隐藏子桌面、自动开始 MaaNOP 任务、Worker
replacement UI、可调 Preview FPS、截图历史、录制、保存截图、画面点击控制或可调分辨率/DPI。
