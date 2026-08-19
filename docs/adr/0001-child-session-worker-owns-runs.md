# Child Session Worker 独占并持有 MaaNOP Run

NarutoAutoGUI 的正常执行链路固定为主 GUI → Child Session Worker → MaaFramework → MaaNOP，MFAAvalonia 仅作为人工诊断后备且不得与 Worker 并行执行。Worker 是运行状态的唯一真相来源：接受带唯一 Run ID 的启动后，即使主 GUI 崩溃或本机 IPC 断开也继续执行并保留有界日志，重连后通过 Run Snapshot 恢复观察；停止当前 Run 不退出 Worker、游戏或 Child Session。该保证不跨 Worker 进程退出。显式结束桌面分身时，有活动 Run 且 IPC 可用则先发出 `run.stop` 并做有界等待；若停止未确认，GUI 明确警告后仍按用户结束整个环境的意图继续 WTS Logoff。只有 WTS Logoff 失败才保持结束错误状态。
