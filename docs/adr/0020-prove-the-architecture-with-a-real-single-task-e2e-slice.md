# 首个端到端切片用真实单任务闭环验证最终架构

NarutoAutoGUI 的首个 Worker/MaaFramework 实现切片必须是一个最小但真实的端到端 tracer bullet：沿 NarutoAutoGUI → Child Session Worker → MaaFramework → MaaNOP 正常执行链路，在已验证的 Child Session 中启动真正的 NarutoAutoWorker，完成 Launch Manifest、Pending Admission、Named Pipe admission 和 fresh Worker Snapshot；使用 Worker 自带的固定 MaaFramework runtime 加载当前本机 MaaNOP 项目、检查 Python Agent 依赖，并在用户手工登录游戏且隐藏 Child Session 后执行真实 top-level task。首片只有同时证明正常执行能够成功结束、真正运行中的 MaaFramework task 能被可靠停止，以及同一 Worker 能再次执行，才算完成；真实 Run 进入 Failed 只能作为调试证据，不能作为通过。

该切片可以使用当前开发机器上的 MaaNOP 本地项目作为 test fixture，也可以增加仅支撑上述闭环的最小临时 UI，但不得把该 fixture 宣布为 Supported Baseline，也不得引入未来需要推翻的旁路。GUI 不直接运行 MaaFramework，不以 MFAAvalonia 作为正常执行链，不让 Worker 解析 `interface.json`，不绕过 Named Pipe 控制 Worker，也不从 MaaNOP 或 MFAAvalonia 目录动态借用 MaaFramework runtime。

## 必须通过的终态验收

Success scenario 使用一个真实 MaaNOP top-level task 和只含一个 Plan Item 的 Run，必须真实经过 GUI、IPC、Child Session Worker、MaaFramework、Python Agent、MaaNOP Resource 和游戏目标窗口。最终 Plan Item 与 Run 均为 Succeeded，`activeRun=null`，`lastRun.state=Succeeded`，Worker 保持 Ready。

Cancellation scenario 使用一个真实 MaaNOP top-level task 和只含一个 Plan Item 的 Run。GUI 必须先从 fresh Snapshot 或连续 state event 确认 Run 与当前 Plan Item 均已进入 Running，之后才能显式发送 `run.stop`；立即响应必须为 `disposition=stop_requested`，后续先观察到 Run Stopping，再在 MaaFramework Stop 得到确认且 execution context、Python Agent 清理完成后观察到 Run Cancelled、`activeRun=null`、`lastRun.state=Cancelled` 和 Worker Ready。不得在 `run.start` 后立即停止并用 Starting 阶段的取消冒充 MaaTasker.Stop 验证。取消完成后，原游戏进程、同一 Worker PID 和 Child Session 必须仍存在。

Success 与 Cancellation 可以使用两个不同的真实 top-level task，以分别选择稳定短任务和可可靠保持 Running 的长任务，但不得使用 fake task、sleep stub 或 Worker 内部测试任务。两个场景必须使用不同 Run ID 且由同一 Worker instance 执行；后一个 Run 被真实接受并进入执行，同时证明前一个 Run 的 execution context 已释放、Worker 可以复用。

最终 PASS 必须同时具备 Worker admission 与 fresh Snapshot、Dependency Readiness Ready、一个真实 Succeeded Run、一个从真实 Running 经 `run.stop` 到 Cancelled 的 Run、取消后的游戏/Worker/Child Session 存活，以及同一 Worker 再次接受真实 Run。任一项缺失均只记录为 partial；Failed Run 必须保留错误、日志和 Snapshot，但不计为架构 E2E 验证成功。若 Run 已真实进入 MaaNOP Resource/pipeline 后因脚本逻辑、识别资源、账号状态或游戏前置条件而 Failed，该结果本身不认定为 NarutoAutoGUI GUI/IPC/Worker 缺陷；本仓库只负责如实呈现终态并保留诊断证据，不在本切片中修改 MaaNOP 来修复下游自动化脚本。该责任边界不改变“Failed 不能作为 E2E PASS”的验收规则。

## 默认解析的单任务 Run Plan 输入

GUI 从当前真实 MaaNOP Project Directory 读取并解析 `interface.json`，按 PI 声明顺序展示真实 top-level task，临时 UI 只允许单选。选择结果使用最终 SchemaVersion 1 `config/maanop-config.json` 保存为唯一 `SelectedTasks`，`ExplicitOptions` 保持空或按最终 serializer 规则省略；不得另建 tracer-bullet 配置、硬编码 task entry、隐藏 fixture override 或注入测试专用 pipeline override。

没有 option 编辑 UI 不表示跳过 PI Resolver。GUI 仍必须沿最终路径从 `interface.json` 和 `SelectedTasks` 递归解析 global、task 与 nested option，应用 `default` / `default_case`，执行 validation，按既定 PI merge order 生成 `resolvedGlobalOptions`、`resolvedOptions` 和 `pipelineOverride`，再由正式 Run Plan builder 构造单 Plan Item 的不可变 Run Plan并计算 Canonical Digest v1 `planDigest`。首片以后扩为多选或显式 option 编辑时继续复用同一 MaaNOP Config、Resolver 和 builder。

