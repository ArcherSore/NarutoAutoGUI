# Canonical Digest v1 独立版本化并区分摘要域

共享协议程序集固定 `CanonicalDigestVersion = 1`，不借用 launchContextVersion 或 planVersion 隐式表示摘要算法。Runtime Profile Digest 和 Plan Digest 分别计算：

```text
SHA256("NarutoAutoGUI.RuntimeProfileDigest.v1\n" + canonicalUtf8Json)
SHA256("NarutoAutoGUI.RunPlanDigest.v1\n" + canonicalUtf8Json)
```

外部格式统一为 `sha256:<64 lowercase hex>`。两个 domain prefix 防止 shape 偶然相同的不同对象共享摘要命名空间；未来若 canonicalization 规则变化，必须新增 v2，不能静默修改 v1 writer。

v1 使用共享、手写固定 schema 的 `Utf8JsonWriter` 实现，而不依赖普通 DTO serializer 的 property 排列、ignore-null 或 ignore-default 设置。object key 在每一层按 `StringComparer.Ordinal` 排序，array 严格保持原顺序，且不写无意义空白。null、空字符串和 missing 是三种不同输入；`RuntimeProfileDigestInputV1` 与 `RunPlanDigestInputV1` 必须显式区分 required、nullable 和 optional 字段，并始终输出协议规定的同一 schema。

结构化 option 和 pipelineOverride 中的 JSON number 保留严格 JSON parser 接受的原始 number token lexical representation；v1 不主动把 `1`、`1.0`、`1e0` 归一化，因此三者产生不同 digest。GUI 和 Worker 必须通过同一个 canonical writer 处理这些 JSON 值，不能在中间用会改写 number lexeme 的 serializer 重建。首版不声称实现 RFC 8785/JCS；若未来采用其 number serialization，必须完整实现为新版本并增加相应测试。

所有参与 digest 的 timestamp 先转换为 UTC，再由 shared writer 使用唯一、culture-independent 格式 `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` 输出。等价时间不得因调用方 serializer 差异出现多种表示。

Runtime Profile Digest 中的 projectRoot、resource paths 和 Agent workingDirectory 在进入 DigestInput 前执行 `PathCanonicalizerV1` 纯字符串转换：输入必须 fully-qualified，relative path 是 validation failure；调用 `Path.GetFullPath`，分隔符统一为 `\`，Windows drive letter 大写，filesystem root 以外移除 trailing separator。它不展开 `%VAR%`、不解析 symlink/junction、不根据文件系统实际大小写重写，也不要求路径存在。UNC path 保持 UNC 语义并应用相同 separator 与 trailing-root 规则。resource paths 不排序、不去重，严格保留 PI 声明顺序。路径存在性属于 Launch Context validation 或 dependency check，不属于 digest canonicalization。

Agent `childExec` 保留 PI 声明的命令名或路径字符串，不由 GUI 使用主 Session PATH 解析。Worker 在 Child Session 最终解析出的 Python absolute executable path 只进入 dependencyStatus，不能反向改变 runtimeProfileDigest。

Plan Digest 表示 immutable Run Plan content，而不是仅表示 execution semantics。它不包含 runId、requestId 或 planDigest 自身，但包含 planVersion、固定格式 createdAtUtc、project/interface metadata、runtimeProfileDigest、resolvedGlobalOptions，以及保持顺序的 Plan Item 全部内容：planItemId、taskName、taskLabel、entry、resolvedOptions 和 pipelineOverride。因此 GUI 对一次用户 Start 只构造一次 runId、时间、Plan Item ID、resolved values、Run Plan 和 digest；若 `run.start` 因 timeout 或 response 丢失重试，必须原样重发同一对象，不重新读取 PI、重新生成时间/ID 或重新 resolve。GUI 重启后不得用旧 runId 重建一个“看起来相同”的计划，而应按 Worker reconnect、fresh Snapshot 流程恢复。

`interfaceDigest` / `sourceInterfaceDigest` 继续只是 provenance，格式同为 `sha256:<64 lowercase hex>`，但直接 hash 磁盘上 `interface.json` 的原始 bytes，不 parse 后 canonicalize，也不 decode 后重新 encode。文件仍必须是可接受的 UTF-8 JSON；BOM、换行、缩进和 property 顺序改变摘要是预期行为。

实现归属一个 GUI 与 Worker 共同引用的协议程序集，概念上至少包含 `CanonicalDigest`、`CanonicalJsonWriterV1`、`PathCanonicalizerV1`、`RuntimeProfileDigestInputV1`、`RunPlanDigestInputV1`、`ComputeRuntimeProfileDigestV1` 和 `ComputePlanDigestV1`。两端只能调用这套共享实现，不得复制两份“等价算法”。

golden tests 至少覆盖：dictionary insertion order 和 nested object property order 不影响摘要；array、resource 和 childArgs 顺序改变摘要；slash、trailing separator 与 drive-letter case 的规定规范化；regex 大小写或空格改变摘要；null、empty、missing 的区别；`1`、`1.0`、`1e0` 的 v1 差异；中文/emoji key 的 ordinal 排序；深层 pipelineOverride object 排序；GUI/Worker 入口输出完全相同的 canonical bytes 与 digest；同一 Run Plan 重试不变；重新生成 createdAtUtc 或 planItemId 后摘要改变。
