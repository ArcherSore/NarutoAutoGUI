# NarutoAutoGUI

NarutoAutoGUI 是 MaaNOP 的专用 Windows GUI，负责协调主桌面上的用户操作与 Child Session 中的自动化运行环境，并计划逐步替代 MFAAvalonia 的前端职责。它通过桌面分身隔离游戏和自动化输入，使主桌面在挂机期间保持可用。

## Language

**MaaFramework（MaaFW、Maa）**:
执行自动化任务的框架运行时；MaaNOP 基于它定义和运行具体自动化流程。
_Avoid_: MaaNOP、MFAAvalonia

**MaaNOP**:
基于 MaaFramework 构建的火影忍者 Online 自动化项目，拥有自己的 Project Interface、任务、选项、资源和 Agent。
_Avoid_: MaaFramework、Maa

**NarutoAutoGUI**:
面向 MaaNOP 的专用 Windows 前端和运行环境协调者，长期承担当前由 MFAAvalonia 提供的用户交互职责。
_Avoid_: MaaNOP

**MFAAvalonia**:
开发期间用于人工验证 MaaNOP/MaaFramework 和对照前端行为的诊断后备；它不属于 NarutoAutoGUI 的正常执行链路，不提供或共写运行配置，也不得与正在执行 MaaNOP 的 Child Session Worker 并行控制游戏。
_Avoid_: MaaFramework、MaaNOP

**正常执行链路**:
用户从 NarutoAutoGUI 发起并观察 MaaNOP 自动化运行的唯一产品链路：NarutoAutoGUI → Child Session Worker → MaaFramework → MaaNOP。
_Avoid_: MFAAvalonia 执行链路

**Project Interface**:
MaaFramework 项目向前端声明控制器、资源、任务、选项及展示元数据的标准界面定义；MaaNOP 的定义文件名为 `interface.json`。
_Avoid_: 用户配置、运行配置

**MaaNOP PI Subset**:
NarutoAutoGUI 首版完整支持的 Project Interface v2 执行语义，仅覆盖当前 MaaNOP 实际使用的单 controller、单 resource、global/task option、input、switch、嵌套 option、校验、占位符和 pipeline override。
_Avoid_: 通用 Project Interface V2 客户端、猜测兼容

**Application Settings**:
NarutoAutoGUI 拥有并以 SchemaVersion 2 保存在 `config/settings.json` 的应用级环境设置，包括 launch-only 的游戏启动入口/参数和直接包含 `interface.json` 的 MaaNOP Project Directory；它不包含 MaaNOP task/option 选择。项目目录影响 desired runtime profile，游戏启动入口可能是 launcher且不等同于 Controller 目标进程。
_Avoid_: MaaNOP Config、Run Plan

**Game Launch Entry**:
主 GUI 在 Child Session 中启动游戏环境时使用的 executable 和参数；它可能是 launcher，只负责启动，不约束最终拥有目标 HWND 的进程路径。
_Avoid_: Controller Target Process、目标窗口身份约束

**MaaNOP Config**:
主 GUI 以 SchemaVersion 1 保存在 `config/maanop-config.json` 的一份当前 MaaNOP 用户意图，只记录选中的顶层 task，以及由用户操作明确形成的 SelectedCase/input 值；显式值即使等于当前 default 也保存，只有用户选择“跟随项目默认”才删除并回到 Unset。
_Avoid_: Application Settings、Run Plan、MFAAvalonia 配置

**Config Status**:
GUI 根据当前 Project Interface 对持久化 MaaNOP Config 得出的 Valid、Warning 或 Invalid/NeedsReview 结果。忽略某条配置可能改变本次 Run 意图时必须 Blocking 并禁止 Start；完全不参与当前解析的旧条目或 dormant intent 才可非阻塞 WARN。
_Avoid_: 一律 WARN 后忽略、自动修复配置文件

**Dormant Intent**:
合法但因父 case 当前未激活而暂不参与 resolved options、pipeline override 或 Run Plan 的嵌套 option 显式值。它可继续保存在扁平 ExplicitOptions 中，父分支重新激活时恢复。
_Avoid_: 无效配置、当前有效参数、树形持久化

**桌面分身**:
让游戏和自动化输入在隔离桌面中运行、避免抢占主桌面鼠标键盘的用户能力；当前由 Windows Child Session 提供隔离环境。
_Avoid_: 普通后台进程、远程桌面主机

