# Status

## 当前阶段

第一轮正式 GUI 和 ADR 0020 的首个最小 Worker/IPC + MaaFramework 端到端切片已完成并通过交互式实机验收。
`win-x64-options-v2-scroll` 在同一 Worker 上同时通过 admission、fresh Snapshot、Dependency Readiness、真实自然
Succeeded Run、真实 Running 后取消、取消后存活和再次执行；GUI 使用正式 PI 显式 option 编辑与最终 MaaNOP
Config/Run Plan 路径，不含测试旁路。Phase 1 Windows x64 release workflow 已实现并通过 `workflow_dispatch`，可生成
经自检、布局校验和 SHA256 校验的 Actions artifact；`v0.1.0-rc.2` prerelease 已创建并成为 MaaNOP Windows x64
打包的固定 frontend baseline。GitHub Release 和 Actions artifact 只发布 ZIP，不再发布独立 SHA256 sidecar；MaaNOP
baseline 内部保留固定 SHA256 用于下载校验。Python 继续按现有 MaaNOP Project Interface 使用系统 `python`，不进入发布包；当前外部
Python 语义下的 E2E 与本机回归已由用户完成，Python runtime 打包不再作为本阶段前置项。
已验证的 Child Session 实现现位于 `src/NarutoAutoGUI/ChildSession`，不再保留独立 Demo；Worker 位于
`src/NarutoAutoWorker`。

## 本轮已实现

- 2026-08-27：将火影忍者 Online 游戏启动从用户可配置项收口为固定 launch profile。新建
  `NarutoGameLaunchProfile`（AppId=`1103286479`、Arguments=`-/appid:1103286479`、ExecutablePath 从
  `Environment.SpecialFolder.ApplicationData` + `Tencent\QQMicroGameBox\Launch.exe` 推导），不再硬编码 Windows
  username，不暴露 AppId override，不读 settings.json。删除 `AppSettings`、`AppSettingsStore`、
  `settings.json` load/save/reset 流程、App 启动时"Application Settings 无效"的整套逻辑、
  `ApplyLegacyGameSettingsMigration`、Settings 页中的游戏程序 TextBox / 浏览按钮 / 启动参数 TextBox / 说明文案、
  `BrowseGameButton_Click` / `BrowseExecutable` / `PathsTextBox_LostKeyboardFocus` / `SaveSettings` /
  `TrySaveSettings`。`PrepareEnvironmentButton_Click` 改用 `NarutoGameLaunchProfile.Resolve(_logger)`；
  `LoadProject` 直接计算 `AppContext.BaseDirectory\config\maanop-config.json`。
  `ChildSessionProgramService` 错误文案去除"配置"字样，改用通用"executable 路径不能为空""指定的程序不存在"。
  启动器缺失时 `NarutoGameLaunchProfile.Resolve(logger)` 抛出面向安装的可行动错误
  "未检测到火影忍者 Online 微端启动器。请先通过 QQ 游戏平台安装或启动一次火影忍者 Online。"，并在
  diagnostic log 记录实际路径。Settings 页删除全部可配置项后只保留静态应用行为说明。
  Release build 与 build-output `--self-test` 通过，0 警告、0 错误；新增 `VerifyGameLaunchProfile` 自检
  覆盖 ApplicationData 推导、不包含 username、固定 AppId/Arguments、启动器缺失 actionable error 和
  production default。

- 2026-08-27：将 MaaNOP Project Directory 从用户可配置项收口为打包约定。删除 AppSettings.MaaNopProjectDirectory、
  Settings 页面中的 MaaNOP 项目目录 TextBox / 浏览按钮 / 说明文案、AppSettingsStore 中的
  NormalizeProjectDirectory、ReadLegacyProjectDirectory、`MaaNopExecutablePath` → Project Directory 旧迁移和
  `requireInterface` 目录校验；SchemaVersion 由 2 bump 至 3，旧 settings.json 不再兼容，不实现 migration；
  production 默认使用 `AppContext.BaseDirectory` 作为唯一 Project root，`interface.json` 从
  `NarutoAutoGUI.exe` 同级目录加载。`ProjectPlanModule.Open(projectDirectory, configPath)` 签名保留作为
  self-test 的 test seam，production 调用点传入 `AppContext.BaseDirectory`。缺失 `interface.json` 时
  ProjectInterfaceLoader 抛出“安装目录缺少 interface.json，请确认使用完整的 MaaNOP 发布包。”并附带
  `AppContext.BaseDirectory` 到 diagnostic log；不再提示“前往设置选择项目路径”。Settings 页面只保留游戏启动
  与应用行为，未留空占位。GameExecutablePath、GameArguments、Child Session、Worker、IPC、Preview、Tasks
  页面与发布打包行为未改。`MainWindow.xaml`、`MainWindow.xaml.cs`、`AppSettings.cs`、`AppSettingsStore.cs`、
  `ProjectInterfaceLoader.cs`、`SelfTestRunner.cs` XML 解析、Release build 与 build-output `--self-test`
  通过，0 警告、0 错误；新增 `interface.json` 缺失错误信息与 v3 schema 未知字段拒绝自检覆盖。

- .NET 8 WPF x64 正式主窗口和独立 RDP 子桌面预览。
- 创建/恢复、显示、隐藏、结束 Child Session；展示连接状态、RDP ConnectedState 和 `childSessionId`。
- 启动时探测已有 Child Session 并自动恢复 RDP 连接。
- 固定 `1920×1080 @ 100%`，不提供分辨率或 DPI 配置。
- 火影忍者 Online 使用固定 launch profile：`NarutoGameLaunchProfile` 从 `%APPDATA%\Tencent\QQMicroGameBox\Launch.exe` 推导启动器路径，AppId 固定 `1103286479`，参数固定 `-/appid:1103286479`，均不由用户配置。MaaNOP project payload 与 NarutoAutoGUI 一同打包，Project root 固定为 application base directory，`interface.json` 位于 `NarutoAutoGUI.exe` 同级目录。工作目录自动取启动器所在目录，不提供配置字段。
- 启动器缺失时给出面向安装的可行动错误，提示用户通过 QQ 游戏平台安装或启动一次火影忍者 Online。
- 分别启动游戏/MaaNOP，以及“恢复/创建 → 显示 → 游戏 → MaaNOP → 保持显示”的一键启动。
- 按进程名和 Session ID 避免在当前 Child Session 中重复启动，并记录 PID/SessionId。
- 主窗口 X 隐藏到托盘；托盘提供显示主窗口、显示子桌面、结束分身和退出。
- 真正退出且 Session 存在时确认；确认后注销 Session，注销失败则取消退出。
- DEBUG/INFO/WARN/ERROR/CRITICAL diagnostic log 写入程序目录滚动文件，可从 GUI 直接打开日志目录；GUI 运行日志
  只显示 MaaNOP 字符串 `focus` 投影出的 `maanop.run`，不显示 GUI/Worker/IPC/RDP 等诊断信息。
