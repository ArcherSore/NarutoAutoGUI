# Updater V2 验证记录

## 2026-09-21：真实更新链路补验通过

用户确认 idle 更新、Active Run 点击更新，以及 Stop → Preview cleanup → Child Session / Worker /
Game / Agent 退出 → 替换 → relaunch 全部完成、无明显问题，连续两次更新也通过。
本次依据用户实机反馈，未重新采集进程退出时间、安装日志或版本/哈希；见
[统一验收记录](ACCEPTANCE-2026-09-21.md)。2026-09-15 的日志与版本证据继续按原轮次保留。
关闭失败时阻止安装与已有 Child Session 恢复不在本次清单内，工单 05 的这两个专项继续保留。
后文“尚未执行”属于当轮历史状态，正常更新链路不再待验收。

## 2026-09-15：第二轮人工验收通过

用户按第二轮操作步骤完成测试，反馈“目测没看到问题”；取消安装确认、画面与配置保留按用户反馈记录，
不冒充本会话自动观察或文件哈希验证。正常 GUI 连续 v2.2.3 → v2.3.1 → v2.3.2 更新已通过。

第二轮 GUI 日志记录：

- 14:57:27 安装前停止任务与 Preview；Run `197d5be3-f3b4-4b56-b378-efd54f51f6f7` 收到 Stop。
- 14:57:28 MaaFramework Stop 确认，Run 终结为 Cancelled，随后注销 Child Session 3。
- 14:57:40 Session 注销完成，GUI 确认 Session 与已跟踪 Worker 结束后开始交接。
- 14:57:41 实际 Engine ready；14:57:42 GUI 重启，随后加载 v2.3.2 且检测无 Child Session。
- 14:58 新建 Child Session 4，Worker admission/Ready，内置 Python Agent 连接并提交真实游戏任务。
  该任务正常停止为 Cancelled，随后 Session 注销、GUI 正常退出。

updater.log 的第二轮 prepare/install 均成功，安装后 check 当前 v2.3.2；PI 仓库仍为测试源。
检查 cache/updater 仅有 run，old/Payload 已清理。本轮没有独立采样旧游戏/Agent PID 退出时刻；
退出证据来自 Session 注销、Worker 确认及用户观察。

第二轮安装前的另一次游戏任务于 14:57:08 终结为 Failed，随后新任务成功提交并被安装流程停止。
该失败原因未诊断，不据此声明游戏任务自然成功；第一轮 IPC 警告也继续作为独立观察保留。
尚缺关闭失败时阻止安装、已有分身恢复的本轮实机证据，因此工单 05 不全部勾选。
用户随后决定暂缓“已有分身恢复”验收。更新后免密进入的是新建 Session 4，不能作为恢复旧 Session 3 的证据；
该项未计为通过，也未观察到对应功能故障。
后续章节为各阶段历史记录，未执行边界以本节最新结论为准。

## 2026-09-15：公开测试更新源

### 第一轮人工验收

用户报告第一轮通过。实际目录 PI 已为 v2.3.1；updater.log 记录取消 prepare、重新 prepare 成功、
install 成功及重启后 check 当前 v2.3.1。cache/updater 仅剩 run，old/Payload 已清理。
GUI 日志确认升级后创建 Child Session 2、Worker admission/readiness、内置 Python Agent 和真实任务提交，
多次停止均终结为 Cancelled，关闭子桌面转为隐藏，最终注销 Session 并正常退出。
没有据此宣称自然 Succeeded、活动任务中的安装关闭或故障阻止安装已通过。
14:40:14 有一次 IPC JSON 解析警告，随后 admission 恢复且再次执行任务；原因未诊断，单独保留观察。

第二轮测试包 v2.3.2 的 SHA256 为
`9B7E255423E42A81FF4FBDD20BE6147FB6DDCF05DCEC3B5A7D0EB669C32BF92B`，与 GitHub digest 一致。
已发布为测试源 latest 正式候选，待人工验证活动任务期间安装。

用户授权创建 `https://github.com/ArcherSore/MaaNOP-UpdateTest` 并发布测试包；v2.3.1 已发布为
非 draft、非 prerelease，资产名 `MaaNOP-win-x86_64-v2.3.1.zip`，238043649 bytes，SHA256：
`BA59A57CBE288F205F796A55AE383FC290E02DB7CB52025AEA212BE1B6ED8BD3`，与 GitHub asset digest 一致。

