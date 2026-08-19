# 问题跟踪器：GitHub

本仓库的问题和规格说明统一记录为 GitHub Issues。所有操作均使用 `gh` CLI。

## 操作约定

- **创建问题**：`gh issue create --title "..." --body "..."`。多行正文使用 `--body-file <path>`。
- **读取问题**：`gh issue view <number> --comments`，同时获取标签，并根据需要使用 `jq` 筛选评论。
- **列出问题**：`gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'`，并根据需要添加 `--label` 和 `--state` 筛选条件。
- **评论问题**：`gh issue comment <number> --body "..."`
- **添加或移除标签**：`gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **关闭问题**：`gh issue close <number> --comment "..."`

仓库信息从 `git remote -v` 推断；在仓库克隆目录中运行时，`gh` 会自动完成此操作。

## 是否将拉取请求作为分诊入口

**PRs as a request surface: no.**

该行是供技能读取的机器配置，保持原样。若本仓库以后将外部拉取请求视为功能请求，可将 `no` 改为 `yes`。

设置为 `yes` 后，拉取请求与问题使用相同的标签和状态，并通过对应的 `gh pr` 命令操作：

- **读取拉取请求**：使用 `gh pr view <number> --comments` 读取详情，并使用 `gh pr diff <number>` 查看差异。
- **列出待分诊的外部拉取请求**：运行 `gh pr list --state open --json number,title,body,labels,author,authorAssociation,comments`，只保留 `authorAssociation` 为 `CONTRIBUTOR`、`FIRST_TIME_CONTRIBUTOR` 或 `NONE` 的项目，排除 `OWNER`、`MEMBER` 和 `COLLABORATOR`。
- **评论、添加标签或关闭**：分别使用 `gh pr comment`、`gh pr edit --add-label`、`gh pr edit --remove-label` 和 `gh pr close`。

GitHub Issues 和拉取请求共用同一编号空间，因此单独出现的 `#42` 可能指向其中任意一种。先运行 `gh pr view 42`，若不存在，再运行 `gh issue view 42`。

## 当技能要求“发布到问题跟踪器”时

创建一个 GitHub Issue。

## 当技能要求“获取相关工单”时

运行 `gh issue view <number> --comments`。

## Wayfinder 操作

供 `/wayfinder` 使用。一个 **map** 是包含多个子问题工单的单一问题。

- **Map**：使用标签 `wayfinder:map` 的单一问题，其正文包含 Notes、Decisions-so-far 和 Fog。创建命令为 `gh issue create --label wayfinder:map`。
- **子工单**：作为 map 的 GitHub 子问题，通过子问题 API 使用 `gh api` 关联。如果仓库未启用子问题，则将子工单加入 map 正文的任务列表，并在子工单正文顶部写入 `Part of #<map>`。标签使用 `wayfinder:<type>`，其中类型为 `research`、`prototype`、`grilling` 或 `task`。认领后，将工单分配给负责推进的开发者。
- **阻塞关系**：优先使用 GitHub 原生问题依赖关系，确保阻塞信息可在界面中查看。添加依赖边时运行 `gh api --method POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`。其中 `<blocker-db-id>` 是阻塞问题的数字数据库 ID，可通过 `gh api repos/<owner>/<repo>/issues/<n> --jq .id` 获取，不是问题编号或 `node_id`。GitHub 通过 `issue_dependencies_summary.blocked_by` 报告仍未关闭的阻塞项。如果依赖功能不可用，则在子工单正文顶部添加 `Blocked by: #<n>, #<n>`。所有阻塞问题关闭后，该工单视为解除阻塞。
- **前沿查询**：列出 map 下仍然开放的子工单，排除存在开放阻塞项或已有负责人者，然后选择 map 顺序中的第一个。
- **认领**：运行 `gh issue edit <n> --add-assignee @me`。这是会话中的第一次写操作。
- **解决**：先运行 `gh issue comment <n> --body "<answer>"`，再运行 `gh issue close <n>`，最后在 map 的 Decisions-so-far 中追加上下文指针和链接。
