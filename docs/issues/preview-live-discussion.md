# 游戏画面 Preview 连续预览设计讨论

状态：讨论已收口，[可执行规格](preview-live-spec.md) 已发布到本地问题跟踪器并标记 ready-for-agent。
本文保留 grill-with-docs 的需求与调研过程；技术定稿以规格为准，正式实现尚未修改。

## 已确认的用户目标

- 希望预览支持最高约 30 fps，不要求稳定锁定 30，重点是观感顺畅。
- 仍需关注 CPU 和内存开销，但不设数值指标限制；不采用此前建议的 CPU +2 个百分点、内存 +64 MiB 门槛。
- 截图方式由 MaaNOP 负责，本仓库遵循其控制器配置，不独立指定 FramePool/GDI/PrintWindow 或提供后端选型。
- 自动任务处于等待、没有主动截图时，预览仍应持续显示游戏动画。
- 启动运行环境完成、检测到火影微端后即应开始展示预览；任务开始前和结束后也需要显示。
- 用户允许 GUI 预览不可见时停采；仅隐藏子桌面而 GUI 预览可见时继续采集。
- 微端关闭后清除旧画面并等待；同一运行环境中重新出现匹配的游戏窗口时，自动恢复预览，
  不要求点击“重新连接”，不自动启动游戏、重启 Worker 或重跑任务。
- 窗口仍在但截图暂时失败时，保留最后一帧并低频自动重试，不显示“画面更新暂停”等新提示，
  不弹窗、不改变任务状态；若窗口已关闭则仍清除旧画面。
- 先探索并用简单 Demo 看实际效果，再讨论、形成 SDD 规格，随后开发。
- 用户已亲自观察 Demo，确认 GDI、FramePool、PrintWindow 三种方式流畅度都够用。
- 暂不增加高清采集；沿用最大 640×360、保持比例的预览，放大查看复用同一帧。
- 用户确认游戏不允许打开多个游戏窗口，本次不设计多窗口选择或切换。
- 游戏窗口本身最小化时遵循 MaaNOP 截图能力，不主动还原或置顶；失败时保留上一帧，
  成功返回黑帧时按原样显示，不额外识别黑屏来判断截图失败。

## 已知实现与实验事实

正式 Preview 仅在活动任务 Starting/Running、GUI 首页预览可见且连接正常时显示。
Worker 使用自动化 Controller 的 cached image，约每 200 ms 采样，缩放后编码 PNG，
通过现有 Named Pipe JSON/base64 发送。GUI 停止轮询不等于 Worker 当前采样循环停止。

2026-09-19 的独立 Demo 在 Child Session 内截图，经固定 640×360 像素缓冲传到主桌面 WPF 窗口。
它没有发送游戏输入或提交自动任务；各方式共享同一显示路径。
短时样本的预览更新中位数约为 GDI 25.87/s、FramePool 24.93/s、PrintWindow 23.69/s。
两个 Demo 进程的 CPU 中位数分别为 3.15%、0.77%、8.83%，按当时 16 个逻辑处理器归一化。
私有内存分别约为 136.95、195.60、154.16 MiB。

这些数据不是同场景的严格 A/B：未计入游戏、DWM、RDP/GPU，包含独立 WPF/.NET 进程成本，
且模式切换会保留部分分配。不能视为生产集成增量或完整设备负载验收。
FramePool 也出现过短时较低更新率，显示更新计数不等于独立视频帧数。

本地原始记录位于忽略目录 `artifacts/preview-demo/` 的 README、FEASIBILITY 和 CSV；
上述必要结论在此保留，避免将未提交实验文件视为仓库长期可用证据。

## 现有架构约束

[ADR 0021](../adr/0021-use-run-scoped-latest-frame-preview.md) 规定复用同一个 Controller 缓存、
约 5 FPS、PNG/JSON，并排除第二个 Controller 与二进制预览通道。
实时预览候选方向会改变其中部分决策，必须明确记录替代范围，不能静默覆盖。
[ADR 0016](../adr/0016-bound-ipc-frames-and-never-block-runs-on-gui-delivery.md) 的有界传输和
GUI 消费不能阻塞 Run 的不变量仍应保留。

