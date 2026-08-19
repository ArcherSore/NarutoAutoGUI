# 配置只保存显式用户意图，并阻止会改变执行含义的无效项

Application Settings 使用 SchemaVersion 2，字段为 GameExecutablePath、GameArguments、MaaNopProjectDirectory。项目目录规范化为绝对路径，定义为直接包含当前 `interface.json` 的目录，保存和使用前验证该文件直接存在；repo root、assets 父目录或 MFAAvalonia executable directory 均不能混称项目目录。GameExecutablePath 和 GameArguments 只影响下一次用户显式启动游戏，不属于 Run Plan或 active Run，也不触发 Worker replacement，activeRun 期间仍可编辑。

旧 `MaaNopExecutablePath` 仅在新 MaaNopProjectDirectory 字段完全不存在时参与内存迁移；新字段即使为空也优先。旧 exe 目录直接有 interface.json 时采用该目录，否则仅检查 `assets/interface.json` 并采用 assets，再失败则留空、WARN并要求用户选择，不增加其他启发式搜索。读取不覆写文件；用户正常保存时才原子写 SchemaVersion 2 并移除旧字段。旧 exe 永远不再进入正常启动链路。

MaaNOP Config 使用 SchemaVersion 1，SelectedTasks 保存 task.name 并按当前 PI 声明顺序写出；ExplicitOptions 按 option 自身顶层 key 扁平保存，select/switch 使用 SelectedCase=case.name，input 使用 Inputs 的 field-name 到 string。显式性来自用户操作事实，而不是与 default 比较：用户明确选择或填写即使等于当前 default 也保存；“恢复默认/跟随项目默认”删除对应项或字段使其回到 Unset。首版不支持的 option type fail closed。

嵌套 option 不序列化成树。父 case 切换后，未激活的合法子 option 显式值作为 Dormant Intent 保留，但不参与 resolved options、pipeline override 或 Run Plan；父分支重新激活时可恢复。MaaNOP Config 不保存 label、entry、default/default_case、展开状态或路径、resolved option graph、pipelineOverride、resolvedGlobalOptions、最终 MaaFramework 参数或 interface digest。

配置校验按“忽略是否可能改变本次 Run 意图”区分 Blocking 与 Warning。SelectedTasks 中任务消失、现存 option 的显式 case 不存在、当前参与解析的 input 字段失效、激活的嵌套显式值无效或显式值违反 regex/PI validation 时，ConfigStatus 为 Invalid/NeedsReview，显示具体 JSON/PI path并禁止 Start，不自动改写文件。完全不可能参与当前选中 task/global option 的旧 ExplicitOptions key 可非阻塞 WARN；Dormant Intent 也不是错误。用户明确修正并保存后才清理确认无效项。

malformed JSON、缺失/非法/未知 SchemaVersion 或 schema 无法解析时，原文件保持不变，GUI 明确报错、禁止 Start并提供主动“重置当前 MaaNOP 配置”；不能 fallback `{}` 后 autosave 覆盖。Application Settings 和 MaaNOP Config 都以同目录临时文件写入、flush/close，再 atomic replace/move，失败时旧文件保持完整且 GUI 不宣称保存成功。首版不实现配置 migration framework、profiles、import/export。

MaaNopProjectDirectory 的编辑锁严于 launch-only 游戏设置。只有 WorkerNotStarted，或 Connected + fresh Snapshot + activeRun=null，或其他能确定无 activeRun 且不处于 Worker launch/replacement transaction 的安全状态才允许编辑；activeRun、WorkerStarting/Pending Admission、replacement 进行中、无法确认空闲的 stale Snapshot、WorkerRecoveryConflict 时均禁用。修改后立即重读/验证 PI并计算 Desired Runtime Profile Digest；与 admitted digest 不同则禁止 Start，在 fresh Snapshot 确认 Idle 后按 Worker replacement 流程应用。
