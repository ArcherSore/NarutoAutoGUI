# Updater V2 验证记录

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
