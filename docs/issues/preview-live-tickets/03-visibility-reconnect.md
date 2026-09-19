---
status: in-progress
labels: [in-progress, awaiting-validation]
issue-source: local-docs/issues
breakdown-confirmed-on: 2026-09-19
---

# 03: 不可见时停采，返回或重连后恢复预览

**Parent:** [连续游戏画面 Preview 规格](../preview-live-spec.md)。

**What to build:** 用户离开首页或将 GUI 隐藏、最小化后，预览停止消耗采集资源；返回后自动恢复。
仅隐藏桌面分身时继续预览。GUI 断线再连不会显示旧画面，也不会影响运行中的任务。

**Blocked by:** 01 — 从分身 Worker 到主桌面显示连续游戏画面。

**Status:** in-progress — 可见性与租约路径已接入；实际 GUI 显隐和同 Worker 重连待验收。

验证证据与缺测项见 [验收记录](../preview-live-validation.md)。

## Acceptance criteria

- [x] 订阅严格要求环境启动完成、当前 Worker admission、Ready、Fresh Snapshot 及 GUI 首页可见。
      任务配置切换或 Active Run 是否存在不作为预览启停依据。
- [x] GUI 隐藏、最小化或离开首页立即清空并停止消费，撤销订阅；返回时使用新订阅等待新帧。
      卡片与放大层仍共用一个订阅，仅隐藏 RDP 宿主不撤销订阅。
- [x] start/renew/stop 重试幂等；旧订阅 renew/stop 不影响新订阅。约 2 秒续订、6 秒租约到期停采，
      租约使用单调时钟，只控制 Preview，不退出 Worker 或停止 Run。
- [x] IPC 断线立即清空；同一 Worker 重连后先获取 Fresh Snapshot 再重新订阅。
      Worker/Session 身份失效同样清空，不重用旧映射和旧订阅。
- [x] 处理取消后迟到响应、新旧连接交错、尚未准备好或正在回收的描述符，不能误断主 Pipe 或重复 producer。
- [x] 映射与 Controller 回收不阻塞控制 ACK；GUI 视图与 Worker 资源在使用者退出后安全释放。
      反复显隐/重连最多保留一个正在回收的采集实例，不积累映射、句柄或定时器。
- [x] 专用目录残留只在确认所有者进程身份已退出时清理；不按年龄删除，不触碰存活环境资源。
      文件或 Mutex 权限失败仅隔离预览，不回退高频主 Pipe 传图或放宽权限。
- [ ] 通信测试覆盖可见性、租约到期、重复操作、旧订阅、断线、重连和旧 Worker 帧拒绝；
      真实显示/隐藏及同 Worker 重连演示通过，相关构建与自检通过。

## Scope boundary

本片可在固定已打开窗口上独立验证，因此不依赖 02。涉及窗口重建与订阅切换的交错由 04 汇合验证。
不修改 Child Session、RDP、Task Scheduler 或 Worker admission 的现有生命周期。
