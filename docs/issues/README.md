# 需求与规格快照

当前仓库没有远端 GitHub Issues；本目录是问题、规格说明和切片工单的权威本地记录，详见 [`docs/agents/issue-tracker.md`](../agents/issue-tracker.md)。如果未来启用远端 Issue，再把这里的文件迁移为远端记录并补充编号/链接。

文档边界如下：

- `docs/issues/`：需求、用户故事、验收条件和切片工单的本地源记录。
- `docs/issues/archive/`：已经完成代码实现、暂时退出当前工作区的历史工单；归档不等于所有实机验收均已通过。
- `docs/adr/`：已经做出的、难以逆转且需要长期解释的领域/架构决策。
- `docs/UPDATER-VALIDATION.md`：Updater 当前实现的验证边界和未完成的实机验收，不是需求规格。
- `artifacts/`：构建、发布和测试产物；被 `.gitignore` 排除，不能保存规格源文件。

当前 Updater 的 01–05 切片已移入归档，06 保留在活动目录，等待完整 MaaNOP 包和 Windows 实机 E2E 验收。
