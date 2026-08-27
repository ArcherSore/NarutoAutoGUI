# 配置只保存显式用户意图，并阻止会改变执行含义的无效项

Application Settings 已被移除；火影忍者 Online 游戏启动使用固定 launch profile（`NarutoGameLaunchProfile`），ExecutablePath 从当前用户 `%APPDATA%\Tencent\QQMicroGameBox\Launch.exe` 推导，AppId 固定为 `1103286479`，Arguments 固定为 `-/appid:1103286479`，不由用户配置、不持久化到 settings.json、不提供 command-line override。MaaNOP project payload 与 NarutoAutoGUI 一同打包，Project root 固定为 application base directory（`AppContext.BaseDirectory`），`interface.json` 位于 `NarutoAutoGUI.exe` 同级目录。

启动器不存在时 `NarutoGameLaunchProfile.Resolve(logger)` 抛出面向安装的可行动错误（"未检测到火影忍者 Online 微端启动器。请先通过 QQ 游戏平台安装或启动一次火影忍者 Online。"），并在 diagnostic log 中记录实际路径；不自动 fallback 到未知路径。`ChildSessionProgramService.LaunchIfNeededAsync()` 仍是通用启动服务，错误文案使用"executable 路径不能为空""指定的程序不存在"，不再描述为"用户配置错误"。

MaaNOP Config 使用 SchemaVersion 1，SelectedTasks 保存 task.name 并按当前 PI 声明顺序写出；ExplicitOptions 按 option 自身顶层 key 扁平保存，select/switch 使用 SelectedCase=case.name，input 使用 Inputs 的 field-name 到 string。显式性来自用户操作事实，而不是与 default 比较：用户明确选择或填写即使等于当前 default 也保存；"恢复默认/跟随项目默认"删除对应项或字段使其回到 Unset。首版不支持的 option type fail closed。

嵌套 option 不序列化成树。父 case 切换后，未激活的合法子 option 显式值作为 Dormant Intent 保留，但不参与 resolved options、pipeline override 或 Run Plan；父分支重新激活时可恢复。MaaNOP Config 不保存 label、entry、default/default_case、展开状态或路径、resolved option graph、pipelineOverride、resolvedGlobalOptions、最终 MaaFramework 参数或 interface digest。

配置校验按"忽略是否可能改变本次 Run 意图"区分 Blocking 与 Warning。SelectedTasks 中任务消失、现存 option 的显式 case 不存在、当前参与解析的 input 字段失效、激活的嵌套显式值无效或显式值违反 regex/PI validation 时，ConfigStatus 为 Invalid/NeedsReview，显示具体 JSON/PI path并禁止 Start，不自动改写文件。完全不可能参与当前选中 task/global option 的旧 ExplicitOptions key 可非阻塞 WARN；Dormant Intent 也不是错误。用户明确修正并保存后才清理确认无效项。

malformed JSON、缺失/非法/未知 SchemaVersion 或 schema 无法解析时，原文件保持不变，GUI 明确报错、禁止 Start并提供主动"重置当前 MaaNOP 配置"；不能 fallback `{}` 后 autosave 覆盖。MaaNOP Config 以同目录临时文件写入、flush/close，再 atomic replace/move，失败时旧文件保持完整且 GUI 不宣称保存成功。首版不实现配置 migration framework、profiles、import/export。

`interface.json` 缺失时抛出面向正式安装包的错误（"安装目录缺少 interface.json，请确认使用完整的 MaaNOP 发布包。"），并在 diagnostic log 中记录 `AppContext.BaseDirectory` 实际路径；不自动搜索其他 interface.json。
