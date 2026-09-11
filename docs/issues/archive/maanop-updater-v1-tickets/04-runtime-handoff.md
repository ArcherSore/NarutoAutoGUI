---
status: archived-implementation-awaiting-interactive-validation
archived: 2026-09-11
---

# 04：确认安装后关闭运行环境并交给 TEMP Updater

## What to build

用户在 ReadyToInstall 点击“安装更新”，经一次简洁确认后，GUI 结束本应用运行环境、启动 TEMP Updater 并真正退出，完成实际安装和重启链路。

## Acceptance criteria

- [ ] 只有已通过下载及包预验证的目标能进入安装确认，最终按钮仅“取消”与“安装并重启”。
- [ ] 空闲说明将重启；Active Run 时说明当前任务将停止；取消不停止环境、不启动 Updater、不修改安装目录。
- [ ] 确认后复用应用级操作门禁止新任务/环境操作，等待在途操作后检查最终运行态；不新增无关抽象。
- [ ] 优先正常停止 Run、Preview、Worker、Child Session；必要时复用现有仅针对本应用拥有 Worker/Child Session/游戏的受控强制清理路径。
- [ ] 不把 run.stop ACK 当成停止完成，不把 IPC disconnect 当成退出；最终仍无法确认相关进程退出或文件释放才中止安装。
- [ ] 不终止无关用户进程，不要求保留 Child Session/游戏登录态，不设计通用 Windows process identity/PID reuse 防护系统。
- [ ] 将可独立运行的 Updater 复制到安装目录外并通过 `--probe` 确认可启动，再传递已有生命周期信息；启动成功后 GUI 真正退出且无第二次退出确认。
- [ ] 接管失败或清理后仍不满足安全替换条件时保留旧安装并报告；不承诺恢复已关闭的任务和登录态。
- [ ] 连接既有 ReadyToInstall、PreparingRuntime、LaunchingUpdater 与独立 Updater 阶段，重复点击不产生并行安装。
- [ ] 记录安装开始、正常/受控强制 shutdown 结果、Updater 启动与交接失败；保留 ADR 0012/0014/0021 既有不变量。
- [ ] Windows 实机手工验证空闲和 Active Run 安装、Preview/Worker/Session/游戏/Agent 清理、TEMP 接管、重启及无关进程不受影响。
- [ ] 按现有自检与构建做回归；手工场景未执行时明确待验证，不为它们新增 Worker/RDP/Game mock framework。

## Blocked by

- 02：从 Update Drawer 下载并预验证完整更新包。
- 03：独立 Updater 保留用户数据并完成目录替换与回滚。

## Scope guard

不改动已验证 RDP/COM/WTS 的底层行为，不增加 Worker 更新 IPC 或保留运行中 Session 的热更新。
