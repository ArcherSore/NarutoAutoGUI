---
title: MaaNOP Windows x64 完整包 Updater V1
status: implemented-awaiting-integration-validation
origin: recovered-from-ignored-artifacts
implementation-baseline: 40c00f1
issue-source: local-docs/issues
---

# MaaNOP Windows x64 完整包 Updater V1

> 这是 Updater 目标规格的仓库快照。当前实现已经形成 NarutoAutoGUI baseline 的主要更新链路；MaaNOP 完整包集成、真实 Windows 更新/重启和最终 E2E 仍未完成。验证边界见 [`docs/UPDATER-VALIDATION.md`](../UPDATER-VALIDATION.md)，当前进度见 [`docs/STATUS.md`](../STATUS.md) 和 [`docs/ROADMAP.md`](../ROADMAP.md)。

## Problem Statement

MaaNOP 用户目前必须重新下载完整 Release ZIP 并手动解压或覆盖来升级，操作成本高，覆盖还可能残留新版已删除的程序文件。
用户需要在 NarutoAutoGUI 内主动完成下载、校验、安装和重启，同时保留自己的配置与日志，并在安装失败时尽可能继续使用旧版。

## Solution

为 MaaNOP Windows x64 完整发布包提供一键更新。用户唯一可见的产品版本为 MaaNOP version。
NarutoAutoGUI 启动并完成项目加载后异步检查一次最新正式 Release；发现新版通过 Home Banner 提示。
用户打开右侧 Update Drawer 阅读 Release Notes，主动下载，等待 SHA256 与包预验证通过，再确认“安装并重启”。
GUI 使用现有生命周期信息安全关闭运行环境并确认已跟踪进程退出后，由安装目录外运行的独立 NarutoAutoUpdater
等待 GUI 退出，再完成目录替换、必要的事务回滚和新版启动。

## User Stories

