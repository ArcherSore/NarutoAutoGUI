# NarutoAutoGUI

NarutoAutoGUI 是面向 MaaNOP 的 Windows 图形前端；这里统一项目特有的术语。

## Language

**MaaNOP**:
基于 MaaFramework 的火影忍者 Online 自动化项目，定义任务、选项与资源。
_Avoid_: MaaFramework、MFAAvalonia

**NarutoAutoGUI**:
用户配置和观察 MaaNOP 自动化的专用前端。
_Avoid_: MaaNOP、Worker

**MaaFramework**:
执行 MaaNOP 自动化任务的框架。
_Avoid_: MaaNOP

**MFAAvalonia**:
用于人工对照 MaaNOP 行为的另一前端，不属于 NarutoAutoGUI 的正常运行链路。
_Avoid_: MaaNOP、Child Session Worker

**桌面分身**:
让游戏和自动化输入在隔离桌面中运行、使主桌面可继续使用的用户能力。
_Avoid_: 普通后台进程

**Child Session**:
承载桌面分身的 Windows 会话，与用户正在操作的主会话相区分。
_Avoid_: 子进程、Worker

**Child Session Worker**:
在 Child Session 中承载 MaaNOP 自动化运行的进程角色。
_Avoid_: Windows 服务、GUI 后台线程

**Project Interface**:
MaaNOP 向前端声明可用任务、选项、资源和展示信息的项目接口。
_Avoid_: 用户配置、Run Plan

**MaaNOP Config**:
NarutoAutoGUI 保存的任务配置集合与当前激活配置，表达用户选择。
_Avoid_: Application Settings、Run Plan

**Task Configuration（任务配置）**:
一份独立的 MaaNOP 用户选择，包含要执行的任务、顺序和显式设置的选项。
_Avoid_: task 实例、Run Plan

**Active Configuration（激活配置）**:
当前任务工作区中显示、供下一次运行使用的任务配置。
_Avoid_: Active Run

**Application Settings**:
NarutoAutoGUI 自身的偏好设置，不包含 MaaNOP 任务和选项。
_Avoid_: MaaNOP Config

**Settings Definition**:
NarutoAutoGUI 设置页面的内容定义，不是用户偏好或 MaaNOP 项目接口。
_Avoid_: Application Settings、Project Interface

**MaaNOP Run**:
Worker 接受的一次 MaaNOP 自动化执行。
_Avoid_: IPC 连接、GUI 操作

**Active Run**:
Worker 当前尚未终结的 MaaNOP Run。
_Avoid_: Active Configuration、Last Run

**Last Run**:
Worker 最近一次已终结的 MaaNOP Run。
_Avoid_: Active Run、持久化运行历史

**Run Plan**:
一次 MaaNOP Run 使用的任务和参数计划，在开始执行后不随用户配置变化。
_Avoid_: MaaNOP Config、Worker Launch Context

**Plan Item**:
Run Plan 中一次顶层任务执行。
_Avoid_: 任务定义、整个 Run

**Worker Launch Context**:
一个 Worker 实例使用的 MaaNOP 项目和执行环境。
_Avoid_: Run Plan、用户任务配置

**Run Snapshot**:
Worker 提供的完整运行状态视图。
_Avoid_: GUI 推测状态、日志流

**GUI Observation**:
GUI 对 Worker 身份、连接和存活情况的本地观察，与 Worker 报告的运行状态不同。
_Avoid_: Worker State、Run State

**Preview（游戏画面预览）**:
供用户观察游戏画面的只读视图，可在没有活动任务时使用。
_Avoid_: 任务状态、子桌面交互窗口

**Update Engine**:
负责 MaaNOP Windows 完整包检查、准备与安装规则的更新组件。
_Avoid_: GUI 更新窗口、独立组件更新
