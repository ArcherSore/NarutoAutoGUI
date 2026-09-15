---
status: completed
completed-on: 2026-09-15
issue-source: local-docs/issues
---

# 01: 通过 Rust Update Engine 检查并展示更新

**Parent:** [MaaNOP Updater V2 规格](../maanop-updater-v2-spec.md)

**What to build:** 用户从 GUI 启动检查或手动检查，经真实 Rust Engine 进程得到更新版本与说明。
本片同时建立最小 Rust 工程骨架、GUI 进程调用与 JSONL seam，使后续 prepare/install 沿用同一 interface。

**Blocked by:** None (can start immediately).

**Status:** completed

## Acceptance criteria

- [x] 建立可在 Windows x64 构建、独立启动的最小 Rust Update Engine；开发构建可供 GUI 调用，
      不依赖 GUI 的 .NET/runtime，不要求先完成正式发布布局工单。
- [x] GUI 经真实短命进程调用 check；stdin/stdout 使用逐行 JSON，stdout 仅包含结构化消息，诊断日志另写。
- [x] 落实供三种操作共用的最小消息约定和进程适配，明确字段、消息大小预算、结果/错误和进程异常的处理；
      不预先实现通用 RPC、Named Pipe、常驻服务或未使用的抽象。
- [x] Engine 自己读取 PI 产品名、仓库和版本，按 SemVer 检查最新正式 Release，选择唯一精确 Windows x64 资产，
      拒绝非法来源、非升级版本、draft/prerelease、缺失/歧义资产和无效 SHA256 digest。
- [x] check 不下载完整 ZIP；更新元数据错误不阻断普通任务配置或运行。
- [x] Engine 分别返回展示字段与 opaque Update Descriptor；GUI 展示当前/目标版本及 Release Notes，
      只保存 descriptor，不解析内部结构、不拼接下载地址、不执行版本或资产选择规则。
- [x] 启动检查默认开启、每次启动最多一次；偏好由 GUI 保存于保留配置中的独立小文件，不迁移 V1 LocalAppData 偏好。
- [x] 自动无更新/失败安静处理，手动操作明确反馈；保留 Home 提示、Drawer 和 Settings，更新状态不替代底部运行状态。
- [x] GUI 串行更新操作，进程启动失败、异常退出、无效消息不使界面无限等待或误报成功。
- [x] 移除由本片替代的 C# Release 检查/选择和旧偏好实现，以及一次性完成 Banner 的 GUI 读取/呈现；
      不以 V1 检查作为 fallback。尚未贯通的操作不得把 Rust descriptor 送入旧安装链路。
- [x] 通过 check interface 验证 PI、SemVer、精确资产、摘要、无下载和错误行为，使用可控网络响应；
      GUI 适配验证展示字段、opaque 保存和异常反馈，不绑定 Engine 私有 helper。
- [x] Rust 开发构建、受影响 GUI 构建及相关自动化通过；记录真实进程 check 演示结果，不依赖在线 latest 作为测试断言。

## Scope boundary

本片交付真实检查闭环，不只是空工程；prepare 由 02 完成，install 与最小安装 UI 由 03 完成。
正式发布布局由 05 负责。必要的小范围整理随本片完成，不新增独立预重构工单。

设计约束以 [ADR 0023](../../adr/0023-centralize-updates-in-a-rust-engine.md) 和父规格为准。
## 实现与验证记录

2026-09-15：已完成本片；Rust 工程、开发构建脚本、JSONL check 与 GUI 适配已接入。
Rust 8 项测试、Clippy（warnings as errors）、开发 Release build、GUI/Worker 自检和 Updater 自动化通过。
真实 Rust check 读取公开 GitHub Release 元数据并返回无更新结果，未下载完整包；网络 smoke 不替代可控响应测试。
未运行 WPF 鼠标交互或更新重启实机验收。02/03 尚未完成，GUI 下载和安装入口关闭，不调用旧安装链路。
