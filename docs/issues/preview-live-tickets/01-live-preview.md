---
status: in-progress
labels: [in-progress, awaiting-validation]
issue-source: local-docs/issues
breakdown-confirmed-on: 2026-09-19
---

# 01: 从分身 Worker 到主桌面显示连续游戏画面

**Parent:** [连续游戏画面 Preview 规格](../preview-live-spec.md)。

**What to build:** 游戏窗口已存在、运行环境已就绪时，用户无需启动任务，即可在主桌面首页和现有放大层
看到分身 Worker 连续采集的游戏画面。交付真实截图、跨 Session 传输和 GUI 显示的完整路径。

**Blocked by:** None (can start immediately).

**Status:** in-progress — 代码已接入；同 Session 自动检查通过，跨 Session 实机演示待用户任务结束。

验证证据与缺测项见 [验收记录](../preview-live-validation.md)。

## Acceptance criteria

- [x] 现有 Worker 创建独立只截图 Controller，使用已接纳 MaaNOP 配置并验证窗口所属 Child Session；
      不创建额外进程、Tasker、Resource 或 Agent，不启用输入。
- [x] 预览不依赖 Active Run；任务保留自己的 Controller。移除旧 execution 内的预览采样与其清理依赖，
      不改变任务执行、Stop 确认或既有资源清理语义。
- [x] GUI/Worker 协议同时升级为 2，固定 Pipe 名称不变；建立 start/renew/stop 契约，
      基本续订为 2 秒、租约为 6 秒，重复操作幂等；不保留旧 PNG 预览回退路径。
- [x] 使用受限权限的随机临时文件支持映射和 Global Mutex，单映射容量 925696 字节；
      头部携带当前 Worker、Session、订阅、目标代次与帧信息，GUI 只读打开。
- [x] 从第一片就落实固定容量、尺寸/长度/身份校验、完整提交及 Mutex 非阻塞取得，
      不以“后续加固”为由临时允许撕裂、无界分配、越权路径或慢消费者阻塞。
- [x] 最多一帧采集在途，最高约 30 fps，慢采集丢过期时隙；输出最大 640×360、保持比例的 BGR32。
      仅发布最新帧，不在主控制 Pipe 内发送像素，不逐帧 PNG/base64 编解码。
- [x] GUI 复用 WriteableBitmap，待办显示最多一帧，卡片与放大层共用；不新增高清或 FPS 配置。
- [x] 基本取消先失效发布权限，在途读取/截图退出后才释放其资源；正常停止后关闭映射并清理文件。
      可见性、断线、租约等复杂交错由 03/04 补齐，但本片不得并发 Dispose 在途资源。
- [ ] 在用户确认的 GUI–Worker 通信入口运行真实预览服务与真实映射，以可控原生适配器验证 Idle 首帧、
      连续完整帧、最大合法帧、非 16:9 比例、错误身份/长度拒绝和慢显示不积压。
- [ ] 实际双进程传输测试通过；GUI/Worker 构建与受影响自检通过。真实 Child Session 中演示已打开游戏的
      首页连续预览，明确记录包版本、Session 与结果，不以同 Session 自检代替该演示。

## Scope boundary

已有有效窗口的真实闭环是本片交付；首次等窗口、关闭重开和失败恢复由 02 完成，完整可见性及重连由 03，
任务并行与阻塞采集交错由 04，长期与全量实机验收由 05。必要的窗口匹配提取和测试适配先在本片小范围完成，
不单列横向框架或预重构工单。
