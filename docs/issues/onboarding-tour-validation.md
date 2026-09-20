# Onboarding Tour 验证记录

日期：2026-09-20。规格：[四步新手指引](onboarding-tour-spec.md)。

## 测试目录准备

2026-09-20 16:17 按用户要求部署 `816651c`：确认 GUI/Worker 未运行后，将 GUI 主程序集和
`libs` 中的 ProjectModel/Protocol/Updates 同步至 `D:\MaaNOP-win-x86_64-v2.4.0`，四项 SHA256 一致。
通过 `dotnet NarutoAutoGUI.dll --self-test` 验证目标目录 GUI 完整自检。
随后将原 config 目录移至 `artifacts/onboarding-first-run-backup-20260920-161713/config/`，
逐文件核对备份哈希；同一备份目录还保存旧程序集和部署哈希清单。
目标 config 不存在，下次正常启动将走新用户首次初始化；尚未启动正常 GUI 消耗首次体验。
后续模拟老用户可在退出 GUI 后恢复该备份 config，保留新版程序集。
interface 哈希未变，未改 Worker、MaaNOP 资源、日志或缓存，未执行真实游戏/分身操作。
以下“未覆盖实测安装目录”描述仅对应实现阶段，部署事实以本节为准。

## 实现范围

首次缺失配置由 ProjectPlanModule 预置并保存 PI 第一项，ExplicitOptions 为空，首次 GUI 渲染即展开。
已有/损坏/迁移/用户新增配置保留原规则。GUI 单独维护完成版本和空的新用户资格标记，
四步 Overlay 使用真实 WPF 控件、滚动裁剪、Popover placement、有限 Pulse、输入和焦点限制。
Settings replay 不修改用户配置或 Onboarding 文件，隐藏/最小化只保留本窗口内步骤。

MainWindow 的启动顺序提取为一个异步入口：Project → 更新偏好 → 现有 Session 恢复 → 启动完成。
生产 Loaded 调用这个入口；测试在原生 Session 操作边界提供立即完成或受控等待的 Task，
直接执行相同初始化顺序，不通过设置“启动完成”字段模拟成功。Child Session 的原生实现没有修改。

## 已执行自动化

- GUI Release 构建通过，0 警告/0 错误。Worker Release 构建通过，0 警告/0 错误。
- 首次初始化测试先失败再通过：PI 首项、不同首项顺序、保存/重开稳定身份、清空后不补任务、
  初始保存失败的空工作区和警告；原有多配置、迁移、异常 bytes 保护及错误保存自检通过。
- 真实 WPF 窗口在屏幕外布局，使用隔离 PI/配置目录；已验证默认参数在 Tour 前展开，
  四步标题/计数/按钮、前进返回、1180×760/920×640 的 popover 边界和高亮输入不穿透。
- 已验证 replay 有任务与空计划 fallback、配置/完成版本/资格文件 bytes 不变、结束回 Settings，
  隐藏/恢复同一步、busy 暂停恢复、底层 Preview/Update 打开入口互斥和 Preview modal 延迟自动显示。
- 已验证老用户、损坏配置、已处理未来版本、版本损坏不自动弹；未完成关闭后的重开从 Step 1 开始；
  Skip、Esc、Finish 写版本；target 缺失不记完成；完成版本写入失败关闭、保留资格并给出提示。
- 有限 Pulse 在规定周期后停止；生产启动入口覆盖原生边界立即完成和延迟完成两种时序。
  超高卡片的 Spotlight 外扩后仍受真实滚动祖先裁剪限制。
- GUI 完整 `--self-test` 已执行通过。Updater C# 客户端测试通过；Rust Engine tests 与 Clippy 通过。
- 完整 `test-automated.ps1` 已尝试：GUI 通过，Worker 在既有 Preview 双进程完整帧测试超时，
  因而该脚本整套未通过。脚本未执行到的 Updater/Rust 检查已单独补跑通过。
  该失败与 ROADMAP 既有本地记录一致；用户反馈 GitHub CI 可以通过，本次未独立复验线上结果，
  不将其认定为此次 Onboarding 引入的 Worker 回归，也未修改 Worker 实现来绕过测试。

## 视觉与审查

- 离屏 RenderTargetBitmap 输出已目检，覆盖首卡完整参数、四步 target 和小窗口的左右 placement。
  发现并修正主操作按钮的文字色继承问题；截图等待淡入结束后采集。
  本地图片输出在忽略的 `artifacts/onboarding-qa`，不提交测试产物。
- code-review 的 Standards/Spec 两轴审查发现共享展开逻辑的可访问帮助文本遗漏、
  viewport 外扩越界及启动顺序测试缺口；已针对性修改并补回归。
  Pulse 内部允许 1–3 次的参数是规格明确要求，保留该有限参数，不增加用户设置。

## 仍需人工验收

- 正式 MaaNOP 包中的真实第一项说明与参数内容，不以测试 fixture 代替资源内容验收。
- 真实 100%/150%/200% DPI、跨显示器 DPI 切换，导航展开/收起和长内容的可读性。
- 系统动画关闭及运行中切换设置后的观感；快速操作、键盘 Tab/Shift+Tab 和焦点可见性。
- 原生标题栏最小化/最大化/关闭到托盘、Alt+F4、系统托盘恢复，以及与既有模态 hook 的真实交互。
- 真实已有 Child Session 恢复、已有运行/预览继续工作及完整包内首次体验。

本轮未启动真实游戏/分身，未安装更新，未覆盖用户的实测安装目录或配置，未发布 Release。
离屏窗口及系统边界受控任务不等同于上述真实桌面验收。
