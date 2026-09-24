# MXU 任务分类与 MaaFramework PI

核对日期：2026-09-24。MXU 源码快照：`5604eff21de99ed051b4f48f50c5dd40a54430a3`。
初次为源码与协议调研，未运行 MXU 交互测试；同日后续按用户要求完成本仓库解析支持，见下文。

## 官方协议

MaaFramework Project Interface 自 v2.4.0（2026-03-09）增加任务分组：
顶层 `group` 数组声明分组，各任务的 `group` 字符串数组引用分组 `name`。
分组支持 `name`、`label`、`description`、`icon`、`default_expand`（默认 true）。
一个任务可以属于多个组；身份、启用状态、预设引用与选项仍以 `task.name` 为键。
`interface_version` 仍写数字 2；PI 文档版本与框架发布版本分别管理。

来源：[官方 PI 文档](https://github.com/MaaXYZ/MaaFramework/blob/main/docs/en_us/3.3-ProjectInterfaceV2.md)。

## MXU 实现

- `src/types/interface.ts` 定义 `GroupItem` 和 `TaskItem.group`。
- `src/services/interfaceLoader.ts` 合并 import 的 group，按 name 去重，先定义优先。
- `src/components/AddTaskPanel.tsx` 先按任务名称/显示名称搜索，再建立 group name 到任务列表的 Map。
- 分组按声明顺序展示，组内保持任务原顺序；同一任务可出现在多个已声明组。
- 无 group 或所有组名均未匹配的任务进入“未分组”；部分匹配时只进入匹配的组。
- 分组标题可折叠，初始值来自 default_expand；过滤后为空的组不显示。
- 当前分组标题渲染名称与数量，未使用分组 description/icon；不能把协议字段全部视为已展示。
- 没有顶层分组时使用普通任务列表。分组是添加任务入口的组织方式，不产生执行依赖。
- 运行中追加任务仍传 task.entry 与 pipelineOverride 给 maaService.runTask，不传 group。

来源：
[类型](https://github.com/MistEO/MXU/blob/5604eff21de99ed051b4f48f50c5dd40a54430a3/src/types/interface.ts)、
[导入](https://github.com/MistEO/MXU/blob/5604eff21de99ed051b4f48f50c5dd40a54430a3/src/services/interfaceLoader.ts)、
[添加面板](https://github.com/MistEO/MXU/blob/5604eff21de99ed051b4f48f50c5dd40a54430a3/src/components/AddTaskPanel.tsx#L417-L609)。

## 对本仓库的含义

2026-09-24 已补充 `ProjectInterfaceLoader` 的 group 解析，`ProjectPlanModule.Groups` 和
`ProjectTaskChoice.Groups` 暴露分类元数据。保留声明顺序和未知组引用，拒绝重复组名及不合法的字段类型。
当前仅保存描述、图标路径和国际化字符串原值，不增加 import、翻译或图标加载功能。
同日 `task` 分支已实现分类折叠、任务搜索、限高的可用任务区和独立滚动的执行计划。
无分类声明时保留平铺，多组任务共享已添加状态；分类图标和描述暂不展示。
继续以 task name 保存选择与顺序，不新增 Worker/IPC 分类字段或调整 MaaFramework 执行流程。
Release 构建及完整 GUI 自检通过，宽窄窗口完成离屏截图检查，具体范围见 `docs/STATUS.md`。

本地依据：`src/NarutoAutoGUI.ProjectModel/ProjectInterfaceLoader.cs`、
`src/NarutoAutoGUI.ProjectModel/ProjectDefinition.cs`、
`src/NarutoAutoGUI.ProjectModel/ProjectPlanModule.cs`。
