# NarutoAutoGUI

面向 [MaaNOP](https://github.com/ArcherSore/MaaNOP) 的 Windows 图形界面。

通过 Windows Child Session（桌面分身）在独立会话中运行火影忍者 Online 和自动化任务，在当前桌面完成任务配置、运行控制与状态查看。

> 使用游戏自动化，请前往 [MaaNOP](https://github.com/ArcherSore/MaaNOP) 获取完整包和使用说明。本仓库发布前端及配套运行组件，不包含游戏任务资源。

## 界面预览

![NarutoAutoGUI 首页](docs/images/homePage.png)

## 功能

- **桌面分身**：创建和管理独立运行环境，隐藏分身后任务仍可继续。
- **任务配置**：支持多份独立任务配置，通过 Tab 切换，每份配置分别保存任务列表、执行顺序和参数。
- **运行监控**：展示任务动态和游戏画面预览，支持放大查看。
- **完整包更新**：提供更新检查、版本说明、下载与安装入口。

## 构建

需要 Windows x64、.NET 10 SDK、Rust 1.98.1 MSVC 和 Visual Studio C++ Build Tools。

```powershell
.\src\NarutoAutoGUI\scripts\build.ps1
```

产物位于 `artifacts\NarutoAutoGUI\win-x64`。

自动自检：

```powershell
.\src\NarutoAutoGUI\scripts\test-automated.ps1
```

详细说明见 [开发文档](src/NarutoAutoGUI/README.md)与[架构设计](docs/ARCHITECTURE.md)。

## 鸣谢

- [MaaNOP](https://github.com/ArcherSore/MaaNOP)：游戏自动化任务与资源。
- [MaaFramework](https://github.com/MaaXYZ/MaaFramework)：自动化执行框架。
- [BetterGI](https://github.com/babalae/better-genshin-impact)：桌面分身方案参考了其 v0.63.0 引入的 Windows Child Session 实现。
