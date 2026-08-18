# NarutoAutoGUI

NarutoAutoGUI 是 MaaNOP 的 Windows GUI / Frontend，并计划逐步替代 MFAAvalonia。主桌面 GUI 负责启动、路径配置、Child Session 生命周期以及状态和日志展示；游戏和 MaaNOP 运行在独立的 Windows Child Session 中。

仓库已经进入第一轮正式 GUI 开发。`src/ChildSessionDemo` 作为已验证且冻结的 baseline 保留；`src/NarutoAutoGUI` 是 .NET 8 WPF 正式程序。本轮没有引入 MaaFramework、Worker/IPC，也没有修改 MaaNOP、MFAAvalonia 或系统文件。

## 当前能力

- 创建、恢复、显示、隐藏和注销 RDP Child Session。
- Child Session 固定为 `1920 × 1080 @ 100%`。
- 展示 RDP 状态和 `childSessionId`；隐藏预览时保持连接存活。
- 持久化游戏 exe、启动参数与 MaaNOP exe 路径，分别启动或一键启动挂机环境。
- 启动前按进程名和 Session ID 检测，避免在当前 Child Session 重复启动。
- 主窗口关闭时隐藏到托盘；真正退出前确认并注销仍在运行的 Child Session。
- GUI 显示 INFO+；文件保存 DEBUG+，按天/10 MB 滚动并保留 14 天。

## 构建

正式 GUI 需要 Windows x64 和 .NET 8 SDK。在仓库根目录运行：

```powershell
.\src\NarutoAutoGUI\scripts\build.ps1
```

自包含发布结果位于 `artifacts\NarutoAutoGUI\win-x64`。冻结 baseline 仍可使用 `src\ChildSessionDemo\scripts\build.ps1` 单独构建。

## 运行

无需 UAC/RDP 的自动自检：

```powershell
.\src\NarutoAutoGUI\scripts\test-automated.ps1
```

正式程序要求 UAC 管理员权限和交互式 Windows 桌面。配置保存在程序目录的 `config\settings.json`；日志保存在程序目录的 `logs\`。关闭主窗口或子桌面窗口只会隐藏；请使用“结束桌面分身”或托盘“退出程序”完成清理。

火影忍者 Online 的默认启动配置为 `QQMicroGameBox\Launch.exe -/appid:1103286479`。工作目录无需配置，正式 GUI 会自动使用所选 exe 的所在目录。

进一步说明见 [正式 GUI](src/NarutoAutoGUI/README.md)、[架构](docs/ARCHITECTURE.md)、[当前状态](docs/STATUS.md)和[路线图](docs/ROADMAP.md)。PoC 的具体参数与 baseline 验证见 [Child Session Demo](src/ChildSessionDemo/README.md)。
