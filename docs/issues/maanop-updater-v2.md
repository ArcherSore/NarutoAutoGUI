---
title: MaaNOP Updater V2 设计讨论
status: design-finalized
confirmed-on: 2026-09-14
issue-source: local-docs/issues
---

# MaaNOP Updater V2

本设计已于 2026-09-14 经用户确认定稿。本阶段仅讨论和记录设计，不修改实现。
Q24 的最终答案替代 Q19 的清理顺序：先尽力清理，再 relaunch；新版 GUI 不等待 Engine PID。
当前实现仍为 V1，现有验证证据见 [UPDATER-VALIDATION](../UPDATER-VALIDATION.md)。

可执行规格已整理为 [Updater V2 spec](maanop-updater-v2-spec.md)，在本地问题跟踪器标记 ready-for-agent。
本文保留已定稿的设计决策与追问记录。

## 已确定约束

- 仅支持完整 Release 包，更新单位不拆分为内部组件。
- 保留安装根目录的 `config/`、`logs/`、`debug/`、`cache/`；其余根目录内容默认由程序管理。
- 保留 SHA256、ZIP 安全校验，以及更新前关闭 Worker/Child Session 的要求。
- 不采用 rollback、整目录 swap、长期 backup、install lock、completed journal 或复杂 fallback。
- 设计方向为 Rust Update Engine，集中拥有 Release 检查、下载、SHA256、安全解压、包验证、缓存生命周期、
  安装和 relaunch；GUI 只拥有更新 UI、用户确认与 Worker/Child Session 生命周期。
  同一 Rust 二进制提供 check/prepare/install 三种操作，以短命命令进程和结构化 stdin/stdout 与 GUI 通信。
- MXU 的原地覆盖与短命旧文件缓冲作为用户提供的参考方向；本轮尚未核对 MXU 源码。

## 当前实现与设计冲突

- [ADR 0022](../adr/0022-use-an-external-updater-for-whole-package-swap.md) 明确采用目录交换、有限回滚和旧备份。
  [ADR 0023](../adr/0023-centralize-updates-in-a-rust-engine.md) 已记录 V2 的替代架构决策；
  ADR 0022 保留为 V1 当前实现的历史依据，V2 尚未实现。
- `InstallTransaction` 复制 config/logs，清理 staging 的 state，再交换目录；它不是原地安装模型。
- `UpdateStorage` 把缓存放到 LocalAppData，并提供 install.lock 与 completed.json。
  文档所称 TEMP 副本实际位于该安装路径对应的缓存下随机子目录，并非系统 TEMP 或安装内 cache。
- `UpdatePackage` 将 ZIP 安全检查与 staging 解压、完整包布局校验绑定，布局还要求 .NET Updater 入口文件。
- 独立 Updater 等待 GUI 退出后执行事务，并写完成记录、启动新版；运行环境关闭仍由 GUI 负责。
- CONTEXT 的 MaaNOP Updater 指整个更新协作者；V2 另以 Update Engine 表示拥有更新规则和执行的角色。

## 第一轮决策（已确认）

1. 四个保留目录之外，新完整包决定最终程序文件集合；允许覆盖用户修改，删除旧版残留和用户自加文件。
2. 修改前检查失败不改变程序文件；开始修改后允许留下部分安装。失败停止，不自动启动混合版本、不回滚。
   用户需要到 GitHub 重新下载完整包修复，不承诺旧版仍可运行。
3. cache 内允许划出 Updater 专属子树，由 Updater 管理和清理；其余 cache 内容不动。
4. 拒绝“Rust 仅安装、C# 负责联网与准备”的职责划分，采用上述集中 Update Engine 的设计方向。

## 两种 Module 划分的比较

| 维度 | Rust 仅安装（已拒绝） | Rust Update Engine（当前方向） |
| --- | --- | --- |
| 更新规则 | Release、下载、包准备在 C#，安装在 Rust | 所有更新规则集中 Rust |
| GUI 必须知道 | Release/包模型、准备路径、缓存清理和错误分类 | 用户意图、展示结果、进度、运行环境关闭与接管结果 |
| 跨进程 seam | 主要是一次安装交接 | 检查、准备的结果/进度/取消，以及安装交接 |
| 维护代价 | 进程通信较少，但包和缓存知识跨语言分散 | 增加固定消息契约，但包布局和缓存变化不再要求 GUI 跟着修改 |
| 深度风险 | C# GUI 仍编排大量更新规则 | 若 GUI 指挥解压、校验、清理每一步，仍会退化为浅 Module |

