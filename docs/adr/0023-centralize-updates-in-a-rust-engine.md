---
status: accepted
confirmed-on: 2026-09-14
---

# 将 MaaNOP 更新规则集中到 Rust Update Engine

V2 采用一个 Rust Update Engine，集中负责 Release 检查、下载、SHA256、安全解压、包验证、缓存生命周期、
原地安装和 relaunch；GUI 负责更新 UI、用户确认及 Worker/Child Session 生命周期。
本设计已于 2026-09-14 经用户确认定稿，尚未实现；V1 当前实现仍由 ADR 0022 描述。

## 取舍

只将安装改写为 Rust 虽然减少跨进程通信，却让 Release、包结构、准备与缓存规则继续分散在 C# 和 Rust。
V2 接受一个小型结构化进程 interface，将更新规则集中到 Engine；GUI 提交用户意图、显示结果，
只保存并回传 Engine 给出的 opaque descriptor/reference，不编排文件步骤。

同一二进制以短命 check/prepare/install 进程工作，通过 stdin/stdout 通信，不引入常驻服务、Named Pipe 或通用 RPC。
prepare 在 GUI 退出前形成 cache/updater/ 下的 Prepared Payload，只包含新包内容，不复制用户数据。
install 只做本地安装与 relaunch，允许提供最小进度和失败 UI。V2 不跨 GUI 重启恢复 prepared 状态、不续传。

四个保留目录 config/logs/debug/cache 以外的内容由完整包决定，允许删除过期和用户自加程序文件；
Engine 可以清理自己拥有的 cache/updater/，不清理其他缓存内容。
原地安装不提供 rollback、整目录 swap、长期 backup、install lock、completed journal 或复杂 fallback。
修改程序文件后失败可能留下不可运行的混合版本；停止安装且不自动启动，由用户去 GitHub 重新下载完整包修复。

本决策替代 ADR 0022 的 V2 职责划分、目录事务和旧备份策略；其余未重议的更新源和完整包边界继续作为讨论基线。
Engine 自行复制到 cache/updater/ 下的运行目录并由实际安装副本接管；GUI 不理解该内部路径。
实际安装副本完成当时能完成的非破坏性检查并回复 ready 后，GUI 才退出；Engine 确认 GUI PID 结束后才修改程序文件。
保留 cache/updater/old/ 作为先移走旧文件再写入新版的短命隔离区，不赋予恢复或长期备份语义。
安装根目录保持原位，先将四个保留目录之外的根条目全部移入 old，再写入新包。
文件安装完成后，安装 Engine 先尽力清理 old 和 Payload，再创建新版 GUI 进程；新版 GUI 不等待 Engine PID。
运行副本与异常残留由下一次 prepare 清理。
清理失败不把已完成安装改报失败，不承诺用户不再更新时残留会自动消失。
首个 V2 包由用户手动安装，不为 V1 包验证契约引入兼容入口或迁移 bootstrap。
V2 完整包必须内置 Python，继续要求 python/python.exe，并拒绝携带 state 运行态的包。
GUI 保留默认开启的启动检查偏好，写入 config 下独立文件；删除首次更新完成 Banner，不迁移旧 LocalAppData 偏好。
详细 interface、文件所有权、失败语义与待执行验收见 [V2 设计讨论](../issues/maanop-updater-v2.md)。
