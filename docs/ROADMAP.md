# Roadmap

## 已完成：第一轮正式 GUI

1. WPF 主窗口、托盘和统一日志。
2. Child Session 创建/恢复、显示/隐藏、状态展示和安全注销。
3. 配置并分别启动游戏/MaaNOP，以及幂等的一键启动挂机环境。

## 下一步：先稳定当前 baseline

4. 完成并记录正式 GUI 的 Windows 交互式回归。
5. 根据实机结果修复第一轮 GUI 的明确缺陷，不改变已验证的 RDP/COM/WTS baseline。
6. 改善状态呈现和可诊断性，仅增加已被实际回归需要证明的内容。

## 已完成：首个最小 Worker/IPC + MaaFramework 端到端阶段

7. 已按 ADR 0020 实现首个真实单任务闭环：NarutoAutoGUI → Child Session Worker → MaaFramework → MaaNOP；Worker admission、fresh Snapshot、依赖就绪、真实 Running 后停止、取消后存活和同 Worker 再次运行已完成实机验证。
8. 历史 default-only `AccountTraining` Run 曾因下游脚本/资源或游戏前置条件终结为 Failed，并作为 partial 检查点保留；该结果没有被误记为通过。
9. 已在保持单个 top-level Plan Item 的前提下实现正式 MaaNOP Config/PI option 编辑与 ExplicitOptions 解析；PI 驱动 UI 可设置 `ServerRange=978` 及 task/nested option，不包含硬编码 fixture、隐藏 Run Plan override 或 MaaNOP 默认值修改。
10. `win-x64-options-v2-scroll` 已在同一 Worker 上完成真实 Success、Running 后 Cancellation、取消后存活和 Worker 复用验收；首片已从 partial 更新为 PASS。
11. MFAAvalonia 只保留为人工诊断后备，不进入正常执行、配置、恢复或 Worker 替换链路。
12. Supported Baseline 所采用的 MaaNOP 本机目录、tag 或 commit 仍未自动冻结；首个真实 E2E 已通过，下一轮可单独决定是否开始固定 MaaNOP snapshot、Python `maa` 与 Maa.Framework 精确组合。

自动登录/扫码、自动隐藏子桌面、自动开始 MaaNOP 任务和可调分辨率/DPI 不属于第一轮，也不会在未单独确认范围前实现。