目标是 GUI 提交意图而非文件步骤；install 消费 Engine 自己的准备结果，不能要求 GUI 拼接 ZIP、摘要、
解压路径和删除列表。准备内容只在 Engine 工作缓存中服务于本轮操作，GUI 不跨启动恢复 reference，
不把准备内容建模为 completion journal。

## 第二轮决策（Q5–Q9，已确认）

1. check 只检查并返回 opaque Update Descriptor，不下载；prepare 下载、SHA256 校验、安全解压并验证包；
   install 只消费 Prepared Payload，执行本地文件替换和 relaunch。
2. 每次操作采用短命命令进程；GUI/Engine 通过简单结构化 stdin/stdout 通信。
   GUI 只保存并原样回传 opaque descriptor/reference，不理解内部结构。不引入常驻服务、Named Pipe 或通用 RPC。
3. Prepared Payload 位于 Engine 专属的 cache/updater/，只包含新包内容，不复制用户数据、不进行目录 swap。
   它不是 V1 staging。所有解压和包验证在 GUI 退出前完成。
4. V2 首版不跨 GUI 重启恢复 prepared 状态，不做续传。
5. Rust install 阶段允许最小 UI：简单“正在更新”、失败错误与日志位置；不承接正常更新 UI、Release Notes 或设置。

## 第三轮决策（Q10–Q17，已确认）

- Q10：用户确认后，GUI 先关闭并确认 Worker/Child Session 结束，再启动 install。
  实际安装 Engine 完成接管和所有当时能够完成的非破坏性前置检查后回复 ready；GUI 收到 ready 才真正退出。
  Engine 确认 GUI PID 已结束后才开始删除、移动或替换程序文件。启动/检查/等待失败不修改程序文件，
  已经停止的运行环境不自动恢复。ready 不等于文件修改不会失败，也不表示新版健康。
- Q11：Engine 内部将自身复制到 cache/updater/ 下专属运行目录，由副本接管安装。
  GUI 不理解、不管理该路径，不复制 Engine 文件。ready 必须来自实际安装副本的接管结果。
- Q12：包中的 config/logs/debug/cache 内容在更新中一律忽略，不合并、不补默认内容；默认配置由程序自身处理。
  ZIP 安全检查仍覆盖全部条目。安装根目录的保留名称若为普通文件等类型冲突，在修改前失败，不自动转换。
- Q13：保留 cache/updater/old/ 作为短命旧文件隔离区，用于先挪走旧文件再写入新版。
  不承担 rollback、恢复来源或长期备份；移动粒度和清理时机见第四轮决策。
- Q14：Prepared Payload 视为 Engine 独占工作文件，不承诺抵抗外部篡改。
  install 只做最小前置检查，不重新解压、不重复整包校验；外部改动与后续 I/O 故障按更新失败处理。
- Q15：同一安装目录并发更新不受支持。GUI 串行发起操作，不增加跨进程 install lock 或竞争恢复机制。
- Q16：prepare 支持固定 stdin 取消消息，停止准备、尽力清理并退出；GUI 异常退出时中止准备。
  意外终止残留不成为 Prepared Payload，不恢复下载。install 开始修改文件后不可取消。
- Q17：relaunch 成功只表示新版进程成功创建，不确认健康、不 rollback、不写 completion journal。
  启动调用失败应区分为文件安装完成但启动失败，允许用户手动启动，不误报文件安装失败。

## 第四轮决策（Q18–Q23，已确认）

- Q18：先将四个保留目录以外的安装根目录条目全部移入 cache/updater/old/，全部移动成功后再写入 Prepared Payload。
  安装根目录本身不移动，保留目录不动；无需新旧文件差异算法。任一步失败立即停止，不反向搬回。