1. 作为 MaaNOP 用户，我希望只看到 MaaNOP 产品版本，以便不必理解内部组件版本。
2. 作为用户，我希望一次升级整个 Windows x64 产品包，以便 GUI、Worker、资源和运行时保持发布时的组合。
3. 作为用户，我希望启动后自动检查一次更新，以便及时知道正式新版可用。
4. 作为用户，我希望关闭启动检查且保留此选择，以便控制启动时的网络请求。
5. 作为用户，我希望随时在 Settings 手动检查，以便主动获取新版信息。
6. 作为用户，我希望检查更新不阻塞启动、Home、任务配置和运行环境操作，以便继续正常使用。
7. 作为用户，我希望自动检查没有新版时保持安静，以便不受无意义提示干扰。
8. 作为用户，我希望网络不可用时自动检查只记录日志，以便离线使用不受影响。
9. 作为用户，我希望手动检查给出“当前已是最新版本”或普通错误状态，以便了解结果。
10. 作为用户，我希望只收到最新正式 Release，以便不会误装 draft 或 prerelease。
11. 作为用户，我希望版本按 Semantic Version 比较，以便正确识别 2.10.0 高于 2.9.0。
12. 作为用户，我希望只匹配 Windows x64 的准确包名，以便不会误装 Linux、Android 或 ARM64 包。
13. 作为用户，我希望缺少兼容包时明确告知，以便不会下载其他资产。
14. 作为用户，我希望 Home 用轻量 Banner 提示新版，以便任务运行不被阻塞。
15. 作为用户，我希望通过“查看更新”打开右侧 Drawer，以便查看当前版本、目标版本和 Release Notes。
16. 作为用户，我希望长 Release Notes 可滚动阅读，以便不使主窗口布局失控。
17. 作为用户，我希望只有点击“下载更新”才传输完整 ZIP，以便自己决定下载时机。
18. 作为用户，我希望看到真实百分比、已下载/总大小及下载速度，以便了解下载状态。
19. 作为用户，我希望取消下载后清理半成品，以便它不会被误认为有效更新。
20. 作为用户，我希望下载失败可以重试，以便恢复网络后继续升级。
21. 作为用户，我希望下载后必须校验 SHA256，以便损坏文件不能安装。
22. 作为用户，我希望校验失败后重新获取包，以便不复用不可信的下载结果。
23. 作为用户，我希望更新包在独立 staging 中解压并验证，以便坏包不会修改旧安装。
24. 作为用户，我希望所有配置及未来配置子目录完整保留，以便不用重新配置任务或 Saved Plans。
25. 作为用户，我希望已有日志保留，以便升级后仍能诊断此前的问题。
26. 作为用户，我希望安装前有一次简洁确认，以便明确知道应用将重启。
27. 作为正在运行任务的用户，我希望确认说明当前任务将停止，以便知道安装的影响。
28. 作为用户，我希望安装前停止任务、预览、Worker、Child Session 和其中的游戏，以便文件可安全替换。
29. 作为用户，我希望不能确认进程退出时取消安装，以便保留可用旧安装。
30. 作为用户，我希望 Updater 自身也能被更新，以便未来 baseline 变化仍可正常升级。
31. 作为用户，我希望安装过程中有简单阶段提示，以便知道更新仍在进行。
32. 作为用户，我希望原安装路径保持不变，以便原快捷方式继续有效。
33. 作为用户，我希望新版删除的旧程序文件不再残留，以便运行目录与新版发布包一致。
34. 作为用户，我希望目录替换失败时尝试恢复旧版，以便不被留在不可运行状态。
35. 作为用户，我希望安装成功后自动启动新版，以便无需手动寻找程序。
36. 作为用户，我希望首次启动看到轻量更新完成提示并可查看对应版本说明，以便确认升级结果。
37. 作为用户，我希望旧备份不会无限积累，以便磁盘空间不会持续被占用。
38. 作为用户，我希望更新状态与底部运行状态分离，以便仍能清楚判断 Worker、Session 和 IPC 状态。
39. 作为维护者，我希望更新源取自 Project Interface，以便不在 GUI 硬编码仓库地址。
40. 作为维护者，我希望沿用 GitHub Releases 元数据，以便无需维护自定义更新协议或服务器。
41. 作为维护者，我希望关键阶段有诊断日志，以便定位检查、下载、关闭、替换、回滚与启动失败。
42. 作为首次使用 Updater 的用户，我希望只需手动升级到第一个包含它的版本，以便此后使用内置更新。

## Implementation Decisions

### 更新源、版本和包选择

- 唯一更新单元是 MaaNOP Windows x64 完整 Release ZIP；不独立升级任何内部组件。
- 根 Project Interface 的 name 必须为 MaaNOP；github 提供 GitHub 仓库，version 提供已安装产品版本。
  仓库 owner/name 必须解析自该元数据，不硬编码 ArcherSore/MaaNOP。
- 扩展现有 ProjectModel 元数据读取以接受 github，保持任务、配置、Run Plan 与 Runtime Profile 既有语义。
  缺失或无效的更新元数据只使更新不可用，不应新增正常任务使用的阻塞条件。
- 使用 GitHub Releases API 的最新正式 Release，拒绝 draft/prerelease；不扫描寻找“下一个恰好可安装”的旧版。
- 本地 version 与远程 tag_name 规范化可选 v 前缀后按 SemVer 比较；相等或远程更低均不升级。
  非法版本不能降级为字符串比较；构建元数据不改变 SemVer 优先级。
- Asset 名必须精确等于 `MaaNOP-win-x86_64-<原始 tag_name>.zip`；版本比较的规范化不得改变资产匹配用的 tag。
  缺失或歧义匹配视为无兼容包，不选首个 ZIP、最大 ZIP 或模糊平台名。
