# Roadmap

## 已完成：第一轮正式 GUI

1. WPF 主窗口、托盘和统一日志。
2. Child Session 创建/恢复、显示/隐藏、状态展示和安全注销。
3. 配置并分别启动游戏/MaaNOP，以及幂等的一键启动挂机环境。
4. 已完成正式 GUI 的真实 Windows 桌面 Child Session 交互式回归。

## 下一步：补齐剩余实机边界

5. 补齐游戏/MaaNOP 跨 Session 启动、异常断开后重建连接、创建/启动过程中的并发退出等边界回归。
6. 根据实机结果修复明确缺陷，不改变正式 GUI 内已验证的 RDP/COM/WTS baseline。
7. 改善状态呈现和可诊断性，仅增加已被实际回归需要证明的内容。

## 已完成：首个最小 Worker/IPC + MaaFramework 端到端阶段

7. 已按 ADR 0020 实现首个真实单任务闭环：NarutoAutoGUI → Child Session Worker → MaaFramework → MaaNOP；Worker admission、fresh Snapshot、依赖就绪、真实 Running 后停止、取消后存活和同 Worker 再次运行已完成实机验证。
8. 历史 default-only `AccountTraining` Run 曾因下游脚本/资源或游戏前置条件终结为 Failed，并作为 partial 检查点保留；该结果没有被误记为通过。
9. 已在保持单个 top-level Plan Item 的前提下实现正式 MaaNOP Config/PI option 编辑与 ExplicitOptions 解析；PI 驱动 UI 可设置 `ServerRange=978` 及 task/nested option，不包含硬编码 fixture、隐藏 Run Plan override 或 MaaNOP 默认值修改。
10. `win-x64-options-v2-scroll` 已在同一 Worker 上完成真实 Success、Running 后 Cancellation、取消后存活和 Worker 复用验收；首片已从 partial 更新为 PASS。
11. MFAAvalonia 只保留为人工诊断后备，不进入正常执行、配置、恢复或 Worker 替换链路。
12. Supported Baseline 所采用的 MaaNOP 本机目录、tag 或 commit 仍未自动冻结；首个真实 E2E 已通过，下一轮可单独决定是否开始固定 MaaNOP snapshot、Python `maa` 与 Maa.Framework 精确组合。

## 已完成：Active Run 游戏画面 Preview V1

13. Worker 复用当前 Active Run 的唯一 MaaWin32Controller cached image，固定约 5 FPS 只保存最新一帧；GUI 通过现有
    Named Pipe JSON 轮询 PNG + base64，并以 Worker Instance、Run 和 revision 拒绝陈旧或重复画面。
14. Idle、Stopping、终态、断线、Worker replacement、窗口隐藏/最小化或离开 Home 时显示 Placeholder；所有 Preview
    失败只记诊断，不改变 Run、Worker admission、cleanup 或 Child Session 生命周期。
15. 本阶段不继续实现 30/60 FPS、可配置 FPS、帧历史、录制、截图保存、点击控制、独立 Preview Window、二进制传输或
    Child Session 整个桌面捕获。

## 已完成：Phase 1 Windows x64 发布基础设施

16. 已实现 `workflow_dispatch` 的 locked restore、Release build/publish、GUI/Worker 自动自检、发布目录与 ZIP 解包校验、
    SHA256 sidecar 和 Actions artifact；已通过提交 `b41553c` 的 GitHub Actions run `32968253563`。
17. `v*` tag 路径只接受明确的稳定版或 alpha/beta/rc 版本格式，并使用现有 tag 创建 GitHub Release；dispatch 不创建
    tag 或 Release。当前尚未创建首个 RC。
18. 发布包继续只包含 NarutoAutoGUI、固定 Worker 和 Maa.Framework runtime；本阶段不捆绑 MaaNOP 或 Python runtime，
    也不升级 Maa.Framework 或 target framework。

## 下一步：首个 RC 与固定运行时 E2E

19. 单独冻结受支持的 MaaNOP snapshot、Python `maa` 与 Maa.Framework 精确组合，再完成 Python runtime 打包 E2E。
20. 在上述 baseline、打包边界和消费级电脑交互式回归明确通过后，再创建并验证首个 RC tag/GitHub Release。

自动登录/扫码、自动隐藏子桌面、自动开始 MaaNOP 任务和可调分辨率/DPI 不属于第一轮，也不会在未单独确认范围前实现。
