# 分离 Application Settings、MaaNOP Config 与 Run Plan

NarutoAutoGUI 将 SchemaVersion 2 Application Settings 保存在 `config/settings.json`，将 SchemaVersion 1 的唯一当前 MaaNOP 用户意图保存在 `config/maanop-config.json`，两者均由主 GUI 独占写入且与 MFAAvalonia 完全隔离。MaaNOP Config 只保存按 PI 顺序排列的 task.name，以及由用户操作明确形成的 option SelectedCase 和 input 字段值；显式值即使等于当前 default 也保留，用户主动选择“跟随项目默认”时才删除对应字段。它不保存 label、entry、default/default_case、展开状态或路径、resolved graph、pipeline override、resolvedGlobalOptions、最终参数或 interface digest。

每次加载和开始 Run 前都按当前 `interface.json` 校验。忽略持久化条目可能改变本次 Run 意图时必须将 ConfigStatus 置为 Invalid/NeedsReview 并禁止 Start，例如 SelectedTasks 中任务消失、当前参与解析的 option case/input 失效、激活的嵌套显式值无效或正则校验失败。完全不参与当前选中 task/global option 的旧 key 可 WARN；合法但父 case 未激活的嵌套值是 Dormant Intent，可保留但不得进入本次解析或 Run Plan。GUI 不自动改写文件，只有用户明确修正并保存后才清理已确认无效项。

malformed JSON、缺失/非法/未知 SchemaVersion 或 schema 无法解析时，不 fallback 为空配置、不自动保存：原文件保持，GUI 显示错误、禁止 Start，并提供用户主动的“重置当前 MaaNOP 配置”。两个文件都使用同目录 temp、flush/close、atomic replace/move；保存失败保留旧文件并明确报错，不将内存状态伪装成已保存。解析出的 Run Plan 通过 IPC 交给 Worker 后仅存在于 Worker 内存，不做 profiles、import/export、复杂迁移、持久化恢复或断点续跑。controller、resource、Agent、项目根目录等环境由 Worker Launch Context 持有。