- 获取资产的 browser_download_url、size、digest、name，以 Release body 为 Release Notes。
  digest 必须是有效的 sha256 摘要；缺失、格式错误或其他算法时不可安装，不用 sidecar 或跳过校验兜底。

### GUI 与下载

- GUI 更新协调模块负责检查、下载、验证及安装准备，并使用现有生命周期信息完成运行态关闭；独立 Updater
  等待 GUI 退出后负责目录事务和重启。不引入更新服务，不扩充现有 Worker IPC 为更新通道。
- 启动检查默认开启，在项目加载后异步执行，每次进程启动最多一次；手动检查独立可用，不周期轮询。
- 检查/下载需避免重复操作和迟到结果覆盖当前目标；关闭 Drawer 不等于取消下载。
- 自动检查无新版不显示 UI；失败仅日志。手动无新版显示轻量提示，失败显示普通错误状态。
- Home Banner 提供“查看更新”；Drawer 沿用 Task Description Drawer 的右侧覆盖式布局、视觉及关闭交互。
  展示当前/最新版本、可滚动 Release Notes、下载状态。远端内容不得执行脚本或任意嵌入内容。
- 下载必须由“下载更新”触发，以流式方式写入安装目录外缓存；不把 ZIP 整体加载到内存。
  进度来自实际收到的字节，显示百分比、已下载/总大小和当前速度；不要求 ETA 或多线程下载。
- 取消清理未完成文件；中断、大小不符及 I/O 失败不可成为有效包。下载失败允许重新下载，不要求断点续传。
- 下载完成计算本地 SHA256，与 GitHub digest 比较；失败删除或废弃该文件并显示“下载文件校验失败”。
  SHA256 只用于完整性校验，不代表发布者身份认证。
- ReadyToInstall 必须同时满足 SHA256 校验与 staging 包预验证成功；验证失败必须重新获取更新包。
- Settings 仅新增“更新”区：当前 MaaNOP 版本、默认开启且持久化的启动检查开关、“检查更新”按钮。
  不恢复已经删除的游戏启动路径配置；不把更新偏好混入 MaaNOP task/option Config 语义。
- 更新状态通过 Home Banner / Update Drawer 呈现，不长期进入底部整体、Worker、Session、IPC、当前操作状态栏。

### staging、用户数据与完整目录替换

- 解压到当前安装目录之外、可执行同卷目录重命名的 staging。下载缓存可以在其他卷，但最终 staging 与安装目录同卷。
- 解压逐项验证路径，拒绝绝对路径、父目录逃逸、盘符注入、UNC 和任何规范化后越界的 entry；
  Windows 路径别名、链接/reparse point、重复或冲突 entry 不得绕过边界检查。
- 包结构至少必须包含 GUI EXE/DLL、Updater EXE、Worker EXE/DLL、Worker 的 MaaFramework 与
  MaaWin32ControlUnit 原生库、Project Interface、resource 与 agent 目录，以及内置 Python executable。
  这里的名称与布局是完整发布包契约，不是允许实现自行挑选的弱检查。
- 新 Project Interface 的 name 必须为 MaaNOP，version 按同一版本规范化规则与目标 Release 对应。
  包布局错误、关键文件缺失或版本不符时，不得修改旧安装。
- config 与 logs 的完整子树属于用户数据，复制到 staging，不能移动；config 完全由用户拥有。
  若新包携带同名目录，不得让发布包默认数据覆盖旧用户内容，也不得向原有 config 擅自合并新默认文件。
- 在旧 GUI/Worker 等写入者退出并释放文件后复制用户数据；任意复制失败都在目录替换前终止。
  对日志的保留以旧进程结束时的内容为基线，新版运行后正常追加不算数据丢失。
- 发布包拥有的其他内容完整采用新版；未知自加程序文件无需保留，旧 runtime admission 状态不作为用户数据迁移。
- 安装事务：当前安装目录重命名为旧备份，再将 staging 重命名为原安装目录；原路径和快捷方式保持有效。
  禁止使用直接覆盖解压代替目录替换。
