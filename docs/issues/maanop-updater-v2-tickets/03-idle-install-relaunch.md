---
status: ready-for-agent
labels: [ready-for-agent]
issue-source: local-docs/issues
---

# 03: 在无运行环境时完成安装与重启

**Parent:** [MaaNOP Updater V2 规格](../maanop-updater-v2-spec.md)

**What to build:** 无 Worker/Child Session 时，用户确认安装，经实际 Engine 副本的 ready handoff 退出 GUI，
看到最小安装 UI，完成旧文件隔离、原地写入、尽力清理和新版启动；失败按 V2 语义明确报告。

**Blocked by:** [02: 从更新候选准备可安装 Payload](02-prepare-payload.md)。

**Status:** ready-for-agent

## Acceptance criteria

- [ ] GUI 对已就绪 Payload 提供一次安装确认；取消不调用 install，不修改程序内容。
      使用既有操作门阻止新运行环境操作，并确认无 Worker/Child Session；存在运行环境时本片不开放安装。
- [ ] GUI 仅向 install 提供安装根目录、原样 prepared reference 和 GUI PID，不管理 Engine 运行副本位置。
- [ ] Engine 内部复制自身到专属缓存运行目录，由实际副本接管；复制内容不依赖 GUI .NET/runtime，
      释放会阻塞安装的根启动入口，不引入公开的第四个操作或独立 probe。
- [ ] install 提供最小 UI：简单“正在更新”、失败错误与日志位置；不承接正常更新 UI、Release Notes 或设置。
- [ ] 实际安装副本完成当时能完成的非破坏性检查后才回复 ready；检查 reference 的归属/存在等必要条件，
      不重新下载、解压或重复整包校验，不将入口进程创建成功当作接管成功。
- [ ] GUI 收到实际副本 ready 才真正退出；Engine 确认 GUI PID 结束后才移动、删除或替换程序文件。
- [ ] ready 前 GUI 退出/通信断开中止；双方等待均有有限期限，超时不修改程序文件。
      ready 后 stdout 因 GUI 退出而断开不阻断安装，不增加第二次确认或持久交接记录。
- [ ] 四个保留目录原内容不动，包内同名内容忽略、不合并、不补默认；保留名称类型冲突在修改前失败。
      相关链接/重解析点拒绝处理，不遍历其他保留目录内部，不跟随外部目标。
- [ ] 保持安装根目录原位，先将全部受管理根条目移入旧文件隔离区，全部移动成功后才写入 Payload。
      覆盖旧程序、自加文件、旧 state 及文件/目录类型变化，不做新旧差异安装或整根 swap。
- [ ] 修改开始后不可取消；移动或写入失败立即停止、不反向搬回、不 rollback、不 relaunch 混合版本。
      最小 UI 提供从 GitHub 重新下载完整包并重建程序内容的修复指引和日志位置，不指引使用 old 恢复。
- [ ] 文件安装完成后先尽力清理 old 与本次 Payload，再 relaunch 原位置新版 GUI；清理失败只记日志，继续启动。
      不让新 GUI 等待 Engine PID，不把清理放到 relaunch 之后。
- [ ] Engine 自身副本允许留到下次 prepare；日志留在保留日志区域，不随工作内容删除，其他 cache 不被删除。
      与 02 的废弃工作清理规则衔接。
- [ ] relaunch 成功只表示新版进程创建；启动调用失败提示“安装完成但启动失败”并允许手动启动。
      不做健康确认、不生成 completion journal，不保留旧版本恢复能力。
- [ ] 移除被替代的 V1 安装事务、rollback、install lock、完成记录写入及 TEMP runtime/probe 编排；
      同步移除阻碍 V2 GUI 启动的旧锁检查，不保留 V1 fallback。
- [ ] 经真实 install 进程与隔离文件系统验证实际副本 ready、根入口释放、PID 门槛、通信断开和超时，
      验证四目录保留、先隔离后写入、移动/写入故障、清理失败继续启动及 relaunch 错误区分。
- [ ] 受影响构建及自动化通过，可独立演示“无运行环境 GUI → 确认 → 最小安装 UI → 原位置新版 GUI”。
      不以进程创建模拟结果代替后续完整包真实升级验收。

## Scope boundary

本片必须完成 install 的最小 UI、实际副本 handoff、自身替换、文件安全和失败语义，不能留给 05 补实现。
04 才扩展已有运行环境的安装；本片不擅自实现 Worker/Child Session 停止的新流程。
正式发布布局和两份真实 V2 完整包验收由 05 负责。

本次仅发布工单，未开始实现。
