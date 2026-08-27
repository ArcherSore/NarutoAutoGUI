# NarutoAutoGUI 正式 GUI

Windows x64 / .NET 10 WPF 程序。需要管理员权限和交互式桌面。

## 构建与自动自检

完整发布脚本需要 .NET 10 SDK。

在仓库根目录运行：

```powershell
.\src\NarutoAutoGUI\scripts\build.ps1
.\src\NarutoAutoGUI\scripts\test-automated.ps1
```

发布目录为 `artifacts\NarutoAutoGUI\win-x64`，并包含 `worker\NarutoAutoWorker.exe` 及固定 runtime。自检通过 `dotnet NarutoAutoGUI.dll --self-test` 运行，不触发 apphost 的 UAC manifest，也不初始化 RDP；它只验证配置与文件日志。

## 运行时文件

- MaaNOP 用户配置：`<程序目录>\config\maanop-config.json`
- 滚动日志：`<程序目录>\logs\NarutoAutoGUI-yyyyMMdd[.序号].log`

MaaNOP 项目根目录固定为应用程序目录，`interface.json` 直接从 `NarutoAutoGUI.exe` 同级目录读取。游戏启动器固定从当前用户的 `%APPDATA%\Tencent\QQMicroGameBox\Launch.exe` 推导，AppId 和启动参数不由用户配置。日志按天和 10 MB 滚动，保留 14 天，可从主窗口直接打开当前日志目录。若程序目录不可写，日志会回退到 LocalAppData 或临时目录并记录 WARN。

火影忍者 Online 固定启动配置：

```text
程序：%APPDATA%\Tencent\QQMicroGameBox\Launch.exe
参数：-/appid:1103286479
```

正式 GUI 不使用 `QQGameLauncher.exe` 作为该游戏的直接入口。
工作目录不提供配置字段，启动时自动使用固定启动器的所在目录。启动器缺失时，GUI 会提示先通过 QQ 游戏平台安装或启动一次火影忍者 Online。

## 操作语义

- 首页“准备运行环境”和“打开完整桌面”会确保 RDP ActiveX 已连接。
- 隐藏子桌面或点击其 X 不会断开 RDP。
- Worker 与任务配置就绪后，无论子桌面当前显示或隐藏，均可从主窗口开始任务。
- “准备运行环境”会依次确保 Session、显示子桌面、使用固定 profile 启动游戏并启动 Worker；启动参数与工作目录均由程序确定。
- 启动前按 exe 文件名与 `childSessionId` 检查，已运行则跳过。
- 主窗口 X 只隐藏到托盘。
- “结束桌面分身”和退出程序会注销 Child Session；退出前会确认。

## Known Issue

首次 Child Session 偶尔出现 `CrossDeviceResume.exe` 的 Windows 系统弹窗，目前不影响功能。本项目不修改 SystemApps、ACL 或系统文件来处理该弹窗。