**Child Session**:
支撑桌面分身的隔离 Windows Session，游戏和 MaaFramework 执行环境在其中运行，同时保持主桌面可用。
_Avoid_: 子进程、Worker

**Child Session Worker**:
由 NarutoAutoGUI 启动在 Child Session 中、承载 MaaFramework 和 MaaNOP 单次运行生命周期的进程；主桌面通过仅限本机的 IPC 控制它并接收状态与日志。
_Avoid_: MFAAvalonia、Windows 服务、远程服务、后台线程

**NarutoAutoWorker**:
实现 Child Session Worker 角色的独立可执行组件，携带固定版本的 MaaFramework C# Binding、Native runtime 和 AgentBinary，并在 Child Session 的实际环境中运行。
_Avoid_: MFAAvalonia、MaaNOP 项目目录中的 MaaFramework

**Worker Launch Context**:
主 GUI 启动 Worker 时提供、并在该 Worker 实例生命周期内唯一确定“在哪里、用什么 MaaFramework 环境执行”的不可变上下文，包含 MaaNOP 项目根目录、单个 Win32 controller、单个 resource、单个 Agent 和 Runtime Profile Digest。GameExecutablePath/GameArguments 是 GUI launch-only 设置，不属于该上下文；Worker 不从 Run Plan 接受第二份运行环境配置。
_Avoid_: Run Plan、Application Settings 文件、运行时热切换

**Launch Manifest**:
GUI 为单个 workerInstanceId 原子写入 `state/launch/` 的一次性、只读 Worker Launch Context 文件，UTF-8 JSON hard limit 为 256 KiB。Worker 启动时只读取和严格校验一次，随后持有不可变内存副本；Manifest 不包含 Launch Token、Run Plan、MaaNOP Config、游戏启动设置或凭据。
_Avoid_: Application Settings、Admission Record、运行时配置推送

**Runtime Profile Digest**:
使用 Canonical Digest v1 和独立 Runtime Profile domain prefix，对 Worker Launch Context 中 projectRoot、Win32 controller 六项配置、有序 resource name/绝对路径、Agent childExec/childArgs/workingDirectory 计算的 `sha256:<64 lowercase hex>` 摘要。它不 hash Manifest 原始 bytes，也不包含 workerInstanceId、项目/interface provenance、Manifest 路径、launchContextVersion 或游戏启动设置；GUI提交，Worker通过共享协议实现独立规范化重算并核对。
_Avoid_: Plan Digest、Interface Digest、版本协商

**Canonical Digest Version**:
独立于 launchContextVersion 和 planVersion 的摘要规范版本。v1 使用手写固定 schema 的 canonical UTF-8 JSON、ordinal object-key 排序、保持 array 顺序、保留严格解析后的 JSON number token 词法，并分别添加 `NarutoAutoGUI.RuntimeProfileDigest.v1\n` 或 `NarutoAutoGUI.RunPlanDigest.v1\n` domain prefix 后计算 SHA-256；规则变化必须新增版本，不能静默改变 v1。
_Avoid_: 普通 JSON serializer 默认行为、完整 RFC 8785 声明、无 domain separation 的通用 JSON hash

**Canonical Runtime Path**:
Runtime Profile Digest 输入中 projectRoot、resource path 和 Agent workingDirectory 的纯字符串 Windows 路径表示：输入必须 fully-qualified，经 `Path.GetFullPath`、反斜杠、盘符大写和非 root 尾分隔符移除处理；不展开环境变量、不解析 symlink/junction、不按文件系统实际大小写改写，也不要求路径当时存在。resource path 保持声明顺序且不去重。
_Avoid_: Child Session 中实际 executable 解析结果、依赖存在性检查、路径等价性探测

**Source Interface Digest**:
以 `sha256:<64 lowercase hex>` 表示、直接对磁盘上 `interface.json` 原始 bytes 计算的 provenance 摘要，不参与 Runtime Profile Digest，也不决定 Worker 是否需要替换。BOM、换行、缩进或 property 顺序变化均可改变它；文件本身仍须通过 UTF-8 JSON 校验。任务、option、默认值或 pipeline override 改变可使当前 Run 使用新的 interface digest，而继续复用 runtime profile 相同的 Worker。
_Avoid_: Runtime Profile Digest、当前 PI 一致性保证

