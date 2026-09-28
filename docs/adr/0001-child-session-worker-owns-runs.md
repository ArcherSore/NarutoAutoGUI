# Child Session Worker 持有 MaaNOP Run

MaaNOP 自动化由 Child Session 中的 Worker 执行并拥有权威 Run 状态，主 GUI 只负责提交意图和观察。若由 GUI 或 MFAAvalonia 直接持有任务，主桌面关闭、IPC 断线或 UI 生命周期就会牵连游戏自动化；独立 Worker 增加了进程通信和接纳成本，却让运行在 GUI 重启后仍可继续观察。此保证止于 Worker 进程退出，不承诺持久化作业恢复。