## 已认可的技术方向与待定细节

- 采集与显示分别放入现有 Worker 和 GUI，避免引入 Demo 的额外进程。
- 保持预览缓冲有界、只取最新帧，减少高频 PNG/JSON 编解码和分配。
- 用户认可现有 Worker 内的独立只截图实例；按要求完成同类开源前端对照后，继续采用该方向。
  它不是任务外预览的必要条件；延长共享 Controller 生命周期也可支持无任务时截图。
  两者均遵循 MaaNOP 截图配置；推荐独立实例的依据是隔离动作队列并保留当前任务生命周期，详见下文。
- 预览实例归现有 Worker 所有，生命周期不依赖 Run；停止/释放协议与传输形式已在规格中确定。

## 第一轮结论

1. 不设资源数值限制；截图方式属于 MaaNOP，本仓库不独立选择。
2. Preview 扩展到没有活动任务的时间，启动完成且游戏画面可用即显示。
3. 用户允许在 GUI 预览不可见时停止采集；本轮采用该简化行为。

上述范围决定记录于 [ADR 0024](../adr/0024-preview-is-independent-of-active-runs.md)。
当前正式代码仍是 ADR 0021 的 V1 实现；领域定义描述已确认的新目标，不表示实现已完成。

## 恢复方案的代码核查

- Worker 在任务结束后回到 Ready，继续连接循环；没有 Idle 超时退出，通常无需为新预览额外启动进程。
- Worker Ready 表示依赖检查通过，不表示游戏窗口已经出现。
- 游戏启动验证以微端启动器或进程名及 Session 为依据，不足以判断游戏画面可截图。
- 现有 Run 初始化已按 MaaNOP 的 class/window regex 查找 HWND，并检查进程所属 Child Session。
  预览需要在任务外复用这类窗口发现规则，不能把截图目标简化为“任意同名进程”。
- 启动完成后进入等待目标窗口的状态，窗口可用才显示画面；关闭后清除旧画面，
  同一 Child Session 内重新出现符合规则的窗口时自动恢复预览。用户已确认该行为。

## 第二轮结论：自动发现与恢复

- 启动完成但游戏窗口尚未出现：显示“等待游戏窗口”。
- 符合 MaaNOP 配置的游戏窗口出现且可截图：自动显示 Preview。
- 游戏窗口关闭：清除旧画面，回到等待状态。
- 等待时低频检查窗口，建议约 1～2 秒一次，不持续提交截图。
- 用户重开微端后，自动发现新的匹配窗口并恢复 Preview；不要求额外的手动重连。
- Preview 恢复只作用于预览本身，不重开游戏、不重跑任务，也不改变现有任务失败/停止语义。
- Worker 退出或 Child Session 结束仍属于既有运行环境生命周期，不在预览中静默重建。

## 后续规格工作

- 产品边界已完成本轮澄清，不再保留未回答的产品问题。
- 技术设计：Preview 独立于 Run 后的身份、传输与停止/释放设计；由代码核查与实验确定，
  不要求用户选择缓冲实现、同步机制或权限细节。

## 第四轮结论

- Q6：暂不另外采集高清图，沿用现有尺寸和同帧放大方式。
- Q7：用户确认游戏不允许多开游戏窗口，因此移除多窗口选择、任务与预览间目标切换等候选设计。
  仍按 MaaNOP 窗口匹配规则和 Child Session 身份确定目标，不将启动器误作游戏窗口。
- Q8：遵循 MaaNOP 截图方式的实际能力，用户授权按实际情况决定暂停或黑屏行为。
  采用与 Q5 一致的简单规则：失败时保留上一帧并低频重试；还没有成功帧时保留原有占位；
  成功返回黑帧或重复帧时照常显示，不通过内容猜测截图失败，不主动还原或置顶游戏窗口。
  窗口关闭或 Worker/Session 身份失效仍清空；隐藏子桌面而 GUI 预览可见时仍继续采集。

本轮产品边界已收口。随后 to-spec 已整理技术设计与验收，不直接开始正式实现。