- 第一段重命名失败则旧目录保持原状；第二段失败则尝试将旧备份恢复为原路径。
  回滚失败必须保留可恢复备份并明确记录失败，不清除唯一旧版副本，不报告安装成功。
- 最多保留一个 MaaNOP.old；本次成功更新后可以继续保留，下一次更新事务开始前清理已有 MaaNOP.old。
  无法安全清理时，本次更新在修改当前安装前失败；不引入 health marker、启动健康握手、A/B 或运行健康检查协议。

### 安装授权、运行态关闭与接管

- ReadyToInstall 提供“安装更新”；点击后显示一次最终确认，按钮只有“取消”和“安装并重启”。
  空闲正文为“NarutoAutoGUI 将重启以完成更新。”；任务运行中正文增加“当前任务将停止”。
  点击取消不停止运行环境、不启动 Updater、不修改安装目录。
- 确认后使用现有应用级操作门禁止新 Run/运行环境操作，等待在途创建和启动结束，再检查最终运行态。
- 复用现有 Run Stop、Preview cleanup、Worker 生命周期及 Child Session 注销路径，必要的改动限于安装编排。
  不把 run.stop ACK 当成执行停止证明，不把 IPC 断开当成进程退出证明。
- 优先正常停止 Run、Preview、Worker 和 Child Session；必要时允许复用 NarutoAutoGUI 现有的受控强制清理路径，
  仅针对本应用拥有的 Worker、Child Session 和游戏进程，不得终止无关用户进程。
  最终仍无法确认相关进程退出或文件释放时才中止安装并报告；V1 不保留 Child Session 或游戏登录态。
- 停止任务及游戏预览，关闭 Worker，结束 Child Session 及其中 Naruto Online/Agent；必须确认相关进程退出。
  停止失败后的旧程序文件保持不变，但不承诺已经关闭的任务或 Session 自动恢复。
- NarutoAutoUpdater 属于 NarutoAutoGUI baseline 并随 MaaNOP 完整包分发；发布形态必须支持将其复制到临时目录后独立运行，
  不能依赖即将被替换的安装目录中的 DLL/runtime。具体采用自包含单文件还是携带必要依赖由实现确定。
- GUI 从安装目录外启动 Updater，确认启动成功后真正退出，而不是隐藏到托盘；无需第二个退出确认 Modal。
- GUI 使用 NarutoAutoGUI 已跟踪的 GUI、Worker 和 Child Session 生命周期信息，确认相关进程退出且目标程序文件
  可以安全替换后才把交接信息交给 TEMP Updater；TEMP Updater 等待已跟踪 GUI 退出，不得仅凭 IPC disconnect
  判断退出，也不设计新的通用 Windows process identity 或 PID reuse 防护系统。
  不设计新的通用 Windows process identity 或 PID reuse 防护系统，不终止无关进程。
- TEMP Updater 使用简洁小窗口、indeterminate progress 和准备/安装/启动短状态，不提供复杂设置或无必要按钮。
- GUI 在停止运行环境前将临时 Updater 复制到安装目录外，并以一次性的 `--probe` 启动检查确认它可以运行；
  probe 失败时不关闭运行环境、不修改安装目录。
- 目录替换成功后启动原路径下新版 GUI；启动失败记录并显示普通错误，保留备份，不宣称成功或扩大为健康回滚系统。
- 用最小的一次性本地完成记录关联目标版本与 Release Notes；不新增远程更新协议。
  新版实际加载版本匹配后首次在 Home 显示“已更新到 MaaNOP vX.Y.Z”，可“查看更新”；不弹大型 Modal。
  记录保存方式不侵入用户完全拥有的 config，也不恢复旧 Worker Admission。

### 状态与诊断

