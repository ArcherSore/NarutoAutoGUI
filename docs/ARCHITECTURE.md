# Architecture

## 当前目录

```text
NarutoAutoGUI/
├─ docs/
│  ├─ ARCHITECTURE.md
│  ├─ STATUS.md
│  └─ ROADMAP.md
└─ src/
   ├─ ChildSessionDemo/              # 已验证、冻结的独立 PoC baseline
   └─ NarutoAutoGUI/                 # 正式 .NET 8 WPF GUI
      ├─ App.xaml(.cs)               # 应用生命周期、托盘、全局异常
      ├─ Views/MainWindow.xaml(.cs)  # 状态、路径、启动按钮、INFO+ 日志
      ├─ Models/                     # 配置和日志模型
      ├─ Infrastructure/             # 配置、滚动日志、自动自检
      ├─ ChildSession/               # 状态模型、生命周期与程序启动编排
      └─ scripts/                    # build/publish 与非交互自检
```

## 复用冻结 baseline

正式项目通过 MSBuild `Compile Include` 链接以下源文件，不复制也不修改其实现：

- `ChildSessionNativeMethods.cs`
- `ChildSessionProcessLauncher.cs`
- `ChildSessionService.cs`
- `RdpActiveXHost.cs`

`ChildSessionDemo/Program.cs`、脚本和 PoC 默认路径不进入正式程序。这样可直接复用已经实机验证的 WTS API、RDP ActiveX、Task Scheduler COM、WMI 降级和清理流程，同时让 Demo 继续作为可重复验证的独立 baseline。

## 正式 GUI 调用流程

1. `App` 初始化统一日志、便携式配置、WPF 主窗口和 WinForms 托盘图标。
2. `ChildSessionManager` 在启动时通过 WTS 探测已有 `childSessionId`；如存在，自动创建预览宿主并恢复 RDP 连接。
3. 创建/恢复时，冻结 baseline 将 `MsRdpClient10` 连接到 `localhost`，启用 `ConnectToChildSession`，强制 `1920×1080`、桌面/设备缩放 `100%`；`SmartSizing` 只缩放预览。
4. 子桌面窗口 X 和“隐藏”只调用 `Hide()`，不销毁 ActiveX，因而保持 RDP 和 Child Session 内程序存活。
5. `ChildSessionProgramService` 先按 exe 文件名与 Session ID 检测目标进程；已运行则记录 PID 并跳过，否则从 exe 自动推导工作目录，并将 exe、参数和工作目录传给冻结的 Task Scheduler COM `RunEx(TASK_RUN_USE_SESSION_ID)`。
6. 启动后使用冻结的 WMI/托管枚举流程在 10 秒内验证 PID 与 Session ID。单个启动失败被记录和呈现，不会终止 GUI 或自动清理仍可用的 Session。
7. “结束桌面分身”或确认退出时，先断开 ActiveX，再同步调用 `WTSLogoffSession`；主窗口 X 只隐藏到托盘。

## 配置与日志

- 启动配置：`<程序目录>\config\settings.json`。保存游戏 exe、参数和 MaaNOP exe 路径，不保存账户密码。
- 火影忍者 Online 默认运行 `C:\Users\17321\AppData\Roaming\Tencent\QQMicroGameBox\Launch.exe -/appid:1103286479`；不使用 `QQGameLauncher.exe`。工作目录不提供配置字段，统一自动使用所选 exe 的父目录。
- 文件日志：默认写入 `<程序目录>\logs`，记录 DEBUG+；按日期命名，单文件最大 10 MB，保留 14 天。若程序目录不可写，则依次回退到 LocalAppData 和临时目录并记录 WARN。
- GUI 日志：订阅同一个日志源，仅显示 INFO+，保留最近 1000 条；主窗口可直接打开当前实际日志目录。
- 日志覆盖应用/Session/RDP 生命周期、程序路径、PID、SessionId、异常堆栈以及冻结 baseline 返回的 Win32/COM 错误码。

## 明确边界

本轮不包含 MaaFramework 直接集成、Worker/IPC、`interface.json`、主桌面 Maa 任务管理、自动登录/扫码、自动隐藏子桌面、自动开始 MaaNOP 任务或可调分辨率/DPI。