**Worker Launch Transaction**:
按 Launch Manifest 原子写入、Pending Admission 原子写入、Task Scheduler COM 提交的固定顺序启动 Worker。前两步任一失败都不得调用 Scheduler；只有 Manifest 而没有有效 Admission Record 的文件是可在恢复时清理的 orphan。
_Avoid_: 数据库事务、先启动后落盘、无 Admission Worker

**Admitted Runtime Profile Digest**:
当前已接纳 Worker 从自身 Launch Context 报告的 runtimeProfileDigest，是该 Worker 实际执行环境的权威诊断值。
_Avoid_: Desired Runtime Profile Digest、Snapshot freshness

**Desired Runtime Profile Digest**:
主 GUI 根据当前 Application Settings 和最新 MaaNOP Project Interface 解析出的期望 runtime profile 摘要。它与 admitted digest 不同会禁止 Start，但不使当前 Worker Snapshot 变 stale；fresh Snapshot 确认 Idle 后可据此替换 Worker。
_Avoid_: Admitted Runtime Profile Digest、Worker 自报配置

**Controller Target Window**:
Worker 在当前 Child Session 中依据 controller classRegex/windowRegex 找到并交给 MaaFramework Win32 Controller 的 HWND。Worker 必须验证 HWND 所属进程实际位于当前 childSessionId，但首版不要求其 executable path 等于 Game Launch Entry。
_Avoid_: launcher 进程、主 Session 窗口、固定 targetProcess 约束

**Worker Identity**:
一个被 NarutoAutoGUI 接纳的 Worker 进程实例，由 Named Pipe 服务端取得的真实 PID、系统查询出的真实 Windows SessionId、Worker Instance ID 和 Launch Token 共同确定；Pipe connection 可反复断开和重建，不属于身份。
_Avoid_: Pipe connection、客户端自报 PID/SessionId、协议版本、Worker 版本

**Worker Instance ID**:
主 GUI 为每次 Worker 启动生成、由 Worker 在 Snapshot 和每次 `connection.open` 中报告的唯一进程实例标识；它用于区分 Worker 和日志 sequence 空间，不是 PID、Launch Token 或协议版本。
_Avoid_: Worker PID、Launch Token、Run ID

**Launch Token**:
主 GUI 启动 Worker 时生成并通过启动参数传入、由 Worker 在首次登记和后续重连时携带的随机 admission credential。它与 Worker Instance ID 分离，随 Worker/Child Session 失效，只保存在 ACL 限制为当前 Windows 用户的 Worker Admission Record；不进入普通日志、UI、Run Plan 或 Snapshot。
_Avoid_: Worker Instance ID、Run ID、协议版本、MaaNOP 配置

**Worker Admission Record**:
主 GUI 原子维护在 `state/worker.json` 的当前 Child Session Worker 最小 admission journal，只包含 workerInstanceId、launchToken、childSessionId、可空 workerPid、runtimeProfileDigest、createdAtUtc。它只用于 GUI 重启后的身份恢复，不保存 Worker/Run State、Run Plan 或日志；GUI 是唯一 writer。
_Avoid_: Worker Snapshot、Run 持久化、Application Settings、MaaNOP Config

**Pending Admission**:
Worker Admission Record 中 `workerPid=null` 的预接纳状态，表示 GUI 已预留 instance ID/token 但尚未确认真实 PID。grace period 内即使暂未发现 Worker也必须保留并禁止启动第二个 Worker；只有到期且确认无匹配进程和有效登记后才作为 abandoned launch 删除。
_Avoid_: 启动失败、WorkerNotStarted、可立即覆盖的空记录

**Worker Recovery Conflict**:
恢复时发现多个 Worker 候选或其他无法安全确定唯一 Worker identity 的本地阻塞状态。GUI 不自动选择、不启动新 Worker，等待人工恢复。
_Avoid_: worker_already_registered、自动抢占、随机选择候选

**Worker Registration**:
Worker 连接固定用户级 Named Pipe 后通过 `connection.open` 完成的接纳过程。GUI 使用操作系统事实验证客户端真实 PID、SessionId 和进程映像，核对 Worker Instance ID、Launch Token、Admission Record 与协议/runtime profile 兼容性；每个 Child Session 同时最多接纳一个有效 Worker。
_Avoid_: 自动抢占、多 Worker 注册、仅信任登记载荷

**Supported Baseline**:
经过 MaaNOP 端到端实机验证并由 NarutoAutoGUI 明确支持的一组 GUI/Worker、MaaFramework、MaaNOP 和 Python Agent 版本组合。
_Avoid_: latest、floating version、推测兼容

