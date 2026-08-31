# Roadmap

## 已完成：第一轮正式 GUI

1. WPF 主窗口、托盘和统一日志。
2. Child Session 创建/恢复、显示/隐藏、状态展示和安全注销。
3. 使用固定游戏启动 profile 与 bundled MaaNOP Project Interface，幂等准备完整运行环境。
4. 已完成正式 GUI 的真实 Windows 桌面 Child Session 交互式回归。

## 下一步：补齐剩余实机边界

5. 补齐游戏/MaaNOP 跨 Session 启动、异常断开后重建连接、创建/启动过程中的并发退出等边界回归。
6. 根据实机结果修复明确缺陷，不改变正式 GUI 内已验证的 RDP/COM/WTS baseline。
7. 改善状态呈现和可诊断性，仅增加已被实际回归需要证明的内容。

## 已完成：首个最小 Worker/IPC + MaaFramework 端到端阶段

7. 已按 ADR 0020 实现首个真实单任务闭环：NarutoAutoGUI → Child Session Worker → MaaFramework → MaaNOP；Worker admission、fresh Snapshot、依赖就绪、真实 Running 后停止、取消后存活和同 Worker 再次运行已完成实机验证。
8. 历史 default-only `AccountTraining` Run 曾因下游脚本/资源或游戏前置条件终结为 Failed，并作为 partial 检查点保留；该结果没有被误记为通过。
9. 已实现正式 MaaNOP Config/PI option 编辑与 ExplicitOptions 解析；PI 驱动 UI 可设置 `ServerRange=978` 及
   task/nested option，并可按 `SelectedTasks` 顺序组成不重复 task 的多项执行计划。当前不包含硬编码 fixture、隐藏
   Run Plan override、MaaNOP 默认值修改或同一 Task 多实例参数模型。
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

16. 已实现 `workflow_dispatch` 的 locked restore、Release build/publish、GUI/Worker 自动自检、发布目录与 ZIP 解包校验
    和 Actions artifact；已通过提交 `b41553c` 的 GitHub Actions run `32968253563`。后续发布只包含 ZIP，不再生成或发布
    独立 SHA256 sidecar。
17. `v*` tag 路径只接受明确的稳定版或 alpha/beta/rc 版本格式，并使用现有 tag 创建 GitHub Release；dispatch 不创建
    tag 或 Release。`v0.1.0-rc.2` 已完成首个 prerelease 的 build、self-test、package validation、SHA256 与 Release 验证。
18. 发布包继续只包含 NarutoAutoGUI、固定 Worker 和 Maa.Framework runtime；本阶段不捆绑 MaaNOP 或 Python runtime，
    也不升级 Maa.Framework 或 target framework。

## 已完成：首个 RC 与 MaaNOP Windows x64 frontend 接管

19. Python 继续由 MaaNOP Project Interface 以 `child_exec = "python"` 解析系统环境，不 bundle runtime、不修改 PATH；
    当前语义下的 E2E 与本机回归已完成，不再把 Python runtime 打包作为 frontend 接管前置项。
20. MaaNOP Windows x64 workflow 已固定 `v0.1.0-rc.2` 的 asset name 与 SHA256，以 NarutoAutoGUI package 为 base，
    仅 overlay MaaNOP-owned payload；Actions run `32982465990` 的 Windows x64 和其余 matrix job 均成功。

## 下一步：稳定版前收口

21. 按需人工下载 MaaNOP Windows x64 Actions artifact 做额外解包目检；该下载不阻塞已在 Actions 内完成的 package
    composition、SHA 与边界校验结论。
22. 在没有新的明确授权前，不创建 MaaNOP stable tag 或 stable Release。

自动登录/扫码、自动隐藏子桌面、自动开始 MaaNOP 任务和可调分辨率/DPI 不属于第一轮，也不会在未单独确认范围前实现。
