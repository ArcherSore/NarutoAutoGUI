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
- 全局 GUI、后台任务和进程异常记录；预期的启动/RDP/Win32/COM 失败不会直接使 GUI 崩溃。
- `WTSGetChildSessionId` 将成功返回 `ULONG(-1)` 或本机实测的 `ERROR_NOT_FOUND (1168)` 识别为“无 Child Session”，其他原生调用失败保留错误码并抛出；退出检查无法确认状态时继续取消退出。
- RDP 状态读取 ActiveX 实时 `ConnectedState`；所有非主动断开都会转入故障状态，后续操作重建预览宿主而不是复用失效连接。
- 同一 Windows Session 只允许运行一个 NarutoAutoGUI 正式 GUI 实例，第二实例提示后退出。
- 主窗口和托盘的 Session/程序操作使用同一个应用级操作门；退出从入口禁止新操作，等待在途操作完成，并在注销前后重新确认 Session 状态。
- 正式 GUI 使用最终 MaaNOP Config schema、真实 Project Interface 默认解析、不可变单项 Run Plan 和 Canonical Digest v1；通过用户级 Named Pipe 管理 Child Session Worker 的 admission、Snapshot、Run/Stop 和有界日志。
- 正式 GUI 从真实 PI 通用生成 global 与当前 task 的 input、switch、select、递归 active option 编辑器；显式值写入 SchemaVersion 1 `maanop-config.json`，可恢复为跟随项目默认，未激活子分支的合法显式值作为 Dormant Intent 保留。首片仍只允许一个 top-level task 和一个 Plan Item，不包含硬编码 `ServerRange`、task entry 或 pipeline override。
- Worker 自带固定 MaaFramework runtime，在 Child Session 中负责 Win32 Controller、MaaNOP Resource、MaaTasker 和每 Run Python Agent 生命周期；MFAAvalonia 不进入正常执行链。

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
- build 期间 NuGet 无法访问漏洞元数据源，产生 `NU1900` 警告；包还原和编译本身成功。该警告不是代码编译错误。

以下项目需要管理员权限、可见桌面或真实外部程序，本轮自动验证不能替代手动回归，当前不声明正式 GUI 已完成实机复验：RDP ActiveX 创建/恢复、托盘交互、游戏/MaaNOP 跨 Session 启动、异常断开后重建连接、创建/启动过程中从托盘退出、退出确认取消/注销失败恢复、第二实例拦截和最终注销。

## 本轮交互式回归

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