- Q19：原先确认的 relaunch 后清理顺序已由 Q24 替代，最终顺序为安装完成后先尽力清理 old/Payload，再 relaunch。
  不等待新版健康，不要求新版 GUI 管理缓存，不增加 cleanup 操作。清理失败只记日志，不改报安装失败。
- Q20：ZIP 在 prepare 成功形成 Payload 后删除，失败/取消时尽力删除。
  old/Payload 失败时允许残留；Engine 运行副本允许留到下一次 prepare。
  下一次 prepare 先清理上次废弃工作内容，清理失败则报错，不积累新一轮缓存。日志写入 logs，不随工作缓存删除。
  如果用户不再更新，异常残留可以继续存在，不承诺每次 GUI 启动清理。
- Q21：ready 发出前发现 GUI 退出或通信断开，中止且不修改程序文件。
  ready 成功发出后才进入等待 GUI PID 结束的路径；此后不依赖 stdout 存活，由 Engine 自己呈现状态与错误。
  不增加第二次确认消息或持久交接记录。
- Q22：prepare 固定消费 check 返回的 Release 候选、资产和摘要，不重新选择 latest。
  资产失效、缺失或摘要不符即失败，用户重新检查；不静默切换版本，GUI 不负责版本比较或资产选择。
- Q23：首个 V2 完整包需要用户手动下载安装；V2 起才进入新的自动更新链路。
  不保留 V1 兼容空壳 DLL、双 Updater 或迁移 bootstrap。

## 已确认流程

1. GUI 请求 check；Engine 返回展示信息和 opaque Update Descriptor，不下载包。
2. 用户点击下载，GUI 回传 descriptor 请求 prepare；Engine 清理废弃工作内容，下载并校验固定候选。
3. Engine 完成安全解压与包验证，删除 ZIP，返回 Prepared Payload 的 opaque reference；GUI 显示可安装。
4. 用户确认安装；GUI 通过既有操作门关闭并确认 Worker/Child Session 结束，再回传 reference 请求 install。
5. Engine 内部自复制，由缓存中的实际安装副本完成接管和当时可完成的非破坏性检查，向 GUI 回复 ready。
6. GUI 收到 ready 后退出；Engine 确认 GUI PID 结束后，先隔离全部受管理旧条目，再写新包。
7. Engine 在文件安装完成后先尽力清理 old/Payload，再从原安装路径启动新版 GUI，然后退出。
   清理失败只记日志，不阻止 relaunch；新版 GUI 不等待 Engine PID。
8. 运行副本和异常残留由下一次 prepare 清理，不恢复本次准备或安装状态。

## 第五轮决策（Q24–Q29，已确认）

- Q24：安装完成后 Engine 先尽力清理 old 和 Payload，再启动新版 GUI；清理失败只记日志。
  自身运行副本留到下次 prepare。不要让新版 GUI 等待 Engine PID，不增加该启动参数或更新等待状态。
- Q25：采用逐行 JSON stdin/stdout；stdout 只放结构化消息，日志另写。
  固定表达阶段/下载进度、结果、错误、ready 和 prepare 取消，不引入通用请求路由。
  展示信息和 opaque 字段分开返回；具体字段拼写和长度上限在实现契约中确定，不扩展产品能力。
- Q26：安装路径、受管理程序树和 Engine 工作目录拒绝会影响操作范围的链接/重解析点，不跟随外部目标。
  其他保留目录内部不遍历、不复制、不清理。包内拒绝路径逃逸、链接、Windows 路径别名与条目冲突。
  不增加解析真实位置后继续的兼容路径，也不承诺抵抗同用户外部篡改。
- Q27：V2 完整包必须内置 Python，继续要求 python/python.exe；保留 GUI、Worker、必要 runtime、PI、
  resource 和 agent 的完整包检查，将旧 .NET Updater 四件套要求替换为 Rust Engine。
  这是 V2 完整 MaaNOP 包发布要求，不表示当前 baseline 已含 Python。
- Q28：prepare 拒绝任何携带 state/ 的包。旧 state 按受管理内容进入 old，新 GUI 自行产生新的运行态。
  更新源沿用 PI 仓库与版本、最新正式 Release、SemVer、精确资产名与必需 SHA256，不新增更新服务。