- GUI 按实际 Session 状态启用创建/显示/隐藏/结束命令，并明确区分子桌面可见与已隐藏；异步操作显示进度与等待光标。
- 首页游戏画面预览底部最多呈现两个桌面操作：单一上下文桌面可见性按钮按 ConnectedVisible / ConnectedHidden 互斥显示「隐藏桌面」或「打开完整桌面」，无 Session 时 Collapsed；「结束桌面分身」按 Session 是否存在显示，使用 destructive 样式且不作为 Primary Action。
- GUI 日志仅在用户接近底部时自动跟随；向上滚动后暂停，并显示新日志计数与恢复跟随操作。
- 主窗口提供访问键和动态状态辅助信息；操作失败给出恢复建议并可打开日志目录；每次程序运行首次关闭到托盘时显示一次通知。
- 主窗口已应用集中式 WPF 视觉设计系统：统一浅色语义令牌、字体与 4/8 DIP 间距、四级按钮、输入框、状态 Badge、日志层级和交互状态；顶部保留状态卡，其余主功能使用扁平分区、留白与细分隔线，仅日志视口保留容器边框；不改变事件处理器和功能行为。
- 正式主窗口已使用 WPF-UI 4.3.0 重构为 Windows 11 Fluent Shell：`FluentWindow`、`TitleBar`、左侧 `NavigationView` 和内置 Fluent 图标承载首页、任务、设置三个顶层页面，操作状态与进度条固定在全局底栏。三个页面仍位于同一个 `MainWindow` XAML namescope，通过根容器 `Visibility` 切换；未引入 `Frame`、独立 Page/UserControl、MVVM、NavigationService 或 PageService。页面内输入、按钮、下拉框和日志列表仍为标准 WPF 控件并沿用 `DesignSystem.xaml`。独立「桌面分身」与「日志」导航页面已删除；桌面分身的显示 / 隐藏 / 结束操作迁移到首页游戏画面预览底部，「准备运行环境」继续作为创建 / 恢复 Child Session 的主流程；详细诊断保留文件日志，首页「运行动态」继续作为普通用户唯一 GUI 日志视图，首页顶部「打开日志目录」入口保留。
- 首页集中呈现当前任务、参数、Session、Worker 和 Run 的用户化摘要；任务页只保留标题、左侧任务列表和右侧动态
  property editor，并在有内容时显示 PI task description，解析其中的 `<span>`/`<br>` 标记为换行与纯文本；
  未配置或无法加载项目时使用单一空状态代替空任务列表与空参数面板。Tasks 页面自身固定，只有动态参数区局部
  纵向滚动；任务描述是参数面板下方的独立同级区域。Tasks 不再提供运行环境诊断；用户化 Worker/Run 状态仍由
  首页与全局底栏呈现，运行细节保留在日志。设置表单改为标签在上、输入框在下。
- 首页“运行控制台”顶部以单一横向区域呈现当前任务、现有状态投影、当前 Plan Item/下一步和单一上下文主操作按钮；
  下方以等宽双列呈现内部 16:9 游戏画面与 `maanop.run` 运行动态。游戏画面在 Active Run 的 Starting/Running 期间通过
  Worker latest-frame cache 固定约 5 FPS 只读显示；Idle、Stopping、终态、断线、Worker replacement、窗口隐藏或离开
  Home 时继续显示原 Placeholder，窗口最小化也停止请求。运行动态图标只按既有日志 Level 映射，底部状态栏只投影已有
  整体、Worker、Session、IPC 连接和操作状态；Home 仍使用禁用横向滚动的外层纵向 overflow fallback。
- 首页顶部「操作」列以单一上下文主按钮呈现当前阶段需要的唯一操作（准备运行环境 / 开始任务 / 停止任务），
  模式与样式随 Child Session / Worker / Run 状态自动切换；Tasks 页只负责选择与配置，
  不再重复运行控制。当前不可执行的首页操作显示禁用态，动态下一步提示继续作为首页说明、悬浮提示和
  辅助功能 HelpText。任务参数仍保持自动保存；现有协议仍为停止/取消本次 Run，不声明暂停后恢复能力。
- 开始任务只要求 Child Session 已连接、Worker Ready、Snapshot fresh 且任务配置有效；子桌面显示或隐藏均可开始，不再将隐藏预览作为 Run 前置条件。
- 全局 GUI、后台任务和进程异常记录；预期的启动/RDP/Win32/COM 失败不会直接使 GUI 崩溃。
- `WTSGetChildSessionId` 将成功返回 `ULONG(-1)` 或本机实测的 `ERROR_NOT_FOUND (1168)` 识别为“无 Child Session”，其他原生调用失败保留错误码并抛出；退出检查无法确认状态时继续取消退出。
- RDP 状态读取 ActiveX 实时 `ConnectedState`；所有非主动断开都会转入故障状态，后续操作重建预览宿主而不是复用失效连接。
- 同一 Windows Session 只允许运行一个 NarutoAutoGUI 正式 GUI 实例，第二实例提示后退出。
- 主窗口和托盘的 Session/程序操作使用同一个应用级操作门；退出从入口禁止新操作，等待在途操作完成，并在注销前后重新确认 Session 状态。
- 正式 GUI 使用最终 MaaNOP Config schema、真实 Project Interface 默认解析、不可变单项 Run Plan 和 Canonical Digest v1；通过用户级 Named Pipe 管理 Child Session Worker 的 admission、Snapshot、Run/Stop 和有界日志。
- Worker 以 `MaaTasker.Callback` 为唯一 MaaNOP 运行日志接入点，只将与 Callback message 精确匹配的字符串
  `focus` 投影为既有 WorkerLogEntry；GUI 按 `source=maanop.run` 精确过滤。日志 cursor 按 Worker Instance 隔离，
  实时 sequence gap 不再越过缺口，而由 `log.getSince` 单飞补取；原有协议 schema 保持不变。
- 正式 GUI 从真实 PI 通用生成 global 与当前 task 的 input、switch、select、递归 active option 编辑器；显式值写入 SchemaVersion 1 `maanop-config.json`，可恢复为跟随项目默认，未激活子分支的合法显式值作为 Dormant Intent 保留。首片仍只允许一个 top-level task 和一个 Plan Item，不包含硬编码 `ServerRange`、task entry 或 pipeline override。
- 当前固定单 Win32 controller、单 resource 的 PI 子集明确拒绝尚未实现的非空 `controller.option`、`resource.option`，以及 `resource.controller`、`task.controller/resource` 和 `option.controller/resource` 约束字段，不再接受后静默忽略；本机 MaaNOP v1.3.0 真实 `interface.json` 未使用这些字段。
- ProjectModel 保持 `ProjectPlanModule` 外部 interface 不变，将内部 definitions、Project Interface Loader 和 option Resolver 拆分到独立文件；Loader 在返回 `ProjectDefinition` 前统一校验 option 类型结构、所有 input 默认值/正则/`pipeline_type`、全部 option 引用和包含未激活 case 的完整递归图，Resolver 与配置编辑器只消费已验证的 PI 模型。
- ProjectOptionResolver 按当前受支持 PI 子集输出有序 `pipeline_override` 数组：task 自身 override 先进入数组，再依次追加 global 与 task option，active nested option 紧随父 case；不再在 GUI 侧递归深合并多个 fragment。Loader 同时拒绝 select/switch 顶层 `pipeline_override`，要求其 override 位于具体 case。
- PI Loader 在生成 Win32 controller definition 前校验 `class_regex`、`window_regex` 的正则语法，以及 Maa.Framework 5.8.0 支持的 `screencap`、`mouse`、`keyboard` 方法名；Worker 仍对 Launch Manifest 做防御性正则创建/匹配超时和 MaaFramework enum 映射检查。
- `ProjectTaskChoice` 只承载 task 名称与显示标签；PI 默认 option 的合法性已成为 Loader 成功返回后的不变量，不再保留 `DefaultOnlyValid/ValidationError` 双重状态。切换 task 时仍在保存 Config 前 Resolve 新激活的 option 图，以拦截非法 dormant intent。
- ProjectModel 内部使用 `OptionDefinitionKind` 与 `PipelineValueKind` 表达已验证的 option/input 类型，不再让 Resolver、配置编辑器重复解释协议字符串；`ProjectInputValue` 统一执行默认值与显式值的 verify、正则超时、InvariantCulture int/bool 解析和类型化 `JsonNode` 转换。
- Worker 自带固定 MaaFramework runtime，在 Child Session 中负责 Win32 Controller、MaaNOP Resource、MaaTasker 和每 Run Python Agent 生命周期；MFAAvalonia 不进入正常执行链。
- WorkerRuntimeExecution 持有唯一后台 producer，复用当前 Run 的唯一 MaaWin32Controller cached image，按 200 ms tick
  缩放、PNG 编码、内容去重并只缓存最新一帧；缓存使用 runId/revision/`sampledAtUtc`/像素尺寸/PNG bytes，不创建第二个
  Controller 或帧队列，并在释放 Controller 前结束 producer。
  `preview.getLatest` 使用现有 Named Pipe JSON 的 PNG + base64，预算为 PNG 1400 KiB、完整响应 2 MiB、transport 4 MiB。
  Preview 采样、编码、IPC、GUI 解码和诊断日志失败均不改变 Run 状态、结果、取消、cleanup、Worker admission 或
  Child Session 生命周期。
