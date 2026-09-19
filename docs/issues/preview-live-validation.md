# 连续 Preview 实现与验收记录

日期：2026-09-19。对应 [父规格](preview-live-spec.md) 与 [开发工单](preview-live-tickets/README.md)。

## 当前结论

代码已接入，GUI/Worker 构建和 C# 自动检查通过；尚不能认定正式实机验收完成。
用户明确要求暂缓实机测试，因为现有完整包正在运行其他任务。本轮没有替换运行包、停止任务或重启分身。
早期独立 Demo 的流畅度反馈不替代本次生产实现的验证。

## 实现范围

- 现有 Worker 内独立只截图 Controller，使用 MaaNOP Launch Context 中的截图方式，任务执行对象不再持有预览。
- 34 ms 最高提交间隔、最多一次原生截图在途，最大 640×360 BGR32；慢截图不补发积压采集。
- 协议 2 的 start/renew/stop，2 秒续订、6 秒租约；像素经固定容量文件映射与 Global Mutex 传输。
- 无任务也可预览；窗口低频发现、关闭清空与重开恢复、暂时截图失败保帧。
- GUI 可见性决定订阅，隐藏 RDP 宿主不决定订阅；断线或身份失效后丢弃旧订阅。
- GUI 两个固定像素缓冲与一个在途 Dispatcher 回调，显示前检查目标代次；显示操作在采样锁外。
  WriteableBitmap 按尺寸复用，卡片与放大层共享，不另采高清。
- 文件与 Mutex 限当前用户/System；旧文件仅在确认进程所有者不再存活时清理。

## 已执行验证

| 验证 | 结果与边界 |
| --- | --- |
| GUI、Worker Release 构建 | 通过，0 警告、0 错误 |
| GUI、Worker self-contained publish | 通过，输出在仓库 `artifacts/preview-live-validation/`；未部署 |
| GUI 自检 | 通过，包含原有配置、协议、日志恢复及新预览检查 |
| Worker 自检 | 通过，保留既有 Stop/清理回归并新增预览测试 |
| 双进程真实映射 | 通过，逐像素检查多个完整 640×360 帧；两个进程位于同一 Session |
| 真实 Worker Host 协议入口 | 通过，Named Pipe → ServeConnectionAsync → 请求处理 → 预览服务 → 真实映射；原生帧源可控 |
| 阻塞截图下控制响应 | 通过，Snapshot、renew、run.stop ACK 与 Stopping；预置 Run/Execution，未运行真实 MaaTasker |
| 窗口与订阅生命周期 | 可控帧源通过无窗口、首帧、暂时失败保帧、关闭、重开、旧 stop、阻塞时切订阅、断线和租约到期 |
| 慢 GUI | 排队回调与实际显示回调分别阻塞 6.5 秒，续订继续且只有一个回调；100 次更新合并为最新帧 |
| UI 提交前目标失效 | 读帧后清空映射，未再次后台采样时显示仍拒绝旧目标；Reset 后不再呈现旧帧 |
| 帧边界与失效 | 最大帧、超界尺寸、清空代次、错误 Worker 控制响应、abandoned Mutex 拒绝通过 |
| C# 更新客户端测试 | 完整自动化脚本中通过全部现有输出项 |
| 完整自动化脚本 | GUI、Worker、C# 更新测试通过后，在调用 cargo 时失败；不能标记整套通过 |
| Rust tests / Clippy | 未运行：当前 PATH 和默认 `.cargo/bin` 均无 cargo |
| 标准打包脚本 | 在 Rust 引擎构建前失败，同上；独立 C# publish 不等价于完整包集成 |
| 两路代码审查 | Standards 与 Spec 复查通过；发现的慢 UI 锁、旧帧提交与真实协议测试缺口已修正 |
| 风格与 diff | 新增/修改的 C# 行不超过 120 字符；diff 空白检查通过；未格式化既有无关超长行 |

首次测试中的真实协议 fixture 使用 undefined JsonElement、双向 Pipe 未持续读取事件导致阻塞，
均已修正；最终完整脚本中的 GUI/Worker 自检通过。一次并行运行的双进程测试读帧超时，
随后隔离复跑及最终串行完整脚本通过，尚无足够证据将其归因于产品缺陷或宣称高负载稳定性。
原生截图资源、RDP 负载与跨 Session 权限仍必须以实机结果判断。

## 后续实机验收

1. 恢复 Rust 工具链，运行完整构建、Rust tests 与 Clippy；生成同版完整包并核对集成目录。
2. 待用户现有任务结束，在实际 MaaNOP 包中启动同版 GUI/Worker，记录版本、后端和 Session/PID。
3. 按 01–04 验证启动等待、任务前/中/等待/终态、游戏关闭重开/最小化、GUI 可见性、隐藏分身及重连。
4. 固定动画与后端，执行预览关闭→开启→关闭对照，记录 GUI/Worker/游戏及可观察 DWM/RDP 资源和停止响应。
5. 至少 30 分钟连续可见预览，检查预热后内存、原生 job 状态、句柄和关闭后的回收；增长则继续定位。
6. 用户确认观感并记录所有缺测项后，才关闭对应工单；不设置最低 FPS 或 CPU/内存数值门槛。

实机包目录由用户提供：`D:\Automation Script\MaaNOP-win-x86_64-v2.4.0`。
本次未向该目录写入验收产物，也未发布 Release。
