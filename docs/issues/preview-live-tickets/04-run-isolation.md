---
status: in-progress
labels: [in-progress, awaiting-validation]
issue-source: local-docs/issues
breakdown-confirmed-on: 2026-09-19
---

# 04: 预览故障与恢复不妨碍任务运行和停止

**Parent:** [连续游戏画面 Preview 规格](../preview-live-spec.md)。

**What to build:** 用户运行真实任务时，等待与任务终结前后持续预览；即使 GUI 消费变慢、截图卡住或
窗口/连接正在恢复，也仍能停止任务，且不会混入旧帧或积累未释放采集实例。

**Blocked by:** 02 — 游戏窗口出现、关闭与重开时自动恢复预览；
03 — 不可见时停采，返回或重连后恢复预览。

**Status:** in-progress — 真实 Host 协议的阻塞截图/Stop ACK 与慢 UI 检查通过；原生任务实机待验收。

验证证据与缺测项见 [验收记录](../preview-live-validation.md)。

## Acceptance criteria

- [ ] 真实任务开始、等待、停止、失败及自然完成前后预览保持独立，不因 Plan Item 交接重置采集。
      不改变任务 Controller、Tasker、Resource、Agent 的既有创建、停止确认和释放顺序。
- [x] 人为阻塞一次原生截图或 GUI 消费后仍能获取 Snapshot、处理续订及接受 run.stop；
      Stop ACK 不冒充 Cancelled，实际终态继续以原有任务停止确认决定。
- [x] 截图未返回期间不 Dispose 其 Controller、不强杀线程、不重启 Worker；
      显隐、租约失效、窗口关闭、重连的交错最多留一个待回收实例。
- [x] 解除阻塞后只响应仍有效的最新订阅与目标；失效调用不发布，不因每次重试再建一个资源集合。
- [ ] 校验写到一半、Mutex abandoned、映射失效、读后 UI 提交前失效：不能显示撕裂或旧画面，
      不能连带改变 Run、Worker 健康状态或主控制连接。
- [x] 预览错误诊断限频且不进入 MaaNOP 用户运行日志；慢消费者不积累 Dispatcher 回调或帧队列。
- [x] 停止预览不成为退出、注销分身或 Updater 安装交接的阻塞前置；已有环境关闭与进程验证语义保持。
- [ ] 使用真实协议处理、预览服务和映射验证上述外部行为；可控截图适配器只替代原生环境，
      不能用假 Worker 按期望回消息来宣称任务隔离通过。
- [ ] 真实 Child Session 中完成任务运行、等待、停止和任务结束后预览演示；
      保留已有 Success、Cancellation、取消后 Worker 存活与复用 baseline 的回归证据。
- [x] GUI/Worker 构建、相关自检及既有 Stop/清理回归通过；对失败先修正再记录，不把仅自动测试当实机证据。

## Scope boundary

本片交付组合行为与所需修正，不单列横向“补测试”或通用容错框架。02 与 03 的真实功能在此合并验证，
因此依赖两者；整体验收和持续资源观测由 05 完成。
