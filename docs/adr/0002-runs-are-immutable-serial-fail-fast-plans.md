# MaaNOP Run 采用不可变、串行、fail-fast 的计划

一个 MaaNOP Run 对应一份开始后不可修改的有序 Run Plan，每个 Plan Item 用独立 ID 冻结顶层 task、entry 和 option/参数快照；Worker 同时只接受一个活动 Run，不排队或覆盖。首版按 Project Interface 声明顺序串行执行且禁止重复 task，任一 Plan Item 失败即令 Run 失败并跳过余项。显式停止按匹配的 Run ID 将 Run 置为 Stopping，立即取消所有未开始项，并在 MaaFramework 停止得到确认后把当时仍未终态的当前项和整个 Run 置为 Cancelled；完成与停止竞态由同一串行状态机决定，已在停止接受前进入终态的项保留真实结果。这样牺牲动态队列、并行和 continue-on-error，换取可重连、可幂等且由 Worker 唯一解释的确定状态。
