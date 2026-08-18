# Status

## 当前阶段

第一轮正式 GUI 已实现，进入交互式回归阶段。`src/ChildSessionDemo` 保持冻结；正式程序位于 `src/NarutoAutoGUI`。

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
- 全局 GUI、后台任务和进程异常记录；预期的启动/RDP/Win32/COM 失败不会直接使 GUI 崩溃。

## 本轮自动验证

- 2026-08-17：正式项目 Release `win-x64` build 通过。
- 2026-08-17：正式项目 self-contained `win-x64` publish 通过，输出到 `artifacts\NarutoAutoGUI\win-x64`。
- 2026-08-17：`--self-test` 通过，覆盖便携式配置 JSON 保存/加载往返、DEBUG 与 INFO 文件日志写入。
- 2026-08-19：补充游戏参数后 Release build 和 `--self-test` 通过；工作目录改为由 exe 自动推导，不在 GUI 或配置文件中暴露。自检覆盖旧版错误入口迁移、火影默认启动配置及三项启动配置 JSON 往返。
- 2026-08-19：默认发布目录因旧版 NarutoAutoGUI 进程正在运行而被锁定；未强制结束进程，改在 `artifacts\NarutoAutoGUI\win-x64-update` 完成 self-contained publish 和发布后自检。
- 2026-08-19：移除工作目录字段后再次完成 Release build、直接 `--self-test`、`artifacts\NarutoAutoGUI\win-x64-no-workdir` self-contained publish 及发布后自检；运行中的 GUI 实例均未被强制结束。
- build 期间 NuGet 无法访问漏洞元数据源，产生 `NU1900` 警告；包还原和编译本身成功。该警告不是代码编译错误。

以下项目需要管理员权限、可见桌面或真实外部程序，本轮自动验证不能替代手动回归，当前不声明正式 GUI 已完成实机复验：RDP ActiveX 创建/恢复、托盘交互、游戏/MaaNOP 跨 Session 启动、异常断开和最终注销。

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