## 跨进程、跨 Session 的部署边界

用户再次强调：游戏运行于桌面分身，画面必须由分身内的 Worker 采集并传回主桌面 GUI。
独立截图 Controller 指现有 Worker 进程内的独立对象，不是在主桌面 GUI 中截图，也不增加一个 Worker。

```text
Child Session：游戏窗口 → 现有 Worker 内的预览 Controller → 有界最新帧
                                                           ↓ 跨进程、跨 Session 传输
主桌面：                                             GUI 更新预览位图
```

Worker 负责发现和验证本 Session 的目标窗口、截图、缩放及发布帧；GUI 负责预览可见性和显示。
任务 Controller 仍在同一 Worker 内按既有执行流程工作。预览无需绕经 MaaNOP Python Agent。

已观察的 Demo 实际覆盖了分身采集进程到主桌面显示进程的跨 Session 传输，采用临时文件映射。
它证明该实验路径能连续显示，但尚未接入正式 Worker 的身份校验、连接恢复和退出清理流程，
不能将 Demo 成功等同于正式 Worker 集成通过。

传输建议是保留现有 Named Pipe 承担控制、状态及预览启停协商，将高频像素与控制消息分开。
不直接把当前 PNG/JSON/base64 的轮询频率从约 5 fps 调到 30 fps：这仍保留逐帧编码、解码及分配成本，
且现有 ProtocolConnection 通过同一个写入门发送完整消息，大帧可能延迟后续任务控制或状态消息。
讨论阶段比较文件映射最新帧缓冲和独立二进制管道；规格最终选择文件支持映射配合跨 Session Mutex，
不默认普通命名映射自然跨 Session 可见。该定稿记录于 [ADR 0025](../adr/0025-separate-preview-pixels-from-control-ipc.md)。

正式设计需覆盖有界容量、读写一致性、慢消费者丢旧帧、Worker/Session 与目标窗口换代识别、
GUI 断开后停采，以及映射或通道的权限和释放。GUI 消费速度不得阻塞任务执行或其停止流程。
对照同类前端只能支持 Controller 生命周期的取舍，不能替代本仓库跨 Session 传输的验证。

## 第三轮已确认与调研要求

- Q4：独立截图实例可接受，但先检查同类开源实现；用户询问它是否必需、是否有更优方案。
- Q5：保留最后一帧、低频重试，不增加暂停文字/状态提示；成功后正常更新，不弹窗、不影响任务。
- Q5 的保留规则不覆盖目标窗口关闭、Worker/Session 身份失效等已明确应清空的情况。

## 同类开源实现对照与建议

2026-09-19 核查官方源码，固定版本以免后续实现变化造成歧义：

| 项目 | 实际采集方式 | 对本项目的参考意义 |
| --- | --- | --- |
| MFAAvalonia，`a3c2483` | 默认独立截图 Controller，也允许共享；现行 UI 主动提交截图，再读取完成帧 | 独立实例不是罕见做法；上一帧未完成时不追加请求，预览折叠时不采集 |
| MFW-PyQt6，`5bf203e` | Monitor 与任务共享 MaaFW/Controller，独立循环主动截图；可在无任务时连接 | 证明无任务预览不必使用第二个 Controller，但连接、任务停止和释放必须协调 |

MFAAvalonia 的证据为 [实例选择][mfa-owner]、[实际 UI 循环][mfa-ui]、[单个在途截图][mfa-capture]。
其独立实例使用同一 Controller 配置工厂，但仍带鼠标/键盘配置，并另外建立 Tasker/Resource；
本项目只需要截图能力，不照搬这一整套对象，也不把它描述为已经禁用输入。

MFW-PyQt6 的证据为 [共享对象的构造实参][mfw-owner]、[主动截图][mfw-capture]、
[任务结束与连接失效时停止 Monitor][mfw-stop]。其行为不完全符合本项目要求的任务结束后继续预览与自动恢复。
部分旧注释与当前调用链不一致，本次结论以实际构造和调用链为准。

