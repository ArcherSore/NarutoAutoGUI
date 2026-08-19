# 每个 Worker 只读取一次有界 Launch Manifest

GUI 为每个 workerInstanceId 在 `state/launch/` 原子写入 instance-specific Launch Manifest，UTF-8 JSON hard limit 为 256 KiB。Manifest 包含 launchContextVersion、workerInstanceId、runtimeProfileDigest、projectRoot、项目/interface provenance、单个 Win32 controller、单个有序 resource path 集合和单个 Python Agent launch definition；不包含 Launch Token、GameExecutablePath/GameArguments、SelectedTasks、ExplicitOptions、Run Plan、MaaNOP Config 原文、密码或 Child Session 凭据。文件及临时替换过程使用当前 Windows 用户 ACL。

Runtime Profile Digest 不能 hash 整份 Manifest 或原始文件 bytes。v1 输入固定为 projectRoot；controller type/classRegex/windowRegex/screencapMethod/mouseMethod/keyboardMethod；resource name 和保持声明顺序的规范化绝对 paths；Agent childExec、保持顺序的 childArgs、workingDirectory。workerInstanceId、projectName/version、interfaceVersion、sourceInterfaceDigest、Manifest 路径和 launchContextVersion metadata 均不参与。GUI 与 Worker 共同引用 Canonical Digest v1：以固定 Runtime Profile domain prefix 加手写固定 schema 的 canonical UTF-8 JSON 计算 SHA-256；Worker 反序列化后独立规范化并重算。路径执行纯字符串规范化，resource path 和 childArgs 严格保序；文件存在性和 Python 最终解析路径不反馈摘要。不一致时 Worker 尽量完成 admission，并进入 `NotReady / LaunchContextInvalid / RuntimeProfileDigestMismatch`。

sourceInterfaceDigest 只记录 Worker 启动时 GUI 所解析 PI 的 provenance，并直接对磁盘上 `interface.json` 的原始 bytes 计算。当前 interface 改变 task、option、default 或 pipelineOverride 可以使新 Run 使用新的 interfaceDigest，同时在 runtime profile 相同的情况下复用 Worker；interface digest 不参与替换资格。

Launch transaction 顺序固定为：原子写 Manifest，原子写 Pending Admission，前两步均成功后才调用 Task Scheduler COM。Manifest 成功而 Admission 失败时不启动并删除 Manifest。GUI 恢复时清理 `state/launch/*.json` 中没有对应有效 Admission Record 的 orphan，因为合法 Scheduler 提交不可能先于 Admission。首版不扩展为事务数据库。

Worker 先检查 Manifest 文件大小，再以 strict schema 有界读取；超过 256 KiB、未知 execution-affecting 字段、workerInstanceId 与参数不一致、digest 不一致、绝对路径规范失败或 resource 顺序无效均进入结构化 LaunchContextInvalid，但尽量通过 admission Pipe报告。Worker只读一次并持有不可变内存副本，不监视、不重读、不热加载；runtime 变化只通过 Idle Worker replacement。

GUI 只有在 fresh Snapshot 确认 workerInstanceId 和 runtimeProfileDigest 匹配、表示 Context 已成功加载后才删除 Manifest。Worker因 Python/resource 等后续依赖 NotReady 但 Context 已加载时可删除；Manifest 本身无法解析或校验时保留至 identity 废弃、Worker退出、Pending abandoned、人工恢复或 Child Session 注销。IPC断开、Snapshot暂缺或 grace period 未过不是删除理由。

Task Scheduler 启动参数只包含 workerInstanceId、Launch Token 和绝对 Manifest path。Named Pipe identity 由固定协议前缀和 Worker 当前 Windows 用户 SID 按共享规则推导，Worker不接受任意外部 Pipe endpoint。Launch Token 是用于防 stale/错误实例并绑定 Pending Admission 的 admission nonce，不是抵御已完全控制同一 Windows 用户账户的强安全边界；它不进入 Manifest、Snapshot、日志或 UI。