- Q29：启动检查开关由 GUI 拥有，放在 config/ 下独立小文件，默认开启；不迁移旧 LocalAppData 偏好。
  删除首次更新完成 Banner，新版正常显示当前版本；不保存完成 Release Notes，不增加一次性完成提示启动参数。

## GUI → Engine interface

| 操作 | GUI 提供 | Engine 返回 |
| --- | --- | --- |
| check | 安装根目录 | 当前/目标版本、说明等展示信息与 opaque descriptor，或无更新/错误 |
| prepare | 安装根目录、原样 descriptor | 阶段/下载进度，最终 opaque prepared reference 或错误/取消结果 |
| install | 安装根目录、原样 reference、GUI PID | ready 或修改前错误；接管后 Engine 自己呈现安装状态/错误 |

Engine 自己读取 PI、比较版本、选择固定资产、管理缓存。GUI 只负责进程调用、消息展示、取消意图、
安装确认和既有运行环境生命周期，不实现 ZIP/SHA256、缓存遍历/删除、包验证或文件安装规则。
Engine 内部的运行副本启动与转交不成为第四个公开操作，也不要求 GUI 构造路径或管理副本。
Rust Engine 脱离安装根目录执行时不能依赖 GUI 的 .NET/runtime 文件；不沿用 V1 复制 libs 和 bootstrap 的流程。

ready 必须代表实际副本，而非仍占用根目录的启动入口；内部转交必须释放会阻塞文件移动的启动入口。
ready 前完成当时可完成的非破坏性检查，程序文件最早在确认 GUI PID 结束后修改。
GUI 等待 ready、Engine 等待 GUI 退出均须有有限等待；精确超时值属于实现细节，不引入重试框架。
ready 不是进程存活期内持续保证，不能排除后续占用、权限变化、磁盘故障或 Engine 意外终止。

## 最终文件所有权与缓存生命周期

| 内容 | 所有权与安装行为 | 清理时机 |
| --- | --- | --- |
| config/logs/debug | 保留本地内容，包内同名内容忽略 | 不由更新清理 |
| cache 中非 updater 内容 | 保留本地内容，包内同名内容忽略 | 不由 Engine 清理 |
| 其余安装根目录条目，包括旧 state | 完整包管理，先全部移入 old，再写新包 | 安装完成后的尽力清理 |
| 下载 ZIP | Engine 工作文件 | prepare 成功后删除；失败/取消尽力删除 |
| Prepared Payload | cache/updater 下的本轮新包内容 | 安装完成后、relaunch 前尽力删除 |
| cache/updater/old | 旧程序内容的短命隔离区，无恢复语义 | 安装完成后、relaunch 前尽力删除 |
| Engine 运行副本 | cache/updater 下的独立执行副本 | 下次 prepare |
| 废弃工作内容 | 不恢复、不复用为本轮 prepared 状态 | 下次 prepare 前清理，失败则中止本次 prepare |
| Engine 诊断日志 | 写入 logs，与工作文件分离 | 不随工作缓存删除 |

保留目录缺失时不从包内补默认数据；同名普通文件等冲突在修改前失败。
state 不新增为保留目录，新包携带 state 即验证失败。
缓存清理只处理 Engine 自己的工作内容，不清空整个 cache。
手动修复应从 GitHub 重新获取完整包并保留四个目录；单纯把 ZIP 覆盖到混合目录不保证清除过期程序文件，
修复指导应要求重建受管理程序内容，不指示从 old 恢复。

## 失败语义

| 失败位置 | 程序文件结果 | 用户可见行为 |
| --- | --- | --- |
| check/prepare 或安装前检查 | 不修改程序文件，允许留下废弃工作缓存 | 报错或取消，可重新检查/准备 |
| Worker/Child Session 关闭失败 | 不修改程序文件 | 不进入安装，不承诺恢复已停止的运行环境 |
| ready 前断线/GUI 退出，或 GUI 退出等待超时 | 不修改程序文件 | 中止，仍存活的一方按既定 UI 报告错误 |
| 移动旧条目或写新版失败 | 可能部分安装、不可运行 | 停止、不 relaunch、不 rollback；提示到 GitHub 下载完整包及日志位置 |
| 安装完成后的缓存清理失败 | 新程序文件安装已完成 | 只记日志，继续 relaunch |
| relaunch 调用失败 | 新程序文件安装已完成，old/Payload 可能已清理 | 提示安装完成但启动失败，可手动启动 |
| 新版进程创建后自行崩溃 | 不属于 Engine 健康判断 | 无健康握手、完成记录或回滚 |

