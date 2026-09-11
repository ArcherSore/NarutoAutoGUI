---
status: archived-implementation-awaiting-interactive-validation
archived: 2026-09-11
---

# 02：从 Update Drawer 下载并预验证完整更新包

## What to build

用户主动下载完整包，在 Drawer 观察真实进度、取消或重试；只有 SHA256 和安全解压后的包预验证均成功，才进入 ReadyToInstall。
本切片在安装目录之外完成，不停止运行环境、不执行安装。

## Acceptance criteria

- [ ] 仅点击“下载更新”才开始，流式写入安装目录外缓存，不整体载入内存；关闭 Drawer 不等于取消下载。
- [ ] 展示实际百分比、已下载/总大小、当前速度；取消删除半成品；断流、大小不符或 I/O 失败允许重新下载。
- [ ] 下载后计算 SHA256 并对比 Release Asset digest；缺失/无效/不匹配均不得安装，删除或废弃文件并允许重新获取。
- [ ] 在安装目录外 staging 安全解压，最终 staging 与安装目录同卷；跨卷缓存可使用，不依赖跨卷重命名。
- [ ] 拒绝绝对路径、父目录逃逸、盘符/UNC 注入及规范化越界；路径别名、链接/reparse point、冲突 entry 不得绕过检查。
- [ ] 校验 GUI EXE/DLL、Updater EXE、Worker EXE/DLL、两项必需原生库、Project Interface、资源、Agent、内置 Python 的完整包契约。
- [ ] 新包产品名为 MaaNOP，版本与目标 Release 规范化后一致；坏包不修改旧安装。
- [ ] Drawer 呈现下载失败、验证中、验证失败及 ReadyToInstall；不得仅凭下载完成或 SHA 成功提前宣布可安装。
- [ ] 使用可控 HTTP 流与真实临时 ZIP 自动验证下载/取消/错误/摘要/路径/结构/版本边界，并对失败前后旧安装内容作断言。
- [ ] 覆盖重复操作与迟到结果；记录下载、SHA256 和包验证日志，底部运行状态保持既有职责。

## Blocked by

- 01：检查 MaaNOP 正式更新并展示 Release Notes。

## Scope guard

不增加断点续传要求、多线程下载、ETA、sidecar、自动下载或自动安装，不引入 Worker/RDP/Game mock framework。
