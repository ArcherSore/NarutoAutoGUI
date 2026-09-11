---
status: archived-implementation-awaiting-interactive-validation
archived: 2026-09-11
---

# 05：新版首次启动展示更新完成 Banner 与对应说明

## What to build

完成安装后的新版 GUI 首次启动，在 Home 轻量提示“已更新到 MaaNOP vX.Y.Z”，用户可重新查看对应 Release Notes。

## Acceptance criteria

- [ ] 安装与新版启动通过最小一次性本地完成记录关联目标版本和 Release Notes，实际加载版本匹配后才展示。
- [ ] 完成记录不侵入用户拥有的 config，不恢复旧 Worker Admission，不构成远程自定义更新协议。
- [ ] Home Banner 可“查看更新”打开对应版本说明；无大型 Modal，后续启动不重复首次完成通知。
- [ ] 记录缺失、陈旧或目标版本不符不产生虚假成功提示；正常启动检查继续遵循一次、异步、非阻塞原则。
- [ ] 完成通知不作为 backup 清理授权或健康证明；最多一个 MaaNOP.old，仍在下一次事务前清理。
- [ ] 使用已有自检方式验证一次性记录及版本匹配行为；真实重启、Banner 和 Drawer 通过 Windows 实机手工 E2E 确认。
- [ ] 不将内部 GUI/Worker/MaaFramework 版本或安装细节加入产品 UI，不长期占用 runtime status bar。

## Blocked by

- 04：确认安装后关闭运行环境并交给 TEMP Updater。

## Scope guard

一次性完成提示不是 health marker 或 health handshake；不增加健康检查协议、A/B、多版本管理或运行后自动回滚。
