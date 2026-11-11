# Worker 持有 Run 遥测，GUI 持有应用级遥测与用户开关

MaaNOP 在 Project Interface 中声明 telemetry.sentry 后，Run 的事务、节点结果和失败证据由 Child Session 中的 Worker 直接上报；GUI 只上报自身崩溃与应用 Session，并持有“帮助改进软件”开关和发布闸门，只把已通过闸门的配置与开关下发给 Worker。MXU、MFAAvalonia 在单个进程内同时持有 MaaFramework 回调和 Sentry SDK，而这里回调、maafw.log 与失败截图都在 Worker 一侧，Run 也会在 GUI 断线或重启后继续（ADR 0001）；若经 Pipe 把遥测转发给 GUI，就要在可丢失的事件之上再造可靠投递，与以完整 Snapshot 恢复观察的原则（ADR 0005）相冲突。代价是两个进程各持一份 SDK，并遵守同一套匿名身份、发布和隐私规则；Worker 还需要知道本次 Run 的密码值才能脱敏。遥测始终是旁路，任何遥测失败都不得改变 Run、Worker 或运行环境的生命周期。
