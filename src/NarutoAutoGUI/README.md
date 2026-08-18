# NarutoAutoGUI 正式 GUI

Windows x64 / .NET 8 WPF 程序。需要管理员权限和交互式桌面。

## 构建与自动自检

在仓库根目录运行：

```powershell
.\src\NarutoAutoGUI\scripts\build.ps1
.\src\NarutoAutoGUI\scripts\test-automated.ps1
```

发布目录为 `artifacts\NarutoAutoGUI\win-x64`。自检通过 `dotnet NarutoAutoGUI.dll --self-test` 运行，不触发 apphost 的 UAC manifest，也不初始化 RDP；它只验证配置与文件日志。

## 运行时文件

- 路径配置：`<程序目录>\config\settings.json`
- 滚动日志：`<程序目录>\logs\NarutoAutoGUI-yyyyMMdd[.序号].log`

配置包含游戏 exe、启动参数与 MaaNOP exe 路径。日志按天和 10 MB 滚动，保留 14 天，可从主窗口直接打开当前日志目录。若程序目录不可写，日志会回退到 LocalAppData 或临时目录并记录 WARN。

火影忍者 Online 默认配置：

```text
程序：C:\Users\17321\AppData\Roaming\Tencent\QQMicroGameBox\Launch.exe
参数：-/appid:1103286479
```

正式 GUI 不使用 `QQGameLauncher.exe` 作为该游戏的直接入口。
工作目录不提供配置字段，启动时自动使用所选 exe 的所在目录；对默认入口即为 `QQMicroGameBox`。旧版配置缺少参数时，仅当游戏入口为空或为 `QQGameLauncher.exe` 才自动迁移；其他自定义 exe 不会被覆盖。

## 操作语义

- “创建 / 恢复”和“显示子桌面”会确保 RDP ActiveX 已连接。
- 隐藏子桌面或点击其 X 不会断开 RDP。
- 游戏启动会把界面配置的参数和由 exe 自动推导的工作目录传给 Task Scheduler COM；一键启动会依次确保 Session、显示子桌面、启动游戏、启动 MaaNOP，并保持显示；单个程序失败时仍尝试另一个。
- 启动前按 exe 文件名与 `childSessionId` 检查，已运行则跳过。
- 主窗口 X 只隐藏到托盘。
- “结束桌面分身”和退出程序会注销 Child Session；退出前会确认。

## Known Issue

首次 Child Session 偶尔出现 `CrossDeviceResume.exe` 的 Windows 系统弹窗，目前不影响功能。本项目不修改 SystemApps、ACL 或系统文件来处理该弹窗。