包沿用已验证 baseline runtime、已有 v2.3.0 MaaNOP 资源与 Python，替换 7451150 对应的 Release Engine
和 Updates DLL；PI 仓库改为测试源，版本改为 v2.3.1。这不是本轮全量 baseline 重建，也不是正式 MaaNOP 发布。
组装仅复制发布内容，不包含用户 config/logs/debug/cache 或 admission state；baseline 布局与独立 Engine 校验通过。
当前源码的 prepare_local_package 校验通过；正式 Release Engine 在独立 remote-check 目录通过真实 GitHub
check/prepare，包括实际下载、SHA256、完整包校验和解压。证据位于忽略目录 `artifacts/updater-v2/github-test/`。

`manual-test-v2.2.3/interface.json` 已切换测试源，原 PI 备份在上述证据目录中。
真实 GUI 已打开，但电脑操作被用户停止；用户随后明确自行操作 GUI/游戏。
本轮未执行 GUI 安装、Active Run 关闭或 Child Session baseline，相关验收项继续待办。
先人工验证 v2.2.3 → v2.3.1，再发布 v2.3.2 验证第二轮，不提前改变 latest 候选。
下文“未发布”的表述均为此前本地验收阶段的历史边界。

## 2026-09-15：简化复验

在 e184677 后落实四项审查建议：合并实际副本的重复 preflight、删除取消令牌中间层、
去掉重复链接检查和 Payload 保留目录过滤、安装终态统一由 main 记录。
入口复制前检查、实际副本 ready 前检查、GUI 退出后检查与清理失败即时日志继续保留。

- Rust locked/offline 自动化 22 项通过，含真实缓存副本、PID 等待、文件操作失败与目录保留。
- 补充安装成功和前置失败的终态日志单次记录检查；成功用例仍覆盖 ready 后 stdout 断开。
- C# JSONL 适配测试通过，含取消、超时、opaque 值传递和 ready 条件。
- Rust Clippy 全 targets（warnings as errors）、GUI Release 构建通过，构建 0 警告、0 错误。
- 未重新生成完整发布包或运行真实 GUI/Child Session/游戏交互验收；下文包散列仍为历史产物。

### 人工测试目录同步

`artifacts/updater-v2/manual-test-v2.2.3/` 已替换新 Release `maanop-update-engine.exe` 与
`libs/NarutoAutoGUI.Updates.dll`。保留产品版本 v2.2.3；同步时其余 3351 个文件逐项 SHA256 不变。
目标目录 GUI 自检与独立 Engine check 通过，未启动正常 GUI 或执行更新交互。
本次仅同步这两个成功构建的文件：完整 baseline 在线 restore 受网络限制，离线 GUI publish 的 NetBeauty
artifact 查询失败，因此沿用测试目录已验证的 runtime 布局。文件散列、构建说明和验证记录位于
`artifacts/updater-v2/manual-test-v2.2.3-sync.json`，不将本次同步描述为完整发布包重建。

用户随后针对该目录反馈人工测试“没太大问题”，并同意提交本轮简化。
此反馈作为用户人工测试记录；未提供逐项场景、日志或升级结果，不等同于下方所有待验收项均已通过。

## 2026-09-15：已执行

代码来源为工单 01–04 提交 c89b3eb、1a69cd9、025c024、d3d7491，包契约修复 100ee2b，加本工单发布集成改动。
本地正式构建使用 .NET 10 locked restore、Rust 1.98.1、Cargo.lock、Release 与静态 Windows CRT。

- 正式 baseline：`artifacts/updater-v2/baseline`，GUI + libs + Worker + Rust Engine。
- baseline 布局校验通过；无旧 .NET Updater，独立目录仅复制 Rust EXE 后 check 正常返回缺 PI 错误。
- 最终 Engine 为 3,054,080 bytes；PE imports 只有 Windows 系统 DLL，没有 .NET 或 VC runtime 私有 DLL。
- 正式发布 DLL 的 GUI/Worker 自检、C# JSONL 适配、Rust 21 项测试与 Clippy 全部通过。
  GUI 自检必须在发布目录执行，以满足既有 app-local runtime 的相对 probing；脚本现自动切换工作目录。
- Rust 测试包括实际缓存副本、GUI PID 门槛、ready 后 stdout 断开、保留目录、移动/写入失败、清理故障、
  relaunch 失败，以及缓存可写但安装根目录 ACL 禁止创建文件时不发送 ready。
- prepare 测试使用完整有效基包注入逃逸、Windows 别名、重复/类型冲突、链接和重解析属性；包含真实进程 EOF 取消。
  ZIP 库隐藏精确重复名称的情况由中央目录条目计数检查覆盖。