- Worker 使用专用的 Task Scheduler 强化启动路径：`RunEx` 后等待新的 Worker PID 并验证 Child Session，记录 Task State 与 `LastTaskResult`，再清理临时任务；进程验证成功后 PID 写回 Admission。若进程未生成则 10 秒内失败并清理 Pending Admission；若 admission + fresh Snapshot 在 60 秒内未完成且 Worker PID 缺失或进程已退出，则自动回滚 `worker.json` 与 launch manifest，避免下一次准备环境被陈旧记录阻塞。

## 本轮自动验证

- 2026-08-27：Dashboard 操作区收敛。顶部「操作」列的三个独立按钮（准备运行环境 / 开始任务 / 停止任务）合并为单一
  上下文主按钮 `HomePrimaryActionButton`：未准备环境 → 准备运行环境（Secondary），环境 Ready 且无 active Run →
  开始任务（Primary），Run Running → 停止任务（Destructive），Starting/Stopping/busy/exit → 保持当前阶段文字
  但 Disabled。模式由 `DerivePrimaryAction()` 从 `ChildSessionSnapshot` / `WorkerCoordinatorSnapshot` /
  `RunState` / busy/exit 推导，不新增状态机，不通过 UI 文本反向判断；dispatcher 调用既有
  `PrepareEnvironmentButton_Click` / `StartRunButton_Click` / `StopRunButton_Click`。预览底部的「打开完整桌面」与
  「隐藏桌面」合并为单一 `HomeDesktopVisibilityButton`（ConnectedVisible → 隐藏，ConnectedHidden → 打开，
  无 Session → Collapsed），与 `HomeTerminateSessionButton` 水平居中排列为最多两个按钮。删除「仅启动游戏」快捷入口
  及只服务它的 `LaunchGameButton_Click` / `LaunchSingleAsync`；Prepare Environment 中的游戏启动逻辑不受影响。
  NarutoAutoGUI Release `win-x64` build 与 whitespace `--verify-no-changes` 通过，0 警告、0 错误，120 列审计无新增
  违规；build-output `--self-test` 因本机 Application Control policy `0x800711C7` 阻止载入
  `NarutoAutoGUI.ProjectModel.dll` 未能完成（与既有记录相同的环境限制，非代码回归）。未修改 Worker、IPC、ProjectModel、
  Preview 协议或 Child Session/RDP baseline；Primary Action 三种模式、Desktop toggle 两种模式、Terminate 确认、
  Session 不存在时按钮 Collapse 及跨页 Preview 轮询仍需人工回归。

- 2026-08-27：MainWindow 信息架构收缩。删除独立「日志」和「桌面分身」导航页面与对应导航项，主导航收缩为首页、
  任务、设置三页。桌面分身的显示 / 隐藏 / 结束操作迁移到首页游戏画面预览底部（复用既有
  `ShowSessionButton_Click` / `HideSessionButton_Click` / `TerminateSessionButton_Click` handler
  与确认 MessageBox），「准备运行环境」继续作为创建 / 恢复 Child Session 的主流程，不新增重复入口。
  「查看全部日志」按钮删除；首页「运行动态」继续作为普通用户唯一 GUI 日志视图，首页顶部「打开日志目录」保留，
  AppLogger、文件日志、Worker LogReceived、LogLines、MaaNOP focus 过滤和 Home auto-follow 均未改。
  清理 `MainSection` 枚举、`UpdateSessionPresentation`、`GetStateText` / `GetStateDetail`、
  `_logScrollViewer`、`ViewLogsButton_Click`、`CreateSessionButton_Click` 及 page-only
  `StatusBadgeStyle`；保留 `GetStateBadgeText` / `GetBottomSessionText` / `GetSessionStatusBrushKey`
  服务全局底栏。Preview 生命周期不受影响：`TryGetPreviewTarget` 仍以 `HomeView.Visibility` 为门槛。
  `MainWindow.xaml` / `MainWindow.xaml.cs` / `DesignSystem.xaml` XML 解析、Release build 与 whitespace
  `--verify-no-changes` 通过，0 警告、0 错误，120 列审计无新增违规；build-output `--self-test` 因本机
  Application Control policy `0x800711C7` 阻止载入 `NarutoAutoGUI.ProjectModel.dll` 未能完成（与
  2026-08-25/26 记录相同的环境限制，非代码回归）。未修改 Worker、IPC、ProjectModel、Preview 协议
  或 Child Session/RDP baseline；真实 Session 状态切换下的按钮 visibility/enabled、Show / Hide /
  Terminate 交互和跨页 Preview 轮控行为仍需人工回归。

- 2026-08-27：移除 Tasks 页 option editor 中显式值非默认时出现的“恢复项目默认”按钮及其
  `FollowProjectDefaultButton_Click` 与 `OptionDefaultTag`。`ProjectPlanModule.FollowProjectDefault`
  仍为公共 API 并保留自检覆盖；用户仍可在下拉框选择默认 case 或在输入框填回默认值。
  NarutoAutoGUI Release `win-x64` build 与 build-output `--self-test` 通过，0 警告、0 错误；未修改
  Worker、IPC、ProjectModel 或 Child Session/RDP baseline。

- 2026-08-27：修复 Tasks 页任务描述将 PI `<span>`/`<br>` 标记原样显示为文本的问题。`MainWindow.RenderDescriptionText`
  将 `<br>` 转为换行、剥离其余 HTML 标记后赋给 `TaskDescriptionText`；option/input 描述仍为纯文本，未改其渲染。
  新增 `VerifyTaskDescriptionMarkup` 自检覆盖 span/br、大写、null/空白、无标记纯文本和带 style 的 span。
  NarutoAutoGUI Release `win-x64` build 与 build-output `--self-test` 通过，0 警告、0 错误；未修改 Worker、IPC、
  ProjectModel 解析或 Child Session/RDP baseline。

