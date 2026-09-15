---
status: ready-for-agent
labels: [ready-for-agent]
issue-source: local-docs/issues
---

# 05: 集成正式发布布局并完成真实升级验收

**Parent:** [MaaNOP Updater V2 规格](../maanop-updater-v2-spec.md)

**What to build:** 将已完成的 V2 功能纳入正式 Windows x64 发布布局与校验，使用真实 MaaNOP 完整包完成更新验收，
明确首包手动安装要求及自动化与实机证据的范围。

**Blocked by:** [04: 安装前关闭活动运行环境](04-runtime-handoff.md)。

**Status:** ready-for-agent

## Acceptance criteria

- [ ] 正式构建/发布流程消费已完成的 Rust Engine，并按 V2 契约分发；移除正式布局中旧 .NET Updater 四件套、
      TEMP probe 和 updater runtime bootstrap 要求，不保留空壳兼容入口或双 Updater。
- [ ] 正式产物中的 Engine 复制到独立运行位置后可执行，不依赖安装根目录 GUI runtime；
      该证据来自正式构建产物，不只来自开发构建。
- [ ] 分别校验 GUI baseline 与完整 MaaNOP 产品包，不把 baseline 自检当作完整包安装验证。
- [ ] 完整包包含 GUI、Rust Engine、Worker、必要 runtime、PI、resource、agent 和必需内置 Python，
      PI 产品版本、仓库、精确资产名和 SHA256 满足已有 V2 检查/准备契约，包中没有 state 运行态。
- [ ] 发布校验使用 01–04 已实现的规则和测试；本片不新增另一份 GUI 更新逻辑或独立包验证规则来兜底。
- [ ] 用正式产物执行已存在的相关自动化和布局检查，并记录构建来源、版本及结果。
- [ ] 准备足以覆盖连续两次升级的真实 V2 完整包和隔离安装目录；记录初始版本、每次目标版本、产物来源和日志。
- [ ] 实机覆盖空闲安装、取消确认、Active Run 停止、Preview cleanup、Worker/Child Session/游戏/Agent 退出，
      以及最终无法确认退出时不安装；不终止无关用户进程。
- [ ] 实机确认实际副本 ready、GUI 真正退出、根入口释放、自身替换、四目录保留、过期程序条目消失，
      并确认清理在 relaunch 前完成尝试、新 GUI 不等待旧 Engine。
- [ ] 连续两次真实 V2 升级验证运行副本和废弃缓存生命周期，不生成完成 journal、旧版恢复入口或长期备份。
- [ ] 复验已验证的 Child Session 创建/恢复、显示/隐藏、结束和退出 baseline，分别记录实际执行过的场景。
- [ ] 正式发布说明明确首个 V2 手动安装、之后 V2 → V2 更新；不声明支持 V1 自动迁移，
      故障修复说明指向重新下载完整包，不将 old 宣传为可恢复备份。
- [ ] 同步实际产物能力与验证文档，明确区分开发构建、正式布局、自动化、完整包集成和 Windows 实机结果。
      缺失测试包或交互环境时记录外部阻塞，不将未运行场景勾选完成。

## Scope boundary

本片只负责正式发布布局集成和真实升级验收。Engine 工程/JSONL seam 属于 01，准备规则属于 02，
最小安装 UI、ready、自复制、替换及失败语义属于 03，活动运行环境关闭属于 04。
发现缺失功能或行为缺陷应回到对应责任工单修复，不把本片变成剩余功能的集中实现工单。

正式完整包产物和可交互 Windows 环境是验收的外部前提；需要下游仓库变更时遵守其单独授权边界。
本工单不隐含创建 stable tag 或发布 Release 的授权。
本次仅发布工单，未执行构建、下游修改或实机更新。
