# Status

## 当前阶段

第一轮正式 GUI 和 ADR 0020 的首个最小 Worker/IPC + MaaFramework 端到端切片已完成并通过交互式实机验收。`win-x64-options-v2-scroll` 在同一 Worker 上同时通过 admission、fresh Snapshot、Dependency Readiness、真实自然 Succeeded Run、真实 Running 后取消、取消后存活和再次执行；GUI 使用正式 PI 显式 option 编辑与最终 MaaNOP Config/Run Plan 路径，不含测试旁路。Supported Baseline 的 MaaNOP snapshot、Python `maa` 与 Maa.Framework 精确组合仍未冻结，须下一轮单独决定。`src/ChildSessionDemo` 的共享 baseline 仍保持冻结；正式 GUI 位于 `src/NarutoAutoGUI`，Worker 位于 `src/NarutoAutoWorker`。

## 本轮已实现

- .NET 8 WPF x64 正式主窗口和独立 RDP 子桌面预览。
- 创建/恢复、显示、隐藏、结束 Child Session；展示连接状态、RDP ConnectedState 和 `childSessionId`。
- 启动时探测已有 Child Session 并自动恢复 RDP 连接。
- 固定 `1920×1080 @ 100%`，不提供分辨率或 DPI 配置。
- 游戏 exe、启动参数和 MaaNOP exe 路径配置，保存到程序目录的 `config\settings.json`。
- 火影忍者 Online 默认通过 `QQMicroGameBox\Launch.exe -/appid:1103286479` 启动；不使用 `QQGameLauncher.exe`。工作目录自动取 exe 所在目录，不提供配置字段。
- 旧版配置若没有参数且入口为空或为 `QQGameLauncher.exe`，加载时迁移到上述正确入口；其他自定义 exe 不覆盖。
- 分别启动游戏/MaaNOP，以及“恢复/创建 → 显示 → 游戏 → MaaNOP → 保持显示”的一键启动。
- 按进程名和 Session ID 避免在当前 Child Session 中重复启动，并记录 PID/SessionId。
- 主窗口 X 隐藏到托盘；托盘提供显示主窗口、显示子桌面、结束分身和退出。
- 真正退出且 Session 存在时确认；确认后注销 Session，注销失败则取消退出。
- DEBUG/INFO/WARN/ERROR/CRITICAL 统一日志；GUI INFO+，程序目录滚动文件 DEBUG+，可从 GUI 直接打开日志目录。
- GUI 按实际 Session 状态启用创建/显示/隐藏/结束命令，并明确区分子桌面可见与已隐藏；异步操作显示进度与等待光标。
- 桌面分身的创建、显示和隐藏命令统一使用清晰的蓝色描边可用态；不可用命令保持灰色降级态，结束命令保持红色危险态。
- GUI 日志仅在用户接近底部时自动跟随；向上滚动后暂停，并显示新日志计数与恢复跟随操作。
- 主窗口提供访问键和动态状态辅助信息；操作失败给出恢复建议并可打开日志目录；每次程序运行首次关闭到托盘时显示一次通知。
- 主窗口已应用集中式 WPF 视觉设计系统：统一浅色语义令牌、字体与 4/8 DIP 间距、四级按钮、输入框、状态 Badge、日志层级和交互状态；顶部保留状态卡，其余主功能使用扁平分区、留白与细分隔线，仅日志视口保留容器边框；不改变事件处理器和功能行为。
- 正式主窗口已使用 WPF-UI 4.3.0 重构为 Windows 11 Fluent Shell：`FluentWindow`、`TitleBar`、左侧 `NavigationView` 和内置 Fluent 图标承载首页、任务、桌面分身、日志、设置五个顶层页面，操作状态与进度条固定在全局底栏。五个页面仍位于同一个 `MainWindow` XAML namescope，通过根容器 `Visibility` 切换；未引入 `Frame`、独立 Page/UserControl、MVVM、NavigationService 或 PageService。页面内输入、按钮、下拉框和日志列表仍为标准 WPF 控件并沿用 `DesignSystem.xaml`。
- 首页集中呈现当前任务、参数、Session、Worker 和 Run 的用户化摘要；任务页直接承载动态 option editor，将 Worker/PID/Snapshot/runId 等信息收进默认折叠的运行环境诊断；桌面分身操作按钮按实际状态互斥显示。设置表单改为标签在上、输入框在下，日志页取消固定高度并填满剩余页面空间；各配置页独立滚动，日志页没有外层滚动器。
- 根据实机可发现性反馈，首页和任务页均固定展示“准备运行环境 / 开始任务 / 停止任务”三个任务控制入口；当前不可执行的操作保留位置并显示禁用态，动态下一步提示同时作为任务页说明、悬浮提示和辅助功能 HelpText。任务页明确说明参数修改自动保存；现有协议仍为停止/取消本次 Run，不声明暂停后恢复能力。
- 开始任务只要求 Child Session 已连接、Worker Ready、Snapshot fresh 且任务配置有效；子桌面显示或隐藏均可开始，不再将隐藏预览作为 Run 前置条件。
- 全局 GUI、后台任务和进程异常记录；预期的启动/RDP/Win32/COM 失败不会直接使 GUI 崩溃。
- `WTSGetChildSessionId` 将成功返回 `ULONG(-1)` 或本机实测的 `ERROR_NOT_FOUND (1168)` 识别为“无 Child Session”，其他原生调用失败保留错误码并抛出；退出检查无法确认状态时继续取消退出。
- RDP 状态读取 ActiveX 实时 `ConnectedState`；所有非主动断开都会转入故障状态，后续操作重建预览宿主而不是复用失效连接。
- 同一 Windows Session 只允许运行一个 NarutoAutoGUI 正式 GUI 实例，第二实例提示后退出。
- 主窗口和托盘的 Session/程序操作使用同一个应用级操作门；退出从入口禁止新操作，等待在途操作完成，并在注销前后重新确认 Session 状态。
- 正式 GUI 使用最终 MaaNOP Config schema、真实 Project Interface 默认解析、不可变单项 Run Plan 和 Canonical Digest v1；通过用户级 Named Pipe 管理 Child Session Worker 的 admission、Snapshot、Run/Stop 和有界日志。
- 正式 GUI 从真实 PI 通用生成 global 与当前 task 的 input、switch、select、递归 active option 编辑器；显式值写入 SchemaVersion 1 `maanop-config.json`，可恢复为跟随项目默认，未激活子分支的合法显式值作为 Dormant Intent 保留。首片仍只允许一个 top-level task 和一个 Plan Item，不包含硬编码 `ServerRange`、task entry 或 pipeline override。
- 当前固定单 Win32 controller、单 resource 的 PI 子集明确拒绝尚未实现的 `resource.controller`、`task.controller/resource` 和 `option.controller/resource` 约束字段，不再接受后静默忽略；本机 MaaNOP v1.3.0 真实 `interface.json` 未使用这些字段。
- ProjectModel 保持 `ProjectPlanModule` 外部 interface 不变，将内部 definitions、Project Interface Loader 和 option Resolver 拆分到独立文件；Loader 在返回 `ProjectDefinition` 前统一校验 option 类型结构、所有 input 默认值/正则/`pipeline_type`、全部 option 引用和包含未激活 case 的完整递归图，Resolver 与配置编辑器只消费已验证的 PI 模型。
- Worker 自带固定 MaaFramework runtime，在 Child Session 中负责 Win32 Controller、MaaNOP Resource、MaaTasker 和每 Run Python Agent 生命周期；MFAAvalonia 不进入正常执行链。
- Worker 使用专用的 Task Scheduler 强化启动路径：`RunEx` 后等待新的 Worker PID 并验证 Child Session，记录 Task State 与 `LastTaskResult`，再清理临时任务；进程验证成功后 PID 写回 Admission。若进程未生成则 10 秒内失败并清理 Pending Admission；若 admission + fresh Snapshot 在 60 秒内未完成且 Worker PID 缺失或进程已退出，则自动回滚 `worker.json` 与 launch manifest，避免下一次准备环境被陈旧记录阻塞。

