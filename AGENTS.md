# Agent Development Guide

## 修改前必读

处理本仓库任务前，依次阅读：

1. `README.md`
2. `docs/ARCHITECTURE.md`
3. `docs/STATUS.md`
4. `docs/ROADMAP.md`
5. 涉及 Child Session 时再阅读 `src/NarutoAutoGUI/ChildSession/` 下的相关源码。

## 开发原则

- 保持实现简单，修改范围只覆盖当前任务。
- 不为潜在需求提前增加抽象、框架或复杂配置。
- 避免无关格式化、重命名和大规模重构。
- 已验证的 Child Session、RDP ActiveX、Task Scheduler COM、分辨率/缩放、进程 Session 验证和清理流程不得随意改变。
- 修改已验证流程前，说明必要性，并保留可重复的 baseline 验证方式。
- 不复制 BetterGI 无关代码；只保留 Child Session 所需的最小实现。
- 除非任务明确要求，否则不修改 MaaNOP、MFAAvalonia 或 MaaFramework，也不引入 Worker/IPC。
- 不提交 `bin/`、`obj/`、`artifacts/`、发布包、日志或本机凭据。
- 不在代码、文档、脚本或命令历史中提交密码。

## 代码风格

- 手写代码每行最多 120 个字符；120 是硬上限，不是目标宽度。
- 优先保持代码紧凑、自然、易读；在 120 字符内可以清晰表达时保持单行。
- 仅在超过 120 字符或明显影响可读性时换行；按语义边界换行，不机械采用“一参数一行”“一条件一行”“一链式调用一行”。
- 大括号采用混合风格：
  - 类型、方法、构造函数、local function 等声明使用 Allman 风格，左大括号单独一行。
  - `if`、`else`、`for`、`foreach`、`while`、`switch`、`try`、`catch`、`finally`、`using`、`lock` 等控制流使用 K&R 风格，左大括号与语句同行；写作 `} else {`、`} catch (...) {`。
- 简短属性、表达式成员、对象/集合初始化器优先使用紧凑写法。
- 不做装饰性换行、手工列对齐或与当前任务无关的格式化。
- 修改完成后检查受影响的手写代码是否符合上述规则。

## 完成任务

- 运行与改动风险相称的构建和测试。
- 如果当前能力、限制或验证结果发生变化，同步更新 `docs/STATUS.md`。
- 如果后续方向或优先级发生变化，同步更新 `docs/ROADMAP.md`。
- 报告实际执行过的验证，不能把未运行的交互式测试描述为已验证。

## 代理技能

### 问题跟踪器

问题和规格说明统一记录在 `ArcherSore/NarutoAutoGUI` 的 GitHub Issues 中。详见 `docs/agents/issue-tracker.md`。

### 领域文档

本仓库采用单上下文布局，使用根目录的 `CONTEXT.md` 和 `docs/adr/`。详见 `docs/agents/domain.md`。