**Dependency Readiness**:
NarutoAutoWorker 在 Child Session 实际环境中对 MaaFramework、Python、Python `maa` 模块、MaaNOP Agent 和资源可用性给出的权威就绪结果及诊断。完整检查发生在 Worker 初始化；每次接受 Run 前只执行必要的轻量 preflight；IPC/GUI 重连和活动 Run 期间不触发重检。
_Avoid_: 主桌面环境推测

**Agent Probe**:
Worker 初始化时在 Child Session 中使用真实 child_exec、项目根目录 working directory、ArgumentList、`UseShellExecute=false` 和真实 Agent 环境启动的有超时短进程，用于报告 sys.executable、Python 版本、`maa`/AgentServer/Toolkit 导入状态和 Agent 入口有效性；它不得启动长期 AgentServer 或调用 `AgentServer.join()`。
_Avoid_: 主 Session PATH 探测、自动安装、正式 Agent 生命周期

**Agent Identifier**:
每个 Run 中由绑定当前 MaaTasker 的 MaaAgentClient 在 LinkStart 时提供的连接标识。Worker 只在 LinkStart 启动回调中把它作为 Python Agent ArgumentList 的最后一个参数；GUI 和 Worker 均不在 MaaFramework 之外自行生成。
_Avoid_: Worker Instance Token、Run ID、interface 中的显式 identifier

**Worker Replacement**:
没有 activeRun 时，因 MaaNOP 项目根目录或其他 Runtime Profile 字段显式变化而通过 `worker.shutdown` 结束旧 Worker，并以新的 Worker Instance ID、Launch Token 和 Worker Launch Context 启动新 Worker。它不是热加载；旧 Worker 的 lastRun、accepted-run ledger 和日志随进程退出失效。
_Avoid_: IPC 重连、活动 Run 中重启、进程内 runtime 热切换

**Worker State**:
由 NarutoAutoWorker 维护并通过 Run Snapshot 报告的进程内部健康与执行就绪状态：Starting、Ready、NotReady、Faulted、Stopping。Ready 与 Run 是否正在执行正交；NotReady 表示进程和 IPC 健康但依赖或项目环境不满足执行条件，并携带结构化原因；Faulted 只表示仍存活的 Worker 已进入不可继续工作的内部故障。
_Avoid_: GUI Observation、Run State、IPC 断开、Worker 退出

**Run State**:
由 NarutoAutoWorker 唯一维护的 Run 生命周期状态集合：Idle、Starting、Running、Stopping、Succeeded、Failed、Cancelled。Snapshot 顶层 `runState` 只表示 execution slot，只允许 Idle、Starting、Running、Stopping；activeRun 为空当且仅当顶层为 Idle，终态 Succeeded、Failed、Cancelled 只出现在 lastRun.state。依赖初始化失败属于 Worker NotReady，而不是 Run Failed。
_Avoid_: Worker State、GUI Observation、GUI 推测状态

**Run Stop Acceptance**:
Worker 对匹配 activeRun 的 `run.stop` 完成串行状态变更、将 Run 置为 Stopping 并取消尚未开始 Plan Item 的时刻。IPC ACK 只确认停止请求已接受，不确认 MaaFramework 已停止；GUI 只有观察到后续 Cancelled 终态才能显示“已停止”。
_Avoid_: MaaTasker Stop 确认、Run Cancelled、同步停止调用

**Run-layer Cancellation**:
NarutoAutoGUI 在已接受 `run.stop` 且 MaaFramework 停止得到确认后赋予仍受该停止影响的 Plan Item 和 Run 的 Cancelled 终态。它不是 MaaJobStatus 的直接映射；停止导致的 MaaJobStatus Failed 不自动变成 Plan Item Failed。
_Avoid_: MaaJobStatus、自然任务失败、未确认停止

**GUI Observation**:
主 GUI 根据 Admission Record、Named Pipe、真实 Worker PID/SessionId 和 WTS Child Session 状态在本地派生的身份、可达性与存活观察：WorkerNotStarted、WorkerStarting、Connected、IpcDisconnected、WorkerExited、WorkerRecoveryConflict、ChildSessionEnded；它不属于 Worker Snapshot，也不覆盖 Worker 或 Run 的最后已知状态。判定优先级为 ChildSessionEnded > WorkerRecoveryConflict > WorkerStarting > Connected > IpcDisconnected > WorkerExited > WorkerNotStarted。
_Avoid_: Worker State、Run State、Worker 自报状态

