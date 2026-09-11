# 使用安装目录外的 Updater 替换完整 MaaNOP 包

MaaNOP 更新必须同时替换 GUI、Child Session Worker、固定 MaaFramework runtime、资源、Agent 和 Python 等协同发布内容。直接覆盖正在运行的安装目录既不能可靠删除旧版文件，也可能因为 GUI 或 Updater 自身持有文件句柄而留下半更新状态。

## Decision

NarutoAutoGUI 只把更新目标定义为 MaaNOP Windows x64 完整 Release ZIP。GUI 在安装目录外缓存并验证目标包，将用户拥有的 `config` 和 `logs` 子树复制到同卷 staging；确认运行环境关闭后，从临时目录启动独立的 `NarutoAutoUpdater`，由它等待 GUI 退出，执行“旧安装目录 → `MaaNOP.old`、staging → 原路径”的目录交换，并在第二段交换失败时尝试恢复旧路径。

Updater 日志写在临时目录，避免目录交换被自身文件句柄阻塞；成功后从原路径启动新版，并保留最多一个旧备份。V1 不引入 health marker、启动健康握手、A/B 版本或运行健康回滚；启动失败必须记录为失败并保留可恢复备份。

更新源使用 Project Interface 提供的 GitHub 仓库和 MaaNOP 产品版本，消费 GitHub 最新正式 Release 的精确 Windows x64 asset 及其 SHA256 digest。更新元数据无效只使更新不可用，不改变正常任务配置和运行链路。

## Consequences

- 完整包发布保持 GUI、Worker 和运行时的版本组合，旧版已删除的程序文件不会因覆盖安装残留。
- 原安装路径和快捷方式保持不变，`config`/`logs` 的用户内容不被发布包默认内容覆盖。
- 目录事务具备有限的失败恢复能力，但不能承诺任意断电恢复、启动健康验证或自动健康回滚。
- 首个包含 Updater 的 MaaNOP 完整包仍需用户手动安装；本仓库 baseline 的构建、自检和 Updater 自动化不能替代 MaaNOP 跨仓库包集成及 Windows 实机 E2E。