- 2026-08-27：进一步压缩 Tasks 非核心信息：移除标题副文案、项目/任务/参数概览、所有“前往设置”入口和底部
  运行环境诊断；对应的 Tasks-only Worker 明细投影与“准备 Worker”事件入口一并清理，Dashboard 的准备环境、
  Run/Stop、用户化 Worker/Run 摘要、全局状态栏、日志及底层 Worker/IPC 行为未改。`MainWindow.xaml` XML 解析
  与 Tasks 单滚动区结构检查通过；Release build 0 警告、0 错误，build-output `--self-test` 通过。

- 2026-08-27：根据实机反馈收紧 Tasks 布局：空目录不再显示红色错误条或两块空编辑器；任务描述改为独立面板；
  页面外层不再滚动，仅参数列表保留纵向滚动。`MainWindow.xaml` XML 解析和结构检查通过（Tasks 根节点为 Grid，
  仅含一个参数 ScrollViewer）；使用本机 NuGet 缓存完成 Release build，0 警告、0 错误；build-output
  `--self-test` 通过。按用户要求未再次启动 GUI 或 Worker，固定窗口、长描述和 200% DPI 仍需人工视觉确认。

- 2026-08-27：完成 Tasks 页选择/配置 UI 重构。`MainWindow.xaml` 与 `DesignSystem.xaml` XML 解析通过；
  NarutoAutoGUI Release `win-x64` build 通过，0 警告、0 错误；GUI build-output `--self-test` 通过，新增覆盖
  单 task、多 task、长 label、task description 存在/缺失/null/空白、无 task options、selection 自动保存和
  显式 option intent 切换保留。静态布局 contract 覆盖 920×640、1180×760 和 1500px 宽窗口，参数 editor
  分别限制在约 240、370 和 400 DIP 内；参数区不产生横向滚动。完整自动化脚本的 GUI 阶段通过；
  Worker 阶段被本机 Application Control policy 以 `0x800711C7` 阻止载入，按用户要求未继续重试。
  真实窗口、滚轮、键盘导航和 200% DPI 视觉 E2E 仍待人工确认；未修改 Worker、IPC、Dashboard 状态机或
  Child Session/RDP baseline。

