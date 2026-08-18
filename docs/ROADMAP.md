# Roadmap

## 已完成：第一轮正式 GUI

1. WPF 主窗口、托盘和统一日志。
2. Child Session 创建/恢复、显示/隐藏、状态展示和安全注销。
3. 配置并分别启动游戏/MaaNOP，以及幂等的一键启动挂机环境。

## 下一步：先稳定当前 baseline

4. 完成并记录正式 GUI 的 Windows 交互式回归。
5. 根据实机结果修复第一轮 GUI 的明确缺陷，不改变已验证的 RDP/COM/WTS baseline。
6. 改善状态呈现和可诊断性，仅增加已被实际回归需要证明的内容。

## 后续阶段（需单独立项）

7. 读取 MaaNOP `interface.json`，在主桌面选择任务和参数。
8. 设计 Child Session Worker 与 IPC。
9. 直接集成 MaaFramework，逐步替代 MFAAvalonia。

自动登录/扫码、自动隐藏子桌面、自动开始 MaaNOP 任务和可调分辨率/DPI 不属于第一轮，也不会在未单独确认范围前实现。
