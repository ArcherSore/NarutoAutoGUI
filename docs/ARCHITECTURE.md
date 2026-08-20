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
   ├─ NarutoAutoGUI.Protocol/        # GUI/Worker 共享 IPC schema 与 framing
   ├─ NarutoAutoGUI.ProjectModel/    # Project Interface、Config 与 Run Plan
   ├─ NarutoAutoWorker/              # Child Session Worker 与固定 runtime
   └─ NarutoAutoGUI/                 # 正式 .NET 8 WPF GUI
      ├─ App.xaml(.cs)               # 单实例、应用操作门、退出、托盘、全局异常
      ├─ Views/MainWindow.xaml(.cs)  # 状态、路径、启动按钮、INFO+ 日志
      ├─ Models/                     # 配置和日志模型
      ├─ Infrastructure/             # 配置、滚动日志、自动自检
      ├─ ChildSession/               # 状态模型、生命周期与程序启动编排
      └─ scripts/                    # build/publish 与非交互自检
```

## 复用冻结 baseline

正式项目通过 MSBuild `Compile Include` 链接以下源文件，不复制实现：

- `ChildSessionNativeMethods.cs`
- `ChildSessionProcessLauncher.cs`
- `ChildSessionService.cs`
- `RdpActiveXHost.cs`

`ChildSessionDemo/Program.cs`、脚本和 PoC 默认路径不进入正式程序。这样可直接复用已经实机验证的 WTS API、RDP ActiveX、Task Scheduler COM、WMI 降级和清理流程，同时让 Demo 继续作为可重复验证的独立 baseline。2026-08-19 对共享 baseline 完成两项最小生命周期修复后重新冻结：`WTSGetChildSessionId` 仅将成功返回 `ULONG(-1)` 或本机实测的 `ERROR_NOT_FOUND (1168)` 识别为“无 Session”，其他原生失败保留错误码并抛出；RDP `ConnectedState` 改为读取 ActiveX 实时值，所有非主动断开都会上报给正式 GUI。

2026-08-20 为 Worker 增加独立的 `LaunchElevatedVerifiedAsync` 强化入口：原有 Demo、游戏和普通程序使用的 `LaunchAsync`/`LaunchElevatedAsync` 提交与清理语义保持不变；只有 Worker 路径会在 `RunEx` 后暂时保留任务，等待枚举到新的目标 PID 并验证 Session ID，同时采集 Task State 与 `LastTaskResult` 后再删除临时任务。

## 正式 GUI 调用流程

1. `App` 先取得按当前 Windows Session 区分的命名 Mutex；同一 Session 中的第二个正式 GUI 实例提示后退出。随后初始化统一日志、便携式配置、WPF 主窗口和 WinForms 托盘图标。
2. `ChildSessionManager` 在启动时通过 WTS 探测已有 `childSessionId`；API 成功并返回 `ULONG(-1)`，或原生返回本机实测的 `ERROR_NOT_FOUND (1168)`，均表示没有 Child Session；其他原生调用失败会保留错误码并进入故障状态。如存在 Session，则自动创建预览宿主并恢复 RDP 连接。
3. 创建/恢复时，冻结 baseline 将 `MsRdpClient10` 连接到 `localhost`，启用 `ConnectToChildSession`，强制 `1920×1080`、桌面/设备缩放 `100%`；`SmartSizing` 只缩放预览。
4. 子桌面窗口 X 和“隐藏”只调用 `Hide()`，不销毁 ActiveX，因而保持 RDP 和 Child Session 内程序存活。Manager 只在 ActiveX 实时 `ConnectedState == 1` 且状态为已连接时复用宿主；任何非主动断开都会进入 `Faulted`，下一次创建/显示/启动会销毁旧宿主并重新连接。
5. `ChildSessionProgramService` 先按 exe 文件名与 Session ID 检测目标进程；已运行则记录 PID 并跳过，否则从 exe 自动推导工作目录，并将 exe、参数和工作目录传给冻结的 Task Scheduler COM `RunEx(TASK_RUN_USE_SESSION_ID)`。
6. 启动后使用冻结的 WMI/托管枚举流程在 10 秒内验证 PID 与 Session ID。单个启动失败被记录和呈现，不会终止 GUI 或自动清理仍可用的 Session。
7. 主窗口和托盘的 Session/程序操作共用一个应用级操作门。退出在入口立即禁止新操作并等待在途操作完成，然后在门内重新查询 Session、按原行为确认、调用 Manager 注销，并在释放资源前再次确认 Session 已不存在。Manager 内部仍先断开 ActiveX，再同步调用 `WTSLogoffSession`；主窗口 X 只隐藏到托盘。
8. Worker 启动先写入 Pending Admission，再通过 Worker 专用 Task Scheduler 路径等待新 PID/Session 验证；验证成功后将 PID 写回 Admission 并继续等待 Pipe admission 与 fresh Snapshot。`RunEx` 未真正生成进程时在 10 秒内携带 Task State/`LastTaskResult` 失败并清理；60 秒 admission 超时且没有存活的已验证 Worker 时自动回滚 `worker.json` 与 launch manifest，存活 Worker 则保留 Admission 供重连。

## 配置与日志

- 启动配置：`<程序目录>\config\settings.json`。保存游戏 exe、参数和 MaaNOP exe 路径，不保存账户密码。
- 火影忍者 Online 默认运行 `C:\Users\17321\AppData\Roaming\Tencent\QQMicroGameBox\Launch.exe -/appid:1103286479`；不使用 `QQGameLauncher.exe`。工作目录不提供配置字段，统一自动使用所选 exe 的父目录。
- 文件日志：默认写入 `<程序目录>\logs`，记录 DEBUG+；按日期命名，单文件最大 10 MB，保留 14 天。若程序目录不可写，则依次回退到 LocalAppData 和临时目录并记录 WARN。
- GUI 日志：订阅同一个日志源，仅显示 INFO+，保留最近 1000 条；主窗口可直接打开当前实际日志目录。
- 日志覆盖应用/Session/RDP 生命周期、程序路径、PID、SessionId、异常堆栈以及冻结 baseline 返回的 Win32/COM 错误码。

## 明确边界

当前只包含一个 top-level task、一个 Plan Item 的最小 Worker/IPC + MaaFramework 闭环。不包含多任务调度、自动登录/扫码、自动隐藏子桌面、自动开始 MaaNOP 任务、Worker replacement UI 或可调分辨率/DPI。
