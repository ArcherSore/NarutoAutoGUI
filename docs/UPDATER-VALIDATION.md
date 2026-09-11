# Updater V1 验证

> 对应目标规格：[`docs/issues/maanop-updater-v1.md`](issues/maanop-updater-v1.md)。当前结论是“Updater baseline 已实现，完整 MaaNOP 包集成和 Windows 实机 E2E 待完成”，不是完整产品升级已验收。

## 实现对照摘要

- 已实现 baseline：Project Interface 的 `github/version` 更新源、正式 Release/精确 asset 选择、流式下载、SHA256、ZIP 路径检查、staging、config/logs 保留、完整目录 swap/rollback、GUI Banner/Drawer/Settings、TEMP Updater 和一次性完成提示。
- 部分实现：GUI 用现有 Worker/Child Session 生命周期完成运行态关闭，TEMP Updater 只等待 GUI 退出；Updater 阶段目前通过窗口短文本和日志表达，没有独立状态机。
- 待补齐：MaaNOP 完整包需要实际提供 `interface.json`、resource、agent、内置 Python 和有效 GitHub digest；当前 NarutoAutoGUI baseline 产物不包含这些跨仓库内容。运行时预验证目前覆盖关键路径和 `name/version`，完整 Project Interface 形状及 resource/agent 内容仍应作为最终集成门槛补强。
- 待验证：真实 TEMP Updater 接管、Active Run/Preview/Child Session/游戏/Agent 关闭、目录替换后的新版重启、完成 Banner 和连续两次更新。

## 自动化边界

Updater 测试通过更新模块入口使用可控 HTTP 和真实临时目录，不创建 Worker/RDP/Game mock framework。
覆盖 SemVer、Release/asset 选择、下载/SHA256、ZIP 路径与包结构、config/logs 保留及目录 swap/rollback。
使用 `src/NarutoAutoGUI/scripts/test-automated.ps1` 运行 GUI、Worker 与 Updater 自动化。

Windows x64 baseline 发布脚本同时生成可独立运行的单文件 NarutoAutoUpdater.exe，放在发布根目录。
该 baseline 仍不是 MaaNOP 完整包；不得用它自身作为 updater 的目标 Release ZIP。

## 完整包集成前置条件

MaaNOP Windows x64 发布流程需要消费包含 Updater 的新 baseline，保留 GUI/Worker/native runtime 布局，
并包含 resource、agent、内置 Python 与根 Project Interface。Project Interface 必须包含 name=MaaNOP、
github 仓库地址，以及 CI 根据目标 Release tag 写入的 version。资产名称必须精确为
`MaaNOP-win-x86_64-<tag>.zip`，GitHub asset 必须有有效的 sha256 digest。

更新 API 采用 [GitHub 最新正式 Release](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)，
摘要取自 [Release Asset](https://docs.github.com/en/rest/releases/assets#get-a-release-asset)。
没有发布独立 SHA256 sidecar 或增加自定义服务。

首次含 Updater 的完整 MaaNOP Release 必须手动安装一次。本仓库构建不创建 MaaNOP tag/Release，也不自动修改其 baseline pin。

## Windows 手工 E2E（必须单独记录）

使用两个真实完整包与隔离安装目录；记录版本、baseline、Windows 版本、日志和实际结果。
未执行的步骤不能因为自动化通过而标记通过。

1. 使用已含 Updater 的完整旧包启动，检查当前版本、启动检查开关、手动检查和网络失败隔离。
2. 从 Home 打开 Drawer，确认长 Release Notes 滚动、关闭/Esc/键盘焦点以及最小窗口与多 DPI 布局。
3. 主动下载，观察真实进度，取消并重试；校验成功且包结构验证通过后才出现安装按钮。
4. 空闲安装：取消不产生运行态关闭；确认后 TEMP Updater 小窗口接管、原路径替换并自动启动新版。
5. Active Run 安装：先正常 Stop 与 Preview cleanup；必要时按既有 Session 注销路径受控清理，
   确认 Worker、Child Session、Naruto Online/Agent 已结束，无关进程继续运行。
6. 验证正常停止失败但受控清理成功的安装；验证最终无法确认退出/文件释放时安装中止，旧目录不变。
7. 验证 TEMP Updater 启动前检查失败不关闭运行环境；包括系统 Application Control 拒绝运行新 EXE 的情形。
8. 验证 config 全部旧内容和 logs 旧内容保留，新版删除的程序文件消失，Updater 自身可被替换。
9. 确认新版首启 Home 完成 Banner 和对应 Release Notes；后续启动不重复首次完成提示。
10. 再更新一次，确认最多一个 MaaNOP.old；已有备份无法安全删除时旧安装保持不变。
11. 回归 Child Session 创建/恢复、显示/隐藏、结束与托盘退出 baseline。

系统保护策略可能暂时阻止新 EXE 启动。此情形不能算安装或重启已验证；由用户重启后再执行手工 E2E，
不得为测试关闭保护策略。TEMP Updater 的 `--probe` 只检查旧 Updater 能否启动，不是新版运行健康协议。

## 限制

只做目录事务失败回滚；不保证任意断电时刻恢复，不进行运行健康检查、A/B 或健康回滚。
旧备份最多一个，可留到下次事务前清理。更新完成记录仅用于一次性 Home 提示，不影响备份清理策略。
缓存和偏好按原安装路径存放于 LocalAppData，config 用户内容完全保留；不迁移已关闭运行环境的 admission。
