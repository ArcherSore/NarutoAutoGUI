---
status: archived-implementation-awaiting-interactive-validation
archived: 2026-09-11
---

# 03：独立 Updater 保留用户数据并完成目录替换与回滚

## What to build

独立 NarutoAutoUpdater 能从 TEMP 运行，对已预验证的隔离安装执行完整目录替换，在小窗口显示阶段，并从原路径启动替换后的 GUI。
本切片可用临时完整包验证安装事务；正式 GUI 的安装授权与运行态交接由后续工单接入。

## Acceptance criteria

- [ ] Updater 纳入 NarutoAutoGUI baseline 构建/发布和包布局检查，复制到安装目录外后可独立运行，不依赖被替换目录的 DLL/runtime。
- [ ] 消费已预验证的目标包与最小安装交接信息；限定目标、staging、备份的关系，不新增远程更新协议。
- [ ] 消费 GUI 使用现有已跟踪 GUI/Worker/Child Session 生命周期信息确认进程退出后的交接；TEMP Updater 等待 GUI 退出，IPC disconnect 不算退出。
- [ ] 目录事务前确认已跟踪进程退出且目标程序文件可安全替换；不增加通用 process identity/PID reuse 系统，不终止无关进程。
- [ ] 写入者退出后将 config/logs 全子树 copy 到 staging，保留用户内容而非 move；包内同名默认文件不得覆盖或擅自补入 config。
- [ ] 复制失败在目录交换前终止；不迁移旧 Worker admission，不保留未知程序文件或新版已删除的旧程序文件。
- [ ] 最多一个 MaaNOP.old，本次成功后可保留；下一次事务前清理已有备份，不能安全清理则不修改当前安装。
- [ ] 当前目录→旧备份、staging→原路径；首次失败保留当前目录，第二次失败尝试恢复旧路径，回滚失败保留可恢复副本并报告。
- [ ] 小窗口仅显示 indeterminate progress 和简短阶段；安装日志不因自身文件句柄阻止目录替换。
- [ ] 目录替换成功后启动原路径 GUI；启动失败记录并显示普通错误，不误报成功、不丢弃旧备份。
- [ ] 真实临时文件系统自动化覆盖 config/log 字节保留、程序文件集合、复制失败、两段 swap 失败、rollback 失败和连续两次更新的备份上限。
- [ ] 构建/发布验证 TEMP 独立运行所需布局；正式 TEMP Updater、窗口和真实 GUI 重启的手工证据在最终 E2E 工单记录。

## Blocked by

None (can start immediately).

## Scope guard

无需先开发新的进程管理平台或测试框架。隔离安装 fixture 仅用于目录事务测试，不模拟 Worker/RDP/Game。
不引入 health marker、健康握手、A/B 或健康回滚；不因启动确认增加备份清理协议。
