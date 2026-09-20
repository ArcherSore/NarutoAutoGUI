# Status

## 当前阶段

2026-09-20：用户报告本轮桌面交互已完成。按部署时字节偏移检查新增日志，17:03:47 启动、
PI 加载成功、无已有 Child Session、17:04:06 正常退出；GUI 无新增 WARN/ERROR，更新检查未报错。
实测 DLL 仍匹配本次部署，任务配置、Onboarding 完成版本及 interface 哈希未变，GUI/Worker 已退出。
日志没有逐步指引、键盘或最小化恢复记录，用户也未提供逐项结果，不据此将全部人工验收标记通过。

2026-09-20 17:02：按用户要求将布局重算修复后的 GUI DLL 同步至 `D:\MaaNOP-win-x86_64-v2.4.0`，
同步前确认 GUI/Worker 均未运行，旧 DLL 备份至 `artifacts/onboarding-layout-backup-20260920-170248/`。
源/目标 SHA256 一致，config 与 interface 哈希未变；目标目录 project-only 自检通过。
已保存同步时日志基线，等待用户真实桌面交互后检查新增日志；本次未提交。

2026-09-20：修复新手指引箭头几何重复赋值触发的持续布局，只在 Popover/target 边界变化时重建箭头；
复用目标裁剪边界并移除无用数组分配。离屏复验静止 1 秒的布局事件从 7257 次降为 0，
新增首次显示与窗口缩放后的布局稳定性回归；Release 构建（0 警告/错误）、project-only 自检、
120 列与 diff 空白检查通过。未同步实测目录、未提交；真实桌面交互验收范围不变。

2026-09-20：设置按钮改为“查看新手指引”；手动查看完成、跳过或 Esc 后留在首页，焦点不返回隐藏的
设置按钮，原配置和指引持久化状态保持不变。Release 构建与 project-only 自检通过，覆盖结束回首页。
已备份并同步测试目录 GUI DLL，SHA256 一致，配置和 interface 未变；按用户要求继续不提交。

2026-09-20：用户反馈上一轮视觉调整“其他没啥大问题”，要求 Pulse 默认扩散三次后再看效果。
已将默认次数从 2 调为 3，更新有限播放自检等待时间；Release 构建和 project-only 自检通过。
确认用户从托盘退出后再次同步测试目录 GUI DLL，SHA256 一致，配置和 interface 未变；继续不提交。

2026-09-20：按实测反馈减轻新手指引 footer 按钮，移除字符箭头，主操作固定宽度；说明图标 Pulse
改为无填充的居中细圆环，850 ms 扩散并淡出两次，中间留 200 ms 间隔；Popover 避开标题栏。
Release 构建通过（0 警告/错误），`--self-test --project-only` 通过，离屏截图已目检。
已备份并同步主 GUI DLL 至 `D:\MaaNOP-win-x86_64-v2.4.0`，SHA256 一致，配置和 interface 哈希未变。
旧 DLL 在 `artifacts/onboarding-polish-backup-20260920-163900/`；按用户要求不提交，等待直接查看实际效果。

2026-09-20：按用户要求将新手指引版本 `816651c` 的 GUI 及 ProjectModel/Protocol/Updates 四个程序集
同步至 `D:\MaaNOP-win-x86_64-v2.4.0`，逐项 SHA256 一致，目标目录 GUI 完整自检通过。
旧程序集及原 config 已备份至 `artifacts/onboarding-first-run-backup-20260920-161713/`，配置备份哈希一致。
目标 config 目录已移出，保留新用户首次正常启动体验；后续可恢复旧配置模拟老用户。
interface 哈希未变，未改 Worker、任务资源或真实 Session；尚未执行首次启动的交互验收。

2026-09-20：按 [新手指引规格](issues/onboarding-tour-spec.md) 接入四步 Fluent Spotlight/Popover，
首次缺失配置预置并展开 PI 第一项，后续 `+` 仍为空；独立保存首次资格和完成版本，老用户不自动弹，
设置 replay 不改配置或版本。支持真实 target/滚动裁剪、有限 Pulse、暂停恢复和输入/焦点限制。
GUI/Worker Release 构建、GUI 完整自检、Updater 客户端测试、Rust tests/Clippy 通过；
完整本地自动化在既有 Worker 双进程完整帧测试处超时，未记作整套通过。
用户反馈该测试在 GitHub CI 可通过，本次未独立复验线上结果，也未修改 Worker 实现。
已目检离屏截图；真实 DPI、标题栏/托盘、键盘及正式包体验仍待验收，见
[验证记录](issues/onboarding-tour-validation.md)。未操作真实游戏/分身，未替换实测包或发布。

2026-09-20：核对用户实测目录的 GUI 日志并修复连续 Preview 的两处 GUI 生命周期问题。
Ready 与准备结束的先后顺序影响首次订阅；`SetBusy` 现在同步重评预览，准备完成后无需切页或开始任务。
任务 Stop 不再撤销预览订阅或清空位图，仍沿用原 run.stop 与任务终态流程。
离屏真实 MainWindow + 测试 Pipe 回归先复现失败，再分别验证两种 Ready 时序均自动首帧，
停止任务保留同一位图和订阅且收到 Stop ACK；GUI Release 构建与完整自检通过。
WPF 增量构建首次缺少旧 BAML 缓存，clean 后构建通过。未替换实测目录产物、未操作真实游戏或分身；
随后按用户要求，在确认目标 GUI/Worker 均已退出后，仅将修复后的 GUI DLL 替换至实测目录，
SHA256 一致，配置和 interface 哈希未变；旧 DLL 备份于 `artifacts/preview-lifecycle-backup-20260920-101232/`。
部署后用户反馈实测“没有大问题”；此反馈对应本轮 GUI 修复，不扩展为全部场景和长时间资源验收通过。
提交前 Standards/Spec 复查均无明显新问题。详细证据见 [验收记录](issues/preview-live-validation.md)。

2026-09-19：按 [连续 Preview 规格](issues/preview-live-spec.md) 实现 Worker 独立截图、协议 2 订阅、
跨 Session 文件映射与 GUI 最新帧显示，覆盖无任务预览、窗口恢复、可见性、租约和任务隔离。
GUI/Worker Release 构建与独立发布产物生成通过；新增双进程完整帧、真实 Worker 协议入口、
阻塞截图下 Snapshot/renew/Stop ACK、窗口恢复、过期停采、abandoned Mutex、慢 UI 与旧目标拒绝测试。
两路审查发现的 UI 阻塞续订和排队旧帧问题已修正并复查。
用户因正在运行其他任务，明确将实机测试暂缓；未替换现用完整包，未停止任务，未执行 30 分钟资源验收。
当前环境缺少 Rust 工具链，完整打包和 Rust 检查受阻；具体执行结果、产物及待办见
[验收记录](issues/preview-live-validation.md)。[5 张工单](issues/preview-live-tickets/README.md) 保留未验收项，未关闭。

2026-09-18：补齐配置 Tab 左边线与新建按钮右边线，使用现有标准边框色；移除左右滚动箭头。
标签溢出时，鼠标滚轮转为横向滚动并消费事件，避免带动下方工作区；未溢出时保留正常工作区滚动。
底部仅溢出时显示 2 DIP、25% 不透明度的细滚动条（6 DIP 区域），可拖动且不遮挡选中下划线。
Release 构建及离屏检查通过：滚轮横移、滚动条拖动、无溢出隐藏/事件传递、既有配置交互回归及
1180×760/920×640 布局；截图已目检，实际桌面效果待用户确认。
已备份并同步主 GUI DLL 至 `D:\MaaNOP-win-x86_64-v2.4.0`，SHA256 一致，用户配置未改动。

2026-09-18：配置区改为连续 Tab 栏：38 DIP 高、14 DIP 字号，选中半粗体配底部蓝色下划线，
移除胶囊间距和左侧指示条，共用底部分隔线；新建按钮接在标签末尾，溢出使用左右箭头滚动。
保留紧凑关闭按钮（10 DIP 图标、14 DIP 按钮、16 DIP 文字预留、6 DIP 右边距）及无悬浮提示。
未改变配置持久化、删除、重命名或运行锁定规则。Release 构建及离屏交互检查通过，覆盖箭头滚动、
既有名称/参数/删除/运行锁回归与 1180×760、920×640 布局；截图已目检，实际桌面效果待用户确认。
已备份并同步主 GUI DLL 至 `D:\MaaNOP-win-x86_64-v2.4.0`，SHA256 一致，用户配置未改动。

2026-09-18：强化顶层配置 Tab 层级：高度 32→40 DIP，文字使用 15 DIP 半粗体，新建按钮同步增高。
继续收紧删除区域：按钮宽度 16→14 DIP，文字预留 20→16 DIP，标签右内边距 12→6 DIP。
名称编辑器跟随标签字号和字重；保留小号淡色叉号及既有交互。GUI Release 构建通过（0 警告/错误），
离屏配置交互与 1180×760/920×640 布局检查通过，截图已目检；实际桌面效果待用户确认。
已备份并同步主 GUI DLL 至 `D:\MaaNOP-win-x86_64-v2.4.0`，SHA256 一致，用户配置未改动。

2026-09-18：移除配置 Tab 与删除按钮的悬浮提示；叉号从 14 缩为 10 DIP，常态透明度 55%，
悬停/键盘聚焦恢复完整强调；删除按钮宽度 20→16 DIP，文字预留空间 28→20 DIP，出现时不挤动文字。
GUI Release 构建通过（0 警告/错误），既有离屏配置交互与两种窗口尺寸检查通过，截图已目检。
主 GUI DLL 已备份后同步安装目录，哈希一致；未执行实际桌面交互验收。

2026-09-18：按配置 Tab 设计图调整 UI：32 DIP 胶囊标签、8 DIP 间距、主题色选中指示及相邻新建按钮。
移除配置操作菜单和独立名称输入区；双击 Tab（或 F2）在标签内重命名，Enter/失焦保存，Esc/空名恢复原名。
编辑期间禁止切换和配置增删；删除按钮固定预留空间，仅悬停/选中显示，运行锁定及最后一份时隐藏。
删除非当前项仍按 Id 执行且保留 active；沿用原保存失败处理和参数事件归属，不改模型或执行流程。
GUI Release 构建与多配置专项自检通过；临时离屏检查通过：名称保存/取消/空名、编辑锁、删除 active 保持、
最后一份保护、运行隐藏删除、参数失焦及保存失败回归，1180×760/920×640 截图已目检。
已同步主 GUI DLL 至 `D:\MaaNOP-win-x86_64-v2.4.0`，SHA256 与构建产物一致；旧 DLL 备份于
`artifacts/config-tabs-ui-backup-20260918-161420/`，现有配置未改动。
未执行实际桌面交互、多 DPI 或真实游戏验收。

2026-09-18：实现多份独立任务配置：Tab 切换、新建空配置并激活、同名重命名、直接删除及激活位置持久化。
配置以稳定 Id 区分，独立保存 SelectedTasks 和 ExplicitOptions；删除当前项选择左邻或右邻，最后一份不可删除。
参数仍在任务卡编辑，失焦事件绑定来源配置 Id；保存失败保留原文件和当前 Tab，沿用整个工作区的运行锁。
Schema V1 首次加载包装为 V2，不重解释显式参数；无效 active 选首项，空列表/读取异常退回可用空配置。
异常原文件在首次真实编辑保存前按原始 bytes 备份，备份失败不覆盖；PI 语义失效仅阻止该配置运行。
Resolver 复用现有解析，RunPlan、Protocol、Worker 和 Child Session 生产代码未改动；同步架构、领域与相关 ADR。
GUI/Worker Release 构建通过（0 警告/错误）；完整 test-automated.ps1 通过，包括 GUI/Worker 自检、
C# 更新客户端测试、Rust Engine 测试和 Clippy；多配置专项自检、120 列及 diff 空白检查通过。
修正自检 DisposeAsync 在 UI 同步上下文中同步等待造成的阻塞，仅调整测试清理方式。
临时离屏 GUI 检查通过：真实键盘失焦归属、非法输入、文件锁下保存失败阻止切换、同名/长名称和
1180×760、920×640 布局，截图已目检。未执行真实游戏运行、多 DPI 或实际桌面交互验收；已同步 GUI/ProjectModel DLL 至安装目录，未发布。

2026-09-18：执行计划任务卡折叠时，在任务名后显示当前参数摘要；直接读取现有 ProjectConfigurationView，
复用编辑器的 GlobalOptions → TaskOptions 及 ActiveChildren 深度优先原始顺序，包含默认值和显式值。
input 使用可见 label/value，select/switch 使用 option label/当前 case label，参数以 ` · ` 分隔。
标题与摘要间为 1 DIP 浅色 Border，摘要使用剩余星号列、NoWrap 和 CharacterEllipsis；右侧按钮保留独立 Auto 列。
展开或无参数时不显示摘要及分隔线，不缓存第二份配置，不新增 Tooltip。
GUI x64 Release 构建（0 警告/错误）及 GUI `--self-test` 通过；临时专项检查覆盖默认/显式值、嵌套启用树、
顺序、展开/空参数和 320/540/800 DIP 标题行的截断及按钮空间，离屏渲染已目检，120 列和 diff 空白检查通过。
仅同步主 GUI DLL 至 `D:\MaaNOP-win-x86_64-v2.4.0`，SHA256 与构建产物一致；旧 DLL 备份于
`artifacts/task-summary-backup-20260918-114026/`。未执行目标目录自检或实际桌面交互验收；ROADMAP 不变。

2026-09-18：修正「菜单」文字与导航项未对齐：WPF-UI 4.3 默认 PaneTitle 左边缘多出 6 DIP，
仅调整原生切换按钮标题的 Margin，保留原生展开/折叠行为。离屏坐标检查先复现 51/45 DIP 不一致，
修复后菜单/首页均为 45 DIP；1180×760、920×640 与展开/折叠回归通过，已目检截图。
GUI Release 构建通过（0 警告/错误），仅同步 GUI DLL 至 `D:\MaaNOP-win-x86_64-v2.4.0`，哈希一致；
旧 DLL 备份于 `artifacts/menu-align-backup-20260918-112843/`。实际桌面交互与多 DPI 仍待用户复验。

2026-09-18：导航使用原生 PaneTitle 增加「菜单」文字，展开显示图标与文字，折叠仅显示图标。
GUI Release 构建通过（0 警告/错误），1180×760 与 920×640 的离屏导航及 Drawer 检查通过。
首次同步因 DLL 占用未完成；用户退出后已同步至 `D:\MaaNOP-win-x86_64-v2.4.0`，SHA256 与构建产物一致。
仅替换主 GUI DLL，旧文件备份于 `artifacts/menu-label-backup-20260918-112617/`；未执行实机交互验收。

