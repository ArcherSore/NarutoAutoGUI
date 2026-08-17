# NarutoAutoGUI

NarutoAutoGUI 的目标是成为 MaaNOP 自己的 Windows GUI / Frontend，并逐步替代 MFAAvalonia。主桌面 GUI 将负责启动、任务配置以及状态和日志展示；MaaFramework Worker 与游戏未来运行在独立的 Windows Child Session 中。

当前仓库处于 PoC 迁移阶段，只包含已经实际验证过的 Child Session demo。本轮没有引入 MaaFramework、Worker/IPC 或正式 GUI，也没有修改 MaaNOP 与 MFAAvalonia。

## 当前能力

- 在 Windows 上启用、创建、连接和注销 RDP Child Session。
- Child Session 固定为 `1920 × 1080 @ 100%`。
- 通过 RDP ActiveX 窗口预览独立桌面；关闭窗口时清理 Child Session。
- 获取 `childSessionId`。
- 通过 Task Scheduler COM 将程序启动到指定 Child Session。
- 使用 WMI 验证目标进程的 PID 与 Session ID。
- 已验证启动 `notepad.exe` 和 MFAAvalonia；MaaNOP、游戏与 MFAAvalonia 的组合也已在迁移前的 baseline 中实机验证。

## 构建

需要 Windows x64 和 .NET 8 SDK。在仓库根目录运行：

```powershell
.\src\ChildSessionDemo\scripts\build.ps1
```

自包含发布结果位于 `artifacts\child-session-launcher\win-x64`。

## 运行

运行默认测试（当前默认启动代码中配置的 MFAAvalonia）：

```powershell
.\src\ChildSessionDemo\scripts\test-default.ps1
```

启动任意单个测试程序：

```powershell
.\src\ChildSessionDemo\scripts\test-exec.ps1 -TargetPath "C:\Windows\System32\notepad.exe"
```

程序要求 UAC 管理员权限和交互式 Windows 桌面。关闭 RDP 预览窗口会断开并注销 Child Session。

进一步说明见 [架构](docs/ARCHITECTURE.md)、[当前状态](docs/STATUS.md)和[路线图](docs/ROADMAP.md)。PoC 的具体参数与风险见 [Child Session Demo](src/ChildSessionDemo/README.md)。