Success/Cancellation 候选 task 必须仅靠当前 PI 默认语义就能得到合法计划：所有激活 option 有合法默认结果，required input 无需额外值，默认值通过 regex/PI validation，最终 pipeline override 合法。若当前本机 MaaNOP 没有合适的 default-only task，首片停止扩展并报告最合适的候选 task、缺失的 explicit option/input 及默认解析失败原因；随后再单独决定最小 option UI，不得硬编码缺失值、偷偷提供 fixture value、篡改 Run Plan 或修改 MaaNOP 项目默认值。

当前开发机器的首次 Success 验证选择 MaaNOP `AccountTraining`（“练小号”）作为真实 task fixture。该选择只固定本轮交互式 E2E 的验收输入，不冻结 MaaNOP snapshot、tag、commit、Python `maa` 或 Maa.Framework 组合，也不构成 Supported Baseline；若该真实 Run 进入 Failed，只按本 ADR 保存为调试证据并继续视为 partial，不得改用隐藏参数或测试旁路使其通过。

首片明确不要求完整多任务 UI、全部递归 option 编辑体验、多任务串行执行、GUI 崩溃后的 active Run 恢复、完整日志断档/分页压力测试、WorkerRecoveryConflict 人工恢复 UI、Runtime Profile 热替换完整 UI、配置迁移完整 UI、所有异常路径或 Supported Baseline 最终定版。未纳入首片的能力仍必须遵守 ADR 0001–0019；延后验收不授权实现与这些决定冲突的临时架构。

## Partial 检查点后的首片扩展

首次实机检查点已证明 admission、fresh Snapshot、Dependency Readiness、真实 Running 后 `run.stop`、MaaFramework Stop 确认、Cancelled、取消后游戏/Worker/Child Session 存活和同 Worker 再次执行；真实 default-only `AccountTraining` 因 MaaNOP 下游 pipeline Failed，整体仍为 partial。该检查点之后允许在同一首片内补齐 PI 驱动的显式 option 编辑，再统一完成 Success 验收；这不是把下游脚本问题归入 GUI，而是让用户通过 NarutoAutoGUI 表达 MaaNOP 本来就声明的运行意图。

扩展后仍只允许选择一个 top-level task、生成一个 Plan Item，不提前引入多任务串行执行。GUI 必须从 PI 展示 global option 和当前 task 的 active option graph，支持当前 MaaNOP PI Subset 中的 input、switch、select 和嵌套 option；用户可将值明确设为 explicit，也可选择“跟随项目默认”删除对应 intent。父 case 暂时停用的合法嵌套 explicit value 按 ADR 0017 作为 Dormant Intent 保留，而不参与本次 resolve。

`ServerRange=978` 必须由用户通过上述正式 UI 写入最终 SchemaVersion 1 MaaNOP Config，并由同一个正式 Resolver 完成 regex/PI validation、default/explicit resolution、nested activation、PI merge order、pipeline override、不可变 Run Plan 和 Canonical Digest v1；它是合法的显式用户配置，不是隐藏 fixture override。不得仅为 `ServerRange` 或 `AccountTraining` 硬编码控件/解析分支，不得手改 PI default、让 GUI 直接拼最终 pipeline override，或绕过 MaaNOP Config/Resolver。

扩展完成后的统一实机验收使用单服务器配置缩短反馈环：先取得一个不经 Stop、自然终结为 Succeeded 的真实 Run，再回归已经通过的 Running → Stopping → Cancelled、取消后存活和同 Worker 复用。只有全部最终条件同时成立，首片才从 partial 更新为 PASS。

## 验收结果

2026-08-19，`win-x64-options-v2-scroll` 在 Child Session 18、同一 Worker PID 29960 和真实 MaaNOP `AccountTraining` 上完成统一验收。SchemaVersion 1 MaaNOP Config 通过正式 ExplicitOptions 设置 `ServerRange=978` 及任务开关；Run `16c48275-2f8b-4c04-885f-e38ff3cf3fe6` 经真实 GUI/IPC/Worker/MaaFramework/Python Agent/MaaNOP Resource/游戏窗口自然终结为 Succeeded。随后同一 Worker 接受 Run `f86ba849-d556-443c-bfd2-0686562be705`，在 Run 与唯一 Plan Item 均为 Running 后收到 `run.stop`，先返回 `stop_requested`，再确认 MaaFramework Stop并终结为 Cancelled。两次 Run 均释放各自 Agent/execution context，游戏、Worker 与 Child Session 保持运行；首片全部 PASS 条件已满足。该结果不冻结 Supported Baseline，MaaNOP snapshot、Python `maa` 与 Maa.Framework 精确组合仍须单独决策。