2026-09-18：任务说明直接按 Markdown 渲染，保留任务卡入口与右侧可滚动 Drawer；复用已有 Markdig 1.3.2
和原生 WPF FlowDocument，不新增依赖。渲染器由 ReleaseNotesDocument 更名为 MarkdownDocument，更新说明
行为保持不变。完全删除旧 HTML-ish 标签剥离与 `<br>` 换行处理，旧 `<span>/<br>` 将作为普通文本显示，
需 MaaNOP 内容侧迁移 Markdown；不修改 PI 内容或 pipeline。
删除底部技术状态栏、44 DIP 占位及专用显示计算；保留首页按钮所需的操作状态、内部快照与诊断日志。
导航默认展开（168 DIP），启用标准 hamburger 折叠/展开；铃铛、红点及检查环移入持久图标槽，折叠时保留。
首页任务列最小宽度从 360 调整为 320 DIP，避免 920×640 下展开导航时右侧内容越界。
GUI x64 Release build 通过（0 警告/错误），GUI `--self-test` 通过，替换旧 HTML 自检为 Markdown 排版断言。
临时离屏真实 XAML 检查通过：1180×760 与 920×640 × 导航展开/折叠，标准按钮切换、首页/设置导航、
更新红点与检查环互斥、20 组长 Markdown 滚到底部、新任务说明回到顶部、无水平滚动及无底栏空白。
已检查离屏截图、120 列及 diff 空白；未执行真实桌面鼠标/键盘、链接打开、不同 DPI 或实际更新交互验收，
未启动分身/游戏、未覆盖完整包或发布；ROADMAP 不变。

随后按用户要求仅将最新 GUI DLL 同步至 `D:\MaaNOP-win-x86_64-v2.4.0`，SHA256 与构建产物一致，
旧 DLL 备份于 `artifacts/ui-small-backup-20260918-112018/`；未修改配置、资源及其他运行组件。
本轮 Release 构建通过（0 警告/错误）；目标目录通过系统 dotnet 启动自检报 `Could not resolve CoreCLR path`，
未修改其发布运行时配置；构建目录自检本轮重跑长时间无输出，已结束该测试进程，不计为通过。
目标目录自检及实际窗口效果仍待验证。
2026-09-18：复核发布链路，v1.4.1 标签准确指向 7479f6b，MaaNOP v2.4.1 baseline 固定该前端。
下载正式 MaaNOP v2.4.1 ZIP，主 GUI DLL ProductVersion 为 `1.4.1+7479f6b…`，SHA256 为
`A867F27251E6C18960AA6EBABDDBFB44DDFC4241EBC12CFAAF414A56C2EC98E2`，与今天两份替换前备份一致。
更新日志显示 09:13、09:30 已启动 v2.4.1；因此不能把本地 main 落后误判为正式包漏修复。
今天首次本地 UI 精简构建基于 30820d0，覆盖测试目录时未带入已发布的弹窗修复，造成样式回退；
后续布局同步已补入该修复。本次仅核查并纠正文档，未替换测试目录文件，未进行交互式复测。

2026-09-18：补充无参数任务的点击反馈：标题仍为可点击按钮，点击切换左侧绿色标记，卡片保持单行，
不显示参数区域；辅助说明使用「选中/取消选中」。GUI Release 构建（0 警告/错误）、构建目录自检通过，
首次同步时目标 DLL 被进程占用；用户退出后已同步至 `D:\MaaNOP-win-x86_64-v2.4.0`，
SHA256 与构建产物一致。未执行实际窗口点击验收。

2026-09-18：将 7479f6b 的更新弹窗布局修复应用到当前工作区，保留今天的安装就绪与任务卡片精简改动。
正文增加右侧留白、左对齐，弹窗宽度调整为 440 DIP；就绪状态仍显示「更新已就绪」，不显示新版本标签。
GUI Release 构建通过（0 警告/错误）、构建目录 GUI 自检通过，代码行宽与 diff 空白检查通过。
仅同步主 GUI DLL 至 `D:\MaaNOP-win-x86_64-v2.4.0`，SHA256 一致，旧文件备份在本地
`artifacts/update-layout-backup-*/`。本轮未执行目标目录自检或交互式布局验证，待用户启动查看；未提交或发布。

2026-09-18：精简更新安装与无参数任务卡片。下载完成后显示「更新已就绪」、简短重启/配置保留说明，
按钮为「稍后 / 安装并重启」，隐藏更新说明正文；安装不再弹系统确认框，不重复增加停止任务提示或文件清理说明。
无可编辑参数任务只保留标题行及拖动、描述、移除操作，不再显示空参数说明、分隔线或可展开的空区域。
GUI Release 构建通过（0 警告/错误）、构建目录 GUI 自检通过；仅同步主 GUI DLL 至
`D:\MaaNOP-win-x86_64-v2.4.0`，SHA256 与构建产物一致，旧 DLL 备份于本地 artifacts/ui-compact-backup-20260918。
目标目录自检尚未通过启动：系统 dotnet 报 Could not resolve CoreCLR path，发布 EXE 非提升启动要求提权；
未修改目标运行时或启动配置。实际界面及真实更新安装/重启仍待交互式验证，未改动安装引擎或分身生命周期。

2026-09-17：按用户确认的方案调整更新弹窗：宽度改为 440 DIP（小窗口自动收窄），新版本提示移至版本号旁，
正文显式左对齐，缩小 Markdown 大标题并增加右侧及底部留白；说明区域最高 260 DIP，底部操作保持固定。
GUI Release 构建通过（0 警告、0 错误）；GUI 自检在沙箱外通过（沙箱内命名管道连接被拒绝）。
临时离屏真实 XAML 检查通过：四状态、三种窗口尺寸的长说明限高、版本标签、Markdown、遮罩恢复与安装关闭限制；
已核对截图示例正文及长说明渲染。临时 QA 项目构建有 NU1900 警告（NuGet 漏洞源无法连接），无编译错误。
随后同步远端 main 至 30820d0，保留上游修复，重新通过 Release 构建、GUI 自检、C# 更新客户端回归和弹窗布局检查。
本轮仅调整显示，未执行真实下载、安装、重启或游戏交互验收。

2026-09-17：修复活动任务期间结束桌面分身后，运行控制仍显示禁用停止图标的问题。
原因是控件继续使用保留的 stale ActiveRun；现在确认 ChildSessionEnded / WorkerExited 后，运行控制
不再把旧快照作为当前运行状态，分身结束恢复准备入口，Worker 退出显示重试入口；历史快照仍保留。
GUI Release 构建通过（0 警告/错误），实际 WPF 控件自检先复现失败、修复后通过；覆盖 Running、Starting、
Stopping 后分身结束的按钮/进度环/计时器/文案恢复，以及暂时 IPC 断线仍禁用停止、旧快照不被改写。
用户退出后已仅同步主 GUI DLL 至 `D:\MaaNOP-win-x86_64-v2.4.0`，哈希一致，目标目录 GUI 自检通过。
未改动 Child Session 原生注销与 Worker/IPC 实现；真实游戏运行中结束分身的交互式复测待用户完成。