程序文件损坏后的修复来自重新下载的完整包，而非缓存。并发更新、断点续传、prepared 跨 GUI 启动恢复、
任意断电恢复、同用户外部缓存篡改防护均不属于 V2 保证。

## 实现与验收要求（待执行，不是验证结果）

- 通过同一 check/prepare/install interface 测试外部结果，使用可控网络响应与真实隔离文件系统。
  不以逐 helper 镜像测试替代 Module 验收，不新增 Worker/RDP mock framework。
- check：PI 来源、SemVer、正式 Release、精确资产名、必需 digest、无更新与错误；check 不下载完整包。
- prepare：固定候选、下载进度/长度/SHA256、取消/断线、ZIP 安全路径、链接/别名/冲突、完整包关键内容、
  内置 Python、拒绝 state、产品版本匹配；失败不改变程序文件，不返回可安装 reference。
- 文件系统：四个目录原内容保持、包内默认内容不合并、未知旧条目及过期文件消失、类型变化、根路径保持、
  全部旧条目移动完成后才写新包；注入移动/写入故障，验证停止且不回滚、不启动混合版本。
- handoff：实际运行副本 ready、根入口释放、ready 前断线中止、GUI 不提前退出、PID 未结束不改文件、
  有限等待失败路径、GUI 退出后 stdout 断开不阻断安装。
- 缓存与重启：清理发生在 relaunch 前、清理失败仍 relaunch、运行副本留待下次 prepare、下次清理失败中止，
  不删除其他 cache、不恢复旧 prepared；启动失败与安装失败区分，不生成 completion journal。
- 发布：Engine 副本不依赖安装根目录 runtime，V2 完整包含 Python 和 Rust Engine；
  GUI baseline 与完整 MaaNOP 包分别验证，不声明支持 V1 → V2 自动迁移。
- Windows 实机：Active Run 停止、Preview cleanup、Worker/Child Session/游戏/Agent 退出、真实自身替换、
  原路径 relaunch、连续两次 V2 更新；复验已验证的 Child Session 创建/恢复、显示/隐藏、结束/退出 baseline。
- C# 删除范围以本规格已迁移的更新职责为限，保留 UI、偏好、消息适配与现有运行环境关闭流程。
  不改 MaaFramework/Worker IPC，不引入系统服务或通用更新框架。

## 设计收口状态

用户已于 2026-09-14 确认整份汇总准确表达共同理解，设计定稿，当前没有待选择的产品/架构分支。
JSON 字段名/大小预算、超时数值、工作子目录命名和 Rust 库选择属于后续实现细节，不能借此扩展本设计范围。
本阶段不自动进入实现、构建、跨仓库发布集成或 Release 创建。

## 收口前核实的现有事实

- V1 由 Engine 待迁移的规则包括 PI name/github/version、最新正式 Release、严格 SemVer 升级比较、
  精确 MaaNOP-win-x86_64-{tag}.zip 资产名及 GitHub SHA256 digest；不是自定义更新源协议。
- V1 UpdatePackage 硬要求 python/python.exe，但历史 STATUS/ROADMAP 仍描述系统 Python；
  V1 spec 已将内置 Python 标为待完整包发布集成核实的依赖，不能把现有打包完成作为事实。
- GUI baseline 发布校验排除 Python 和 MaaNOP payload；它不等于完整 MaaNOP 包校验。
- state/ 包含 Worker admission 和 launch manifest，是运行态而非用户数据；V1 会删除新 staging 的 state。
- 启动检查偏好当前在 LocalAppData 更新缓存的 check-at-startup.txt，尚未迁入安装目录 config。

## 验证记录

本轮只阅读代码及文档并检查文档差异；未运行构建、自动化测试或 Windows 交互式更新。
