---
status: ready-for-agent
labels: [ready-for-agent]
issue-source: local-docs/issues
---

# 04: 安装前关闭活动运行环境

**Parent:** [MaaNOP Updater V2 规格](../maanop-updater-v2-spec.md)

**What to build:** 用户在已有 Worker/Child Session 或 Active Run 时也能确认安装，GUI 先通过既有生命周期关闭运行环境，
确认结束后进入 03 的安装链路；停止失败时仍保留程序文件并明确反馈。

**Blocked by:** [03: 在无运行环境时完成安装与重启](03-idle-install-relaunch.md)。

**Status:** ready-for-agent

## Acceptance criteria

- [ ] 已有运行环境时可进入安装确认；Active Run 情况明确告知任务将停止。取消确认不停止任务、Preview 或 Session。
- [ ] 确认后通过既有应用操作门禁止新的 Run/运行环境操作，等待在途创建/启动结束后再判断最终运行态。
- [ ] 复用既有 Run Stop、Preview cleanup、Worker 生命周期和 Child Session 注销路径；
      不把 Stop ACK 等同于执行停止，不把 IPC 断开等同于进程退出。
- [ ] 确认 Worker/Child Session 及其相关游戏/Agent 运行环境结束后才请求 install；不能确认则取消安装，
      不修改程序文件，不擅自终止无关用户进程。
- [ ] 只沿用既有受控清理能力，不重写已验证的 WTS、RDP ActiveX、Task Scheduler COM 或进程 Session 验证流程。
- [ ] 移除 03 对已有运行环境的临时入口限制；运行环境已结束后，使用同一个 install interface 和实际副本 ready，
      不新增第二套安装器或不同的文件替换逻辑。
- [ ] GUI 不提前退出；运行环境关闭失败、Engine 启动失败或 ready 前检查失败有清楚反馈，
      不承诺自动重建已关闭的 Run/Session，不恢复半释放界面继续执行。
- [ ] Engine 不理解 Worker IPC、Stop 或 Session 注销；GUI 仍不接管包验证、自复制、缓存或文件安装职责。
- [ ] 通过现有 GUI/运行环境验证 seam 覆盖确认取消、禁止新操作、等待在途操作、停止失败不交接和 ready 后退出；
      不新增 Worker/RDP/Game mock framework。
- [ ] 受影响 GUI/Worker 构建及既有自检通过，记录关闭与 handoff 的验证结果，
      未完成的真实游戏/Session 交互场景明确留待 05 验收，不把自动化当作实机通过。
- [ ] 清除本片替代的旧运行环境安装编排，GUI 仅保留 UI、偏好、消息适配和既有生命周期职责。
      03 已实现的文件失败、最小 UI 和清理顺序继续有效。

## Scope boundary

本片是“已有运行环境 → 已关闭 → V2 安装”的垂直扩展，不修改运行协议或增加通用进程管理机制。
03 已拥有 install 全部行为；05 只负责正式产物集成及真实升级验收，不承担本片功能补齐。

本次仅发布工单，未开始实现。
