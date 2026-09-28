# Preview 独立于活动任务

游戏画面预览在运行环境可用时也可用于任务前、等待及任务结束后，因此不依附 Active Run 或任务 Controller。Worker 使用独立的只截图实例，并把连续像素帧与任务控制 Pipe 分离；这增加资源与同步成本，却避免预览拖慢控制消息或牵连任务释放流程。Preview 始终只读，其失败不得改变 Run 或 Child Session 生命周期。