## 本地完整包与连续安装

MaaNOP 内容来源：已有 `D:/MaaNOP-win-x86_64-v2.3.0` 的 interface.json、resource、agent、内置 Python 和许可证。
仅复制发布内容，没有带入该目录的 config/logs/state。未修改 D:/MaaNOP 源仓库。
以下两轮使用包契约补验修复前的正式 baseline，组装 v2.3.0、v2.3.1、v2.3.2；
这些是本地验收版本，代码/资源相同，仅 PI 版本不同，
不是已发布 GitHub Release，也不代表下游发布流水线已经切换。

| 包 | 字节数 | SHA256 |
| --- | ---: | --- |
| MaaNOP-win-x86_64-v2.3.1.zip | 238044197 | 73664F3BCE69494C40D34B254511F2BBAB772AF2A5A75954CAB6C73B406AFAFA |
| MaaNOP-win-x86_64-v2.3.2.zip | 238044197 | 3AC151073DD97EEDB667019A0D819DE79F3247EF6055A10B3D4A4F8DC78F595F |

两轮安装所用 Engine：3,049,984 bytes，
SHA256：25E7341F9767B99D454A86185D0E82A9809C7A90A75B50A349DD4DF304BBCD2E。

随后 100ee2b 补齐 GUI/Worker 启动依赖清单，新增逐项缺失文件拒绝测试；修复后重跑全套自动化、正式构建、
布局与独立运行校验均通过，最终 baseline Engine SHA256 为
784692710D9E61317348D242A8C9622A890A7C105A05368344199DB611EA33BD。
最终 prepare 规则另用上述 v2.3.2 完整 ZIP 验证通过，记录在 `prepare-contract-final.jsonl`。
该补验只收紧安装前验证，不改安装流程；没有重复宣称最终 Engine 又执行了两轮安装。

prepare 使用 `prepare_local_package` 验收工具，复用生产 check/prepare，仅替换 HTTP 边界为本地 ZIP；两包均通过。
安装使用正式 Engine、实际 Windows 安装副本和正式 GUI EXE；验收脚本收到 ready 后结束其创建的空闲 GUI，
由 Engine 等待 PID 退出并执行安装/relaunch。该测试没有驱动 GUI 的“安装”按钮，不替代 GUI 生命周期验收。

| 轮次 | 版本 | GUI PID | 结果 |
| --- | --- | --- | --- |
| 1 | v2.3.0 → v2.3.1 | 31956 → 22556 | 通过 |
| 2 | v2.3.1 → v2.3.2 | 22556 → 7172 | 通过 |

两轮都确认：ready 前旧程序测试文件仍在；四目录测试内容原样保留；过期程序条目消失；old/Payload/prepared 已清理；
新版 GUI 创建成功并正常 check 出当前版本；第二轮 prepare 成功清理第一轮运行副本。没有 completion journal。
日志和测试记录位于 `artifacts/updater-v2/`（忽略的本机产物，不提交）。

## 尚未执行，不计为通过

- GUI 正常“检查 → 下载 → 取消/重试 → 确认安装”整条交互；当前公开 Release 不包含本次 V2 产物，
  本地包由验收工具送入 prepare。管理员 GUI 已成功显示，但本会话窗口输入没有生效，未据此宣称按钮验收。
- Active Run、Preview cleanup、真实 Worker/Child Session/游戏/Agent 退出及关闭失败的实机安装场景。
- 本次 Child Session 创建/恢复、显示/隐藏、结束与正常退出交互回归。历史 baseline 不作为本次通过证据。
- 下游 MaaNOP 发布流水线切换、真实已发布 V2 包之间的自动更新。没有创建 tag/Release，也没有修改下游仓库。

因此工单 05 的发布集成和本地完整包/真实进程安装已完成，真实 GUI/游戏生命周期验收保持待办，不勾选完成。

## 复验入口

1. `build.ps1 -Locked` 生成 baseline，`validate-package.ps1` 校验布局和独立 Engine。
2. `compose-full-package.ps1` 指定 baseline、MaaNOP 发布内容、新输出目录和产品版本；输出目录必须不存在。
3. 将目录打为精确 MaaNOP 资产名 ZIP，使用 `prepare_local_package` 验证生产包契约。
4. 用发布后的实际 V2 候选在 GUI 走正常路径，按工单 05 补齐上述未执行项；保存日志与每轮版本/构建来源。
