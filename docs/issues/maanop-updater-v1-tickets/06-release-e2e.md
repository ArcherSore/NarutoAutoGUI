# 06：集成 MaaNOP 完整发布包并完成 Updater V1 验收

## What to build

将包含 Updater 的 NarutoAutoGUI baseline 接入 MaaNOP Windows x64 完整包，验证用户从首次手动升级后的版本开始，能完成无需手工解压覆盖的完整更新。
本工单负责发布集成和最终产品验收，不重新实现前置切片。

## Acceptance criteria

- [ ] 核实 MaaNOP 完整包包含 GUI、独立可运行 Updater、Worker、固定原生 runtime、Project Interface、资源、Agent 和内置 Python。
- [ ] 核实 github 更新源元数据及 CI 根据 Release tag 写入产品 version，资产名严格遵循 MaaNOP Windows x64 命名规则。
- [ ] 核实不同 NarutoAutoGUI baseline 的完整包可互相衔接，Updater 自身能被替换，用户只看到 MaaNOP 产品版本。
- [ ] 使用现有构建/发布自检完成 SemVer、Release/asset、download/SHA256、ZIP/path/package、config/log preservation、swap/rollback 自动化汇总。
- [ ] Windows 实机手工 E2E 覆盖 Active Run、Preview、Worker、Child Session、Naruto Online/Agent、TEMP Updater、restart、completion Banner。
- [ ] 实机覆盖空闲与运行中确认、取消、正常关闭和必要的既有受控强制清理；确认已跟踪进程及文件释放，不影响无关用户进程。
- [ ] 连续两次更新验证最多一个 MaaNOP.old，下一次前安全清理；清理失败在修改当前安装前中止，无健康握手。
- [ ] 对最终 spec 全部 23 条验收场景记录自动/手工证据及失败项；自动化成功不代替真实生命周期验收。
- [ ] 回归当前 Child Session 创建/恢复、显示/隐藏、结束/退出 baseline，记录实际结果，不新增 Worker/RDP/Game mock framework。
- [ ] 说明历史版本需手动升级一次，不提供 bootstrap workaround；后续成功流程无需手动下载、解压、覆盖或重建快捷方式。
- [ ] 按实际能力、限制和验收结果同步 STATUS；仅在方向/优先级确实变化时同步 ROADMAP。
- [ ] 不将建立工单视作创建 MaaNOP stable tag/Release 的授权；对需真实发布资产的验收记录明确前置条件。

## Blocked by

- 05：新版首次启动展示更新完成 Banner 与对应说明。

## Scope guard

MaaNOP 发布集成为本功能的跨仓库集成依赖，范围仅限完整包消费与元数据/打包契约，不改资源业务、MFAAvalonia 或 MaaFramework。
遵循最终 spec 所有 Out of Scope；禁止借验收引入自动下载安装、独立组件更新或健康回滚。
