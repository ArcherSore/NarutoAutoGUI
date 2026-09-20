# 配置只保存显式用户意图，并阻止会改变执行含义的无效项

Application Settings 已被移除；火影忍者 Online 游戏启动使用固定 launch profile（`NarutoGameLaunchProfile`），ExecutablePath 从当前用户 `%APPDATA%\Tencent\QQMicroGameBox\Launch.exe` 推导，AppId 固定为 `1103286479`，Arguments 固定为 `-/appid:1103286479`，不由用户配置、不持久化到 settings.json、不提供 command-line override。MaaNOP project payload 与 NarutoAutoGUI 一同打包，Project root 固定为 application base directory（`AppContext.BaseDirectory`），`interface.json` 位于 `NarutoAutoGUI.exe` 同级目录。

启动器不存在时 `NarutoGameLaunchProfile.Resolve(logger)` 抛出面向安装的可行动错误（"未检测到火影忍者 Online 微端启动器。请先通过 QQ 游戏平台安装或启动一次火影忍者 Online。"），并在 diagnostic log 中记录实际路径；不自动 fallback 到未知路径。`ChildSessionProgramService.LaunchIfNeededAsync()` 仍是通用启动服务，错误文案使用"executable 路径不能为空""指定的程序不存在"，不再描述为"用户配置错误"。

MaaNOP Config 使用 SchemaVersion 2，每份 TaskConfiguration 独立持有 SelectedTasks/ExplicitOptions。
SelectedTasks 保存不重复 task.name 的实际执行顺序；ExplicitOptions 按 option 自身顶层 key 扁平保存，select/switch 使用 SelectedCase=case.name，input 使用 Inputs 的 field-name 到 string。显式性来自用户操作事实，而不是与 default 比较：用户明确选择或填写即使等于当前 default 也保存；"恢复默认/跟随项目默认"删除对应项或字段使其回到 Unset。首版不支持的 option type fail closed。

嵌套 option 不序列化成树。父 case 切换后，未激活的合法子 option 显式值作为 Dormant Intent 保留，但不参与 resolved options、pipeline override 或 Run Plan；父分支重新激活时可恢复。MaaNOP Config 不保存 label、entry、default/default_case、展开状态或路径、resolved option graph、pipelineOverride、resolvedGlobalOptions、最终 MaaFramework 参数或 interface digest。

配置校验按"忽略是否可能改变本次 Run 意图"区分 Blocking 与 Warning。SelectedTasks 中任务消失、现存 option 的显式 case 不存在、当前参与解析的 input 字段失效、激活的嵌套显式值无效或显式值违反 regex/PI validation 时，ConfigStatus 为 Invalid/NeedsReview，显示具体 JSON/PI path并禁止 Start，不自动清理这些语义失效的用户意图；该配置仍可查看和管理，不阻塞其他有效配置。完全不可能参与当前选中 task/global option 的旧 ExplicitOptions key 可非阻塞 WARN；Dormant Intent 也不是错误。用户明确修正并保存后才清理确认无效项。

2026-09-18 多配置决策替代旧版加载失败后阻止整个工作区的行为：文件不存在时提供空配置；Active 指针
失效选第一份，空列表生成空的“配置 1”；结构无法读取或解析时仍提供空工作区，但不在加载时覆盖原文件。
该异常原文件仅在用户真实修改触发首次替换前按原始 bytes 保存相邻 invalid 备份；备份失败则保存失败。
不增加恢复 UI、历史管理或备份轮换，普通保存和删除不生成此类备份。

必须从源文件明确识别 SchemaVersion。V1 的 SelectedTasks 与 ExplicitOptions 原样包装进一份“配置 1”，
生成稳定 Id 并激活，完整读取、结构校验和 V2 构造成功后才原子替换；新版 PI 语义失效不阻止无损包装。
迁移失败保留原 V1，V2 重开不重新生成 Id；不实现通用迁移框架、profiles 继承或 import/export。
所有保存继续使用同目录临时文件、flush/close 和 atomic replace/move，失败时旧文件保持完整，GUI 不宣称成功。

`interface.json` 缺失时抛出面向正式安装包的错误（"安装目录缺少 interface.json，请确认使用完整的 MaaNOP 发布包。"），
并在 diagnostic log 中记录 `AppContext.BaseDirectory` 实际路径；不自动搜索其他 interface.json。

2026-09-20 按新手指引规格限定替代“文件不存在时提供空配置”：PI 加载成功且确认原文件缺失时，
首次配置由 ProjectPlanModule 预置 PI 第一项并保存，ExplicitOptions 不写默认值。
首次保存失败仍提供空工作区并报告错误；不把未保存的默认任务宣称为成功配置。
损坏文件、已有空配置、V2 空列表修正、V1 迁移和后续 `+` 新建仍保持原有空配置或原意图语义。