- 2026-08-26：首个可用 prerelease `v0.1.0-rc.2` 已由 run
  [32982031166](https://github.com/ArcherSore/NarutoAutoGUI/actions/runs/32982031166) 创建。locked build、GUI/Worker
  自检、发布目录与 ZIP 解包校验、Actions artifact 和 Release job 全部成功；Release ZIP SHA256 为
  `3182cfdb9926a34d4793faa06013f79e2ac8c98532aeab8fbe3c2c3783a98456`。此前 `v0.1.0-rc.1` run
  `32980682112` 的 build-package 已成功，但 Release job 因未显式传递仓库而失败；提交 `2a1d0fe` 增加 `--repo` 后由
  `rc.2` 完成验证。MaaNOP 提交 `d7b3088` 的 install run
  [32982465990](https://github.com/ArcherSore/MaaNOP/actions/runs/32982465990) 已固定下载该 Release asset；Windows x64
  明确跳过 MFAAvalonia 和独立 MaaFramework 下载，SHA、组合边界、最终 package validator 与 artifact 上传均成功，
  其余 matrix job 也全部成功，release job 因无 MaaNOP tag 正确跳过。
  `rc.2` 初次发布时附带的 `.zip.sha256` asset 已按发布策略删除；后续 workflow 只上传 ZIP。

- 2026-08-26：完成 Phase 1 Windows x64 release workflow。`workflow_dispatch` run
  [32968253563](https://github.com/ArcherSore/NarutoAutoGUI/actions/runs/32968253563) 对提交 `b41553c`
  完成 locked restore、Release build/publish、GUI/Worker 自动自检、发布目录校验、ZIP 解包复验、当时的 SHA256 sidecar 和
  Actions artifact 上传，`build-package` 全部 step 成功且无失败，tag-only `release` job 正确跳过。amend 前相同文件树的
  run [32967561275](https://github.com/ArcherSore/NarutoAutoGUI/actions/runs/32967561275) 也成功。已验证 artifact 的
  SHA256 与 sidecar 匹配、ZIP 根直接包含 `NarutoAutoGUI.exe` 且没有 wrapper，发布包包含 GUI、Worker 和所需
  Maa.Framework native runtime，不包含 Python runtime、MaaNOP、源码或顶层 build/log junk。当时尚未创建 RC、tag
  或 GitHub Release；后续结果见上方 `v0.1.0-rc.2` 记录。

- 2026-08-17：正式项目 Release `win-x64` build 通过。
- 2026-08-17：正式项目 self-contained `win-x64` publish 通过，输出到 `artifacts\NarutoAutoGUI\win-x64`。
- 2026-08-17：`--self-test` 通过，覆盖便携式配置 JSON 保存/加载往返、DEBUG 与 INFO 文件日志写入。
- 2026-08-19：补充游戏参数后 Release build 和 `--self-test` 通过；工作目录改为由 exe 自动推导，不在 GUI 或配置文件中暴露。自检覆盖旧版错误入口迁移、火影默认启动配置及三项启动配置 JSON 往返。
- 2026-08-19：默认发布目录因旧版 NarutoAutoGUI 进程正在运行而被锁定；未强制结束进程，改在 `artifacts\NarutoAutoGUI\win-x64-update` 完成 self-contained publish 和发布后自检。
- 2026-08-19：移除工作目录字段后再次完成 Release build、直接 `--self-test`、`artifacts\NarutoAutoGUI\win-x64-no-workdir` self-contained publish 及发布后自检；运行中的 GUI 实例均未被强制结束。
- 2026-08-19：完成状态驱动命令、异步反馈、日志暂停跟随、键盘/辅助信息、错误恢复和首次托盘通知后，Release build、self-contained publish 与 `--self-test` 通过；托盘通知、日志滚动和真实 Session 状态切换仍待交互式回归。
- 2026-08-19：应用主窗口视觉设计系统后，Release build、自包含 `win-x64-design-system` publish 与发布后 `--self-test` 通过；100%/150%/200% 缩放、高对比度、日志视觉层级和真实 Session 各状态仍待交互式回归。
- 2026-08-19：增强桌面分身显示/隐藏按钮的可用态辨识度后，XAML 解析、Release build、自包含 `win-x64` publish 与 `--self-test` 通过；真实 Session 状态切换下的视觉效果仍待交互式回归。
- 2026-08-19：完成主窗口去卡片化视觉调整后，XAML 解析、Release build、自包含 `win-x64` publish 与 `--self-test` 通过；顶部状态卡和日志视口边界保留，桌面分身控制、程序启动及日志外层卡片已移除，真实桌面下的视觉效果仍待交互式回归。
- 2026-08-19：完成 WTS 查询语义、RDP 实时状态/意外断开恢复、正式 GUI 单实例保护和退出生命周期门修复后，正式 GUI Release build、自包含 `win-x64-lifecycle-fixes` publish 及发布后 `--self-test` 通过；共享 baseline 的 `ChildSessionDemo` 也完成 Release build 和自包含 `win-x64-lifecycle-fixes` publish。两次构建均为 0 错误，仅出现既有 `NU1900` 漏洞元数据网络警告。
- 2026-08-19：首次实机回归确认当前 Windows 在没有 Child Session 时会让 `WTSGetChildSessionId` 返回 `ERROR_NOT_FOUND (1168)`；初版严格错误处理因此阻断创建与 fail-closed 退出。已将 1168 明确纳入“无 Session”，其他错误仍抛出；修正后正式 GUI Release build、自包含 `win-x64-lifecycle-fixes-v2` publish 和发布后 `--self-test` 通过，`ChildSessionDemo` Release build 与自包含 `win-x64-lifecycle-fixes-v2` publish 通过，等待再次实机回归。
- 2026-08-19：首个 Worker/IPC 切片完成 Debug/Release build、self-contained GUI + Worker publish 和发布后 `--self-test`；编译 0 错误，仅有环境中既有的 `NU1900` 漏洞元数据警告。
- 2026-08-19：首片显式 option 扩展完成 Debug/Release build、自包含 `win-x64-options-v1` GUI + Worker publish 与发布后 `--self-test`；覆盖真实形状的 `ServerRange=978` input、task switch/select、递归 active graph、Dormant Intent 保留/恢复、非法 input 不落盘、恢复项目默认、resolved pipeline override 和 planDigest 变化。另以本机 MaaNOP v1.3.0 真实 `interface.json` 和隔离临时 Config 验证 `AccountTraining + ServerRange=978 + ClaimLevelExp=No`，正式 Resolver 生成 `ParseServer` 参数 `978` 与 `ClaimLevelEntry.enabled=false`，未修改 MaaNOP/MFAAvalonia 配置。构建为 0 错误，仅有既有 `NU1900` 漏洞元数据网络警告；交互式 Success/Cancellation 统一验收仍待完成。
- 2026-08-19：首片验收期间发现新增 option 区使默认窗口无法访问下方内容；先增加临时整页滚动并固定日志区高度，不在验收前重做布局。`win-x64-options-v2-scroll` 完成 Release GUI + Worker publish 和发布后 `--self-test`，旧版非凭据 settings/MaaNOP Config 已复制到新包；正式 UI 重设计延后到首片统一验收之后。
- 2026-08-20：完成 WPF-UI 4.3.0 Fluent Shell 与五页面重构后，`App.xaml`、`MainWindow.xaml`、`DesignSystem.xaml` XML 解析通过，最终 NarutoAutoGUI Release `win-x64` build 通过（0 警告、0 错误）。以独立目录 `artifacts\NarutoAutoGUI\win-x64-fluent-shell` 完成 GUI self-contained publish，并对该产物运行 `src/NarutoAutoGUI/scripts/test-automated.ps1`，覆盖 settings v2/旧版迁移、PI default/explicit resolver、nested dormant intent、MaaNOP Config v1、RunPlan digest、IPC framing 和 DEBUG+ 文件日志，结果通过。另以 HEAD 旧 XAML 为基线自动比对，35 个原有 `x:Name`（含模板内命名元素）和 17 个 Click/SelectionChanged/LostKeyboardFocus 事件绑定均保留；日志 ScrollChanged 的既有 `AddHandler` 代码保持不变。
- 2026-08-20：当时环境仅安装 .NET 8 SDK，完整发布脚本在恢复面向 .NET 9 的 `NarutoAutoWorker` 时未完成；未修改 Worker，也未将该工具链失败描述为 GUI 编译失败。NarutoAutoGUI 本身的 Release build、GUI publish 和发布后自动自检均已实际通过。随后已安装 .NET 9 SDK，并在后续完整发布验证中覆盖 GUI + Worker。
- 2026-08-20：修复任务操作入口在 Fluent UI 中被状态隐藏且未出现在任务页的问题后，`MainWindow.xaml` XML 解析、NarutoAutoGUI Release `win-x64` build 和直接 `--self-test` 通过；构建 0 错误，仅有 NuGet 漏洞元数据源不可达产生的既有 `NU1900` 警告。任务页与首页的固定按钮、禁用原因提示和键盘访问仍待下一次真实桌面回归。
- 2026-08-20：根据一次 Worker `RunEx` 提交后 60 秒内没有 PID/admission 的实机失败，增加 Worker 专用进程启动验证、Task Scheduler 状态诊断及 admission 超时回滚。NarutoAutoGUI Release `win-x64`、NarutoAutoWorker Release `win-x64` 和冻结 `ChildSessionDemo` Release `win-x64` 均构建通过；GUI 直接 `--self-test` 通过。使用已安装的 .NET 9.0.315 SDK 在独立目录 `artifacts\NarutoAutoGUI\win-x64-worker-launch-fix` 完成 self-contained GUI + Worker 发布，发布后自检通过，并复制既有不含凭据的 settings/MaaNOP Config 供实机复验；构建 0 错误，仅有既有 `NU1900` 警告。新的 Worker 启动诊断与失败回滚仍待真实 Child Session 交互式回归。
- 2026-08-20：移除“隐藏子桌面后才能开始任务”的非必要前置条件及对应界面提示；Child Session 保持显示或已隐藏时均按相同的 Worker、Snapshot 与配置就绪条件启用开始任务。`MainWindow.xaml` XML 解析、NarutoAutoGUI Release `win-x64` build 和直接 `--self-test` 通过；在独立目录 `artifacts\NarutoAutoGUI\win-x64-worker-launch-fix-v2` 完成 self-contained GUI + Worker 发布、复制既有不含凭据的 settings/MaaNOP Config，并通过发布后自动自检。真实可见子桌面下的 Run 启动仍待交互式复验。
- 2026-08-21：PI Loader 对尚未实现的 resource/task/option controller/resource 约束改为 fail closed，并增加三类带准确 JSON path 的负向自检。NarutoAutoGUI Release build 和直接 `--self-test` 通过；构建 0 错误，仅有既有 `NU1900` 漏洞元数据网络警告。
- 2026-08-22：ProjectModel 将原单文件中的 definitions、PI Loader 和 option Resolver 拆分为内部实现文件，并将 option 结构、默认 input、引用和完整递归图合法性前移到 Loader。自动自检新增非法 input/case 组合、缺失 case、非法正则、默认值类型错误和未激活分支循环等负向 fixture；NarutoAutoGUI Release build 和直接 `--self-test` 通过。本机 MaaNOP v1.3.0 真实 PI 的结构/default 只读核对通过；构建 0 错误，仅有既有 `NU1900` 漏洞元数据网络警告。
- 2026-08-22：ProjectOptionResolver 将运行期 `pipeline_override` 从 GUI 递归深合并对象改为 MaaFramework 接受的有序 fragment 数组，并新增 task/global/resource/controller/task/nested 六段顺序、同节点 fragment 隔离、int/bool 精确占位符、嵌入字符串、dormant nested option 和 select 顶层 override fail-closed 自检。NarutoAutoGUI Release `win-x64` build 与直接 `--self-test` 通过，0 警告、0 错误；真实 MaaNOP Run 尚待用户验收，不据此声明交互式 E2E 已复验。
- 2026-08-22：PI Loader 补齐两个 Win32 窗口正则和三个 MaaFramework 控制方式字段的语义校验，自动自检增加五类带准确 JSON path 的负向 fixture；Worker 为不可信 Launch Manifest 保留正则创建错误和实际窗口文本匹配超时诊断。NarutoAutoGUI 与 NarutoAutoWorker Release `win-x64` build、GUI 直接 `--self-test` 均通过，0 警告、0 错误；未修改已验证的 Child Session 流程。
- 2026-08-22：删除 task catalog 的 `DefaultOnlyValid/ValidationError` 冗余状态和构造期二次默认 Resolve；非法 PI 统一在 `ProjectPlanModule.Open` 的 Loader seam 失败，task 切换仍验证可能重新激活的 dormant intent。NarutoAutoGUI Release `win-x64` build 与直接 `--self-test` 通过，0 警告、0 错误。
- 2026-08-22：ProjectModel 以内部 enum 替代 option type 与 pipeline type 字符串，并新增 `ProjectInputValue` 统一 Loader 默认值和 Resolver 显式值的校验/类型转换。自动自检新增非法显式 int/bool 不落盘覆盖；NarutoAutoGUI Release `win-x64` build 与直接 `--self-test` 通过，0 警告、0 错误。
- 2026-08-22：固定单 controller/resource 的当前 UI 对非空 `controller.option` 与 `resource.option` 改为 fail closed；Loader 接受字段缺失或空数组，合法 `ProjectDefinition` 和 Resolver 不再携带不可编辑的 scope。自动自检相应收口为 task/global/task/nested 有序 fragment，并新增两个 option scope 负向 fixture；NarutoAutoGUI Release `win-x64` build 与直接 `--self-test` 通过，0 警告、0 错误。
- 2026-08-22：将四个已验证的 Child Session 核心实现迁入正式 GUI 的 `ChildSession` 目录，删除不再使用的独立 Demo、
  发布脚本和项目文件，并移除 MSBuild 源码链接。全仓库手写 C#、XAML、PowerShell 与项目/配置文件完成 120 列审计，
  可在 120 列内完整表达的 C# 调用、声明和 XAML 起始标记已收回单行；GUI 与 Worker Release `win-x64` build、
  GUI 直接 `--self-test`、Roslyn whitespace `--verify-no-changes` 均通过，0 警告、0 错误。自动验证不包含需要真实桌面
  的 Child Session 交互式回归；对应手动回归见下方记录。
- 2026-08-24：完成 MaaNOP 字符串 `focus` 运行日志接入、GUI user-facing source 过滤、Worker Instance cursor 重置、
  sequence gap 补取和 `log.getSince` 响应预算。GUI 与 Worker Release `win-x64` build 均为 0 警告、0 错误；在独立
  `artifacts\NarutoAutoGUI\win-x64-maanop-run-log` 目录完成 self-contained GUI + Worker publish，发布后 GUI 自检
  覆盖 cursor/source 过滤，Worker 自检覆盖 focus 投影与响应预算，结果全部通过。真实 MaaNOP focus Run、断线补取和
  Worker Instance 替换尚未执行交互式回归，不据此声明 E2E 已验证；Child Session/RDP baseline 未修改。
- 2026-08-24：MaaNOP Run Log 代码审查修复后，Callback 退订失败不再改变 Run outcome；Coordinator 对 live/recovered
  日志串行发布，Child Session 结束或 recovery 期间 Pipe EOF 会取消旧连接的在途补取，补取失败则只在
  active connection 上延迟重试。新增真实 Named Pipe 脚本化自检，覆盖一次补取失败后重试、恢复期间新增事件、
  sequence 顺序、recovery 期间 Pipe EOF 后同 Worker Instance 重连、eviction gap 恢复，以及 teardown 后忽略旧响应；
  Worker 自检改为经 Callback Adapter 验证 `maanop.run` 输出、Run 关联、告警
  限频、UTF-8 截断和并发 sequence，GUI 自检覆盖 timestamp/source 路由与 diagnostic 文件保留。最终 GUI/Worker
  Release build、build-output GUI/Worker 自检、Roslyn whitespace 和 120 列检查均通过；最终 self-contained 产物已生成，
  但从该目录运行 DLL 被本机 Application Control policy 阻止，因此不声明最终发布目录自检通过。真实 MaaNOP Run 与
  Worker Instance replacement 交互式回归仍待执行，Child Session/RDP baseline 未修改。
- 2026-08-25：MaaNOP Run Log 自检覆盖度修复。Worker focus 投影自检不再直接调用
  `MaaRunLogFormatter.Format`，改为经 `MaaRunLogAdapter.Handle` 缝隙验证输出、过滤和告警
  限频（spec line 177）；`WorkerLogSequenceTracker` 自检补充 different-instance cursor 重置后
  的 gap 检测（spec line 199）；`WorkerCoordinatorSelfTest` 的 recovery 验证扩展为单次补取
  flight 内多次 live gap event 追平（spec line 194），后续 disconnect/teardown 测试序号同步
  调整。build-output GUI `--self-test` 通过。
  GUI/Worker Release build 和 build-output 自检均通过，0 警告、0 错误；Child Session/RDP
  baseline 未修改。
- 2026-08-25：完成 Home“运行控制台”信息架构、画面 Placeholder、Level 图标运行动态和底部全局状态栏后，
  `MainWindow.xaml` XML 解析、NarutoAutoGUI Release `win-x64` build 和 build-output `--self-test` 通过，
  构建 0 警告、0 错误。当前 Theme、其他四页、Worker/IPC 协议和 Child Session/RDP baseline 未修改；
  920×640、100%/150%/200% 缩放及真实状态切换下的视觉与键盘回归仍待交互式执行。
- 2026-08-25：为 Home 恢复外层纵向 overflow fallback，并将下方双列约束为 1180×760 正常视口的既有高度，
  避免外层无限测量吞并运行动态列表的独立滚动。XAML contract 与 WPF measure harness 覆盖 920×640、1180×760、
  长任务标签、长 option 摘要、长下一步状态、运行动态内外层滚动范围，以及 PerMonitorV2 下 200% DPI 位图渲染；
  NarutoAutoGUI Release `win-x64` build 与 build-output `--self-test` 通过，0 警告、0 错误。真实窗口视觉、滚轮、
  键盘和跨显示器 DPI 切换仍待交互式回归。
- 2026-08-25：修复 `LogLines` 非公开导致 WPF 无法绑定首页“运行动态”和日志页的问题，并增加
  `TypeDescriptor` 可发现性回归断言。NarutoAutoGUI Release `win-x64` build 通过，0 警告、0 错误；新增断言通过后，
  完整 build-output 自检在后续既有 Named Pipe 场景因当前权限返回 `Access denied`，因此不声明完整自检通过。
  当时正在运行的真实 E2E GUI、Worker 和 Child Session 未被停止或替换；修复后的真实窗口显示已于 2026-08-26
  完成交互式复验。
- 2026-08-26：完成 ADR 0021 Active Run latest-frame Preview V1。Worker 自检通过脚本化 frame source 覆盖 200 ms tick、
  内容去重、revision、`sampledAtUtc`、失败限频、停止清空、在途帧拒绝和 PNG/base64 响应预算；GUI 自检通过真实
  Named Pipe 覆盖 afterRevision、`not_modified`、严格 schema 和 stale Worker Instance 拒绝。NarutoAutoGUI 与
  NarutoAutoWorker Release `win-x64` build 均通过，0 警告、0 错误；最终 Worker build-output DLL 自检通过。GUI 协议与
  Coordinator 自检在本轮较早 build-output 通过；最终重建后的 GUI DLL 在仓库路径和隔离临时副本均被本机 Application
  Control policy 以 `0x800711C7` 阻止载入，因此不声明最终 GUI build-output 自检通过。真实 Maa cached image、Home
  连续显示、页面/窗口可见性切换和 Running→Stopping 仍待消费级电脑上的交互式 Run 回归；Roslyn whitespace、120 列
  和 `git diff --check` 已通过，未修改 Child Session/RDP/WTS/Task Scheduler baseline。
- 2026-08-26：ADR 0021 Preview 代码审查非 P1 修复。恢复 8 处装饰性换行为 120 列内单行
  （`LatestFramePreview`、`WorkerHost`、`WorkerRuntimeExecution`、`WorkerSelfTestRunner`、
  `MainWindow.xaml.cs`、`WorkerCoordinatorSelfTest`）；Coordinator `ValidatePreviewResponse`
  的 `unavailable` 分支增加 run identity 校验，拒绝携带非空且不属于当前请求 runId 的
  unavailable 响应（null runId 仍接受以表示 `no_active_run`）。Coordinator 自检新增三类
  Preview 负路径：unavailable 错误 runId 拒绝、unavailable null runId 接受、frame 错误 runId
  拒绝；Worker 自检新增 2 MiB 响应预算拒绝边界和 4 MiB transport write-before-send guard。
  NarutoAutoGUI 与 NarutoAutoWorker Release `win-x64` build 均通过，0 警告、0 错误；Worker
  build-output DLL 自检与 GUI build-output DLL 自检均通过，`git diff --check` 通过。未修改
  Preview producer 无界 cleanup wait 和 Preview request cancellation 后迟到 response 两个 P1
  问题，留待后续单独设计。Child Session/RDP/WTS/Task Scheduler baseline 未修改。
- 2026-08-26：ADR 0021 Preview P1 复核与 IPC 修复。Coordinator 对调用方已取消或超时、但可能已经写入 Pipe 的请求
  保留有界 requestId tombstone；迟到 response 会被消费并丢弃，不再作为无法关联的 envelope 断开 Worker IPC。
  真实 Named Pipe 自检覆盖取消 Preview、发送迟到 response、随后继续完成下一次 Preview 请求；NarutoAutoGUI Release
  `win-x64` build 与 build-output GUI `--self-test` 均通过，0 警告、0 错误。另结合 MaaFramework 官方接口与实现复核：
  `GetCachedImage` 只同步复制最近 cached image，没有 cancellation/timeout API；为保持单 Controller 与释放安全，cleanup
  继续先等待 producer 结束再释放 Controller，不增加会并发释放或遗留旧 Controller 的 timeout。真实 Maa cached image、
  Running→Stopping 和自然终态的及时结束仍作为交互式回归项，由用户在目标机器验证。Child Session baseline 未修改。
- 2026-08-26：按最新 AGENTS.md 代码风格整改全仓库手写 C#。通过精确 `.editorconfig`（`csharp_new_line_before_open_brace`
  列出 types/methods 等声明块、排除 control_blocks，并设 `csharp_new_line_before_catch/else/finally = false`）以
  `dotnet format whitespace` 将控制流（if/foreach/while/for/switch/try/using/lock）左大括号改同行、`} else`/`} catch`/`}
  finally` 合并，声明块保持换行；同步将机械逐参数/逐条件换行压缩为每行多项，行尾统一 LF。已验证流程（Child Session、
  RDP ActiveX、WTS、Task Scheduler COM、分辨率/缩放、进程 Session 验证与清理）仅改格式不改逻辑。NarutoAutoGUI 与
  NarutoAutoWorker Release `win-x64` build 均通过，0 警告、0 错误；全仓库 120 列检查 0 违规、`dotnet format whitespace
  --verify-no-changes` 与 `git diff --check` 均通过。
- build 期间 NuGet 无法访问漏洞元数据源，产生 `NU1900` 警告；包还原和编译本身成功。该警告不是代码编译错误。

以下项目需要管理员权限、可见桌面或真实外部程序，自动验证不能替代手动回归。Child Session 真实桌面交互式回归已完成；游戏/MaaNOP 跨 Session 启动、异常断开后重建连接、创建/启动过程中并发退出等外部程序或故障场景仍需按后续目标单独记录。

2026-08-20 五页面（已收缩为首页 / 任务 / 设置三页）Fluent UI 的部分真实 Windows 桌面人工回归仍待补齐：默认与最小窗口尺寸下的三页导航、键盘 Tab/访问键、页面独立滚动、日志暂停/恢复跟随和状态驱动按钮切换（含首页 Show / Hide / Terminate Session 按钮的 visibility/enabled）。100%、150%、200% 缩放下的布局与文字可读性已由用户实机检查，未观察到明显裁切、重叠或可读性问题；结合真实 Child Session 的 RDP 显示/隐藏/结束流程已在 2026-08-22 完成回归。

## 本轮交互式回归

- 2026-08-26：用户使用包含 `LogLines` Binding 修复的正式 `artifacts\NarutoAutoGUI\win-x64` 产物完成真实 E2E
  验收，并确认首页“运行动态”会实时显示 MaaNOP `focus` 日志。GUI 创建 Child Session 31，验证 Worker PID 33420，
  Dependency Readiness=Ready；真实 `AccountTraining` Run `db74b582-0789-4ba9-85d9-913f0d54bd8a` 从服务器 979
  处理到 1012（31/31），文件日志记录 `maanop.run` 至 Worker sequence 288，最终自然终结为 Succeeded。验收后
  Child Session 31 已正常注销。此次不额外声明断线补取、Worker Instance replacement 或其他 Fluent UI 交互项通过。
- 2026-08-20：Fluent UI 完整包首次在 Child Session 20 提交 Worker 后未在 60 秒内完成 admission + fresh Snapshot，Admission Record 中没有 Worker PID；结束/重建环境后再次启动成功。该结果证明问题具有偶发性，也暴露出原实现缺少 `RunEx` 后 PID 验证与超时回滚；对应强化修复已进入 `win-x64-worker-launch-fix`，等待复验。
- 2026-08-20：用户在真实 Windows 桌面检查五页面 Fluent UI 的 100%、150%、200% 缩放，三档均未观察到明显布局或文字可读性问题。
- 2026-08-20：在 GUI-only 的 `win-x64-fluent-shell` 产物点击“准备运行环境”时，MaaNOP v1.3.0 已成功加载且 Child Session 21 已连接，随后因产物中缺少 `worker\NarutoAutoWorker.exe` 明确失败。该结果属于不完整测试产物的发布问题，不是 Worker 启动、Named Pipe、RDP 或 MaaNOP 运行时失败；完整 GUI + Worker 发布仍待具备 .NET 9 SDK 后通过正式脚本重新生成。
- 2026-08-19：`win-x64-lifecycle-fixes-v2` 已实机验证创建 Child Session、从托盘结束桌面分身、随后从托盘退出主程序，流程正常。
- 2026-08-19：`win-x64-lifecycle-fixes-v2` 已实机验证同一 Windows Session 启动第二个正式 GUI 时会明确拦截，第二实例不进入主窗口，第一实例及其 Child Session 不受影响。
- 2026-08-19：ADR 0020 首片实机验证 GUI 以 Task Scheduler Worker-specific Highest 路径在 Child Session 17 启动 Worker instance `99a9261d-ec54-48f9-a88d-f9218ae65ef5`；Named Pipe admission、真实 PID 37064/Session 17 验证、fresh Snapshot 和 Dependency Readiness=Ready 均通过。实测 MaaFramework Binding=5.8.0.0、Runtime=v5.8.1，Python Agent probe 使用 `D:\miniconda3\python.exe` 并成功 import 必需模块。GUI 收到 Worker lifecycle、admission 和 readiness `log.entry`。
- 2026-08-19：隐藏 Child Session 后从主 GUI 发送真实单项 `AccountTraining` Run（runId `82f1c3d5-0899-44b5-916c-952a12b4ed20`，planDigest `sha256:947faa82a1bfa2283b720344e9504eb9419bc56bc9adda62458785c8493367b3`），已实测贯通 Named Pipe、Child Session Worker、目标 HWND、Win32 Controller、MaaNOP Resource、MaaTasker 和 Python Agent：目标游戏 PID 38332/Session 17，Agent PID 4184 成功连接，MaaFramework jobId 200000001。该 Run 在真实 pipeline `ClaimLevel -> ClickOnWelfare` 因模板识别连续失败而终结为 `Failed`；MaaFramework 日志记录 `Tasker.Task.Failed`，Agent 随后退出，游戏 PID 11144/38332、Worker PID 37064 和 Child Session 17 均保持运行。正式配置确为 `ExplicitOptions={}`，PI Resolver 使用 `ClaimLevelExp.default_case=Yes`；而本机 MFAAvalonia 已保存配置中的 `ClaimLevelExp` 为索引 1（`No`），会通过正式 PI override 禁用 `ClaimLevelEntry`。因此历史手工链路与本次 default-only Run 并非同一计划，当前证据没有指向 Resolver 错合并。`ClickOnWelfare` 的具体失败属于 MaaNOP 脚本/资源或游戏前置条件的下游问题，不作为 NarutoAutoGUI GUI 缺陷在本仓库修复；该结果仍仅作为调试证据，按验收规则是 partial，不计入 Success scenario 通过，`AccountTraining` 也尚不能认定为合适的纯默认 Success fixture。
- 2026-08-19：首片 Cancellation scenario 已完整实机通过。主 GUI 在 fresh Snapshot 中观察到 Run 与唯一 Plan Item 均为 Running 后，对真实 `AccountTraining` Run `abe80566-2eb8-412d-b7b9-acf87cf25266` 发送 `run.stop`；Worker 与 GUI 均记录 `stop_requested`，随后 Worker 记录 `MaaFramework Stop 已确认`，GUI 交互观察到 Stopping，最终 Snapshot 为 Run/Plan Item Cancelled、`activeRun=null`、`lastRun.state=Cancelled`、Worker Ready。该 Run 由同一 Worker PID 37064 接受，创建新的 Agent PID 6132 并提交 MaaFramework jobId 200000067；终结后 Agent 已退出，原游戏 PID 11144/38332、同一 Worker PID 37064 和 Child Session 17 均仍存活。该第二次真实 Run 也证明同一 Worker 能在前一个真实 Run 终结并释放 execution context 后再次接受 Run；Cancellation、取消后存活和 Worker 复用验收项记为通过。
- 2026-08-19：`win-x64-options-v2-scroll` 的真实 Success scenario 已通过。最终 SchemaVersion 1 Config 选择 `AccountTraining`，以正式 ExplicitOptions 设置 `ServerRange=978`，并显式关闭 `ClaimLevelExp`、`ClaimInfiniteIllusion` 与 `ClaimMail`；Run `16c48275-2f8b-4c04-885f-e38ff3cf3fe6`（planDigest `sha256:9ca9dfd9c8c0b466183c0302cad20e8c111d95d3a2ee1c6ec84a3dcccdd7a1e2`）在隐藏 Child Session 18 后由主 GUI 接受，真实找到游戏 PID 16728/HWND `0x104CE`，启动并连接 Python Agent PID 34244，提交 MaaFramework jobId 200000001，最终不经 Stop 自然终结为 Succeeded。终结后 Agent PID 已退出，Worker PID 29960、游戏 PID 16728/33224 和 Child Session 18 均仍存活。Success 能力记为通过。
- 2026-08-19：同一 `win-x64-options-v2-scroll` Worker 的 Cancellation/复用回归已通过。Success Run 终结后未重新准备或替换 Worker，主 GUI 再次隐藏同一 Child Session 18，并由同一 Worker PID 29960 接受真实 `AccountTraining` Run `f86ba849-d556-443c-bfd2-0686562be705`（planDigest `sha256:2c1a17506a67d6bb7505dd5a1078d012c49a0a9ee72fe0d9e27aced591ff92fc`）；该 Run 重新找到同一游戏 PID/HWND，创建新 Agent PID 38356 并提交 MaaFramework jobId 200000036。用户在 Run 与唯一 Plan Item 均进入 Running 后显式发送 `run.stop`，GUI/Worker 记录 `stop_requested`，随后 Worker 记录 `MaaFramework Stop 已确认`，最终 Run/Plan Item 为 Cancelled、`activeRun=null`、`lastRun.state=Cancelled`、Worker Ready。Agent PID 38356 已退出，Worker PID 29960、游戏 PID 16728/33224 和 Child Session 18 均继续存活。至此 ADR 0020 的 Success、Cancellation、取消后存活和同 Worker 再次执行条件全部满足，首个真实 E2E vertical slice 由 partial 更新为 PASS。
- 2026-08-22：用户完成真实 Windows 桌面的正式 GUI Child Session 交互式回归。迁移后的
  `src/NarutoAutoGUI/ChildSession` 实现已完成真实桌面复验，覆盖 RDP Child Session 创建/恢复、显示/隐藏、结束分身、
  托盘相关入口和退出注销流程；本次不额外声明异常断开后重建、创建/启动过程中的并发退出或外部游戏/MaaNOP 启动故障场景。

## 已验证 Child Session baseline

2026-08-17 已通过当时的独立 Demo 完成交互式实机复验：创建/连接/预览/注销 Child Session、固定
`1920×1080 @ 100%`、启动和验证 notepad/MFAAvalonia，以及关闭预览后的清理均通过。2026-08-22 将四个核心实现
文件迁入 `src/NarutoAutoGUI/ChildSession` 并删除独立 Demo；迁移只调整所有权、目录和命名空间，不改变已验证流程。

## 已知限制与 Known Issues

- 只支持 Windows x64，依赖管理员权限、交互式桌面、系统 RDP ActiveX、WTS API、Task Scheduler COM 和 WMI。
- RDP ActiveX 必须保持存活；正式 GUI 通过隐藏窗口而非关闭控件实现后台保持。
- Windows Hello/PIN 不能保证无提示复用账户密码，必要时 Windows 可能在子桌面显示凭据界面；正式 GUI 不保存密码。
- TermService 回环状态异常时可能需要重启 Windows。
- 配置与日志默认位于程序目录，适合当前解压即用发布；日志目录不可写时会回退到用户目录。配置不会静默回退，保存失败会在 GUI 和日志中明确报告。
- 幂等判断以 exe 文件名 + Session ID 为准；同一 Session 中同名但不同路径的进程会被视为已运行。
- 首次 Child Session 偶尔会出现 `CrossDeviceResume.exe` 的 Windows 系统弹窗，目前不影响功能。本轮仅记录，不修改 SystemApps、ACL、系统文件或相关系统配置。
- MaaFramework v5.8.1 会在载入时自动探测 `MaaFramework.dll` 同目录的可选 `plugins` 目录；NuGet 发布布局未创建该目录时会输出两条 `PluginMgr::load_dll` 错误。当前 MaaNOP 使用 Python Agent 而非该 demo native plugin，且 Worker 实测 Dependency Readiness=Ready，因此该日志不阻断本次 Run；后续需在固定 runtime 打包中创建空的默认探测目录以消除误导日志，不加载可选 `MaaPluginDemo.dll`。
