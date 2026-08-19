# Roadmap

## 已完成：第一轮正式 GUI

1. WPF 主窗口、托盘和统一日志。
2. Child Session 创建/恢复、显示/隐藏、状态展示和安全注销。
3. 配置并分别启动游戏/MaaNOP，以及幂等的一键启动挂机环境。

## 下一步：先稳定当前 baseline

4. 完成并记录正式 GUI 的 Windows 交互式回归。
5. 根据实机结果修复第一轮 GUI 的明确缺陷，不改变已验证的 RDP/COM/WTS baseline。
6. 改善状态呈现和可诊断性，仅增加已被实际回归需要证明的内容。

## 当前进行：首个最小 Worker/IPC + MaaFramework 端到端阶段

7. 已按 ADR 0020 实现首个真实单任务闭环：NarutoAutoGUI → Child Session Worker → MaaFramework → MaaNOP；Worker admission、fresh Snapshot、依赖就绪、真实 Running 后停止、取消后存活和同 Worker 再次运行已完成实机验证。
8. 当前检查点保持 partial：真实 default-only `AccountTraining` Run 已进入 MaaNOP pipeline，但因下游脚本/资源或游戏前置条件终结为 Failed；尚缺一个不经 Stop、自然终结为 Succeeded 的真实 Run。
9. 下一版在保持单个 top-level Plan Item 的前提下扩展正式 MaaNOP Config/PI option 编辑与 ExplicitOptions 解析，使用户能够通过 PI 驱动的 UI 设置 `ServerRange=978` 及 task option；不得增加硬编码 fixture、隐藏 Run Plan override 或修改 MaaNOP 默认值。
10. 下一版完成后统一执行真实 Success 验收，并回归已通过的 Cancellation、取消后存活和 Worker 复用；全部条件满足后才将首片从 partial 标记为 PASS。
11. MFAAvalonia 只保留为人工诊断后备，不进入正常执行、配置、恢复或 Worker 替换链路。
12. Supported Baseline 所采用的 MaaNOP 本机目录、tag 或 commit 暂缓决定；先收敛不依赖该身份的其他边界，端到端实机验证后再明确固定对象。

自动登录/扫码、自动隐藏子桌面、自动开始 MaaNOP 任务和可调分辨率/DPI 不属于第一轮，也不会在未单独确认范围前实现。
