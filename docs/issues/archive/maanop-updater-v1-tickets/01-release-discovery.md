---
status: archived-implementation-awaiting-interactive-validation
archived: 2026-09-11
---

# 01：检查 MaaNOP 正式更新并展示 Release Notes

## What to build

用户启动完成项目加载后可自动获知 MaaNOP 最新正式版，也可在 Settings 主动检查；新版通过 Home Banner 进入右侧 Update Drawer 阅读 Release Notes。
这是完整的“检查到查看说明”切片，不下载或安装包。

## Acceptance criteria

- [ ] 从 Project Interface 读取 github/version，扩展现有元数据解析，更新元数据不可用不额外阻塞正常任务配置与运行。
- [ ] 不硬编码仓库；只使用 GitHub 最新正式 Release，排除 draft/prerelease，不回退寻找其他旧 Release。
- [ ] SemVer 支持可选 v 前缀、数字优先级及构建元数据语义；本地相等/更高不提示更新，非法版本不能字符串兜底。
- [ ] 精确匹配 MaaNOP-win-x86_64-<原始 tag>.zip；其他平台、缺包或歧义不误选。
- [ ] 解析资产 name、browser_download_url、size、sha256 digest 和 Release body；缺失/无效摘要不可用于安装。
- [ ] Settings 仅展示 MaaNOP 当前版本、持久化且默认开启的启动检查开关和手动检查按钮。
- [ ] 每次启动最多自动检查一次且不阻塞现有功能；无新版自动静默，自动失败只记日志；手动给轻量结果或普通错误。
- [ ] Home 新版 Banner 可打开与 Task Description Drawer 一致的右侧 Drawer，展示两个版本及安全呈现、可滚动的 Release Notes。
- [ ] 防止重复检查和迟到结果覆盖；更新信息不长期进入 runtime status bar，不自动传输完整 ZIP。
- [ ] 自动化验证 SemVer、Release 解析、精确资产选择与错误隔离；使用可控 HTTP，不依赖线上 latest；记录检查诊断。
- [ ] 沿用现有项目加载自检验证任务/Config/Run Plan 未受元数据扩展影响；UI 交互按实际执行情况记录，不虚报实机通过。

## Blocked by

None (can start immediately).

## Scope guard

遵循最终 MaaNOP Updater V1 spec；不引入自定义更新源、频道、Token 设置或组件独立更新。
