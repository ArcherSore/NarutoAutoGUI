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

### 构建与测试入口

在仓库根目录运行 `./src/NarutoAutoGUI/scripts/verify.ps1`：增量构建 GUI 并运行完整 GUI Self-Test。
默认 Release；支持 Windows PowerShell 5.1 和 PowerShell 7，从其他工作目录调用绝对脚本路径也可运行。
成功只显示 PASS/耗时；完整 stdout/stderr 在忽略的 `artifacts/validation/<本次运行>/` 中。
失败先读摘要，必要时只读取失败阶段的日志；脚本保留原始进程退出码，构建失败后不会测试旧 DLL。

| 改动范围 | verify.ps1 参数 | 验证范围 |
| --- | --- | --- |
| 一般 GUI、共享布局或不确定影响范围 | 默认（`-Suite Gui`） | GUI 构建 + 全 GUI 自检 |
| 任务配置、标签条、参数编辑、TaskShelf、引导 | `-Suite Project` | 现有 `--project-only`，也包含 Support 自检 |
| 通知、诊断 ZIP、Debug 图片清理、设置、导航 | `-Suite Support` | 现有 `--support-only` |
| 运行控制栏、侧栏宽度、Run 进度、运行环境接线 | `-Suite RuntimeControls` | 现有 `--runtime-controls-only` |
| Worker | `-Suite Worker` | Worker 构建 + 全 Worker 自检 |
| GUI 与 Engine 的更新适配器 | `-Suite Updater` | 现有 .NET Updater 测试（含构建） |
| Rust Engine | `-Suite Engine` | Cargo tests + 全 targets Clippy |
| 跨组件、Protocol、依赖或完成工作前的完整自动验证 | `-Suite All` | GUI/Worker 增量构建 + 原 test-automated.ps1 全覆盖 |

定向验证用于迭代；共享代码修改应扩大范围，完成后运行 `-Suite All`。
`-NoBuild` 仅用于已成功构建且源码/依赖未变时重跑测试；All 时也跳过 Updater 构建，Rust 仍由 Cargo 编译。
不要在同一输出目录并行构建/自检；DLL 被占用时先确认正在运行的程序或自检，不要盲目重试或终止用户进程。
临时目录权限或交互桌面条件导致失败时，应如实报告环境限制，不得跳过断言或将旧 DLL 的结果当成新代码验证。

只有发布、打包或 runtime/依赖布局改动才运行 `build.ps1`（保留完整发布语义），并运行
`validate-package.ps1` 和发布产物自检；原生框架诊断改动还需 `test-framework-diagnostics.ps1`。
普通 WPF 修改无需组装发布包或运行 Rust 测试；Child Session/RDP/UAC/托盘等人工回归仍按改动风险执行。

## Agent skills

### Issue tracker

GitHub Issues 是唯一工作跟踪器，含 spec、tickets、状态与验收条件。操作约定见 docs/agents/issue-tracker.md。

### Triage labels

使用 needs-triage、needs-info、ready-for-agent、ready-for-human、wontfix；映射见 docs/agents/triage-labels.md。

### Domain docs

本仓库是单上下文布局：根目录 CONTEXT.md 与 docs/adr/。消费规则见 docs/agents/domain.md。