**Stale Snapshot**:
GUI 在 IPC 断开、Worker 退出或 Child Session 结束后保留的最后一次 Run Snapshot，必须标记为非实时并记录最后更新时间；只有重连后取得的新完整 Snapshot 才能替换它。
_Avoid_: 当前权威状态、GUI 推测的失败终态

**Fresh Snapshot**:
GUI 在当前 Connected Pipe 上取得、requestId 对应当前请求、workerInstanceId 匹配、schema 可解析且内部状态一致的最新完整 Snapshot。它不要求 admitted 与 desired Runtime Profile Digest 相等；二者不等时 Snapshot 仍可 fresh，但 Start 禁用。
_Avoid_: Event-only 状态、stale Snapshot、上一 Worker 的 Snapshot

**Snapshot Freshness**:
与 GUI Observation 正交的 GUI 本地标志。Connected 只证明 Worker identity admission 完成；requestId、workerInstanceId、schema 和内部一致性校验通过后即可为 true，false 时显示“已连接，正在同步”。Runtime Profile Digest equality 不属于 freshness 条件。
_Avoid_: Connected、Worker State、Run State

**State Revision**:
单个 workerInstanceId 内从 1 开始递增的原子权威状态提交序号。一次提交可以同时改变多个 Worker/Run/Plan Item 字段但只增加一次，并产生恰好一个同 revision 的 stateChanged notification；它与日志 sequence 完全独立。
_Avoid_: Log sequence、字段变更计数、跨 Worker revision

**Configuration Edit Lock**:
只要最新可信 Snapshot 的 activeRun 非空，主 GUI 就禁止编辑 MaaNOP task/option；IPC 断开后若最后可信 Snapshot 仍有 activeRun，锁继续保持，直到重连并取得 fresh Snapshot 明确显示 activeRun 为 null。
_Avoid_: IPC 断开即解锁、下一次 Run 草稿

**MaaNOP Run**:
Child Session Worker 接受启动请求后独立持有的一次 MaaNOP 执行，具有唯一 Run ID，并按照不可变 Run Plan 串行处理计划项；它可以跨主 GUI 或 IPC 断线继续，但不跨 Worker 进程退出恢复。
_Avoid_: IPC 连接、GUI 操作、可持久化作业

**Active Run**:
Worker 当前正在 Starting、Running 或 Stopping 的唯一 Run；不存在时 Snapshot 的 `activeRun` 为 null，且顶层 `runState` 为 Idle。
_Avoid_: Last Run、排队 Run

**Last Run**:
Worker 最近一次已经进入 Succeeded、Failed 或 Cancelled 的 Run 终态快照；它与 Idle 并存，并保留到新 Run 被接受或 Worker 退出。
_Avoid_: Active Run、持久化历史记录

**Run ID**:
标识一次 MaaNOP Run 的唯一值，用于关联幂等控制请求、状态、日志和最终结果。
_Avoid_: Request ID、进程 ID、Session ID

**Plan Digest**:
使用 Canonical Digest v1 和独立 Run Plan domain prefix 对不可变 Run Plan 完整内容计算的 `sha256:<64 lowercase hex>` 摘要，用于判断同一 Run ID 的重试是否仍是同一计划。它包含 createdAtUtc、项目/interface metadata、Plan Item ID 和 label 等内容身份字段，但不包含 runId、requestId 或 digest 自身；它不是仅表示执行语义的摘要，也不代替 Run Plan 或用于恢复执行。
_Avoid_: Run ID、Request ID、用户配置版本

**Run Start Attempt**:
用户单次点击 Start 后由 GUI 只构造一次的 runId、createdAtUtc、planItemIds、resolved values、pipelineOverride、Run Plan 和 Plan Digest 集合。IPC timeout 或 response 丢失后的重试必须原样重发该集合，不重新读取 Project Interface、重新解析配置、更新时间或生成 ID；GUI 重启后通过 Worker Snapshot 恢复，不重建旧 runId 的计划。
_Avoid_: 每次 transport retry 重新 resolve、GUI 重启后猜测重建 Run Plan

