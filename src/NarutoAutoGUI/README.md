# NarutoAutoGUI 正式 GUI

Windows x64 / .NET 10 WPF 程序。需要管理员权限和交互式桌面。

## 构建与自动自检

日常验证需要 .NET 10 SDK；完整验证还需要仓库固定的 Rust MSVC 工具链。

在仓库根目录运行：

```powershell
.\src\NarutoAutoGUI\scripts\verify.ps1
.\src\NarutoAutoGUI\scripts\verify.ps1 -Suite All
```

`verify.ps1` 增量构建后运行自检，只输出摘要，完整日志保存在 `artifacts/validation/`。
可选 `-Suite Project/Support/RuntimeControls/Worker/Updater/Engine`（每次选择一个分类）；
只有源码及依赖未变、刚成功构建时，才用 `-NoBuild` 重跑测试。分类范围见根目录 [AGENTS.md](../../AGENTS.md)。

完整发布仍使用 `scripts/build.ps1`；发布目录为 `artifacts\NarutoAutoGUI\win-x64`，
并包含 `worker\NarutoAutoWorker.exe` 及固定 runtime。
自检通过 `dotnet NarutoAutoGUI.dll --self-test` 运行，不触发 apphost 的 UAC manifest，不连接 RDP；完整 GUI 自检会创建本机 RDP 控件验证输入禁用和窗口模式。
通知 tracker、诊断 ZIP 与 Settings 离屏布局可定向验证：

```powershell
.\src\NarutoAutoGUI\scripts\verify.ps1 -Suite Support
```

## 运行时文件

- MaaNOP 用户配置：`<程序目录>\config\maanop-config.json`
- 滚动日志：`<程序目录>\logs\NarutoAutoGUI-yyyyMMdd[.序号].log`
- Worker 启动失败日志：`<程序目录>\logs\NarutoAutoWorker.bootstrap.log`，只在 Worker 启动失败或异常退出时写入
- 框架日志与图片：`<程序目录>\debug`，失败截图位于 `on_error`，识别可视化图片位于 `vision`
- 诊断包：`<程序目录>\debug\exports`，每卷不超过 24.5 MB，保留最近 10 次导出
- 框架偏好：`<程序目录>\config\maa-{save-on-error,debug-mode}.txt`

设置页提供三个独立开关：SaveOnError 默认开启，SaveDraw 和 DebugMode 默认关闭。SaveDraw 不保存，程序重启后恢复关闭。
更改后从下一次识别开始生效，无需结束桌面分身；GUI 在切换开关和 Worker 连接时同步当前选择。
框架日志和图片没有容量上限，由 MaaFramework 在设置 LogDir 时删除 7 天前的文件；设置页可手动清理。

验证打包后的原生 runtime 图片保存（不启动游戏或 RDP）：

```powershell
.\src\NarutoAutoGUI\scripts\test-framework-diagnostics.ps1 -PackageDirectory artifacts\NarutoAutoGUI\win-x64
```

MaaNOP 项目根目录固定为应用程序目录，`interface.json` 直接从 `NarutoAutoGUI.exe` 同级目录读取。日志按天和 10 MiB 滚动，保留 14 个文件，可从主窗口直接打开当前日志目录。若程序目录不可写，日志会回退到 LocalAppData 或临时目录并记录 WARN。

游戏可在分身中手动启动，也可由当前任务配置选中的 Project Interface pretask 启动。GUI 不固定游戏启动器路径或参数；启动、登录等游戏行为由 MaaNOP 项目声明的程序和资源实现。

## 操作语义

- 首页“准备运行环境”、运行控制栏的“观看分身”会确保 RDP ActiveX 已连接。
- 隐藏子桌面或点击其 X 不会断开 RDP。
- Worker 与任务配置就绪后，无论子桌面当前显示或隐藏，均可从主窗口开始任务。
- “准备运行环境”只创建或恢复 Session、连接 RDP 并启动或恢复 Worker；准备完成后仍由用户点击“开始任务”。
- 启动 Worker 前确认目标 Child Session 中没有同名 Worker，连接后再核验进程和 Session 身份。
- 主窗口 X 默认隐藏到托盘；关闭该设置后进入安全退出流程。
- “结束桌面分身”和退出程序会注销 Child Session；退出前会确认。

## 项目预设

项目可在 `interface.json` 的 `preset` 数组声明预设。在配置标签条点击“从预设新建配置”，
选择后会新建并激活一份普通配置；可继续编辑、重命名或删除，不覆盖已有配置，也不自动同步后续预设变化。
未声明预设时隐藏此入口，原有“新建空配置”保持可用。

支持任务顺序、`enabled`（默认 true）、select/switch 的 case.name、checkbox 的 case.name 数组、input 字段值和嵌套选项。
`enabled: false` 的任务或前置任务不加入配置，其选项也被忽略；未指定的选项跟随项目默认值。
支持 `preset.pretask`：只选择其中启用且适用的前置任务，跳过不适用项，并仍按顶层 pretask 顺序执行；
省略或为空数组时不选择前置任务。新配置只使用项目默认值和预设内容，不继承当前配置的任务、前置任务或参数。

沿用现有配置的共享选项模型：不同任务对同名选项指定的值必须一致，input 的不同字段可合并；
发生冲突时拒绝创建，不覆盖原配置。未知任务或前置任务、非法选项值及重复任务也会拒绝。
本功能不扩展现有 Project Interface 支持范围：`import`、hotkey、国际化和 Markdown/图标渲染仍未支持，
展示字符串沿用现有任务的处理方式。

## 前置任务

任务配置的“启动阶段”固定展示项目声明的适用 pretask，通过开关保存各配置的选择，并可展开编辑参数。
启动阶段提供图标按钮“仅执行启动”，只运行勾选的前置项，不改变下方普通任务的选择。开始时，前置任务按 Project Interface 声明顺序在所有普通任务之前执行；配置中只有前置任务时也可开始，Worker 不创建 MaaFramework Controller。

前置程序在 Child Session 中运行，工作目录为 `interface.json` 所在目录。固定参数之后按需追加一个紧凑 JSON 参数，内容为该 pretask 引用的选项值；这些选项不合并到普通任务的 Pipeline Override。

启动失败、非零退出或每项 180 秒超时会停止后续任务。停止或超时后最多等待 30 秒确认清理；退出未确认时保留 Active Run，界面只允许通过结束桌面分身恢复。成功完成的前置任务保留其启动的游戏。

## Known Issue

首次 Child Session 偶尔出现 `CrossDeviceResume.exe` 的 Windows 系统弹窗，目前不影响功能。本项目不修改 SystemApps、ACL 或系统文件来处理该弹窗。
