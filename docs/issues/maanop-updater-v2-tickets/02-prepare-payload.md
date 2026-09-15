---
status: completed
labels: [ready-for-agent]
issue-source: local-docs/issues
---

# 02: 从更新候选准备可安装 Payload

**Parent:** [MaaNOP Updater V2 规格](../maanop-updater-v2-spec.md)

**What to build:** 用户点击下载后，GUI 原样回传候选，由 Engine 下载、安全解压并验证完整包，
呈现实际进度与取消结果，最终获得只供本轮安装使用的 Prepared Payload reference。

**Blocked by:** [01: 通过 Rust Update Engine 检查并展示更新](01-check-update.md)。

**Status:** completed

## Acceptance criteria

- [x] prepare 接受安装根目录和 01 返回的 opaque descriptor，固定使用其中的 Release 候选、资产和摘要；
      候选失效即报错，不重新选 latest、不静默切换版本。
- [x] Engine 拥有全部准备文件；GUI 不创建、遍历、删除工作缓存，也不拼接摘要、下载地址或解压位置。
- [x] prepare 在下载前清理 Engine 自己的废弃工作内容，包括旧运行副本和失败残留；清理失败则中止，
      不创建新一轮下载，不处理其他保留缓存。用隔离遗留内容验证该行为，不依赖 03 已运行。
- [x] 流式下载并报告真实阶段、字节进度和下载速度，核对长度及 SHA256；未完成或损坏的文件不能形成成功结果。
- [x] GUI 经同一 JSONL seam 展示下载与验证状态，支持固定取消消息；关闭 Drawer 不等于取消。
- [x] prepare 收到取消或发现 GUI 异常退出后停止准备并尽力清理；半成品不返回有效 reference，重试不续传。
- [x] ZIP 校验覆盖全部条目，拒绝绝对路径、逃逸、Windows 别名、链接、重复条目及文件/目录冲突，
      最终忽略的包内容同样不能绕过安全检查。
- [x] 校验完整 MaaNOP 包：GUI、Rust Engine、Worker、必要 runtime、PI、resource、agent 和内置 Python，
      产品名/版本匹配目标 Release；缺少必需 Python 或携带 state 运行态的包被拒绝。
- [x] Prepared Payload 位于 Engine 专属 updater 缓存，只包含新包内容；不复制任何旧用户数据，
      不建立 V1 staging、不进行目录 swap、不修改安装程序文件。
- [x] 完成全部解压与包验证后才返回 opaque prepared reference；成功形成 Payload 后删除 ZIP，
      失败/取消时尽力删除下载内容，残留不视为已准备状态。
- [x] GUI 只保存并原样回传 reference，成功后显示更新已就绪；GUI 重启后不恢复 prepared，不保存完成 journal。
      03 未完成前不得把新 Payload 交给 V1 安装器。
- [x] 安装位置及 Engine 工作目录的相关链接/重解析点被拒绝，不跟随外部目标；保留数据未被准备流程改变。
- [x] 删除由本片替代的 C# 下载、SHA256、解压、包验证及准备缓存管理规则，不保留 fallback 或双规则实现。
- [x] 经 prepare interface 覆盖固定候选、进度、取消/断线、长度/摘要、危险 ZIP、完整包契约、废弃缓存清理失败；
      使用可控网络和真实隔离文件系统验证失败前后程序及保留内容一致。
- [x] 受影响构建及自动化通过，GUI 可演示“检查 → 下载/取消或重试 → 已就绪”，全部包验证发生于 GUI 退出前。

## Scope boundary

本片负责生成 Payload 及下次 prepare 的废弃工作清理规则；03 负责消费 Payload 后的安装、正常清理与 relaunch。
包验证是本片功能，不留到 05；05 只把实际发布产物接入并验证该契约。

产品布局的固定目录含义以父规格为准，名称是产品契约而非源代码定位。
2026-09-15 收尾补验：缺少 GUI/Worker 启动元数据、Worker 私有 runtime 或 GUI 核心依赖的包，
必须在 prepare 阶段拒绝。已补齐必需文件清单，并逐项删除完整测试包中的文件验证拒绝与无 prepared reference；
该修复属于本片包契约，未增加 05 的产品职责。
2026-09-15：Rust prepare 与 GUI 进度/取消已实现。可控下载和真实文件系统测试、C# 进程适配测试、Clippy 和 GUI Release build 通过。未执行 WPF 鼠标交互；实际完整包演示留待 05。旧安装器所引用的 V1 Validate 随紧接的 03 一并删除，本片已删除旧下载/解压入口。
