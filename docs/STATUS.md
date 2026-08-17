# Status

## 当前阶段

仓库初始化与已验证 Child Session PoC 迁移阶段。当前目标是保存一个可构建、可重复手动验证的 baseline，不进行正式 GUI 产品化。

## 已完成并验证

以下能力已在迁移前的 Windows 实机 baseline 中验证成功，并在迁移后的目标仓库中完成交互式复验：

- .NET 8 Windows x64 自包含发布。
- 创建、连接、预览和注销 RDP Child Session。
- Child Session 固定 `1920×1080 @ 100%`；预览 SmartSizing 不改变子桌面尺寸。
- 获取 `childSessionId`。
- 在 Child Session 中启动并验证 `notepad.exe`。
- 在 Child Session 中启动并验证 MFAAvalonia。
- MaaNOP、游戏与 MFAAvalonia 可在 Child Session 中运行，且不影响主桌面操作。
- 关闭预览窗口后断开并清理 Child Session。

本轮只迁移原 PoC 和项目文档。迁移后的构建以及涉及 RDP、UAC 和可见桌面的交互式端到端验证均已完成。

## 已知限制

- 只支持 Windows x64，并依赖系统 RDP ActiveX、WTS API、Task Scheduler COM 和 WMI。
- 需要管理员权限和交互式桌面，无法由普通无头 CI 完整验证。
- RDP ActiveX 连接必须保持存活；关闭预览窗口会终止 Child Session 内程序。
- Windows Hello/PIN 不能保证可复用账户密码；必要时需提供真实账户密码。
- 当前默认 MFAAvalonia 路径是本机 PoC 路径，不是产品配置系统；其他机器应使用 `--exec`。
- TermService 回环状态异常时，可能需要重启 Windows。
- WMI 枚举仅用于验证；验证超时不会主动销毁已经建立的 Child Session。

## 最近一次可用 baseline

- 来源：`MaaNOP/launcher/MaaNOP.ChildSessionLauncher` 已验证 PoC。
- 迁移目标：`src/ChildSessionDemo`，保留原运行逻辑。
- 迁移后构建：2026-08-17 已在目标仓库路径通过 Release `win-x64` build 和 self-contained publish，0 个警告、0 个错误。
- 迁移后交互式实机测试：2026-08-17 已完成，现有 Child Session baseline 在目标仓库中复验通过。
