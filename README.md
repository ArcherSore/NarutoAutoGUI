# NarutoAutoGUI

NarutoAutoGUI 是 MaaNOP 的 Windows GUI，通过独立 Windows Session 运行火影忍者 Online 自动化，使游戏可以在后台持续运行，而不长期占用当前桌面和鼠标。

## 界面预览

Dashboard 页面：

<img src="docs/images/homePage.png" width="80%" alt="Dashboard" />

Tasks 页面：

<img src="docs/images/taskPage.png" width="80%" alt="Tasks" />

## 快速开始

1. 前往 [MaaNOP Releases](https://github.com/ArcherSore/MaaNOP/releases) 下载完整的 Windows x64 ZIP。
2. 解压整个 ZIP，不要只复制其中的可执行文件。
3. 确保已经安装过 **火影忍者 Online 微端**！！！当前仅支持 **微端**！！！QQ游戏大厅、360游戏大厅等均不行！！！
4. 运行 `NarutoAutoGUI.exe`，并在 Windows 提示时允许管理员权限。
5. 在首页点击“准备运行环境”。
6. 在打开的完整桌面中完成必要的游戏登录。
7. 在“任务”页选择任务并配置参数，然后回到首页开始任务。

NarutoAutoGUI 会从完整发布包中自动读取 `interface.json`，并自动确定 QQMicroGameBox 启动器路径、启动参数和 AppId

## 运行要求


### 系统与环境
- **操作系统**：Windows 10 / Windows 11（x64）
  - **支持版本**：家庭版（Home）、专业版（Pro）、企业版（Enterprise）、教育版（Education）全版本支持。
  - **权限要求**：管理员权限（启动时需允许 UAC 弹窗提示，用于启用系统 Session 接口）。
- **游戏客户端**：必须安装并能正常运行 **火影忍者 Online 微端**。

> [!NOTE]
> **关于 Windows Child Session（桌面分身）支持说明**
>
> 很多人常误以为“Windows 家庭版不支持远程桌面”，实际上家庭版仅限制了外部网络的“远程桌面服务端接入”。
> 本项目所使用的 Child Session（子会话）是微软自 Windows 8 / Server 2012 起内置的**本地回环技术（Loopback Session）**。根据[微软官方文档 (Child Sessions)](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions)，子会话无需普通远程桌面的远程交互权限，原生支持包括**家庭版在内的所有 Win10/Win11 桌面版本**。本项目完全调用 Windows 原生 API（`wtsapi32.dll` 与 `MsRdpClient10`），**无需**安装 RDP Wrapper 或修改系统组件。

### 开发者本地构建

完整构建需要 Windows x64 和 .NET 10 SDK。在仓库根目录运行：

```powershell
.\src\NarutoAutoGUI\scripts\build.ps1
```

发布结果位于 `artifacts\NarutoAutoGUI\win-x64`。无需 UAC、RDP 或真实游戏的自动自检：

```powershell
.\src\NarutoAutoGUI\scripts\test-automated.ps1
```

## 工作方式

```text
Main Windows Session
└─ NarutoAutoGUI

Child Session
└─ NarutoAutoWorker
   └─ 火影忍者 Online
      └─ MaaFramework / MaaNOP
```

NarutoAutoGUI 留在当前桌面负责配置、预览和控制；Worker、游戏与自动化流程运行在独立 Child Session 中。隐藏完整桌面或主窗口不会结束后台任务。

详细技术设计见 [架构文档](docs/ARCHITECTURE.md)。当前能力与后续方向分别记录在 [STATUS](docs/STATUS.md) 和 [ROADMAP](docs/ROADMAP.md)。正式 GUI 的开发说明见 [src/NarutoAutoGUI/README.md](src/NarutoAutoGUI/README.md)。

## 鸣谢

- [BetterGI (Better Genshin Impact)](https://github.com/babalae/better-genshin-impact)：本项目核心的 Windows Child Session（桌面分身 / 独立后台会话）功能借鉴并参考了 BetterGI `v0.63.0` 版本引入的方案与原生接口实现，在此对原作者及开源社区表示诚挚感谢！