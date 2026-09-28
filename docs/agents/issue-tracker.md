# Issue tracker: GitHub

本仓库使用 [GitHub Issues](https://github.com/ArcherSore/NarutoAutoGUI/issues) 作为唯一问题跟踪器。spec、tickets、当前状态、未完成验收与后续工作均在此记录；仓库内不维护第二套 docs/issues/。

## 操作

- 读取与列出：gh issue view <number> --comments；gh issue list --state open --json number,title,body,labels,comments。
- 创建：gh issue create --title ... --body-file <path>。多行正文写入临时文件，不在命令行拼接正文。
- 更新：gh issue edit <number> --add-label ... / --remove-label ...；进度与验收结果用评论补充。
- 完成：验收条件满足后 gh issue close <number>，或在 PR/commit 中引用 Issue 关闭。
- 仓库由当前 git remote 确定；在本仓库目录运行 gh。
- 如有依赖，优先使用 GitHub 原生 issue blocking 关系；不可用时在票据中写明 Blocked by: #<number>。依赖只记录真正阻塞开工的工作。

**PRs as a request surface: no.** 外部 PR 不自动加入 Issue 分诊队列。GitHub 的 Issue 与 PR 共用编号；单独的 #<number> 先确认对象类型。

## 工程 skills

- grill-with-docs 用于尚未决定的设计讨论；只把形成的领域术语写入 CONTEXT.md，符合标准的长期架构取舍写入 ADR。
- to-spec 仅在工作需要跨多个会话时，将已决定的内容发布为单个 GitHub Issue；应用 ready-for-agent，不把 spec 存到 docs。
- to-tickets 将跨会话工作拆成可独立演示的纵向切片，按依赖顺序发布 GitHub Issues。发布前按该 skill 的要求确认粒度与阻塞关系。完成的票据关闭。
- 单个会话能完成的改动直接实施；tdd 依其测试边界和逐个 red→green 规则执行，不为每个改动生成 spec/tickets。
- triage 的标签词汇见 [triage-labels.md](triage-labels.md)。需要真实 Windows 桌面或用户设备的验收用 ready-for-human。

GitHub Issues 的关闭记录和 Git history 保存工作过程；长期文档只保留仍成立的系统知识。
