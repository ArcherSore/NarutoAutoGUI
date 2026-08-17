# Child Session Demo

这是从已验证 MaaNOP Child Session Launcher PoC 原样迁移的 Windows-only demo。它通过本机 RDP Child Session 创建独立桌面，并用 Task Scheduler COM 将进程启动到指定 Session。

默认启动：

```text
D:\Automation Script\MaaNOP-win-x86_64-v1.3.0\MFAAvalonia.exe
```

游戏当前由用户在子桌面中手动启动。使用 `--exec <path>` 可以只启动指定程序。

## 已保留的实现

- WTS API：启用、查询、获取和注销 Child Session。
- 系统 `MsRdpClient10` ActiveX 连接 `localhost`，设置 `ConnectToChildSession=true`。
- Child Session 固定 `1920×1080 @ 100%`，预览使用 SmartSizing。
- Task Scheduler COM `RunEx` 加 `TASK_RUN_USE_SESSION_ID` 启动目标进程。
- WMI `Win32_Process` 验证 PID 与 Session ID，并提供托管枚举降级。
- 全局异常日志写入 `%TEMP%\MaaNOP.ChildSessionLauncher.log`。

## 构建

在仓库根目录运行：

```powershell
dotnet restore .\src\ChildSessionDemo\MaaNOP.ChildSessionLauncher.csproj -r win-x64
dotnet build .\src\ChildSessionDemo\MaaNOP.ChildSessionLauncher.csproj -c Release -p:Platform=x64 -r win-x64 --no-restore
dotnet publish .\src\ChildSessionDemo\MaaNOP.ChildSessionLauncher.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o .\artifacts\child-session-launcher\win-x64 --no-restore
```

或直接执行：

```powershell
.\src\ChildSessionDemo\scripts\build.ps1
```

## 手动验证 baseline

1. 在交互式 Windows 桌面运行 `scripts\test-exec.ps1 -TargetPath C:\Windows\System32\notepad.exe`，接受 UAC。
2. 确认 RDP 预览出现、控制台输出 `childSessionId`，标题显示 `1920x1080 @ 100%`。
3. 确认预览内出现记事本，日志记录其 PID 与 `SessionId == childSessionId`。
4. 确认主桌面鼠标键盘仍可使用。
5. 关闭预览，确认日志显示断开并注销 Child Session。
6. 使用 `scripts\test-default.ps1` 验证当前默认 MFAAvalonia 路径；其他机器可改用 `--exec` 指定实际路径。

若父桌面使用 PIN 登录且出现密码框，可临时设置 `MAANOP_RDP_PASSWORD` 并传入 `--user`。环境变量仍是明文凭据，测试后应立即清除，禁止提交仓库。

## 风险

- 只支持 Windows x64，需要管理员权限和可见桌面。
- 不要开启普通 Remote Desktop Host，也不要修改 `fDenyTSConnections`；Child Session 支持由运行时 API 探测。
- RDP ActiveX 必须在 Child Session 生命周期内保持连接。
- TermService 回环状态异常时可能需要重启系统。
- `--password` 会暴露在进程命令行中，仅适合临时 PoC 测试。