**Accepted-run Ledger**:
Worker 进程内用于记录所有已正式接受 Run 的轻量防重账本，至少保存 runId、planDigest 和已有的 terminal summary。新 Run 可以清除完整 lastRun，但不能使旧 runId 在同一 Worker 实例内再次执行；账本不持久化，也不用于恢复 Run。
_Avoid_: Run 历史数据库、持久化恢复、任务队列

**Run Plan**:
主 GUI 在启动 MaaNOP Run 时根据当前 Project Interface 和 MaaNOP Config 解析并提交的不可变有序计划，描述“这一次执行哪些任务和参数”。它包含项目/interface 诊断信息、Runtime Profile Digest、解析后的全局 option 和一个或多个 Plan Item，但不重复 controller、resource、Agent 或项目根目录等 Worker Launch Context 内容；开始后的 GUI 配置变化不影响它。
_Avoid_: Worker Launch Context、可变任务队列、用户配置、第二份 runtime 配置

**Plan Item**:
Run Plan 中一次顶层 MaaNOP task 执行，具有独立 Plan Item ID，并冻结该次执行的 task name、entry 和 option/参数快照。
_Avoid_: 顶层 task 定义、task name

**Plan Item ID**:
在一个 MaaNOP Run 内唯一标识 Plan Item 的值，用于关联状态、结果和日志。
_Avoid_: task name、Run ID、Request ID

**Run Snapshot**:
由 Child Session Worker 提供的某一时刻完整权威运行视图，包含非敏感 Worker 标识（如 workerInstanceId、真实 PID/SessionId）和版本、Worker State、顶层 runState、activeRun、lastRun、完整 Run Plan、各 Plan Item 状态、解析参数、时间、错误和可续接的近期日志位置；Launch Token 明确不属于 Snapshot。
_Avoid_: GUI 缓存、推测状态

**Snapshot Payload Budget**:
完整 `worker.getSnapshot` response envelope 加 WorkerSnapshot 序列化后的 UTF-8 JSON payload 必须不超过 3 MiB（不含 4-byte frame prefix）。Run Plan 不超过 1 MiB，Run 接受时还需预留整个 Run 共享的 512 KiB terminal diagnostics；4 MiB 仅是 transport hard limit。
_Avoid_: 仅计算 Run Plan 字符串、贴近 4 MiB 发送、无界诊断

**IPC Operation**:
NarutoAutoGUI 首版 Named Pipe 协议中固定的请求/响应操作集合：`connection.open`、`ping`、`worker.getSnapshot`、`worker.shutdown`、`run.start`、`run.stop`、`log.getSince`。
_Avoid_: 通用 RPC、pause/resume、任务队列操作

**IPC Frame**:
4-byte unsigned 32-bit little-endian `payloadLength` 加紧随其后的 UTF-8 JSON payload；单帧 hard limit 为 4 MiB。读取端先读取并校验固定前缀，再租用有界缓冲并精确读取 payload 字节，不支持压缩、通用分片或二进制 payload。
_Avoid_: 有符号长度、无界分配、可配置 frame size

**IPC Event**:
Worker 为实时显示发送、允许丢失的通知：`worker.stateChanged`、`run.stateChanged`、`log.entry`。GUI 不通过补齐 Event 恢复状态；重连后必须重新取得 Run Snapshot，并用 `log.getSince` 补取日志。
_Avoid_: 权威持久状态、可靠事件流、细粒度 task started/completed 事件

**Log Entry**:
Worker 初始化、依赖检查、内部诊断、IPC 生命周期或 Run 执行产生的一条结构化日志，具有 Worker instance 内单调递增的 sequence、UTC 时间、level、source、message、truncated 和可选 originalByteLength；runId、planItemId、taskName 只在适用时填写。message 按最多 64 KiB UTF-8 合法边界截断，Agent stdout/stderr 的 Plan Item 关联只是接收时上下文，不是任务状态依据。
_Avoid_: 仅限 Run 的日志、无序文本流、跨 Worker instance 日志序号

**Log Transport Cursor**:
GUI 为当前 workerInstanceId 持续维护的 `lastReceivedSequence`，用于实时去重和断线补取；用户“清空显示”不得重置它。
_Avoid_: 可见日志起点、跨 Worker instance 游标

**Log Display Baseline**:
GUI 本地控制当前可见日志范围的 displayEntries/displayBaseline；清空它只影响界面，不清除 Worker 缓冲，也不会使下一次补取重新灌入已清除的历史日志。
_Avoid_: Worker sequence、Log Transport Cursor、权威日志删除
