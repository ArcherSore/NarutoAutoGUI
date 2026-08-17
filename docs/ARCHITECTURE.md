# Architecture

## 当前目录

```text
NarutoAutoGUI/
├─ README.md
├─ AGENTS.md
├─ docs/
│  ├─ ARCHITECTURE.md
│  ├─ STATUS.md
│  └─ ROADMAP.md
└─ src/
   └─ ChildSessionDemo/
      ├─ MaaNOP.ChildSessionLauncher.csproj
      ├─ Program.cs
      ├─ ChildSessionNativeMethods.cs
      ├─ ChildSessionService.cs
      ├─ ChildSessionProcessLauncher.cs
      ├─ RdpActiveXHost.cs
      ├─ app.manifest
      └─ scripts/
```

`ChildSessionDemo` 是迁移进来的独立 PoC，不代表最终 NarutoAutoGUI 产品目录或 UI 架构。

## Child Session 调用流程

1. `Program` 创建最小 WinForms 预览窗口并启用 Child Session。
2. `ChildSessionService` 要求 `RdpActiveXHost` 连接 `localhost`，并等待 RDP 登录完成。
3. `RdpActiveXHost` 承载系统 `MsRdpClient10` ActiveX，设置 `ConnectToChildSession=true`、`1920×1080`、桌面和设备缩放 `100%`；`SmartSizing` 只缩放预览。
4. `ChildSessionNativeMethods` 通过 WTS API 取得 `childSessionId`。
5. `ChildSessionProcessLauncher` 通过 Task Scheduler COM 的 `RunEx`，带 `TASK_RUN_USE_SESSION_ID` 将目标程序启动到该 Session。
6. `ChildSessionNativeMethods` 使用 WMI `Win32_Process` 验证进程 PID 和 Session ID；WMI 不可用时降级到托管进程枚举。
7. 预览窗口保持 RDP ActiveX 连接存活。关闭窗口后断开连接并注销 Child Session。

## 模块职责

- `Program.cs`：PoC 入口、参数、日志、连接与启动编排、进程验证。
- `RdpActiveXHost.cs`：RDP ActiveX COM 声明、控件宿主、预览窗口和连接事件。
- `ChildSessionService.cs`：Child Session 连接生命周期和超时处理。
- `ChildSessionNativeMethods.cs`：WTS API、注册表信息探测及进程 Session 查询。
- `ChildSessionProcessLauncher.cs`：跨 Session 的 Task Scheduler COM 启动。
- `app.manifest`：请求 `requireAdministrator`，满足启用 Child Session 的权限要求。

## 与 MaaNOP / MFAAvalonia 的关系

当前仓库不引用也不修改 MaaNOP、MFAAvalonia 或 MaaFramework。MFAAvalonia 只是 PoC 的外部启动目标，路径目前由 demo 中的默认常量指定，也可以通过 `--exec` 覆盖。

未来 NarutoAutoGUI 将在主桌面承担 Frontend 职责；Worker、游戏和 MaaFramework 的正式集成尚未开始，不属于当前架构的已实现部分。