## 本轮自动验证

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
- build 期间 NuGet 无法访问漏洞元数据源，产生 `NU1900` 警告；包还原和编译本身成功。该警告不是代码编译错误。

以下项目需要管理员权限、可见桌面或真实外部程序，本轮自动验证不能替代手动回归，当前不声明正式 GUI 已完成实机复验：RDP ActiveX 创建/恢复、托盘交互、游戏/MaaNOP 跨 Session 启动、异常断开后重建连接、创建/启动过程中从托盘退出、退出确认取消/注销失败恢复、第二实例拦截和最终注销。

2026-08-20 五页面 Fluent UI 仍待真实 Windows 桌面人工回归：默认与最小窗口尺寸下的五页导航、键盘 Tab/访问键、页面独立滚动、日志暂停/恢复跟随、状态驱动按钮切换、托盘隐藏/恢复，以及结合真实 Child Session 的 RDP 显示/隐藏/结束流程。100%、150%、200% 缩放下的布局与文字可读性已由用户实机检查，未观察到明显裁切、重叠或可读性问题；尚未执行的其他交互项目不据此声明通过。

## 本轮交互式回归

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
- 尚未据此声明通过：Child Session 仍运行时直接选择“退出程序”并由退出流程自动注销、异常断开后重建、创建/启动过程中的并发退出。

## 已验证 baseline（冻结 Demo）

2026-08-17 已在 `src/ChildSessionDemo` 完成交互式实机复验：创建/连接/预览/注销 Child Session、固定 `1920×1080 @ 100%`、启动和验证 notepad/MFAAvalonia，以及关闭预览后的清理均通过。正式 GUI 直接编译链接该 baseline 的四个核心实现文件。

## 已知限制与 Known Issues

- 只支持 Windows x64，依赖管理员权限、交互式桌面、系统 RDP ActiveX、WTS API、Task Scheduler COM 和 WMI。
- RDP ActiveX 必须保持存活；正式 GUI 通过隐藏窗口而非关闭控件实现后台保持。
- Windows Hello/PIN 不能保证无提示复用账户密码，必要时 Windows 可能在子桌面显示凭据界面；正式 GUI 不保存密码。
- TermService 回环状态异常时可能需要重启 Windows。
- 配置与日志默认位于程序目录，适合当前解压即用发布；日志目录不可写时会回退到用户目录。配置不会静默回退，保存失败会在 GUI 和日志中明确报告。
- 幂等判断以 exe 文件名 + Session ID 为准；同一 Session 中同名但不同路径的进程会被视为已运行。
- 首次 Child Session 偶尔会出现 `CrossDeviceResume.exe` 的 Windows 系统弹窗，目前不影响功能。本轮仅记录，不修改 SystemApps、ACL、系统文件或相关系统配置。
- MaaFramework v5.8.1 会在载入时自动探测 `MaaFramework.dll` 同目录的可选 `plugins` 目录；NuGet 发布布局未创建该目录时会输出两条 `PluginMgr::load_dll` 错误。当前 MaaNOP 使用 Python Agent 而非该 demo native plugin，且 Worker 实测 Dependency Readiness=Ready，因此该日志不阻断本次 Run；后续需在固定 runtime 打包中创建空的默认探测目录以消除误导日志，不加载可选 `MaaPluginDemo.dll`。