2026-09-16：按用户授权正式发布 NarutoAutoGUI v1.4.0（非 prerelease，已设为 latest），标签固定于
`c6d2f8a12956f2486b90bc03228f808fbe446c8f`，包含新版 UI、825d46d 红点改动和 Rust Updater V2 静默安装。
[发布流水线 35049480288](https://github.com/ArcherSore/NarutoAutoGUI/actions/runs/35049480288) 全部通过：
locked 全量构建、自动测试、发布目录 GUI/Worker 自检、包布局及 ZIP 解压复验。
[正式 Release](https://github.com/ArcherSore/NarutoAutoGUI/releases/tag/v1.4.0) 提供
`NarutoAutoGUI-win-x64-v1.4.0.zip`，147412183 bytes，GitHub SHA256：
`52DF22FB43FD7AEC1F07C0AAB3AB38C17AF8E183048F7C3D7BC0F1101EBA5A27`。
公开资产已下载回读，本地 SHA256 与 GitHub 一致；GUI ProductVersion 为 `1.4.0+c6d2f8a…`，
确认包含新增铃铛红点/加载控件及 Engine、Updates DLL、Worker 四个发布组件。
本次仅发布当前仓库的前端包，不含 MaaNOP 游戏资源或 Python；未发布 MaaNOP 完整包，也未改动测试源旧资产。
发布说明明确 V1 首次迁移需手动安装 V2 完整包、四目录保留和安装失败不回滚。

2026-09-16：Updater V2 安装交接后改为静默等待 GUI 退出、替换文件并 relaunch，完全删除 Rust Status
窗口、窗口线程及其 Win32 API，不增加进度窗口、toast 或延迟 fallback。ready 后的等待、安装和启动失败
继续调用 native::failure()，以 Win32 MessageBox 提示具体错误和 updater.log 路径；文件协议与不回滚语义不变。
GUI client 对 terminal error 原样传播 Engine message，不再依赖入口进程退出码，覆盖副本 ready 前报错而
入口进程成功退出的场景。C# JSONL 回归、GUI Release 构建（0 警告/错误）和自检、Rust Release 构建及
Rust 全部 23 项测试与 Clippy 通过。真实 Engine 副本测试覆盖正常交接/安装无窗口、替换失败、
有效 EXE 被占用导致 relaunch 失败，以及真实 30 秒等待超时；自动核对并关闭失败 MessageBox，
确认错误正文、日志路径和对应文件状态。
未重新打包或发布完整包，未执行真实 GUI 更新按钮、Child Session 或游戏环境的交互式升级验收。
随后按用户要求同步至 `artifacts/updater-v2/ui-v2.3.3/MaaNOP-v2.3.3/`，仅替换 Release Engine 与
`libs/NarutoAutoGUI.Updates.dll`，同步时其余 3354 个文件哈希不变；目标目录 GUI 自检和独立 Engine 检查通过。
目录内 PI 当前实际版本为 `v2.2.3`、更新源为 MaaNOP-UpdateTest，均按原值保留，不因目录名称修改。
随后用户发现 825d46d 的铃铛红点改动未呈现：日志确认该目录 10:16 曾升级至 v2.3.4，主 GUI DLL
哈希与不含红点改动的已发布 v2.3.4 ZIP 一致；首次同步仅更新 Engine/Updates DLL，漏查主 GUI 版本。
用户退出程序后补同步最新 `NarutoAutoGUI.dll`，哈希及新增红点/加载控件标识核对通过，目标目录自检通过。
远端 v2.3.4 包未修改，更新至该包仍会覆盖本地新 UI 与本轮 Engine 改动。
用户随后反馈“测试通过”，同意提交本轮修改；未提供逐项交互记录，不扩展为其他升级边界全部通过。

2026-09-16：按用户设计图将更新红点从导航尾部移至铃铛右上角：6 DIP、#F53F3F、1 DIP 白边，
铃铛保持 20 DIP，标记不单独占列；检查中在同一图标位置显示 loading，继续沿用既有更新状态与无障碍提示。
Release 构建（0 警告/错误）、GUI 自检通过；实际 WPF 导航片段离屏渲染确认文字对齐，
默认/有更新/选中/禁用/检查中五种状态图标位置不变。未执行真实窗口 hover 或游戏交互，尚未发布含此改动的新包。
随后按用户要求将新 GUI DLL 同步至 `artifacts/updater-v2/ui-v2.3.3/MaaNOP-v2.3.3/`，仅替换该 DLL，
保留 v2.3.3 元数据与既有配置/资源；目标目录自检通过。已发布 v2.3.3/v2.3.4 ZIP 不含本次红点改动。
用户随后检查同步后的界面并反馈“没问题了”，同意提交本次修改。

2026-09-16：以本地合并提交 9832137 重新完成 locked Release 全量发布构建（GUI、Worker、Rust Engine），
组装 UI 测试版 v2.3.3；MaaNOP 资源与内置 Python 沿用上一轮完整包，PI 指向独立 MaaNOP-UpdateTest 测试源。
GUI/Worker 发布自检、C# JSONL 适配、Rust 测试、Clippy、baseline 布局及完整 ZIP 生产 prepare 校验通过。
本地产物位于 `artifacts/updater-v2/ui-v2.3.3/`，新 UI 的实际观感和交互留待用户测试，未操作真实 GUI/游戏。
已发布测试源 v2.3.3，GitHub asset SHA256 与本地一致，正式 Engine 远端 check 识别 v2.3.2 → v2.3.3。
ZIP 为 238249748 bytes，SHA256：`2C7ADF9EBC8C1ED52EE4EA3D7A7D68AF203BAF1551F3598158A1202CCBF79979`。
随后按用户要求发布测试源 v2.3.4，仅将上述完整包的 PI 版本递增，供新 UI 更新流程人工验收。
生产 prepare 校验通过，GitHub digest 与本地一致，尚未据此声明新版 GUI 下载/安装交互通过。
v2.3.4 ZIP 为 238249746 bytes，SHA256：`D816EEABD4F0292AE21C565B28E077FDBB2E72FCEE5089BBC9931D0905C71E99`。

2026-09-15：合并 ui 分支至 main，保留首页/任务/截图/运行动态改版和全局更新 Modal，更新操作接入 main 的
Rust V2 check/prepare/install JSONL 链路。GUI 仅消费版本、说明、进度及原样 descriptor/reference，
不恢复 V1 C# 下载、缓存管理、完成记录、目录交换或 .NET Updater。Rust check 仅新增 releaseUrl 展示字段；
缺少该字段时仍可在弹窗内查看 Markdown，安装、准备及生命周期实现保持 main 版本。
本机无 Rust 编译器，本轮未运行 Cargo build/test、Clippy 或完整发布构建，新增 Rust 字段断言仅完成源码核对。
GUI locked Release build（0 警告、0 错误）、GUI 自检、C# JSONL 进程适配测试通过；离屏真实 XAML 和
C# Engine 测试替身验证四状态、限高滚动、遮罩/标题栏拦截、安装关闭限制、准备中关闭不取消、取消后重试、
ready 前的界面状态及 opaque 传递。测试替身不代表真实 Rust Engine 或真实更新安装验收。

2026-09-15：测试源连续两次真实 GUI 更新 v2.2.3 → v2.3.1 → v2.3.2 通过，用户操作并反馈未见问题。
第二轮日志确认活动任务 Cancelled 后注销 Child Session、确认 Worker 结束、实际 Engine ready，随后重启 v2.3.2。
更新后重新创建环境、真实游戏任务启动/停止、分身注销与正常退出通过；old/Payload 已清理，仅留运行副本。
关闭失败时阻止安装仍缺本轮实机证据；已有 Child Session 恢复由用户决定暂缓验收，未计为通过。
工单 05 不标为全部完成。
测试源两包均已发布，正式 MaaNOP 发布流水线未改动。详细证据及历史观察见 UPDATER-V2-VALIDATION。

2026-09-15：用户报告测试源第一轮 v2.2.3 → v2.3.1 通过；日志确认下载取消/重试、安装、重启新版本，
升级后真实任务启动/取消、Child Session 隐藏与注销。第二轮继续验收活动任务中的安装关闭。
一次 IPC JSON 解析警告后已重连并再次运行，原因待诊断；不据第一轮勾选全部生命周期验收项。

2026-09-15：经用户授权创建公开测试仓库 `ArcherSore/MaaNOP-UpdateTest`，已发布 v2.3.1 完整测试包。
测试包沿用已验证 runtime 布局，包含 7451150 的 Engine/Updates DLL；不代表完整 baseline 重新构建。
正式 Release Engine 在隔离目录通过真实 GitHub check/prepare（下载、SHA256、完整包校验与解压）。
`manual-test-v2.2.3` 已切换测试源；用户决定自行完成 GUI 和游戏验收，此次尚未验证安装与真实生命周期。
第二轮 v2.3.2 待首轮人工升级通过后发布；正式 MaaNOP 仓库未改动。

2026-09-15：完成 Updater V2 simplification review 的四项收敛：实际安装副本只做一次 ready 前 preflight，
删除中间取消令牌与重复链接/Payload 过滤，安装终态统一由主入口记录，清理失败仍即时记录。
Rust 22 项测试（含真实副本与终态日志回归）、Clippy、C# JSONL 适配测试及 GUI Release 构建通过。
本轮未重新生成完整发布包，未运行 GUI 更新按钮或真实 Child Session/游戏交互验收。
随后将新 Release Engine 与 Updates DLL 同步至 `artifacts/updater-v2/manual-test-v2.2.3/`，保留 v2.2.3 元数据。
同步时其余 3351 个文件哈希未变；目标目录 GUI 自检及 Engine 独立运行检查通过。
用户随后反馈人工测试“没太大问题”，同意提交；未提供逐项覆盖记录，工单 05 剩余验收项不据此全部勾选。

2026-09-15：Updater V2 工单 01–04 已完成，05 正式发布集成完成、真实交互部分待验收。
Rust check/prepare/install、GUI JSONL 适配、活动运行环境关闭、实际副本 ready、原地替换/清理/relaunch 已接通。
V1 .NET Updater、swap/rollback、安装锁、completion journal 和 bootstrap 已删除。
正式 locked build、baseline 布局、独立 Engine、正式 GUI/Worker 自检、C# 适配、Rust 21 项测试及 Clippy 通过。
本地完整包 v2.3.0 → v2.3.1 → v2.3.2 连续两次安装通过：实际 GUI/Engine 进程，但 HTTP 与 GUI 交接由验收工具驱动。
未完成正常 GUI 更新按钮、真实 Active Run/Child Session/游戏退出及 baseline 交互回归；未发布 Release 或修改下游仓库。
详细来源、散列、结果及边界见 [V2 验证记录](UPDATER-V2-VALIDATION.md)，不得把该集成测试当作完整产品 E2E 通过。

2026-09-14（设计阶段记录）：MaaNOP Updater V2 设计已由用户确认定稿，见 [设计记录](issues/maanop-updater-v2.md) 和
[ADR 0023](adr/0023-centralize-updates-in-a-rust-engine.md)。
[V2 可执行规格](issues/maanop-updater-v2-spec.md) 已整理并标记 ready-for-agent，保留已确认的测试 seam 和验收要求。
当时仅更新文档、updater 仍为 V1；后续实现进展以上方记录为准。V2 完整 MaaNOP 包将强制内置 Python，
这不改变下方已完成阶段使用系统 Python 的历史验证事实。

第一轮正式 GUI 和 ADR 0020 的首个最小 Worker/IPC + MaaFramework 端到端切片已完成并通过交互式实机验收。
`win-x64-options-v2-scroll` 在同一 Worker 上同时通过 admission、fresh Snapshot、Dependency Readiness、真实自然
Succeeded Run、真实 Running 后取消、取消后存活和再次执行；GUI 使用正式 PI 显式 option 编辑与最终 MaaNOP
Config/Run Plan 路径，不含测试旁路。Phase 1 Windows x64 release workflow 已实现并通过 `workflow_dispatch`，可生成
经自检、布局校验和 SHA256 校验的 Actions artifact；`v0.1.0-rc.2` prerelease 已创建并成为 MaaNOP Windows x64
打包的固定 frontend baseline。GitHub Release 和 Actions artifact 只发布 ZIP，不再发布独立 SHA256 sidecar；MaaNOP
baseline 内部保留固定 SHA256 用于下载校验。Python 继续按现有 MaaNOP Project Interface 使用系统 `python`，不进入发布包；当前外部
Python 语义下的 E2E 与本机回归已由用户完成，Python runtime 打包不再作为本阶段前置项。
已验证的 Child Session 实现现位于 `src/NarutoAutoGUI/ChildSession`，不再保留独立 Demo；Worker 位于
`src/NarutoAutoWorker`。

## 本轮已实现

- 2026-09-15：更新入口移至侧栏底部、设置上方，替换原首页更新横幅和右侧抽屉。更新窗口为全局
  360 DIP 居中 Modal Dialog，背景整体使用 8 DIP Blur 与半透明暗色遮罩，保留当前页面与任务配置。
  覆盖 checking、up-to-date、update-available、check-failed，并保留未检查初态；检查环与新版本红点互斥，
  启动检查不会自动打开弹窗。关闭按钮、Esc、遮罩统一关闭，安装阶段继续使用原有退出操作门限制关闭。
  Release Notes 通过 Markdig 渲染基础 GitHub 风格 Markdown；说明区域最高 220 DIP 并可滚动，
  Dialog 最高为 600 DIP 与窗口高度 80% 中的较小值。支持标题、列表、任务列表、强调、删除线、引用、
  代码、表格和 HTTP(S) 链接；HTML 不执行，远程图片仅展示替代文本链接。
  GUI x64 Release build 通过（0 警告、0 错误）；真实 XAML 离屏验证四状态、标记互斥、三种窗口尺寸的
  长说明边界、遮罩禁用/模糊恢复、安装关闭限制与 Markdown 渲染。屏幕外真实 HWND 验证标题栏命令拦截、
  Esc/遮罩关闭与 hook 清理通过；脚本化 HTTP 响应驱动实际检查方法，覆盖发现更新、已是最新与失败。
  用户反馈「挺满意的」并确认提交本轮 UI 修改；该反馈不扩展为真实更新安装流程验收。
  本轮未执行真实更新下载、安装、重启或游戏任务；不修改更新包校验、安装事务及 Child Session 流程。

- 2026-09-15：运行控制、实时截图和运行动态卡片统一使用 Radius.Control 圆角；移除左侧独立
  「任务」导航入口及对应页面枚举，首页保留全部任务配置，侧栏只保留首页和设置。
  GUI x64 Release build 通过（0 警告、0 错误）；真实 XAML 离屏加载、导航项数量、首页任务区域保留、
  三张卡片圆角一致性和渲染检查通过。未启动分身或真实任务。

- 2026-09-15：任务区可用任务、执行计划标题及操作按钮保留原底色和边框，禁用时仅轻微淡化内容，
  移除逐个灰色禁用块；任务编辑权限继续使用原有判断。按用户反馈移除新增的大块状态提示和
  「可用任务」旁的小加载环，仅保留任务按钮的禁用外观优化。
  GUI x64 Release build 通过（0 警告、0 错误）；临时离屏 UI 验证覆盖启动分身、开始任务、运行、停止、
  断线等待、恢复编辑及无项目状态，检查实际计划卡片渲染、禁用按钮拒绝 UI Automation 调用和灰块移除。
  常规与窄宽度渲染检查通过；用户反馈「其他没问题了」，确认保留样式优化并提交。
  该反馈不扩展为未逐项记录的完整运行流程验收；本轮不修改 Worker 或 Child Session 流程。

- 2026-09-15：按设计图重做运行动态。列表采用固定时间列与可换行文案，去掉逐行等级图标，
  使用轻分隔线、细滚动条与最新记录浅蓝高亮；空状态增加图标和「任务开始后，将在此处显示关键状态更新」。
  最新记录置顶，按滚动位置自动跟随：翻阅旧记录时保留阅读位置，回到顶部恢复跟随。清空仅移除当前
  GUI 列表，不删除文件日志或重置 Worker 日志 cursor；继续保留 1000 条上限与列表虚拟化。
  按用户后续反馈移除自动滚动勾选框及回到最新浮层，清空按钮改为透明无边框，空态只淡化图标和文字。
  GUI x64 Release build 通过（0 警告、0 错误）；空态、4 条、长列表离屏渲染检查通过。
  临时 UI 验证调用实际处理方法，覆盖倒序追加、高亮更新、手动翻阅暂停、自动恢复、保留上限、
  清空后追加与回到顶部自动恢复；用户实测反馈「没问题了」，确认本轮运行动态优化通过。

- 2026-09-15：按按钮状态设计图细化实时截图控件。显示分身采用蓝色窗口/眼睛图标与浅蓝底，
  隐藏分身改为灰色划线眼睛图标与中性底；两者有独立 hover/pressed 配色，未连接时常驻 disabled。
  按钮圆角复用现有 Radius.Control。截图标题栏与放大关闭按钮统一无边框、浅灰 hover/pressed 样式；
  打开放大时先聚焦模态容器，关闭按钮不再默认出现蓝色焦点框，Tab 导航仍有轻量焦点提示。
  GUI x64 Release build 通过（0 警告、0 错误）；XAML 渲染及显示/隐藏/禁用状态检查通过；
  放大尺寸、背景恢复、原生标题栏拦截与退出恢复回归通过。用户实测反馈「通过，我很满意」，本轮按钮优化验收通过。

- 2026-09-15：完成运行控制与实时截图 UI 调整及只读放大查看，用户实测反馈「没有新问题」并确认满意。
  卡片采用蓝色图标、浅色画面区与空状态，不复刻参考图的大圆角；分身入口采用图标胶囊按钮，
  按真实状态切换「显示分身 / 隐藏分身」，Tooltip 与辅助名称同步，复用原有分身显隐操作。
  放大层共享 Home 最新帧，按帧比例适配窗口并保留 8px 画面内边距；浅色卡片外为 12px 高斯模糊背景
  和半透明遮罩，背景与标题栏操作在放大期间禁用；关闭按钮、点击外围遮罩或 Esc 均可退出并恢复交互。
  临时 HwndSource hook 阻止 WPF-UI 原生标题栏操作绕过遮罩，退出放大时移除；关闭按钮固定 32×32。
  GUI x64 Release build（0 警告、0 错误）、GUI 自检、XAML 加载、离屏渲染和 diff 检查通过；
  自检首次受沙箱 Named Pipe 权限限制，沙箱外重跑通过。临时 UI 验证覆盖 3 种窗口尺寸 × 3 种画面比例、
  两种分身文案、关闭/遮罩退出和模糊/禁用恢复；屏幕外窗口的真实 HWND 命中、最小化/最大化/关闭/还原
  命令拦截与 hook 移除均通过。用户实测确认不扩展为未逐项记录的停止、断线等完整边界验收。
  放大仍使用现有最大 640×360 帧，不提升采集分辨率，不增加画面点击控制，不改动 Worker/Child Session 流程。

- 2026-09-15：运行控制 Header 的 ProgressRing 覆盖开始/停止请求在途和 Worker 的 Starting/Stopping
  状态，过渡期间隐藏其他操作按钮，避免任务开始与停止时短暂空白；Tooltip 与辅助名称同步显示具体操作。
  GUI x64 Release build 通过（0 警告、0 错误），`git diff --check` 通过。真实任务过渡动画仍待交互式复验。

- 2026-09-14：修复游戏准备仅检查 `Launch.exe` 导致可能漏掉启动器交接后的微端进程；固定 profile
  在启动前与启动后同时检查同一 Session 的 `Launch.exe` / `QQMicroGameBox.exe`，普通程序保持原规则。
  超时保留失败并提示打开完整桌面检查后重试；Header 在准备失败但 Worker Ready 时仅显示重试，
  修复重试/开始图标重叠。COM/RDP/WTS/Worker 启动和清理流程未改。
  GUI x64 Release build 通过（0 警告、0 错误）；GUI 自检通过，包含交接进程、跨 Session 拒绝与普通程序回归。
  自检首次因沙箱 Named Pipe 权限失败，沙箱外重跑通过。真实微端启动及故障 Header 交互仍待复验。
  2026-09-15 完成独立完整测试包 `artifacts/NarutoAutoGUI/runtime-launch-handoff-fix` 核对，GUI DLL
  哈希与构建产物一致；保留用户任务配置，清理副本中的旧 state、debug 和 logs，未覆盖原测试包。
  22:31 日志只证明未枚举到启动器，不足以断定该次游戏实际已启动；不把本次修复记为真实启动验收通过。

- 2026-09-14：首页“运行控制”Header 内部 Grid 固定 36px 高，避免 36px 操作按钮与 18px
  ProgressRing 在状态切换时改变整行高度。GUI x64 Release build 通过（0 警告、0 错误）；
  已生成并核对独立完整包 `artifacts/NarutoAutoGUI/runtime-control-fixed-height` 的 GUI DLL。

- 2026-09-14：补齐首页运行控制 Header 中遗漏的停止图标尺寸与按钮样式，并同步重试图标：
  Power/Play/Stop/Retry 均使用 24px `SymbolIcon.FontSize` 和 36px 透明底热区。核对当前
  Wpf.Ui 枚举中四个 Symbol 均有效；GUI x64 Release build 通过（0 警告、0 错误）。
  独立完整包 `artifacts/NarutoAutoGUI/runtime-control-icons-aligned` 已生成并核对 GUI DLL 哈希。

- 2026-09-14：首页“运行环境”Header 改名为“运行控制”，标题采用现有 14px SectionTitle
  字号；主操作图标保留 24px 字形和 36px 热区，常驻蓝色底色改为透明，仅 hover 显示浅主题底色；
  Header 上下 Padding 从 3px 收至 1px。GUI x64 Release build 通过（0 警告、0 错误），
  独立完整包 `artifacts/NarutoAutoGUI/runtime-control-polish` 已生成并核对 GUI DLL 哈希。

- 2026-09-14：用户截图指出只增大了按钮容器，Power/Play 字形仍小。首页运行环境 Header
  删除左侧状态点，Power/Play 的 `SymbolIcon.FontSize` 与容器均设为 24px，操作热区 36px，
  使用已有 Primary 浅色 Brush 提升辨识度；准备中仍用 ProgressRing，其他页面区域未调整。
  GUI x64 Release build 通过（0 警告、0 错误），已生成并核对独立完整包
  `artifacts/NarutoAutoGUI/prominent-runtime-icons`；新包视觉仍待交互式检查。

- 2026-09-14：首页运行环境 Header 的 Power/Play 实际图标增至 20px，操作热区保持紧凑；
  准备状态的 Wpf.Ui ProgressRing 明确设置 `IsIndeterminate=True`，以持续旋转表达没有可靠百分比
  数据的启动流程。GUI x64 Release build 通过（0 警告、0 错误），图标枚举值已核对；生成
  `artifacts/NarutoAutoGUI/icon-progress-fix` 独立完整包并核对 GUI DLL 哈希。尚未进行新包交互式检查。

- 2026-09-14：用户截图确认 `PlugConnected16` 在“准备运行环境”按钮中显示为不符合预期的
  插头形状；改用当前 Wpf.Ui 枚举存在的 `Power20`（18px 呈现）表达开机/准备。GUI x64 Release
  build 通过（0 警告、0 错误）。正在使用的上一份独立包 DLL 被锁定，未强行替换；已生成
  `artifacts/NarutoAutoGUI/power-icon-fix` 完整副本并替换为最新 GUI DLL，哈希已核对。

- 2026-09-14：修复“运行环境”准备图标 `Power16` 不属于当前 Wpf.Ui `SymbolRegular` 而导致的
  `MainWindow.InitializeComponent()` 启动崩溃，改用已存在的 `PlugConnected16`。通过发布目录中的
  `Wpf.Ui.dll` 枚举核对首页 XAML 全部 7 个 Symbol 值，均有效；GUI Release build 通过（0 警告、
  0 错误）。隔离目录完整 publish 因本机 NetBeauty 缺少 `v10.0.11/win-x64` artifact 失败，
  未覆盖正在使用的 `artifacts/NarutoAutoGUI/win-x64`。已从现有完整包复制独立
  `artifacts/NarutoAutoGUI/header-startup-fix` 并替换修复后的 GUI DLL；原目录 DLL 被现有进程锁定，
  无法原地更新，新版本 GUI 尚未完成启动实测。

- 2026-09-14：首页“运行环境”Header 右侧改为纯图标：灰/绿/红状态点、Power/Play/Stop/Retry
  操作图标及准备时的 ProgressRing；状态和操作文案仅保留在 Tooltip/自动化名称。Release build
  通过（0 警告、0 错误），XAML 静态检查确认 Header 无状态或操作 TextBlock。启动新 build-output
  EXE 仍返回 `0xc0000142`；当前桌面已有 artifacts 目录的旧 GUI 实例，新 DLL 入口未呈现独立窗口，
  因此本轮未完成新版本四态交互式验收，也未关闭旧实例或影响现有 Session。

- 2026-09-14：首页运行控制改为右上角“运行环境”单行 Header 的准备、开始、停止和重试入口；
  移除左侧任务工作区底部操作栏及其占位。Header 依据现有 Child Session、Worker ActiveRun/LastRun、
  准备操作状态切换，运行失败时重试任务，运行环境故障时重试准备。GUI Release build 通过
  （0 警告、0 错误）；尝试启动 build-output EXE 仍返回 `0xc0000142`。通过 `dotnet NarutoAutoGUI.dll`
  启动后日志记录 GUI 初始化，但桌面自动化未枚举到应用窗口；未完成四态交互检查。

- 2026-09-14：首页右侧“运行环境”收为单行状态 Header，使用 Child Session、Worker 快照与准备操作状态显示
  未准备 / 准备中 / 已就绪 / 异常；只在未准备和异常时分别提供准备、重试图标。移除折叠正文、重复的
  Worker/MaaNOP/Session/IPC 信息和常驻文字操作；刷新入口不再需要，结束桌面能力仍在托盘。
  GUI Release build 通过（0 警告、0 错误）。尝试启动 build-output EXE 时系统返回 `0xc0000142`，
  本轮未完成新 Header 的交互式状态检查。

- 2026-09-14：首页视觉 polish 依据 Computer Use 的普通窗口和最大化窗口截图完成三轮检查：先压缩运行环境的
  状态行并把截图操作放入 Header，再删除首页空计划时重复的“执行计划为空 / 前往任务”底栏、收紧侧栏按钮、
  去掉展开任务选项的内层卡框和阴影，最后移除运行动态的内层日志边框。MaaNOP v2.3.0 的 `python`、
  `resource`、`agent`、`interface.json` 已复制到本地测试构建输出以加载任务。最终普通窗口截图已复查；
  最后一处日志边框改动后的最大化输入受测试程序管理员权限影响未能重复触发，上一轮最大化布局已检查。
  正式 GUI Release build 通过（0 警告、0 错误）；未进行真实 Session、Worker 或游戏运行回归。

- 2026-09-14：首页改为可扩展任务工作区加 392px 固定运行侧栏。首页和“任务”导航共用原有任务编辑控件与事件处理；
  侧栏依次放置可折叠的运行环境、16:9 实时截图及填满剩余高度的运行动态。保留全局状态栏和现有执行、
  Preview、Session 语义。GUI Release 构建通过；新布局的真实桌面/DPI 交互检查仍待进行。

- 2026-09-12：完成 MaaNOP Updater runtime consolidation。Updater 改为非 single-file 入口（EXE/DLL/deps/runtimeconfig），
  使用 NetBeauty loader + `includedFrameworks` + `SubdirectoriesToProbe=libs` 复用 GUI 的 app-local runtime；发布包只保留
  一份根 `libs/`，GUI 安装前将 Updater 入口、`hostfxr.dll`、`hostpolicy.dll`、共享 `libs/` 和 bootstrap 文件复制到安装目录外
  的 TEMP，再按原顺序执行 `--probe`、runtime shutdown 和 InstallTransaction。真实 TEMP 副本 `--probe`、正式 locked build、
  package validation、published GUI/Worker self-test 及 Updater 自动化均通过；未创建 stable release、未运行真实更新重启 E2E。
  真实 `D:\MaaNOP-Updater-E2E-v2\MaaNOP` 改造前为 675,407,181 bytes（644.12 MiB），其中单文件 Updater 为 140,025,042 bytes；
  仓库 baseline 对比包由 479,271,285 bytes / 206,794,374-byte ZIP 降至 339,705,000 bytes / 146,111,232-byte ZIP。
  新 Updater 入口合计 456,699 bytes，按同一 MaaNOP 完整包内容替换后为 535,838,838 bytes / 237,003,306-byte ZIP，
  完整包分别减少 139,568,343 bytes 和 60,684,044 bytes；新 Updater 额外体积约 0.44 MiB，低于 10 MB。

- 2026-09-11：按 Updater 规格对照补齐 SemVer 任意长度数字标识符的比较，并增加超出 `uint` 范围的核心版本和预发布版本回归；
  同时修正 Updater UI XAML 的 120 列格式问题。Updater 自动化在进入本机缓存目录权限检查前已通过这些 SemVer 断言；
  完整测试仍受本机 `LocalApplicationData` 缓存目录权限限制。

- 2026-09-11（已由上方规格对照修正版本范围）：Updater review 最小修复；完成记录在版本解析
  成功后才消费；TEMP Updater 启动成功后，GUI 清理异常仍继续退出，不恢复半释放界面的操作入口。
  GUI Release build（0 警告、0 错误）及 GUI 自检通过；Updater 自动化通过，新增数字溢出、完成记录解析
  失败保留及一次性消费回归。完成记录测试使用独立随机缓存目录，需要沙箱外本地缓存写权限。
  本次测试产物已能运行，不代表真实 Updater EXE、运行态关闭与重启 E2E 已验收；未运行交互式更新。

- 2026-09-11：完成 Updater 双轴审查修复：手动检查明确显示兼容包/摘要错误；安装前 staging 失效时
  废弃准备状态并恢复下载入口，运行态/TEMP 启动失败仍允许重试安装。规范审查无待修项，规格审查两项已复核修复。
  GUI Release build 与 GUI/Worker 自检通过；本次 Updater 测试 DLL 被应用控制策略以 `0x800711C7` 拦截，
  因此不声明本次完整自检通过。用户重启后仍报告无法启动，并要求暂不处理；未继续运行 EXE 或调整系统保护策略。
  2026-09-10 的 Updater 自动化通过记录保持为历史证据，不能替代当前产物启动或实机 E2E 验收。

- 2026-09-10：实现 MaaNOP Updater V1 的 GUI 检查/下载/校验/安装接管和独立 TEMP Updater 目录事务。
  只按 Project Interface 的 github/version 检查最新正式完整包，Home Banner/Drawer 与 Settings 呈现更新，
  流式下载、SHA256、安全 staging、用户 config/logs copy、完整目录 swap/rollback 及单旧备份策略已接入。
  安装优先 Stop，再复用现有 Child Session 注销清理；只确认已有跟踪进程与文件释放，不增加通用进程身份系统。
  TEMP Updater 先做启动可用性检查，再停止运行环境；完成提示不承担 health handshake 或备份清理职责。
  GUI、Worker、Updater 构建及包含单文件 Updater 的 locked baseline publish 通过；GUI/Worker 自检、Updater
  自动化和 baseline package layout 检查通过。未运行真实 EXE/游戏 E2E：用户报告本机保护策略阻止新 EXE，
  需重启后按既定 Windows 手工 E2E 计划复验。
  MaaNOP 完整包消费新 baseline、github/version 元数据、内置 Python 和真实 Release digest 仍需发布集成；
  不把本仓库 baseline 构建或自动化通过描述为完整产品升级已验收。

- 2026-09-06：完成 NarutoAutoGUI 程序木叶村风格应用图标设计与系统集成。
  基于用户满意的火之意志木叶旋涡与圆角黑色底板设计，完成高精度圆角外透明切边与居中平移，
  导出 512×512 PNG 与多尺寸（256/128/64/48/32/24/16）Windows ICO 图标。
  集成到可执行文件 PE 元数据（ApplicationIcon）、主窗口 FluentWindow、TitleBar 图标以及系统托盘 NotifyIcon。
  Release build、自动化自检、120 列与 `git diff --check` 全数通过。

- 2026-09-06：合并 `7badeec` 的顶层 Task callback 终态修复，使用 `Tasker.Task.Starting/Succeeded/Failed`
  驱动运行状态，不再轮询可能已被 runtime 清除的 `job.Status`。同时保留 Stop/cleanup 互斥与 Preview 重复取消保护；
  两者分别覆盖终态来源和迟到停止的资源生命周期竞争。合并两组自检并适配 Run 级 Preview revision 构造参数。
  Worker Release `win-x64` build（0 警告、0 错误）及包含 callback completion、Stop/cleanup 竞争的自检均通过；
  120 列与 diff 检查通过。真实 MaaNOP 自然结束、停止及再次运行仍待交互式复验。

- 2026-09-06：修复 Worker 自然结束清理与迟到 `run.stop` 并发时访问已释放 Tasker 的竞争。
  `WorkerRuntimeExecution` 将 Stop 与 cleanup 串行化；清理先进入时，迟到 Stop 等待清理并沿既有 Run 终态流程结束，
  不再使用 `_taskerReady` 中保留的旧 Tasker。Stop 先进入但未确认时继续保留 context，并保持 `StopTimedOut` 语义，
  不允许普通清理结果覆盖停止失败。Preview 重复取消忽略 `ObjectDisposedException`。未修改 GUI、IPC 或 Child Session。
  Worker Release `win-x64` build 通过（0 警告、0 错误），build-output `--self-test` 通过；新增自检覆盖清理中停止、
  清理后重复停止、已释放 Preview 取消、Stop 先进入时阻止清理及停止未确认后的 context 保留与超时结果。
  受影响 C# 文件 120 列与 `git diff --check` 检查通过。真实游戏自然结束后点击停止的交互式复验仍待完成；
  本轮未终止或替换当前运行中的 GUI、Worker 或游戏进程。

- 2026-09-05：修复同一 Run 跨 Plan Item 后 Home Preview 长时间停更的问题。预览 revision 改由
  `WorkerHost.AcceptRun` 创建的 Run 级计数器分配，后续 execution 共享该计数器；各项仍独立持有和清理
  Controller 与 latest-frame cache，不修改 GUI、IPC schema 或 Child Session 生命周期。Worker 自检补充上一项
  缓存清空后下一项首帧继续递增（含相同 PNG）、重复内容去重、新 Run 从 1 开始，以及停止时在途帧不消耗序号。
  NarutoAutoWorker Release `win-x64` build 与 build-output `--self-test` 通过，0 警告、0 错误；新增/修改代码
  120 列与 `git diff --check` 检查通过。真实游戏跨 Plan Item 的 Preview 连续刷新仍待交互式复验。

- 2026-09-02：完成 Preview 卡片与 Child Session 操作区常驻与 UI/UX Polish。
  Preview 画面容器优化为更饱满的媒体层（Media Surface），最大化占满卡片可用宽度并保留 16:9 比例与最大尺寸限制；
  弱化空状态占位，采用淡雅 Desktop 图标与中性字号文本；
  底部桌面控制收口为统一靠右的 Action Cluster（间距 8px）；
  底部 Child Session 按钮始终常驻占位，无 Session 时 disabled，有 Session 后按状态启用并切换文案；
  无 Session 时在操作按钮下方靠右展示精炼提示说明「需先准备运行环境」，有 Session 后自动隐藏；
  升级显隐桌面按钮为 Neutral Secondary 按钮并内嵌 Desktop 图标与 AccessKey 快捷键（`_H`/`_S`）；
  升级溢出按钮为 34×34 方形 Fluent `MoreHorizontal` 图标按钮；
  Overflow Menu 采用紧贴右对齐定位（与按钮右边缘对齐、紧贴下方 2px）并应用 Destructive 浅红 tint 样式；
  Release build、`build.ps1` 与 `test-automated.ps1` 自动化自检全数 PASS。

- 2026-09-02：完成 Home / Dashboard V2 UI/UX 重构。明确 Home 作为 Runtime Console 的产品定位；
  采用 top-flow 布局（主内容容器 MaxWidth=1240 居顶流式排列）；删除原四栏 Dashboard，改为轻量 Run Context 状态卡片；
  收口 Primary CTA 为单一大按钮（支持前往任务、准备环境、开始任务、停止任务、过渡中状态切换，开始任务采用绿色 TasksAccent 强调色）；
  日志目录按钮改为右上角 Subtle 按钮带 Folder 图标；主体调整为 16:9 游戏画面预览（42%）与运行日志（58%）双栏；
  Child Session 状态在预览 Header 展示，底部收口为高频桌面显隐按钮与低频危险操作溢出菜单（`[⋯]` 承载结束桌面分身）；
  增加运行中 $X / N$ 与已运行时间（`HH:mm:ss`）动态计算和刷新，未运行时不显示 `0 / N`；Release build 与自动化自检全数 PASS。

- 2026-09-02：完成 Tasks V2 纯视觉 Polish。弱化 Task Shelf 外层边框嵌套感，透明化外层容器以突出 Command Chip 主体；
  Plan Item 展开态改为中性边框、轻量浮层阴影与左侧 3px 绿色 accent line 局部强调，去除整圈粗绿边框；
  Option Editor 容器采用更轻量表面色 `Brush.Surface.Option` 与弱边框 `Brush.Border.Subtle`，紧凑化 padding 与 margin；
  Task Description Drawer 遮罩调整为轻量 dim `Brush.Overlay.Dim`（#20000000），增加专用左向投影 `Effect.Surface.Drawer`；
  规范 Tasks 页面垂直节奏与间距；Release build 与自动化自检全数 PASS。

- 2026-09-01：修复多项 Run 在停止已接受后仍可能终结为 `Succeeded` 或 `Failed` 的状态竞争。
  `WorkerHost.CompleteRunLocked` 现在优先应用 ADR 0012 的停止获胜语义，将已进入 `Stopping` 的 Run 统一终结为
  `Cancelled`；`StopTimedOut` 仍沿既有分支保持 `Stopping` 并将 Worker 置为 `Faulted`，`CleanupFailed` 仍使 Worker
  进入 `Faulted`。新增 Worker 自检覆盖停止与 `Succeeded`、`Failed`、`Cancelled`、`CleanupFailed` 终态竞争，
  同时保护未停止 Run 的既有映射。NarutoAutoWorker Release `win-x64` build 与 build-output `--self-test` 均通过，
  0 警告、0 错误。

- 2026-08-31：完成 Tasks V2 UI/UX 重构。Tasks 页面改为上方可折叠 Task Shelf 与下方有序执行计划；PI task 以
  text-first command chip 自动换行呈现，不显示 task icon、summary 或 description。Plan Item 使用单开 accordion，
  参数编辑器随 item 移动；新增通用一至三列 `ResponsiveWrapPanel`，按控件实际期望宽度将明显宽项退化为整行。
  option/input description 只通过 label 旁可聚焦 info tooltip 显示。task description 继续使用既有安全纯文本渲染，
  改由右侧 overlay drawer 显示，支持关闭按钮、Esc 和点击外部关闭，主内容不 reflow。drag handle 是唯一鼠标拖动入口，
  使用绿色 drop indicator，拖动前折叠，drop 后保持折叠；另提供 `Alt+↑/↓` 键盘排序。删除无需确认。
  `SelectedTasks` 现保存不重复 task 的实际执行顺序，ProjectModel 生成同序多项 Run Plan，Worker 在不改变协议 schema 的
  前提下逐项执行；当前项失败或停止时取消尚未执行项。`ExplicitOptions` 仍按 option name 共享，不新增 TaskInstanceId。
  NarutoAutoGUI 与 NarutoAutoWorker Release `win-x64` build 和两组 build-output `--self-test` 均通过，0 警告、
  0 错误；正式加载 `D:\MaaNOP\assets\interface.json` 通过，识别 2 个 task 并生成同序 2 项 Run Plan。XAML XML、
  overlay/layout 静态契约、whitespace format、120 列与 diff 检查通过。真实鼠标 drag、tooltip、drawer、多 DPI 视觉回归及
  MaaFramework 多项连续实跑仍需人工确认。

- 2026-08-27：修复 Windows 10 下 RDP ActiveX 初始化失败 `0x80040111 (CLASS_E_CLASSNOTAVAILABLE)`。
  将 `RdpActiveXHost.RdpClientClsid` 从 Win11 专用的 MsRdpClient11 CLSID 修正为 Windows 10 与 Windows 11 全面兼容的标准
  MsRdpClient10 CLSID (`8B918B82-7985-4C24-89DF-C33AD2BBFBCD`)；在 `ChildSessionManager.EnsureConnectedAsync`
  中为 `0x80040111` 异常增加针对 RDP ActiveX 控件与显卡驱动的明确可行动报错提示；将 Worker 进程验证超时放宽至 30 秒以
  自适应虚拟机冷启动登录延迟；在 `SelfTestRunner` 中新增 `VerifyRdpClientClsid` 回归自检，防止 CLSID 被误改。Release
  build 与 `test-automated.ps1` 全部 PASS。


- 2026-08-27：支持 Agent 可执行文件相对路径解析（如 `./python/python.exe`）。
  `DependencyProbe.ResolveAgentExecutablePath` 在 `child_exec` 为相对路径且目标文件存在于 `WorkingDirectory`
  时，自动解析为绝对路径，避免 Worker 位于 `worker/` 子目录时因 Windows `CreateProcess` 相对基准目录差异导致找不到
  Python 产生 `PythonMissing` 错误；保留绝对路径直接使用与 PATH 命令名回退行为。更新 `DependencyProbe`
  与 `WorkerRuntimeExecution`；新增 `VerifyAgentExecutableResolution` 自检覆盖相对路径、绝对路径和系统命令解析。

- 2026-08-27：将 NarutoAutoWorker 使用的 MaaFramework runtime 统一升级到 5.12.3。升级
  `Maa.Framework` (5.10.0) 与 `Maa.Framework.Runtimes` (5.12.3)，匹配 `WorkerRuntimeExecution` 的
  `Win32ScreencapMethods` API；更新 `packages.lock.json`；通过 Release build、`test-automated.ps1`
  与 `validate-package.ps1` 校验。

- 2026-08-27：将全仓库项目统一升级到 .NET 10。NarutoAutoGUI GUI 与 NarutoAutoWorker 统一面向
  `net10.0-windows`，NarutoAutoGUI.Protocol 与 NarutoAutoGUI.ProjectModel 统一面向 `net10.0`；GitHub Actions
  发布工作流与构建环境升级使用 .NET 10 SDK (`10.0.x`)。更新 project lock files (`packages.lock.json`)、清理过时
  .NET 8 / .NET 9 文档描述，保持 Windows x64、self-contained、Maa.Framework 5.8.0 runtime 与现有发布结构不变。

- 2026-08-27：将火影忍者 Online 游戏启动从用户可配置项收口为固定 launch profile。新建
  `NarutoGameLaunchProfile`（AppId=`1103286479`、Arguments=`-/appid:1103286479`、ExecutablePath 从
  `Environment.SpecialFolder.ApplicationData` + `Tencent\QQMicroGameBox\Launch.exe` 推导），不再硬编码 Windows
  username，不暴露 AppId override，不读 settings.json。删除 `AppSettings`、`AppSettingsStore`、
  `settings.json` load/save/reset 流程、App 启动时"Application Settings 无效"的整套逻辑、
  `ApplyLegacyGameSettingsMigration`、Settings 页中的游戏程序 TextBox / 浏览按钮 / 启动参数 TextBox / 说明文案、
  `BrowseGameButton_Click` / `BrowseExecutable` / `PathsTextBox_LostKeyboardFocus` / `SaveSettings` /
  `TrySaveSettings`。`PrepareEnvironmentButton_Click` 改用 `NarutoGameLaunchProfile.Resolve(_logger)`；
  `LoadProject` 直接计算 `AppContext.BaseDirectory\config\maanop-config.json`。
  `ChildSessionProgramService` 错误文案去除"配置"字样，改用通用"executable 路径不能为空""指定的程序不存在"。
  启动器缺失时 `NarutoGameLaunchProfile.Resolve(logger)` 抛出面向安装的可行动错误
  "未检测到火影忍者 Online 微端启动器。请先通过 QQ 游戏平台安装或启动一次火影忍者 Online。"，并在
  diagnostic log 记录实际路径。Settings 页删除全部可配置项后只保留静态应用行为说明。
  Release build 与 build-output `--self-test` 通过，0 警告、0 错误；新增 `VerifyGameLaunchProfile` 自检
  覆盖 ApplicationData 推导、不包含 username、固定 AppId/Arguments、启动器缺失 actionable error 和
  production default。

- 2026-08-27：将 MaaNOP Project Directory 从用户可配置项收口为打包约定。删除 AppSettings.MaaNopProjectDirectory、
  Settings 页面中的 MaaNOP 项目目录 TextBox / 浏览按钮 / 说明文案、AppSettingsStore 中的
  NormalizeProjectDirectory、ReadLegacyProjectDirectory、`MaaNopExecutablePath` → Project Directory 旧迁移和
  `requireInterface` 目录校验；SchemaVersion 由 2 bump 至 3，旧 settings.json 不再兼容，不实现 migration；
  production 默认使用 `AppContext.BaseDirectory` 作为唯一 Project root，`interface.json` 从
  `NarutoAutoGUI.exe` 同级目录加载。`ProjectPlanModule.Open(projectDirectory, configPath)` 签名保留作为
  self-test 的 test seam，production 调用点传入 `AppContext.BaseDirectory`。缺失 `interface.json` 时
  ProjectInterfaceLoader 抛出“安装目录缺少 interface.json，请确认使用完整的 MaaNOP 发布包。”并附带
  `AppContext.BaseDirectory` 到 diagnostic log；不再提示“前往设置选择项目路径”。Settings 页面只保留游戏启动
  与应用行为，未留空占位。当时既有的游戏启动配置、Child Session、Worker、IPC、Preview、Tasks 页面与发布打包
  行为未改。`MainWindow.xaml`、`MainWindow.xaml.cs`、`AppSettings.cs`、`AppSettingsStore.cs`、
  `ProjectInterfaceLoader.cs`、`SelfTestRunner.cs` XML 解析、Release build 与 build-output `--self-test`
  通过，0 警告、0 错误；新增 `interface.json` 缺失错误信息与 v3 schema 未知字段拒绝自检覆盖。

- .NET 10 WPF x64 正式主窗口和独立 RDP 子桌面预览。
- 创建/恢复、显示、隐藏、结束 Child Session；展示连接状态、RDP ConnectedState 和 `childSessionId`。
- 启动时探测已有 Child Session 并自动恢复 RDP 连接。
- 固定 `1920×1080 @ 100%`，不提供分辨率或 DPI 配置。
- 火影忍者 Online 使用固定 launch profile：`NarutoGameLaunchProfile` 从 `%APPDATA%\Tencent\QQMicroGameBox\Launch.exe` 推导启动器路径，AppId 固定 `1103286479`，参数固定 `-/appid:1103286479`，均不由用户配置。MaaNOP project payload 与 NarutoAutoGUI 一同打包，Project root 固定为 application base directory，`interface.json` 位于 `NarutoAutoGUI.exe` 同级目录。工作目录自动取启动器所在目录，不提供配置字段。
- 启动器缺失时给出面向安装的可行动错误，提示用户通过 QQ 游戏平台安装或启动一次火影忍者 Online。
- 分别启动游戏/MaaNOP，以及“恢复/创建 → 显示 → 游戏 → MaaNOP → 保持显示”的一键启动。
- 按进程名和 Session ID 避免在当前 Child Session 中重复启动，并记录 PID/SessionId。
- 主窗口 X 隐藏到托盘；托盘提供显示主窗口、显示子桌面、结束分身和退出。
- 真正退出且 Session 存在时确认；确认后注销 Session，注销失败则取消退出。
- DEBUG/INFO/WARN/ERROR/CRITICAL diagnostic log 写入程序目录滚动文件，可从 GUI 直接打开日志目录；GUI 运行日志
  只显示 MaaNOP 字符串 `focus` 投影出的 `maanop.run`，不显示 GUI/Worker/IPC/RDP 等诊断信息。
- GUI 按实际 Session 状态启用创建/显示/隐藏/结束命令，并明确区分子桌面可见与已隐藏；异步操作显示进度与等待光标。
- 首页游戏画面预览底部最多呈现两个桌面操作：单一上下文桌面可见性按钮按 ConnectedVisible / ConnectedHidden 互斥显示「隐藏桌面」或「打开完整桌面」，无 Session 时 Collapsed；「结束桌面分身」按 Session 是否存在显示，使用 destructive 样式且不作为 Primary Action。
- GUI 日志仅在用户接近底部时自动跟随；向上滚动后暂停，并显示新日志计数与恢复跟随操作。
- 主窗口提供访问键和动态状态辅助信息；操作失败给出恢复建议并可打开日志目录；每次程序运行首次关闭到托盘时显示一次通知。
- 主窗口已应用集中式 WPF 视觉设计系统：统一浅色语义令牌、字体与 4/8 DIP 间距、四级按钮、输入框、状态 Badge、日志层级和交互状态；顶部保留状态卡，其余主功能使用扁平分区、留白与细分隔线，仅日志视口保留容器边框；不改变事件处理器和功能行为。
- 正式主窗口已使用 WPF-UI 4.3.0 重构为 Windows 11 Fluent Shell：`FluentWindow`、`TitleBar`、左侧 `NavigationView` 和内置 Fluent 图标承载首页、任务、设置三个顶层页面，操作状态与进度条固定在全局底栏。三个页面仍位于同一个 `MainWindow` XAML namescope，通过根容器 `Visibility` 切换；未引入 `Frame`、独立 Page/UserControl、MVVM、NavigationService 或 PageService。页面内输入、按钮、下拉框和日志列表仍为标准 WPF 控件并沿用 `DesignSystem.xaml`。独立「桌面分身」与「日志」导航页面已删除；桌面分身的显示 / 隐藏 / 结束操作迁移到首页游戏画面预览底部，「准备运行环境」继续作为创建 / 恢复 Child Session 的主流程；详细诊断保留文件日志，首页「运行动态」继续作为普通用户唯一 GUI 日志视图，首页顶部「打开日志目录」入口保留。
- 首页集中呈现当前任务、参数、Session、Worker 和 Run 的用户化摘要；任务页采用可折叠 Task Shelf + 有序执行计划，
  Plan Item 内联承载动态 property editor，task description 由右侧 overlay drawer 显示并解析 `<span>`/`<br>` 为换行与
  纯文本；未配置或无法加载项目时继续使用单一空状态。Tasks 不提供运行环境诊断；用户化 Worker/Run 状态仍由首页与
  全局底栏呈现，运行细节保留在日志。设置表单保持标签在上、输入框在下。
- 首页“运行控制台”顶部以单一横向区域呈现当前任务、现有状态投影、当前 Plan Item/下一步和单一上下文主操作按钮；
  下方以等宽双列呈现内部 16:9 游戏画面与 `maanop.run` 运行动态。游戏画面在 Active Run 的 Starting/Running 期间通过
  Worker latest-frame cache 固定约 5 FPS 只读显示；Idle、Stopping、终态、断线、Worker replacement、窗口隐藏或离开
  Home 时继续显示原 Placeholder，窗口最小化也停止请求。运行动态图标只按既有日志 Level 映射，底部状态栏只投影已有
  整体、Worker、Session、IPC 连接和操作状态；Home 仍使用禁用横向滚动的外层纵向 overflow fallback。
- 首页顶部「操作」列以单一上下文主按钮呈现当前阶段需要的唯一操作（准备运行环境 / 开始任务 / 停止任务），
  模式与样式随 Child Session / Worker / Run 状态自动切换；Tasks 页只负责选择与配置，
  不再重复运行控制。当前不可执行的首页操作显示禁用态，动态下一步提示继续作为首页说明、悬浮提示和
  辅助功能 HelpText。任务参数仍保持自动保存；现有协议仍为停止/取消本次 Run，不声明暂停后恢复能力。
- 开始任务只要求 Child Session 已连接、Worker Ready、Snapshot fresh 且任务配置有效；子桌面显示或隐藏均可开始，不再将隐藏预览作为 Run 前置条件。
- 全局 GUI、后台任务和进程异常记录；预期的启动/RDP/Win32/COM 失败不会直接使 GUI 崩溃。
- `WTSGetChildSessionId` 将成功返回 `ULONG(-1)` 或本机实测的 `ERROR_NOT_FOUND (1168)` 识别为“无 Child Session”，其他原生调用失败保留错误码并抛出；退出检查无法确认状态时继续取消退出。
- RDP 状态读取 ActiveX 实时 `ConnectedState`；所有非主动断开都会转入故障状态，后续操作重建预览宿主而不是复用失效连接。
- 同一 Windows Session 只允许运行一个 NarutoAutoGUI 正式 GUI 实例，第二实例提示后退出。
- 主窗口和托盘的 Session/程序操作使用同一个应用级操作门；退出从入口禁止新操作，等待在途操作完成，并在注销前后重新确认 Session 状态。
- 正式 GUI 使用最终 MaaNOP Config schema、真实 Project Interface 默认解析、不可变单项 Run Plan 和 Canonical Digest v1；通过用户级 Named Pipe 管理 Child Session Worker 的 admission、Snapshot、Run/Stop 和有界日志。
- Worker 以 `MaaTasker.Callback` 为唯一 MaaNOP 运行日志接入点，只将与 Callback message 精确匹配的字符串
  `focus` 投影为既有 WorkerLogEntry；GUI 按 `source=maanop.run` 精确过滤。日志 cursor 按 Worker Instance 隔离，
  实时 sequence gap 不再越过缺口，而由 `log.getSince` 单飞补取；原有协议 schema 保持不变。
- 正式 GUI 从真实 PI 通用生成 global 与各计划 task 的 input、switch、select、递归 active option 编辑器；显式值写入
  SchemaVersion 1 `maanop-config.json`，可恢复为跟随项目默认，未激活子分支的合法显式值作为 Dormant Intent 保留。
  `SelectedTasks` 按执行顺序保存且拒绝重复 task；不包含硬编码 `ServerRange`、task entry 或 pipeline override。
- 当前固定单 Win32 controller、单 resource 的 PI 子集明确拒绝尚未实现的非空 `controller.option`、`resource.option`，以及 `resource.controller`、`task.controller/resource` 和 `option.controller/resource` 约束字段，不再接受后静默忽略；本机 MaaNOP v1.3.0 真实 `interface.json` 未使用这些字段。
- ProjectModel 保持 `ProjectPlanModule` 外部 interface 不变，将内部 definitions、Project Interface Loader 和 option Resolver 拆分到独立文件；Loader 在返回 `ProjectDefinition` 前统一校验 option 类型结构、所有 input 默认值/正则/`pipeline_type`、全部 option 引用和包含未激活 case 的完整递归图，Resolver 与配置编辑器只消费已验证的 PI 模型。
- ProjectOptionResolver 按当前受支持 PI 子集输出有序 `pipeline_override` 数组：task 自身 override 先进入数组，再依次追加 global 与 task option，active nested option 紧随父 case；不再在 GUI 侧递归深合并多个 fragment。Loader 同时拒绝 select/switch 顶层 `pipeline_override`，要求其 override 位于具体 case。
- PI Loader 在生成 Win32 controller definition 前校验 `class_regex`、`window_regex` 的正则语法，以及 Maa.Framework 5.8.0 支持的 `screencap`、`mouse`、`keyboard` 方法名；Worker 仍对 Launch Manifest 做防御性正则创建/匹配超时和 MaaFramework enum 映射检查。
- `ProjectTaskChoice` 只承载 task 名称与显示标签；PI 默认 option 的合法性已成为 Loader 成功返回后的不变量，不再保留 `DefaultOnlyValid/ValidationError` 双重状态。切换 task 时仍在保存 Config 前 Resolve 新激活的 option 图，以拦截非法 dormant intent。
- ProjectModel 内部使用 `OptionDefinitionKind` 与 `PipelineValueKind` 表达已验证的 option/input 类型，不再让 Resolver、配置编辑器重复解释协议字符串；`ProjectInputValue` 统一执行默认值与显式值的 verify、正则超时、InvariantCulture int/bool 解析和类型化 `JsonNode` 转换。
- Worker 自带固定 MaaFramework runtime，在 Child Session 中负责 Win32 Controller、MaaNOP Resource、MaaTasker 和每 Run Python Agent 生命周期；MFAAvalonia 不进入正常执行链。
- WorkerRuntimeExecution 持有唯一后台 producer，复用当前 Run 的唯一 MaaWin32Controller cached image，按 200 ms tick
  缩放、PNG 编码、内容去重并只缓存最新一帧；缓存使用 runId/revision/`sampledAtUtc`/像素尺寸/PNG bytes，不创建第二个
  Controller 或帧队列，并在释放 Controller 前结束 producer。
  `preview.getLatest` 使用现有 Named Pipe JSON 的 PNG + base64，预算为 PNG 1400 KiB、完整响应 2 MiB、transport 4 MiB。
  Preview 采样、编码、IPC、GUI 解码和诊断日志失败均不改变 Run 状态、结果、取消、cleanup、Worker admission 或
  Child Session 生命周期。
- Worker 使用专用的 Task Scheduler 强化启动路径：`RunEx` 后等待新的 Worker PID 并验证 Child Session，记录 Task State 与 `LastTaskResult`，再清理临时任务；进程验证成功后 PID 写回 Admission。若进程未生成则 10 秒内失败并清理 Pending Admission；若 admission + fresh Snapshot 在 60 秒内未完成且 Worker PID 缺失或进程已退出，则自动回滚 `worker.json` 与 launch manifest，避免下一次准备环境被陈旧记录阻塞。

## 本轮自动验证

- 2026-08-27：.NET 10 升级自动验证。全项目 TargetFramework 升级到 `net10.0` / `net10.0-windows` 后，使用 .NET 10
  SDK 完成 locked restore、NarutoAutoGUI 与 NarutoAutoWorker Release `win-x64` self-contained build 与 publish，
  0 警告、0 错误；`test-automated.ps1` 覆盖 GUI 与 Worker 自动自检全部 PASS；`validate-package.ps1` 校验发布包
  结构与白名单全部 PASS；`dotnet format whitespace --verify-no-changes` 通过，手写代码符合 120 列限制与大括号风格。

- 2026-08-27：Dashboard 操作区收敛。顶部「操作」列的三个独立按钮（准备运行环境 / 开始任务 / 停止任务）合并为单一
  上下文主按钮 `HomePrimaryActionButton`：未准备环境 → 准备运行环境（Secondary），环境 Ready 且无 active Run →
  开始任务（Primary），Run Running → 停止任务（Destructive），Starting/Stopping/busy/exit → 保持当前阶段文字
  但 Disabled。模式由 `DerivePrimaryAction()` 从 `ChildSessionSnapshot` / `WorkerCoordinatorSnapshot` /
  `RunState` / busy/exit 推导，不新增状态机，不通过 UI 文本反向判断；dispatcher 调用既有
  `PrepareEnvironmentButton_Click` / `StartRunButton_Click` / `StopRunButton_Click`。预览底部的「打开完整桌面」与
  「隐藏桌面」合并为单一 `HomeDesktopVisibilityButton`（ConnectedVisible → 隐藏，ConnectedHidden → 打开，
  无 Session → Collapsed），与 `HomeTerminateSessionButton` 水平居中排列为最多两个按钮。删除「仅启动游戏」快捷入口
  及只服务它的 `LaunchGameButton_Click` / `LaunchSingleAsync`；Prepare Environment 中的游戏启动逻辑不受影响。
  NarutoAutoGUI Release `win-x64` build 与 whitespace `--verify-no-changes` 通过，0 警告、0 错误，120 列审计无新增
  违规；build-output `--self-test` 因本机 Application Control policy `0x800711C7` 阻止载入
  `NarutoAutoGUI.ProjectModel.dll` 未能完成（与既有记录相同的环境限制，非代码回归）。未修改 Worker、IPC、ProjectModel、
  Preview 协议或 Child Session/RDP baseline；Primary Action 三种模式、Desktop toggle 两种模式、Terminate 确认、
  Session 不存在时按钮 Collapse 及跨页 Preview 轮询仍需人工回归。

- 2026-08-27：MainWindow 信息架构收缩。删除独立「日志」和「桌面分身」导航页面与对应导航项，主导航收缩为首页、
  任务、设置三页。桌面分身的显示 / 隐藏 / 结束操作迁移到首页游戏画面预览底部（复用既有
  `ShowSessionButton_Click` / `HideSessionButton_Click` / `TerminateSessionButton_Click` handler
  与确认 MessageBox），「准备运行环境」继续作为创建 / 恢复 Child Session 的主流程，不新增重复入口。
  「查看全部日志」按钮删除；首页「运行动态」继续作为普通用户唯一 GUI 日志视图，首页顶部「打开日志目录」保留，
  AppLogger、文件日志、Worker LogReceived、LogLines、MaaNOP focus 过滤和 Home auto-follow 均未改。
  清理 `MainSection` 枚举、`UpdateSessionPresentation`、`GetStateText` / `GetStateDetail`、
  `_logScrollViewer`、`ViewLogsButton_Click`、`CreateSessionButton_Click` 及 page-only
  `StatusBadgeStyle`；保留 `GetStateBadgeText` / `GetBottomSessionText` / `GetSessionStatusBrushKey`
  服务全局底栏。Preview 生命周期不受影响：`TryGetPreviewTarget` 仍以 `HomeView.Visibility` 为门槛。
  `MainWindow.xaml` / `MainWindow.xaml.cs` / `DesignSystem.xaml` XML 解析、Release build 与 whitespace
  `--verify-no-changes` 通过，0 警告、0 错误，120 列审计无新增违规；build-output `--self-test` 因本机
  Application Control policy `0x800711C7` 阻止载入 `NarutoAutoGUI.ProjectModel.dll` 未能完成（与
  2026-08-25/26 记录相同的环境限制，非代码回归）。未修改 Worker、IPC、ProjectModel、Preview 协议
  或 Child Session/RDP baseline；真实 Session 状态切换下的按钮 visibility/enabled、Show / Hide /
  Terminate 交互和跨页 Preview 轮控行为仍需人工回归。

- 2026-08-27：移除 Tasks 页 option editor 中显式值非默认时出现的“恢复项目默认”按钮及其
  `FollowProjectDefaultButton_Click` 与 `OptionDefaultTag`。`ProjectPlanModule.FollowProjectDefault`
  仍为公共 API 并保留自检覆盖；用户仍可在下拉框选择默认 case 或在输入框填回默认值。
  NarutoAutoGUI Release `win-x64` build 与 build-output `--self-test` 通过，0 警告、0 错误；未修改
  Worker、IPC、ProjectModel 或 Child Session/RDP baseline。

- 2026-08-27：修复 Tasks 页任务描述将 PI `<span>`/`<br>` 标记原样显示为文本的问题。`MainWindow.RenderDescriptionText`
  将 `<br>` 转为换行、剥离其余 HTML 标记后赋给 `TaskDescriptionText`；option/input 描述仍为纯文本，未改其渲染。
  新增 `VerifyTaskDescriptionMarkup` 自检覆盖 span/br、大写、null/空白、无标记纯文本和带 style 的 span。
  NarutoAutoGUI Release `win-x64` build 与 build-output `--self-test` 通过，0 警告、0 错误；未修改 Worker、IPC、
  ProjectModel 解析或 Child Session/RDP baseline。

- 2026-08-27：进一步压缩 Tasks 非核心信息：移除标题副文案、项目/任务/参数概览、所有“前往设置”入口和底部
  运行环境诊断；对应的 Tasks-only Worker 明细投影与“准备 Worker”事件入口一并清理，Dashboard 的准备环境、
  Run/Stop、用户化 Worker/Run 摘要、全局状态栏、日志及底层 Worker/IPC 行为未改。`MainWindow.xaml` XML 解析
  与 Tasks 单滚动区结构检查通过；Release build 0 警告、0 错误，build-output `--self-test` 通过。

- 2026-08-27：根据实机反馈收紧 Tasks 布局：空目录不再显示红色错误条或两块空编辑器；任务描述改为独立面板；
  页面外层不再滚动，仅参数列表保留纵向滚动。`MainWindow.xaml` XML 解析和结构检查通过（Tasks 根节点为 Grid，
  仅含一个参数 ScrollViewer）；使用本机 NuGet 缓存完成 Release build，0 警告、0 错误；build-output
  `--self-test` 通过。按用户要求未再次启动 GUI 或 Worker，固定窗口、长描述和 200% DPI 仍需人工视觉确认。

- 2026-08-27：完成 Tasks 页选择/配置 UI 重构。`MainWindow.xaml` 与 `DesignSystem.xaml` XML 解析通过；
  NarutoAutoGUI Release `win-x64` build 通过，0 警告、0 错误；GUI build-output `--self-test` 通过，新增覆盖
  单 task、多 task、长 label、task description 存在/缺失/null/空白、无 task options、selection 自动保存和
  显式 option intent 切换保留。静态布局 contract 覆盖 920×640、1180×760 和 1500px 宽窗口，参数 editor
  分别限制在约 240、370 和 400 DIP 内；参数区不产生横向滚动。完整自动化脚本的 GUI 阶段通过；
  Worker 阶段被本机 Application Control policy 以 `0x800711C7` 阻止载入，按用户要求未继续重试。
  真实窗口、滚轮、键盘导航和 200% DPI 视觉 E2E 仍待人工确认；未修改 Worker、IPC、Dashboard 状态机或
  Child Session/RDP baseline。

- 2026-08-26：首个可用 prerelease `v0.1.0-rc.2` 已由 run
  [32982031166](https://github.com/ArcherSore/NarutoAutoGUI/actions/runs/32982031166) 创建。locked build、GUI/Worker
  自检、发布目录与 ZIP 解包校验、Actions artifact 和 Release job 全部成功；Release ZIP SHA256 为
  `3182cfdb9926a34d4793faa06013f79e2ac8c98532aeab8fbe3c2c3783a98456`。此前 `v0.1.0-rc.1` run
  `32980682112` 的 build-package 已成功，但 Release job 因未显式传递仓库而失败；提交 `2a1d0fe` 增加 `--repo` 后由
  `rc.2` 完成验证。MaaNOP 提交 `d7b3088` 的 install run
  [32982465990](https://github.com/ArcherSore/MaaNOP/actions/runs/32982465990) 已固定下载该 Release asset；Windows x64
  明确跳过 MFAAvalonia 和独立 MaaFramework 下载，SHA、组合边界、最终 package validator 与 artifact 上传均成功，
  其余 matrix job 也全部成功，release job 因无 MaaNOP tag 正确跳过。
  `rc.2` 初次发布时附带的 `.zip.sha256` asset 已按发布策略删除；后续 workflow 只上传 ZIP。

- 2026-08-26：完成 Phase 1 Windows x64 release workflow。`workflow_dispatch` run
  [32968253563](https://github.com/ArcherSore/NarutoAutoGUI/actions/runs/32968253563) 对提交 `b41553c`
  完成 locked restore、Release build/publish、GUI/Worker 自动自检、发布目录校验、ZIP 解包复验、当时的 SHA256 sidecar 和
  Actions artifact 上传，`build-package` 全部 step 成功且无失败，tag-only `release` job 正确跳过。amend 前相同文件树的
  run [32967561275](https://github.com/ArcherSore/NarutoAutoGUI/actions/runs/32967561275) 也成功。已验证 artifact 的
  SHA256 与 sidecar 匹配、ZIP 根直接包含 `NarutoAutoGUI.exe` 且没有 wrapper，发布包包含 GUI、Worker 和所需
  Maa.Framework native runtime，不包含 Python runtime、MaaNOP、源码或顶层 build/log junk。当时尚未创建 RC、tag
  或 GitHub Release；后续结果见上方 `v0.1.0-rc.2` 记录。

- 2026-08-17：正式项目 Release `win-x64` build 通过。
- 2026-08-17：正式项目 self-contained `win-x64` publish 通过，输出到 `artifacts\NarutoAutoGUI\win-x64`。
- 2026-08-17：`--self-test` 通过，覆盖便携式配置 JSON 保存/加载往返、DEBUG 与 INFO 文件日志写入。
- 2026-08-19：补充游戏参数后 Release build 和 `--self-test` 通过；工作目录改为由 exe 自动推导，不在 GUI 或配置文件中暴露。自检覆盖旧版错误入口迁移、火影默认启动配置及三项启动配置 JSON 往返。
- 2026-08-19：默认发布目录因旧版 NarutoAutoGUI 进程正在运行而被锁定；未强制结束进程，改在 `artifacts\NarutoAutoGUI\win-x64-update` 完成 self-contained publish 和发布后自检。
- 2026-08-19：移除工作目录字段后再次完成 Release build、直接 `--self-test`、`artifacts\NarutoAutoGUI\win-x64-no-workdir` self-contained publish 及发布后自检；运行中的 GUI 实例均未被强制结束。
- 2026-08-19：完成状态驱动命令、异步反馈、日志暂停跟随、键盘/辅助信息、错误恢复和首次托盘通知后，Release build、self-contained publish 与 `--self-test` 通过；托盘通知、日志滚动和真实 Session 状态切换仍待交互式回归。
- 2026-08-19：应用主窗口视觉设计系统后，Release build、自包含 `win-x64-design-system` publish 与发布后 `--self-test` 通过；100%/150%/200% 缩放、高对比度、日志视觉层级和真实 Session 各状态仍待交互式回归。
- 2026-08-19：增强桌面分身显示/隐藏按钮的可用态辨识度后，XAML 解析、Release build、自包含 `win-x64` publish 与 `--self-test` 通过；真实 Session 状态切换下的视觉效果仍待交互式回归。
- 2026-08-19：完成主窗口去卡片化视觉调整后，XAML 解析、Release build、自包含 `win-x64` publish 与 `--self-test` 通过；顶部状态卡和日志视口边界保留，桌面分身控制、程序启动及日志外层卡片已移除，真实桌面下的视觉效果仍待交互式回归。
- 2026-08-19：完成 WTS 查询语义、RDP 实时状态/意外断开恢复、正式 GUI 单实例保护和退出生命周期门修复后，正式 GUI Release build、自包含 `win-x64-lifecycle-fixes` publish 及发布后 `--self-test` 通过；共享 baseline 的 `ChildSessionDemo` 也完成 Release build 和自包含 `win-x64-lifecycle-fixes` publish。两次构建均为 0 错误，仅出现既有 `NU1900` 漏洞元数据网络警告。
- 2026-08-19：首次实机回归确认当前 Windows 在没有 Child Session 时会让 `WTSGetChildSessionId` 返回 `ERROR_NOT_FOUND (1168)`；初版严格错误处理因此阻断创建与 fail-closed 退出。已将 1168 明确纳入“无 Session”，其他错误仍抛出；修正后正式 GUI Release build、自包含 `win-x64-lifecycle-fixes-v2` publish 和发布后 `--self-test` 通过，`ChildSessionDemo` Release build 与自包含 `win-x64-lifecycle-fixes-v2` publish 通过，等待再次实机回归。
- 2026-08-19：首个 Worker/IPC 切片完成 Debug/Release build、self-contained GUI + Worker publish 和发布后 `--self-test`；编译 0 错误，仅有环境中既有的 `NU1900` 漏洞元数据警告。
- 2026-08-19：首片显式 option 扩展完成 Debug/Release build、自包含 `win-x64-options-v1` GUI + Worker publish 与发布后 `--self-test`；覆盖真实形状的 `ServerRange=978` input、task switch/select、递归 active graph、Dormant Intent 保留/恢复、非法 input 不落盘、恢复项目默认、resolved pipeline override 和 planDigest 变化。另以本机 MaaNOP v1.3.0 真实 `interface.json` 和隔离临时 Config 验证 `AccountTraining + ServerRange=978 + ClaimLevelExp=No`，正式 Resolver 生成 `ParseServer` 参数 `978` 与 `ClaimLevelEntry.enabled=false`，未修改 MaaNOP/MFAAvalonia 配置。构建为 0 错误，仅有既有 `NU1900` 漏洞元数据网络警告；交互式 Success/Cancellation 统一验收仍待完成。
- 2026-08-19：首片验收期间发现新增 option 区使默认窗口无法访问下方内容；先增加临时整页滚动并固定日志区高度，不在验收前重做布局。`win-x64-options-v2-scroll` 完成 Release GUI + Worker publish 和发布后 `--self-test`，旧版非凭据 settings/MaaNOP Config 已复制到新包；正式 UI 重设计延后到首片统一验收之后。
- 2026-08-20：完成当时的 WPF-UI 4.3.0 Fluent Shell 初版后，`App.xaml`、`MainWindow.xaml`、
  `DesignSystem.xaml` XML 解析通过，最终 NarutoAutoGUI Release `win-x64` build 通过（0 警告、0 错误）。以独立目录
  `artifacts\NarutoAutoGUI\win-x64-fluent-shell` 完成 GUI self-contained publish，并对该产物运行
  `src/NarutoAutoGUI/scripts/test-automated.ps1`，覆盖 settings v2/旧版迁移、PI default/explicit resolver、nested
  dormant intent、MaaNOP Config v1、RunPlan digest、IPC framing 和 DEBUG+ 文件日志，结果通过。另以 HEAD 旧 XAML
  为基线自动比对，35 个原有 `x:Name`（含模板内命名元素）和 17 个 Click/SelectionChanged/LostKeyboardFocus 事件绑定
  均保留；日志 ScrollChanged 的既有 `AddHandler` 代码保持不变。当前导航结构见上方“本轮已实现”。
- 2026-08-20：当时环境仅安装 .NET 8 SDK，完整发布脚本在恢复面向 .NET 9 的 `NarutoAutoWorker` 时未完成；未修改 Worker，也未将该工具链失败描述为 GUI 编译失败。NarutoAutoGUI 本身的 Release build、GUI publish 和发布后自动自检均已实际通过。随后已安装 .NET 9 SDK，并在后续完整发布验证中覆盖 GUI + Worker。
- 2026-08-20：修复任务操作入口在 Fluent UI 中被状态隐藏且未出现在任务页的问题后，`MainWindow.xaml` XML 解析、NarutoAutoGUI Release `win-x64` build 和直接 `--self-test` 通过；构建 0 错误，仅有 NuGet 漏洞元数据源不可达产生的既有 `NU1900` 警告。任务页与首页的固定按钮、禁用原因提示和键盘访问仍待下一次真实桌面回归。
- 2026-08-20：根据一次 Worker `RunEx` 提交后 60 秒内没有 PID/admission 的实机失败，增加 Worker 专用进程启动验证、Task Scheduler 状态诊断及 admission 超时回滚。NarutoAutoGUI Release `win-x64`、NarutoAutoWorker Release `win-x64` 和冻结 `ChildSessionDemo` Release `win-x64` 均构建通过；GUI 直接 `--self-test` 通过。使用已安装的 .NET 9.0.315 SDK 在独立目录 `artifacts\NarutoAutoGUI\win-x64-worker-launch-fix` 完成 self-contained GUI + Worker 发布，发布后自检通过，并复制既有不含凭据的 settings/MaaNOP Config 供实机复验；构建 0 错误，仅有既有 `NU1900` 警告。新的 Worker 启动诊断与失败回滚仍待真实 Child Session 交互式回归。
- 2026-08-20：移除“隐藏子桌面后才能开始任务”的非必要前置条件及对应界面提示；Child Session 保持显示或已隐藏时均按相同的 Worker、Snapshot 与配置就绪条件启用开始任务。`MainWindow.xaml` XML 解析、NarutoAutoGUI Release `win-x64` build 和直接 `--self-test` 通过；在独立目录 `artifacts\NarutoAutoGUI\win-x64-worker-launch-fix-v2` 完成 self-contained GUI + Worker 发布、复制既有不含凭据的 settings/MaaNOP Config，并通过发布后自动自检。真实可见子桌面下的 Run 启动仍待交互式复验。
- 2026-08-21：PI Loader 对尚未实现的 resource/task/option controller/resource 约束改为 fail closed，并增加三类带准确 JSON path 的负向自检。NarutoAutoGUI Release build 和直接 `--self-test` 通过；构建 0 错误，仅有既有 `NU1900` 漏洞元数据网络警告。
- 2026-08-22：ProjectModel 将原单文件中的 definitions、PI Loader 和 option Resolver 拆分为内部实现文件，并将 option 结构、默认 input、引用和完整递归图合法性前移到 Loader。自动自检新增非法 input/case 组合、缺失 case、非法正则、默认值类型错误和未激活分支循环等负向 fixture；NarutoAutoGUI Release build 和直接 `--self-test` 通过。本机 MaaNOP v1.3.0 真实 PI 的结构/default 只读核对通过；构建 0 错误，仅有既有 `NU1900` 漏洞元数据网络警告。
- 2026-08-22：ProjectOptionResolver 将运行期 `pipeline_override` 从 GUI 递归深合并对象改为 MaaFramework 接受的有序 fragment 数组，并新增 task/global/resource/controller/task/nested 六段顺序、同节点 fragment 隔离、int/bool 精确占位符、嵌入字符串、dormant nested option 和 select 顶层 override fail-closed 自检。NarutoAutoGUI Release `win-x64` build 与直接 `--self-test` 通过，0 警告、0 错误；真实 MaaNOP Run 尚待用户验收，不据此声明交互式 E2E 已复验。
- 2026-08-22：PI Loader 补齐两个 Win32 窗口正则和三个 MaaFramework 控制方式字段的语义校验，自动自检增加五类带准确 JSON path 的负向 fixture；Worker 为不可信 Launch Manifest 保留正则创建错误和实际窗口文本匹配超时诊断。NarutoAutoGUI 与 NarutoAutoWorker Release `win-x64` build、GUI 直接 `--self-test` 均通过，0 警告、0 错误；未修改已验证的 Child Session 流程。
- 2026-08-22：删除 task catalog 的 `DefaultOnlyValid/ValidationError` 冗余状态和构造期二次默认 Resolve；非法 PI 统一在 `ProjectPlanModule.Open` 的 Loader seam 失败，task 切换仍验证可能重新激活的 dormant intent。NarutoAutoGUI Release `win-x64` build 与直接 `--self-test` 通过，0 警告、0 错误。
- 2026-08-22：ProjectModel 以内部 enum 替代 option type 与 pipeline type 字符串，并新增 `ProjectInputValue` 统一 Loader 默认值和 Resolver 显式值的校验/类型转换。自动自检新增非法显式 int/bool 不落盘覆盖；NarutoAutoGUI Release `win-x64` build 与直接 `--self-test` 通过，0 警告、0 错误。
- 2026-08-22：固定单 controller/resource 的当前 UI 对非空 `controller.option` 与 `resource.option` 改为 fail closed；Loader 接受字段缺失或空数组，合法 `ProjectDefinition` 和 Resolver 不再携带不可编辑的 scope。自动自检相应收口为 task/global/task/nested 有序 fragment，并新增两个 option scope 负向 fixture；NarutoAutoGUI Release `win-x64` build 与直接 `--self-test` 通过，0 警告、0 错误。
- 2026-08-22：将四个已验证的 Child Session 核心实现迁入正式 GUI 的 `ChildSession` 目录，删除不再使用的独立 Demo、
  发布脚本和项目文件，并移除 MSBuild 源码链接。全仓库手写 C#、XAML、PowerShell 与项目/配置文件完成 120 列审计，
  可在 120 列内完整表达的 C# 调用、声明和 XAML 起始标记已收回单行；GUI 与 Worker Release `win-x64` build、
  GUI 直接 `--self-test`、Roslyn whitespace `--verify-no-changes` 均通过，0 警告、0 错误。自动验证不包含需要真实桌面
  的 Child Session 交互式回归；对应手动回归见下方记录。
- 2026-08-24：完成 MaaNOP 字符串 `focus` 运行日志接入、GUI user-facing source 过滤、Worker Instance cursor 重置、
  sequence gap 补取和 `log.getSince` 响应预算。GUI 与 Worker Release `win-x64` build 均为 0 警告、0 错误；在独立
  `artifacts\NarutoAutoGUI\win-x64-maanop-run-log` 目录完成 self-contained GUI + Worker publish，发布后 GUI 自检
  覆盖 cursor/source 过滤，Worker 自检覆盖 focus 投影与响应预算，结果全部通过。真实 MaaNOP focus Run、断线补取和
  Worker Instance 替换尚未执行交互式回归，不据此声明 E2E 已验证；Child Session/RDP baseline 未修改。
- 2026-08-24：MaaNOP Run Log 代码审查修复后，Callback 退订失败不再改变 Run outcome；Coordinator 对 live/recovered
  日志串行发布，Child Session 结束或 recovery 期间 Pipe EOF 会取消旧连接的在途补取，补取失败则只在
  active connection 上延迟重试。新增真实 Named Pipe 脚本化自检，覆盖一次补取失败后重试、恢复期间新增事件、
  sequence 顺序、recovery 期间 Pipe EOF 后同 Worker Instance 重连、eviction gap 恢复，以及 teardown 后忽略旧响应；
  Worker 自检改为经 Callback Adapter 验证 `maanop.run` 输出、Run 关联、告警
  限频、UTF-8 截断和并发 sequence，GUI 自检覆盖 timestamp/source 路由与 diagnostic 文件保留。最终 GUI/Worker
  Release build、build-output GUI/Worker 自检、Roslyn whitespace 和 120 列检查均通过；最终 self-contained 产物已生成，
  但从该目录运行 DLL 被本机 Application Control policy 阻止，因此不声明最终发布目录自检通过。真实 MaaNOP Run 与
  Worker Instance replacement 交互式回归仍待执行，Child Session/RDP baseline 未修改。
- 2026-08-25：MaaNOP Run Log 自检覆盖度修复。Worker focus 投影自检不再直接调用
  `MaaRunLogFormatter.Format`，改为经 `MaaRunLogAdapter.Handle` 缝隙验证输出、过滤和告警
  限频（spec line 177）；`WorkerLogSequenceTracker` 自检补充 different-instance cursor 重置后
  的 gap 检测（spec line 199）；`WorkerCoordinatorSelfTest` 的 recovery 验证扩展为单次补取
  flight 内多次 live gap event 追平（spec line 194），后续 disconnect/teardown 测试序号同步
  调整。build-output GUI `--self-test` 通过。
  GUI/Worker Release build 和 build-output 自检均通过，0 警告、0 错误；Child Session/RDP
  baseline 未修改。
- 2026-08-25：完成 Home“运行控制台”信息架构、画面 Placeholder、Level 图标运行动态和底部全局状态栏后，
  `MainWindow.xaml` XML 解析、NarutoAutoGUI Release `win-x64` build 和 build-output `--self-test` 通过，
  构建 0 警告、0 错误。当前 Theme、其他四页、Worker/IPC 协议和 Child Session/RDP baseline 未修改；
  920×640、100%/150%/200% 缩放及真实状态切换下的视觉与键盘回归仍待交互式执行。
- 2026-08-25：为 Home 恢复外层纵向 overflow fallback，并将下方双列约束为 1180×760 正常视口的既有高度，
  避免外层无限测量吞并运行动态列表的独立滚动。XAML contract 与 WPF measure harness 覆盖 920×640、1180×760、
  长任务标签、长 option 摘要、长下一步状态、运行动态内外层滚动范围，以及 PerMonitorV2 下 200% DPI 位图渲染；
  NarutoAutoGUI Release `win-x64` build 与 build-output `--self-test` 通过，0 警告、0 错误。真实窗口视觉、滚轮、
  键盘和跨显示器 DPI 切换仍待交互式回归。
- 2026-08-25：修复 `LogLines` 非公开导致 WPF 无法绑定首页“运行动态”和日志页的问题，并增加
  `TypeDescriptor` 可发现性回归断言。NarutoAutoGUI Release `win-x64` build 通过，0 警告、0 错误；新增断言通过后，
  完整 build-output 自检在后续既有 Named Pipe 场景因当前权限返回 `Access denied`，因此不声明完整自检通过。
  当时正在运行的真实 E2E GUI、Worker 和 Child Session 未被停止或替换；修复后的真实窗口显示已于 2026-08-26
  完成交互式复验。
- 2026-08-26：完成 ADR 0021 Active Run latest-frame Preview V1。Worker 自检通过脚本化 frame source 覆盖 200 ms tick、
  内容去重、revision、`sampledAtUtc`、失败限频、停止清空、在途帧拒绝和 PNG/base64 响应预算；GUI 自检通过真实
  Named Pipe 覆盖 afterRevision、`not_modified`、严格 schema 和 stale Worker Instance 拒绝。NarutoAutoGUI 与
  NarutoAutoWorker Release `win-x64` build 均通过，0 警告、0 错误；最终 Worker build-output DLL 自检通过。GUI 协议与
  Coordinator 自检在本轮较早 build-output 通过；最终重建后的 GUI DLL 在仓库路径和隔离临时副本均被本机 Application
  Control policy 以 `0x800711C7` 阻止载入，因此不声明最终 GUI build-output 自检通过。真实 Maa cached image、Home
  连续显示、页面/窗口可见性切换和 Running→Stopping 仍待消费级电脑上的交互式 Run 回归；Roslyn whitespace、120 列
  和 `git diff --check` 已通过，未修改 Child Session/RDP/WTS/Task Scheduler baseline。
- 2026-08-26：ADR 0021 Preview 代码审查非 P1 修复。恢复 8 处装饰性换行为 120 列内单行
  （`LatestFramePreview`、`WorkerHost`、`WorkerRuntimeExecution`、`WorkerSelfTestRunner`、
  `MainWindow.xaml.cs`、`WorkerCoordinatorSelfTest`）；Coordinator `ValidatePreviewResponse`
  的 `unavailable` 分支增加 run identity 校验，拒绝携带非空且不属于当前请求 runId 的
  unavailable 响应（null runId 仍接受以表示 `no_active_run`）。Coordinator 自检新增三类
  Preview 负路径：unavailable 错误 runId 拒绝、unavailable null runId 接受、frame 错误 runId
  拒绝；Worker 自检新增 2 MiB 响应预算拒绝边界和 4 MiB transport write-before-send guard。
  NarutoAutoGUI 与 NarutoAutoWorker Release `win-x64` build 均通过，0 警告、0 错误；Worker
  build-output DLL 自检与 GUI build-output DLL 自检均通过，`git diff --check` 通过。未修改
  Preview producer 无界 cleanup wait 和 Preview request cancellation 后迟到 response 两个 P1
  问题，留待后续单独设计。Child Session/RDP/WTS/Task Scheduler baseline 未修改。
- 2026-08-26：ADR 0021 Preview P1 复核与 IPC 修复。Coordinator 对调用方已取消或超时、但可能已经写入 Pipe 的请求
  保留有界 requestId tombstone；迟到 response 会被消费并丢弃，不再作为无法关联的 envelope 断开 Worker IPC。
  真实 Named Pipe 自检覆盖取消 Preview、发送迟到 response、随后继续完成下一次 Preview 请求；NarutoAutoGUI Release
  `win-x64` build 与 build-output GUI `--self-test` 均通过，0 警告、0 错误。另结合 MaaFramework 官方接口与实现复核：
  `GetCachedImage` 只同步复制最近 cached image，没有 cancellation/timeout API；为保持单 Controller 与释放安全，cleanup
  继续先等待 producer 结束再释放 Controller，不增加会并发释放或遗留旧 Controller 的 timeout。真实 Maa cached image、
  Running→Stopping 和自然终态的及时结束仍作为交互式回归项，由用户在目标机器验证。Child Session baseline 未修改。
- 2026-08-26：按最新 AGENTS.md 代码风格整改全仓库手写 C#。通过精确 `.editorconfig`（`csharp_new_line_before_open_brace`
  列出 types/methods 等声明块、排除 control_blocks，并设 `csharp_new_line_before_catch/else/finally = false`）以
  `dotnet format whitespace` 将控制流（if/foreach/while/for/switch/try/using/lock）左大括号改同行、`} else`/`} catch`/`}
  finally` 合并，声明块保持换行；同步将机械逐参数/逐条件换行压缩为每行多项，行尾统一 LF。已验证流程（Child Session、
  RDP ActiveX、WTS、Task Scheduler COM、分辨率/缩放、进程 Session 验证与清理）仅改格式不改逻辑。NarutoAutoGUI 与
  NarutoAutoWorker Release `win-x64` build 均通过，0 警告、0 错误；全仓库 120 列检查 0 违规、`dotnet format whitespace
  --verify-no-changes` 与 `git diff --check` 均通过。
- build 期间 NuGet 无法访问漏洞元数据源，产生 `NU1900` 警告；包还原和编译本身成功。该警告不是代码编译错误。

以下项目需要管理员权限、可见桌面或真实外部程序，自动验证不能替代手动回归。Child Session 真实桌面交互式回归已完成；游戏/MaaNOP 跨 Session 启动、异常断开后重建连接、创建/启动过程中并发退出等外部程序或故障场景仍需按后续目标单独记录。

当前首页 / 任务 / 设置三页 Fluent UI 的部分真实 Windows 桌面人工回归仍待补齐：默认与最小窗口尺寸下的三页导航、键盘 Tab/访问键、页面独立滚动、日志暂停/恢复跟随和状态驱动按钮切换（含首页 Show / Hide / Terminate Session 按钮的 visibility/enabled）。100%、150%、200% 缩放下的布局与文字可读性已由用户实机检查，未观察到明显裁切、重叠或可读性问题；结合真实 Child Session 的 RDP 显示/隐藏/结束流程已在 2026-08-22 完成回归。

## 本轮交互式回归

- 2026-08-26：用户使用包含 `LogLines` Binding 修复的正式 `artifacts\NarutoAutoGUI\win-x64` 产物完成真实 E2E
  验收，并确认首页“运行动态”会实时显示 MaaNOP `focus` 日志。GUI 创建 Child Session 31，验证 Worker PID 33420，
  Dependency Readiness=Ready；真实 `AccountTraining` Run `db74b582-0789-4ba9-85d9-913f0d54bd8a` 从服务器 979
  处理到 1012（31/31），文件日志记录 `maanop.run` 至 Worker sequence 288，最终自然终结为 Succeeded。验收后
  Child Session 31 已正常注销。此次不额外声明断线补取、Worker Instance replacement 或其他 Fluent UI 交互项通过。
- 2026-08-20：Fluent UI 完整包首次在 Child Session 20 提交 Worker 后未在 60 秒内完成 admission + fresh Snapshot，Admission Record 中没有 Worker PID；结束/重建环境后再次启动成功。该结果证明问题具有偶发性，也暴露出原实现缺少 `RunEx` 后 PID 验证与超时回滚；对应强化修复已进入 `win-x64-worker-launch-fix`，等待复验。
- 2026-08-20：用户在真实 Windows 桌面检查当时 Fluent UI 的 100%、150%、200% 缩放，三档均未观察到明显布局或文字可读性问题。
- 2026-08-20：在 GUI-only 的 `win-x64-fluent-shell` 产物点击“准备运行环境”时，MaaNOP v1.3.0 已成功加载且 Child Session 21 已连接，随后因产物中缺少 `worker\NarutoAutoWorker.exe` 明确失败。该结果属于不完整测试产物的发布问题，不是 Worker 启动、Named Pipe、RDP 或 MaaNOP 运行时失败；完整 GUI + Worker 发布仍待具备 .NET 9 SDK 后通过正式脚本重新生成。
- 2026-08-19：`win-x64-lifecycle-fixes-v2` 已实机验证创建 Child Session、从托盘结束桌面分身、随后从托盘退出主程序，流程正常。
- 2026-08-19：`win-x64-lifecycle-fixes-v2` 已实机验证同一 Windows Session 启动第二个正式 GUI 时会明确拦截，第二实例不进入主窗口，第一实例及其 Child Session 不受影响。
- 2026-08-19：ADR 0020 首片实机验证 GUI 以 Task Scheduler Worker-specific Highest 路径在 Child Session 17 启动 Worker instance `99a9261d-ec54-48f9-a88d-f9218ae65ef5`；Named Pipe admission、真实 PID 37064/Session 17 验证、fresh Snapshot 和 Dependency Readiness=Ready 均通过。实测 MaaFramework Binding=5.8.0.0、Runtime=v5.8.1，Python Agent probe 使用 `D:\miniconda3\python.exe` 并成功 import 必需模块。GUI 收到 Worker lifecycle、admission 和 readiness `log.entry`。
- 2026-08-19：隐藏 Child Session 后从主 GUI 发送真实单项 `AccountTraining` Run（runId `82f1c3d5-0899-44b5-916c-952a12b4ed20`，planDigest `sha256:947faa82a1bfa2283b720344e9504eb9419bc56bc9adda62458785c8493367b3`），已实测贯通 Named Pipe、Child Session Worker、目标 HWND、Win32 Controller、MaaNOP Resource、MaaTasker 和 Python Agent：目标游戏 PID 38332/Session 17，Agent PID 4184 成功连接，MaaFramework jobId 200000001。该 Run 在真实 pipeline `ClaimLevel -> ClickOnWelfare` 因模板识别连续失败而终结为 `Failed`；MaaFramework 日志记录 `Tasker.Task.Failed`，Agent 随后退出，游戏 PID 11144/38332、Worker PID 37064 和 Child Session 17 均保持运行。正式配置确为 `ExplicitOptions={}`，PI Resolver 使用 `ClaimLevelExp.default_case=Yes`；而本机 MFAAvalonia 已保存配置中的 `ClaimLevelExp` 为索引 1（`No`），会通过正式 PI override 禁用 `ClaimLevelEntry`。因此历史手工链路与本次 default-only Run 并非同一计划，当前证据没有指向 Resolver 错合并。`ClickOnWelfare` 的具体失败属于 MaaNOP 脚本/资源或游戏前置条件的下游问题，不作为 NarutoAutoGUI GUI 缺陷在本仓库修复；该结果仍仅作为调试证据，按验收规则是 partial，不计入 Success scenario 通过，`AccountTraining` 也尚不能认定为合适的纯默认 Success fixture。
- 2026-08-19：首片 Cancellation scenario 已完整实机通过。主 GUI 在 fresh Snapshot 中观察到 Run 与唯一 Plan Item 均为 Running 后，对真实 `AccountTraining` Run `abe80566-2eb8-412d-b7b9-acf87cf25266` 发送 `run.stop`；Worker 与 GUI 均记录 `stop_requested`，随后 Worker 记录 `MaaFramework Stop 已确认`，GUI 交互观察到 Stopping，最终 Snapshot 为 Run/Plan Item Cancelled、`activeRun=null`、`lastRun.state=Cancelled`、Worker Ready。该 Run 由同一 Worker PID 37064 接受，创建新的 Agent PID 6132 并提交 MaaFramework jobId 200000067；终结后 Agent 已退出，原游戏 PID 11144/38332、同一 Worker PID 37064 和 Child Session 17 均仍存活。该第二次真实 Run 也证明同一 Worker 能在前一个真实 Run 终结并释放 execution context 后再次接受 Run；Cancellation、取消后存活和 Worker 复用验收项记为通过。
- 2026-08-19：`win-x64-options-v2-scroll` 的真实 Success scenario 已通过。最终 SchemaVersion 1 Config 选择 `AccountTraining`，以正式 ExplicitOptions 设置 `ServerRange=978`，并显式关闭 `ClaimLevelExp`、`ClaimInfiniteIllusion` 与 `ClaimMail`；Run `16c48275-2f8b-4c04-885f-e38ff3cf3fe6`（planDigest `sha256:9ca9dfd9c8c0b466183c0302cad20e8c111d95d3a2ee1c6ec84a3dcccdd7a1e2`）在隐藏 Child Session 18 后由主 GUI 接受，真实找到游戏 PID 16728/HWND `0x104CE`，启动并连接 Python Agent PID 34244，提交 MaaFramework jobId 200000001，最终不经 Stop 自然终结为 Succeeded。终结后 Agent PID 已退出，Worker PID 29960、游戏 PID 16728/33224 和 Child Session 18 均仍存活。Success 能力记为通过。
- 2026-08-19：同一 `win-x64-options-v2-scroll` Worker 的 Cancellation/复用回归已通过。Success Run 终结后未重新准备或替换 Worker，主 GUI 再次隐藏同一 Child Session 18，并由同一 Worker PID 29960 接受真实 `AccountTraining` Run `f86ba849-d556-443c-bfd2-0686562be705`（planDigest `sha256:2c1a17506a67d6bb7505dd5a1078d012c49a0a9ee72fe0d9e27aced591ff92fc`）；该 Run 重新找到同一游戏 PID/HWND，创建新 Agent PID 38356 并提交 MaaFramework jobId 200000036。用户在 Run 与唯一 Plan Item 均进入 Running 后显式发送 `run.stop`，GUI/Worker 记录 `stop_requested`，随后 Worker 记录 `MaaFramework Stop 已确认`，最终 Run/Plan Item 为 Cancelled、`activeRun=null`、`lastRun.state=Cancelled`、Worker Ready。Agent PID 38356 已退出，Worker PID 29960、游戏 PID 16728/33224 和 Child Session 18 均继续存活。至此 ADR 0020 的 Success、Cancellation、取消后存活和同 Worker 再次执行条件全部满足，首个真实 E2E vertical slice 由 partial 更新为 PASS。
- 2026-08-22：用户完成真实 Windows 桌面的正式 GUI Child Session 交互式回归。迁移后的
  `src/NarutoAutoGUI/ChildSession` 实现已完成真实桌面复验，覆盖 RDP Child Session 创建/恢复、显示/隐藏、结束分身、
  托盘相关入口和退出注销流程；本次不额外声明异常断开后重建、创建/启动过程中的并发退出或外部游戏/MaaNOP 启动故障场景。

## 已验证 Child Session baseline

2026-08-17 已通过当时的独立 Demo 完成交互式实机复验：创建/连接/预览/注销 Child Session、固定
`1920×1080 @ 100%`、启动和验证 notepad/MFAAvalonia，以及关闭预览后的清理均通过。2026-08-22 将四个核心实现
文件迁入 `src/NarutoAutoGUI/ChildSession` 并删除独立 Demo；迁移只调整所有权、目录和命名空间，不改变已验证流程。

## 已知限制与 Known Issues

- 只支持 Windows x64，依赖管理员权限、交互式桌面、系统 RDP ActiveX、WTS API、Task Scheduler COM 和 WMI。
- RDP ActiveX 必须保持存活；正式 GUI 通过隐藏窗口而非关闭控件实现后台保持。
- Windows Hello/PIN 不能保证无提示复用账户密码，必要时 Windows 可能在子桌面显示凭据界面；正式 GUI 不保存密码。
- TermService 回环状态异常时可能需要重启 Windows。
- 配置与日志默认位于程序目录，适合当前解压即用发布；日志目录不可写时会回退到用户目录。配置不会静默回退，保存失败会在 GUI 和日志中明确报告。
- 幂等判断以 exe 文件名 + Session ID 为准；同一 Session 中同名但不同路径的进程会被视为已运行。
- 首次 Child Session 偶尔会出现 `CrossDeviceResume.exe` 的 Windows 系统弹窗，目前不影响功能。本轮仅记录，不修改 SystemApps、ACL、系统文件或相关系统配置。
- MaaFramework v5.8.1 会在载入时自动探测 `MaaFramework.dll` 同目录的可选 `plugins` 目录；NuGet 发布布局未创建该目录时会输出两条 `PluginMgr::load_dll` 错误。当前 MaaNOP 使用 Python Agent 而非该 demo native plugin，且 Worker 实测 Dependency Readiness=Ready，因此该日志不阻断本次 Run；后续需在固定 runtime 打包中创建空的默认探测目录以消除误导日志，不加载可选 `MaaPluginDemo.dll`。

2026-09-15：V2 工单 02 接入 Rust prepare、完整包验证、Engine 缓存清理、GUI 下载进度/取消；构建与定向自动化通过。保留目录、危险 ZIP 和断线清理已加入测试；安装由后续 03/04 接入。

2026-09-15：V2 工单 03 完成独立副本 ready、GUI PID 门槛、原地安装与最小状态/错误窗口。真实 Engine 进程隔离树测试通过，清理失败继续启动、移动/写入失败停止与 relaunch 失败区分已覆盖。此片 GUI 仍要求先结束运行环境；04 接入现有生命周期。旧 .NET Updater 源码已删除，正式发布脚本由 05 切换 Rust。

2026-09-15：V2 工单 04 接入活动运行环境关闭；确认后通过操作门阻止新运行操作，Stop ACK 后仍等待任务状态，随后 WTS 注销并等待已跟踪 Worker 退出。GUI/Worker 构建及 GUI 自检通过，新增 Stop ACK 不冒充终态/进程退出的 Named Pipe 回归。未改动 Child Session 原生实现，真实游戏升级验收仍待 05。