- GUI 至少覆盖 Idle、Checking、UpdateAvailable、Downloading、DownloadFailed、Verifying、VerificationFailed、
  ReadyToInstall、PreparingRuntime、LaunchingUpdater；检查/准备错误及取消必须有可理解的恢复路径。
- Updater 的诊断阶段至少覆盖 WaitingForProcesses、Installing、RollingBack、Restarting、Failed；当前实现以窗口
  短文本和日志表达阶段，尚未将这些阶段建模为独立的持久状态机。
- 日志覆盖检查开始、本地/远程版本、Release 解析、目标 asset、下载开始/完成、SHA256、包预验证、安装开始、
  Runtime shutdown、Updater 启动、目录替换、Rollback 和新版启动结果；检查错误不影响正常应用使用。
- Updater 的安装中日志必须可在安装目录重命名时继续写入，不因自己持有日志文件阻止目录事务；不记录凭据。

## Testing Decisions

- 自动化测试重点覆盖 SemVer、Release parsing / asset selection、download / SHA256、ZIP/path/package validation、
  config/log preservation 和 directory swap / rollback。通过更新模块的高层入口观察外部结果，避免为每个 helper 新增接口。
  使用可控 HTTP 响应/流和隔离临时安装目录；目录交换关键测试使用真实临时文件系统，
  仅在无法稳定复现的故障位置注入失败，不用完全模拟文件系统替代数据保留证据。
- 复用当前 GUI/Worker self-test 与发布包解包校验习惯；ProjectModel 使用既有 ProjectPlanModule 入口验证
  新元数据不会破坏任务配置与 Run Plan。仅对 SemVer/路径验证等边界补充必要的表驱动案例。
- 自动化不得依赖真实 GitHub 最新 Release、生产安装目录、UAC 或真实游戏；模拟网络不是新的产品更新服务器。
- Windows 实机手工 E2E 覆盖 Active Run、Preview、Worker、Child Session、Naruto Online / Agent、TEMP Updater、
  restart 和 completion Banner，以及相关文件释放；保留当前创建/恢复、显示/隐藏、结束/退出 Session 的可重复 baseline。
- 不为自动化测试引入新的 Worker/RDP/Game mock framework；真实运行环境关闭与重启不能用模拟结果替代实机验收。
- 只断言外部行为和数据不变量，不锁定私有方法调用顺序或具体 enum 拼写。

### 必须覆盖的验收场景

1. 本地与最新正式版相同，不显示新版 Banner；手动检查显示已是最新。
2. GitHub 不可达/超时/限流/响应无效时，启动、Home、配置和运行操作仍可使用；自动只记日志。
3. 新版准确识别原始 tag 对应的 Windows x64 asset；draft/prerelease、缺包及歧义资产不可安装。
4. 混合 Linux/Android/ARM64 资产时不误选；v 前缀、2.9.0→2.10.0、相等/较低版本和非法 SemVer 均正确处理。
5. 下载进度来自实际流量，显示百分比、大小、速度；下载前没有未经点击的完整包传输。
6. 取消/断流/截断/磁盘写入失败不会留下 ReadyToInstall 半成品，允许重试。
7. SHA256 不符或 digest 缺失/格式错误时不可安装；重试必须重新获取包。
8. 越界 ZIP、冲突 entry、关键结构缺失、产品名/版本不符时旧安装树与内容不变。
9. config 全子树逐文件内容保持，包括嵌套 plans、未知配置文件及包内同名默认文件冲突。
10. logs 既有内容完整保留，用户数据复制失败不触发目录交换。
11. 新包删除的旧程序文件和未知程序文件不因覆盖策略残留；新版程序文件集合正确。
12. 新包更换 GUI/Worker/MaaFramework baseline 后可完成升级，旧运行态不会被错误恢复。
13. Updater 从 TEMP 独立运行并可替换安装目录内自身，完整包校验确认其可独立启动。
14. Active Run 时确认文案正确；取消不关闭运行态；确认后禁止新的任务并正确停止当前运行。
15. Child Session、游戏及 Agent 在安装前退出；优先正常关闭，必要时复用现有受控强制清理，最终无法确认则旧安装不变。
16. 使用现有生命周期信息确认已跟踪 GUI/Worker/相关进程退出及文件可安全替换，否则不交换目录；不终止无关进程。
17. 第一次目录重命名失败不破坏旧版；第二次失败恢复旧路径；回滚再失败时保留备份和明确失败证据。
18. 成功后从原路径启动新版；启动失败不误报成功，不删除唯一旧备份。
19. 新版首次启动显示匹配的轻量完成提示，能查看对应 Release Notes；后续启动不重复首次完成通知。
20. 从有 Updater 的完整包开始，整个正常升级流程无需手动下载、解压、覆盖或重建快捷方式。
21. 启动自动检查最多一次、开关持久化、重复手动操作不产生并发安装或迟到状态覆盖。
22. 连续两次升级最多保留一个 MaaNOP.old，下次事务前安全清理，清理失败不修改当前安装；
    路径含空格、缓存跨卷及无写入权限情形不破坏现有安装。
