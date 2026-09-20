# 分离 MaaNOP Config 与 Run Plan

NarutoAutoGUI 将 SchemaVersion 2 的多份独立 MaaNOP 用户意图及 ActiveConfigurationId保存在 `config/maanop-config.json`，由主 GUI 独占写入且与 MFAAvalonia 完全隔离。每份 TaskConfiguration 通过稳定 Id 标识，Name 允许重复，只保存按实际执行顺序排列的 task.name，以及由用户操作明确形成的 option SelectedCase 和 input 字段值；显式值即使等于当前 default 也保留，用户主动选择“跟随项目默认”时才删除对应字段。它不保存 label、entry、default/default_case、展开状态或路径、resolved graph、pipeline override、resolvedGlobalOptions、最终参数或 interface digest。Application Settings（曾经的 `config/settings.json`）已随火影忍者 Online 启动配置收口为固定 launch profile 而删除，不再持久化游戏 exe 路径或启动参数。

每次显示配置和开始 Run 前都按当前 `interface.json` 校验。忽略持久化条目可能改变本次 Run 意图时必须将 ConfigStatus 置为 Invalid/NeedsReview 并禁止 Start，例如 SelectedTasks 中任务消失、当前参与解析的 option case/input 失效、激活的嵌套显式值无效或正则校验失败。完全不参与当前选中 task/global option 的旧 key 可 WARN；合法但父 case 未激活的嵌套值是 Dormant Intent，可保留但不得进入本次解析或 Run Plan。GUI 不自动清理或修复语义失效的用户意图；非当前配置的 PI 错误不阻止有效配置运行。

2026-09-18 多配置决策限定性替代旧版“配置异常阻止整个工作区加载”的规则：Active 指针失效选择第一份，
空列表生成空的“配置 1”；无法读取或解析时仍提供空工作区，不引入恢复模式。异常原文件不因加载被覆盖，
仅在用户真实修改首次替换前保护原始 bytes，保护失败则保存失败；正常保存和删除不产生备份历史。
明确声明的 V1 仅包装为一份“配置 1”，保留全部 SelectedTasks/ExplicitOptions，再原子写回。
迁移不以新版 PI 的可执行性为前提，失败保持原 V1；不建立通用 migration framework。

MaaNOP Config 继续使用同目录 temp、flush/close、atomic replace/move；保存失败保留旧文件并明确报错，
不将内存状态伪装成已保存。Start 仅解析 Active Configuration，Run Plan 通过 IPC 交给 Worker 后仅存在于
Worker 内存，不加入配置身份，不做继承、import/export、持久化运行恢复或断点续跑。
controller、resource、Agent、项目根目录等环境继续由 Worker Launch Context 持有。

2026-09-20 新手指引增加有限的首次配置例外：仅原文件确实不存在时，ProjectPlanModule 按 PI 数组顺序
预置第一项到“配置 1”并原子保存；ExplicitOptions 仍为空，参数继续跟随项目默认。
Store 仅报告缺失事实，不依赖 Project Definition；GUI 在首次渲染时展开该任务，不保存展开状态。
已有空配置、异常 fallback、迁移与用户新增配置不适用。Onboarding 的完成版本和未完成新用户资格
由 GUI 独立保存在 config 下，不进入 MaaNOP Config 或 Run Plan。