必须独立的是预览更新循环与可用时间，而非必然使用第二个 Controller。
共享长寿命 Controller 可以减少一份采集上下文，但本仓库目前由每个 Plan Item 创建和释放 Controller；
改为共享需要调整任务停止、窗口切换和资源释放的所有权。
本地使用的 MaaFramework v5.12.3 中，每个 Controller 的截图和输入动作通过同一个队列串行执行，
共享方案的高频预览请求会占用这个队列；独立实例能够分开队列，但不能消除游戏窗口或设备层面的资源竞争。
证据见 [Controller 动作提交][maa-controller] 与 [AsyncRunner][maa-runner]。

因此调研后仍建议：在现有 Worker 内持有独立的只截图 Controller，继续使用 MaaNOP 截图配置，
不创建额外进程，不为预览创建 Tasker/Resource/Agent，不发送输入；既有任务保留自己的 Controller。
采集最多一帧在途，不补积压帧；显示只消费最近完成帧，GUI 预览不可见时停止采集。
这是结合当前仓库改动范围的工程建议，不代表双实例一定比共享更省资源。
任务并行时的负载、任务耗时和停止响应仍须实测，不因其他项目采用此模式就视为已通过。

另外，v5.12.3 AsyncRunner 的截图作业状态会写入 `status_map_`，完成时保留，`clear()` 才统一清理。
这提示长时间高频提交需要观察原生侧内存趋势；固定像素缓冲不足以单独证明整体内存有界。
目前未测得其长期增长量，也未据此决定定时重建策略，不将源码推断表述为已验证的生产内存泄漏。

[mfa-owner]: https://github.com/MaaXYZ/MFAAvalonia/blob/a3c24837f4fe2733ad4c7855e29f03b24b86d407/MFAAvalonia/Extensions/MaaFW/MaaProcessor.cs#L1071-L1109
[mfa-ui]: https://github.com/MaaXYZ/MFAAvalonia/blob/a3c24837f4fe2733ad4c7855e29f03b24b86d407/MFAAvalonia/ViewModels/Pages/TaskQueueViewModel.cs#L3377-L3409
[mfa-capture]: https://github.com/MaaXYZ/MFAAvalonia/blob/a3c24837f4fe2733ad4c7855e29f03b24b86d407/MFAAvalonia/Extensions/MaaFW/MaaProcessor.cs#L1291-L1358
[mfw-owner]: https://github.com/overflow65537/MFW-PyQt6/blob/5bf203e965a416cfbbb5a1cc272490607d7350b9/app/core/runner/runtime_context.py#L162-L175
[mfw-capture]: https://github.com/overflow65537/MFW-PyQt6/blob/5bf203e965a416cfbbb5a1cc272490607d7350b9/app/core/runner/monitor_task.py#L51-L60
[mfw-stop]: https://github.com/overflow65537/MFW-PyQt6/blob/5bf203e965a416cfbbb5a1cc272490607d7350b9/app/view/monitor_interface/monitor_interface.py#L750-L770
[maa-controller]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Controller/ControllerAgent.cpp#L101-L104
[maa-runner]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Base/AsyncRunner.hpp#L110-L179

## 尚待技术验证

- GUI 预览保持可见、隐藏子桌面/RDP 宿主后的连续动画。
- 与真实自动任务并行时的负载、执行耗时和停止响应。
- 同场景关闭/开启/关闭预览的资源增量、稳态内存和释放表现。

这些是实验问题，不能以用户对设计方案的认可代替通过实测。

## to-spec 收口

用户已确认测试边界：复用 GUI–Worker 通信集成入口，以可控截图适配器覆盖任务外预览、恢复、
旧帧隔离与停止，再补真实 Child Session 的流畅度、隐藏分身、任务并行负载和长期内存验收。
规格选择文件支持映射传像素、现有 Pipe 协商带租约订阅；读写采用非阻塞取得的跨 Session Mutex。
协议升为 2，GUI/Worker 同版发布；无旧预览降级链。具体契约及未返回原生调用的释放规则以规格为准。
测试范围确认不等于测试已运行；本轮仅检查规格与文档一致性，未构建、部署或更改生产代码。