23. 安装失败前后的日志覆盖关键阶段；底部 runtime status 不被长期更新进度替代。

## Out of Scope

- 独立 GUI、Worker、Resource、Agent、Python 或 MaaFramework 更新，以及双产品更新通道。
- update.json、update-manifest.json、SHA256 sidecar、自定义更新服务器或协议。
- MirrorChyan、增量/hotfix/delta、多线程下载要求、ETA、自动下载、自动安装、强制更新和后台周期轮询。
- Stable/Beta/Alpha 自定义频道、GitHub Token、CDK、代理等更新设置。
- Windows Service、Updater 服务、MSIX/MSI、Velopack/Squirrel、跨平台 GUI 自更新。
- 保留游戏登录态、保留 Child Session、在线替换运行中 Worker/MaaFramework。
- A/B、多版本管理、长期健康检查与运行后自动健康回滚；不承诺断电时任意步骤的崩溃恢复。
- 为不含 Updater 的历史版本提供 bootstrap workaround。
- 无关 Worker/IPC、RDP ActiveX、WTS、Task Scheduler COM 或 MFAAvalonia 重构。

## Further Notes

- 首个包含 Updater 的 MaaNOP Release 必须由用户手动升级一次，之后才有内置一键更新。
- 当前 Project Interface Loader 严格白名单尚不接受 github，需纳入本功能，而不是绕开现有项目加载验证。
- 当前 STATUS/ROADMAP 的历史说明仍称系统 Python；代码已有相对 Agent executable 支持。
  本需求明确要求内置 Python 完整包，应作为 MaaNOP 发布集成前置条件核实，不能将文档差异当成已完成打包的证据。
- NarutoAutoGUI baseline 需增加 Updater 构建/发布/解包检查；MaaNOP 发布集成需包含该 baseline、内置 Python、
  github 元数据及 CI tag 写入的 version。此规格说明集成依赖，不在规格整理阶段修改 MaaNOP 或发布 Release。
- ADR 0012 的 Stop ACK/真实停止区分、ADR 0014 的进程身份与 Session 生命周期、ADR 0021 的 Preview cleanup
  不变量继续有效。Updater 优先正常关闭，必要时复用现有仅针对本应用拥有运行环境的受控强制清理路径。
- 完整包目录交换、临时 Updater、有限回滚和不引入健康握手的长期架构边界记录在 ADR 0022。
- 现有领域文档 Application Settings 条目包含历史配置字段，实现以当前代码和 STATUS 中删除旧设置的事实为准；
  只增加本次更新偏好，不重建旧设置框架。
- 本规格最初形成于实现前；截至 2026-09-11，Updater baseline 已实现，但完整 MaaNOP 包集成、真实 Windows 更新/重启和最终 E2E 仍待完成。
