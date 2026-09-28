# Agent Development Guide

## 开始任务

先阅读 README.md 和 CONTEXT.md，再阅读与当前工作有关的 docs/ARCHITECTURE.md、docs/adr/ 中的决策、GitHub Issue / Spec 及源码。涉及 Child Session 时阅读 src/NarutoAutoGUI/ChildSession/ 的相关实现。不要把整套历史文档当成必读材料。

## 开发原则

- 保持实现简单，修改范围只覆盖当前任务，不为潜在需求增加抽象或复杂配置。
- 避免无关格式化、重命名和大规模重构。
- 已验证的 Child Session、RDP ActiveX、Task Scheduler COM、分辨率/缩放、进程 Session 验证和清理流程不得随意改变。修改前说明必要性，并保留可重复的 baseline 验证方式。
- 不复制 BetterGI 无关代码；只保留 Child Session 所需的最小实现。
- 除非任务明确要求，否则不修改 MaaNOP、MFAAvalonia 或 MaaFramework。
- 不提交 bin/、obj/、artifacts/、发布包、日志或本机凭据。
- 不在代码、文档、脚本或命令历史中提交密码。

## 文档边界

- CONTEXT.md 只定义项目特有的重要术语；仅在领域语言实际变化时修改。用一两句话定义概念，不写实现、协议、流程或历史。
- ADR 仅记录同时难以逆转、脱离背景会显得意外、且存在真实取舍的决策。普通实现选择、局部协议、状态机和测试约束留在代码、测试或必要的 reference 中。
- docs/ARCHITECTURE.md 只描述当前系统的组件、边界、主要流转和不变量。稳定协议才保留独立 reference。
- 当前状态、后续工作、验收条件、跨会话 spec 和 tickets 放在 GitHub Issues；完成后关闭。只有跨多次会话的工作才需要 spec 或 tickets。
- 不把实现过程、测试输出、人工验收流水账、DLL/SHA/本地同步记录、临时研究、讨论纪要或 session memory 写入长期文档。Git history、Issue/PR 和自动化测试承担这些职责。
- 不建立文档归档目录来保存过时内容。完成工作后检查并清理失效链接与过期说明。

## 代码风格

- 手写代码每行最多 120 个字符；120 是硬上限，不是目标宽度。
- 优先保持代码紧凑、自然、易读；在 120 字符内可以清晰表达时保持单行。
- 仅在超过 120 字符或明显影响可读性时换行；按语义边界换行，不机械采用“一参数一行”“一条件一行”“一链式调用一行”。
- 大括号采用混合风格：
  - 类型、方法、构造函数、local function 等声明使用 Allman 风格，左大括号单独一行。
  - if、else、for、foreach、while、switch、try、catch、finally、using、lock 等控制流使用 K&R 风格，左大括号与语句同行；写作 } else {、} catch (...) {。
- 简短属性、表达式成员、对象/集合初始化器优先使用紧凑写法。
- 不做装饰性换行、手工列对齐或与当前任务无关的格式化。
- 修改完成后检查受影响的手写代码是否符合上述规则。

## 完成任务

运行与改动风险相称的构建和测试，报告实际执行的验证；未运行的交互式测试不得描述为已验证。发现未完成工作时更新对应 Issue，不追加长期状态文件。

## Agent skills

### Issue tracker

GitHub Issues 是唯一工作跟踪器，含 spec、tickets、状态与验收条件。操作约定见 docs/agents/issue-tracker.md。

### Triage labels

使用 needs-triage、needs-info、ready-for-agent、ready-for-human、wontfix；映射见 docs/agents/triage-labels.md。

### Domain docs

本仓库是单上下文布局：根目录 CONTEXT.md 与 docs/adr/。消费规则见 docs/agents/domain.md。
