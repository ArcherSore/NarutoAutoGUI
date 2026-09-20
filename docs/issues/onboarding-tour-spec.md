---
title: 新手指引 V1：四步 Fluent UI orientation tour
status: ready-for-agent
labels: [ready-for-agent]
issue-source: local-docs/issues
specified-on: 2026-09-20
test-seams: confirmed
implementation: implemented-awaiting-interactive-validation
---

# 新手指引 V1：四步 Fluent UI orientation tour

2026-09-20 已按用户后续 `implement` 请求接入实现；本文件保留设计阶段的规格与验收要求。
当前执行证据和待验收项见 [验证记录](onboarding-tour-validation.md)。

## Problem Statement

首次打开 NarutoAutoGUI 的用户需要迅速理解任务配置、说明与参数、运行控制和实时截图的位置。
当前首次加载只提供空的“配置 1”，参数编辑器和任务说明入口需要添加任务后才能看见，
用户容易不知道开始前需要配置什么、到哪里确认游戏初始界面。

已有用户不应因升级而突然收到教程；用户已有任务、顺序、参数和激活配置也不能被教程改变。
新手指引必须能跳过，任何显示或持久化失败都不能阻断核心自动化流程。

## Solution

在首页提供轻量的 Fluent TeachingTip-like popover 与 Spotlight，固定四步：
**任务配置 → 任务说明与参数 → 运行控制 → 实时截图**。
这是 UI orientation tour：用户通过指引按钮前进，Tour 期间不操作底层控件，也不执行自动化。

真正首次创建配置时，正常配置初始化就预置 Project Interface 中第一个真实 task，并在首次渲染时展开。
它是持久化的正常 Task Configuration，不是演示数据，也不依赖 Tour 是否成功显示。
后续点击 `+` 仍创建空配置。已有文件、迁移、损坏文件 fallback 和空列表修正均不补入任务。

仅真正的新用户获得自动 Tour。自动 Tour 的“开始使用”“跳过”和 Esc 都记录已处理 V1；
设置页提供“查看新手指引”，忽略完成版本且不改写任何 Onboarding 持久化状态。
最小化或隐藏到托盘只暂停显示，同一窗口恢复后继续当前步；跨进程只保留新用户资格，不保存步骤。

## User Stories

1. 作为首次用户，我希望看到真实的默认任务，以便立即理解执行计划的形态。
2. 作为首次用户，我希望首张任务卡默认展开，以便直接看到说明入口和参数。
3. 作为首次用户，我希望默认任务遵循项目声明顺序，以便使用资源维护者推荐的第一项。
4. 作为用户，我希望新增配置仍为空，以便自行组织新的任务集合。
5. 作为用户，我希望知道顶部标签代表独立配置，以便保存多套使用方案。
6. 作为用户，我希望知道可用任务区域的用途，以便向计划添加任务。
7. 作为用户，我希望知道执行计划可以排序，以便控制执行顺序。
8. 作为用户，我希望一次看懂任务说明与参数的位置，以便开始前确认要求。
9. 作为用户，我希望说明图标得到短暂提示，以便记住以后在哪里查看说明。
10. 作为用户，我希望 Tour 不强迫我打开说明抽屉，以便快速完成阅读。
11. 作为用户，我希望知道运行控制是状态相关入口，以便理解准备、开始和停止的切换。
12. 作为用户，我希望 Tour 不自动准备环境或启动任务，以便由我决定何时运行。
13. 作为用户，我希望知道实时截图所在区域，以便观察挂机画面。
14. 作为用户，我希望知道可以放大预览和显示桌面分身，以便需要时进一步查看或操作。
15. 作为用户，我希望没有运行环境时也能阅读截图功能介绍，以便无需先启动游戏。
16. 作为用户，我希望指引只有四步并显示进度，以便预估阅读时间。
17. 作为用户，我希望能够返回上一步，以便重读没有看清的内容。
18. 作为用户，我希望最后一步显示“开始使用”，以便明确指引已经结束。
19. 作为用户，我希望可以跳过或按 Esc 退出，以便不被教程阻碍。
20. 作为用户，我希望明确结束自动 Tour 后不再自动出现，以便无需反复关闭。
21. 作为已有用户，我希望缺少完成版本文件也不会自动弹出教程，以便升级保持安静。
22. 作为用户，我希望配置损坏时不补入推荐任务，以便不被误导为原意图已恢复。
23. 作为用户，我希望配置损坏后的原始文件保护继续有效，以便故障不会造成额外数据损失。
24. 作为首次用户，我希望项目加载失败时不显示错误指引，以便先解决安装问题。
25. 作为首次用户，我希望未显示或异常中断不算完成，以便修复后仍有机会查看。
26. 作为用户，我希望最小化或隐藏窗口时暂停指引，以便稍后继续当前步骤。
27. 作为用户，我希望未完成就退出后可以从第一步重来，以便不丢失入门机会。
28. 作为已有用户，我希望从设置页重新查看，以便随时复习界面。
29. 作为用户，我希望 replay 不改变激活配置、任务顺序和参数，以便放心查看帮助。
30. 作为空计划用户，我希望 replay 说明添加任务后的卡片形态，以便无需插入演示任务。
31. 作为用户，我希望窗口缩放、滚动和 DPI 改变后高亮仍准确，以便指引不会指错控件。
32. 作为小窗口用户，我希望文案和操作按钮始终可见，以便能够完成或退出指引。
33. 作为用户，我希望高亮区域仍保持原有亮度，以便看清所介绍的界面。
34. 作为用户，我希望高亮区域不会穿透点击，以便阅读时不会误改参数或启动任务。
35. 作为键盘用户，我希望焦点限制在指引操作中，以便无需鼠标也能完成全部步骤。
36. 作为辅助技术用户，我希望标题、步骤和按钮名称可读，以便理解当前内容。
37. 作为关闭系统动画的用户，我希望指引尊重该设置，以便避免不必要的动态效果。
38. 作为用户，我希望 Pulse 有限且立即响应退出，以便不会持续分散注意力。
39. 作为用户，我希望指引与更新、放大预览互不重叠，以便不会陷入多个遮罩。
40. 作为用户，我希望关闭后焦点回到可见控件，以便继续操作应用。
41. 作为用户，我希望 Onboarding 出错时应用仍能正常使用，以便帮助功能不会成为故障源。
42. 作为用户，我希望完成版本保存失败时得到轻量提示，以便知道下次仍可能出现指引。

## Implementation Decisions

### 1. 当前实现与复用边界

- MainWindow 是单一 WPF namescope，Home 与 Settings 通过 Visibility 切换，没有独立 Tasks 导航页。
  首页左侧 TasksView 包含页面标题、校验提示和 TaskWorkspacePanel；右侧 RuntimeSidebar 宽 392 DIP。
  最小窗口为 920×640 DIP，常规布局验收尺寸为 1180×760 DIP。
- TaskWorkspacePanel 是已有 ScrollViewer，内部依次为 ConfigurationTabs、Task Shelf 和执行计划。
  它恰好排除“任务”标题和校验提示，是 Step 1 最稳定的现成 target，不新增包装层。
- RenderPlanItems 每次重新生成任务卡，并维护按 task name 索引的 `_planItemContainers`。
  PlanItemsPanel 还含排序指示线，因此不能把第一个 child 直接视为第一张任务卡。
- CreatePlanItem 根据 `_expandedTaskName` 渲染真实参数编辑器。当前 LoadProject 和配置切换会清空展开值。
  顶层任务说明按钮仅在 Description 非空时创建；参数旁的 Info12 是另一类说明入口。
- Runtime Control 和 Preview 已各有一个外层 Border，只缺可直接引用的名称。
  在原 Border 上增加名称即可；不改变右侧布局或运行按钮状态推导。
- Task Description Drawer 位于导航内容内；PreviewOverlay、UpdateOverlay 位于 MainWindowContent 外的共同根 Grid。
  两个全局 Overlay 使用 Blur/禁用内容及原生窗口钩子，Esc 和焦点恢复由 MainWindow 管理。
- 当前 IsGlobalModalOpen 仅包含 Preview/Update。其原生钩子会拦截最小化、最大化和关闭；
  MainWindow 的关闭处理也会在这些模态层打开时拒绝隐藏到托盘。
- ProjectPlanModule 先加载并校验 PI，再从 MaaNopConfigStore 读取 V2 配置。
  Store 在文件不存在时返回内存空配置，不立即保存；读取损坏文件也返回空配置，但保留 LoadWarning 和原文件保护。
  当前模块没有对外暴露“首次缺失”与“异常 fallback”的来源区别。
- PI Loader 保留 task 数组顺序，且拒绝空 task 数组。首次任务选择不需要硬编码名称或另行排序。
- 更新偏好已采用 GUI 拥有的单值文本文件和 WARN 降级；沿用该轻量方式，不恢复 AppSettings。
- 启动 Loaded 依次 LoadProject、InitializeUpdates、DetectExistingSession，存在 Session 时等待恢复操作。
  没有 Session 时目前提前 return；新启动稳定信号必须覆盖两条路径。
  启动更新检查只刷新展示状态，不会在返回结果时主动打开 UpdateOverlay。
- 现有真实 WPF MainWindow 自检和 ProjectPlanModule 临时目录自检可复用，无需新测试框架。

### 2. 最小职责划分

1. MainWindow 新增一个 Onboarding partial：固定四步、启动门槛、replay、target resolution、布局、动画、焦点和清理。
   使用少量字段和普通方法即可；不要求独立 Controller 类或通用 step engine。
2. 在共同根 Grid 新增一个 OnboardingOverlay，层级高于现有全局 Overlay。
   同一窗口内承载遮罩几何、边框、小箭头、popover 和可选 Pulse，初始 Collapsed。
3. 一个 GUI 内部小型偏好 helper 负责完成版本和新用户资格标记的读取/写入。
   接受存储目录以便隔离文件测试，不依赖 Project Definition，不新建持久化框架。
4. Store 只暴露本次原配置确实不存在的加载事实；不能根据空 SelectedTasks 或 LoadWarning 是否为空推断首次使用。
   缺文件与无权限、不可读、非法 JSON、未知 schema 等必须分开。
5. ProjectPlanModule 拥有“首次配置选择 PI 第一项”的项目语义，并提供只读首次初始化结果给 GUI。
   结果至少让 GUI 知道本次是否成功预置以及对应 task name；不包含 Tour 版本、步骤或显示状态。
6. Config schema、Run Plan、Worker、Protocol 和 Child Session 均不增加 Onboarding 字段或职责。

### 3. 首次默认任务与展开

- 只有 Store 本次打开时确认原文件不存在，才在 ProjectPlanModule 初始化阶段建立默认选择。
  第一份仍为稳定 Id 的“配置 1”，Active 指向它，SelectedTasks 仅含 PI 的第一个 task name，ExplicitOptions 为空。
- 使用已有 Resolver 校验默认意图，通过已有 Store 原子保存完整 V2；不复制项目默认参数到 ExplicitOptions。
  不用 GUI 点击事件或 Tour Step 2 来执行 AddTask，也不让 Store 引用 Project Definition。
- 仅初始化缺失配置适用；已有合法空配置、V2 空列表 fallback、V1 迁移、错误 active 修正和损坏文件都不适用。
  CreateConfiguration 仍创建空任务、空参数配置，其他配置管理行为保持原约定。
- 初次保存失败不抛成 Project 加载失败：保留可用空工作区和明确加载/保存警告，禁止宣称默认任务已持久化。
  自动 Tour 本次不开始；原文件不得被破坏，之后仍通过正常编辑重试保存。
- GUI 在首次 RenderTaskPlan 前根据成功初始化结果设置 `_expandedTaskName`。
  不等 Step 2 才展开，不在完成后收起；展开状态不增加持久化字段。
- 后续普通 LoadProject、切换配置不再次预置任务；常规折叠/展开和运行锁语义不改变。
- 已记录新用户资格而自动 Tour 尚未完成的重启，在初次渲染前展开当前计划第一张真实卡，
  仅调整 UI 展示，不重新选 task、不补回用户删除的任务。若当前计划为空，按自动 Tour target 缺失处理。

### 4. 自动资格、完成版本与跨启动重试

**明确的需求冲突与最小补充：** 原材料同时要求“首次加载前配置文件不存在才是新用户”和
“默认任务持久化后，未完成退出仍可在下次自动重试”。只保存完成版本无法满足二者：
第二次启动时，无版本且已有配置既可能是老用户，也可能是刚被初始化的新用户。
不能以缺少完成文件重新判定所有已有配置用户，也不能为了教程延迟正常配置保存。

本规格采用一个额外的空资格标记文件解决这个信息缺口：`onboarding-new-user.pending`。
它只证明曾在本功能下确认原配置不存在；不表示完成、不含步骤，也不是通用状态机。
这是根据当前实现补出的最小设计，不是原材料中已经指定的文件。

- GUI 在本进程第一次 Project 加载之前记录原配置存在性；必须区分确实不存在与探测失败，
  不能把 File.Exists 在权限错误时返回的 false 无条件解释为新用户。
- 确认原配置不存在、且没有已处理版本时，在任何可能保存默认配置的操作之前创建空资格标记。
  此后只读取既有标记，不因普通 LoadProject、手动 replay 或发现空计划创建标记。
- `onboarding.txt` 保留单个整数版本，当前为 `1`；有效值大于等于当前版本都视为已处理，不降级改写。
  不存在视为未处理；格式错误、读取失败写 WARN。无法可靠读完成状态时，本次不自动展示，replay 仍可用。
- 自动资格为：完成版本低于当前版本，并且本次确认为首次缺失或已有上述资格标记。
  已有配置但没有资格标记的用户不自动 Tour，哪怕完成文件缺失、损坏或未来增加 Tour 版本。
- 本次配置损坏/不可读的 LoadWarning 路径不自动 Tour，即使残留资格标记也不例外。
  不删除原配置，不加入默认任务，保留现有异常 bytes 备份及失败不覆盖语义。
- Project 加载失败不写完成版本；资格标记保留，安装修好后可重试。
  资格探测或标记写入失败只 WARN，不阻塞项目和默认配置初始化；当前确认的新用户仍可在本次运行展示，
  但不能承诺标记未保存时的跨启动重试，不能随后把所有已有配置认作新用户来补救。
- 自动 Tour 已实际显示后，“开始使用”、任一步可用的“跳过”、Esc 原子写入完成版本 `1`。
  同目录临时文件替换即可；成功后尽力删除资格标记。若中途退出，完成版本优先于残留标记，避免重复出现。
- 保存失败仍关闭 Tour，本进程不再自动弹；日志 WARN，并用设置页帮助区非模态文字提示
  “未能保存新手指引状态，下次启动可能再次显示。”不得抛出影响主窗口的异常或声称已保存。
- target 缺失、异常中断、真实退出、窗口销毁、尚未显示或仅暂停，都不写完成版本，也不清除未完成资格。
- replay 无论 Finish、Skip 或 Esc 都不写、不删、不修复上述文件，保持其 bytes 和存在性不变。
  手动启动后抑制本进程待自动展示，避免同一进程内 replay 后又弹一遍；重启资格仍按文件判断。

| 首次探测/文件状态 | 正常配置初始化 | 自动 Tour |
| --- | --- | --- |
| 原配置不存在，完成版本未处理 | 预置并保存 PI 第一项 | 创建资格标记，等待启动稳定 |
| 原配置不存在，完成版本已处理 | 仍按缺失配置规则预置 | 不展示 |
| 已有正常配置，无资格标记 | 加载原意图 | 不展示，无论是否有完成文件 |
| 已有正常配置，有资格标记且未处理 | 不改配置，只准备 UI 展开 | 从 Step 1 重试 |
| 已有损坏/不可读配置 | 原保护语义、空工作区 | 不展示、不补任务 |
| PI 缺失、非法或 task 数组为空 | 原 Project unavailable 语义 | 不展示、不记完成 |
| 已处理版本与资格标记同时存在 | 加载原配置 | 完成版本优先，不展示 |

### 5. 四步精确 UI contract

#### Step 1：任务配置

- 标题：**配置自动化任务**。
- 正文：

  > 从“可用任务”添加要执行的任务，并调整执行顺序。
  > 顶部标签可以保存多套独立配置。

- Target：TaskWorkspacePanel 的可见 viewport，包含配置标签、可用任务和执行计划所在工作区。
  进入时工作区滚动到顶部，确保配置标签可见；不高亮“任务”标题、错误提示或整个左栏。
  计划比窗口高时不要求全部内容同时入屏。
- Preferred placement：Right。
- 操作按顺序为“跳过”“下一步”；不显示“上一步”。计数 `1 / 4`。

#### Step 2：任务说明与参数

- 标题：**查看说明并设置参数**。
- 正文：

  > 点击 ⓘ 查看任务说明，并在卡片中调整运行参数。
  > 开始前建议先确认任务所需的游戏初始界面。

- Target：Active Configuration 的第一个 SelectedTasks 对应的完整 Plan Item card，
  通过 `_planItemContainers` 取实际 Border；包含标题、顶层任务说明按钮和整个参数编辑区域。
  不依据当前 Active Run 的 Plan Item ID，也不缓存上一次 RenderPlanItems 的控件。
- 首次自动路径中卡片已在正常初始化时展开。进入前 BringIntoView，等布局完成后再计算几何。
- Preferred placement：Right。操作为“跳过”“上一步”“下一步”，计数 `2 / 4`。
- 仅顶层任务说明 ⓘ 播放有限蓝色 Pulse；参数标签旁的说明图标不播放，整个卡片不 Pulse。
- Description 缺失时当前 UI 不创建 ⓘ，参数为空时没有编辑器。这是合法 PI 形态：
  保持第一项选择及真实卡片，不伪造入口、参数或选另一个 task；可选 Pulse target 缺失时省略动画。
  固定文案介绍有说明/参数时的入口，当前 MaaNOP 首次完整体验验收应使用实际有说明和参数的第一项。
  若当前正式包不满足此内容前提，在验收中记录内容限制，不由本需求修改 MaaNOP。
- 仅手动 replay 且计划为空时，Target 改为 TaskShelfContent 的真实可见区域，展开 Task Shelf 并滚动至此；
  标题、计数、按钮和 Right 偏好不变，不播放 Pulse，正文改为：

  > 添加任务后，会在执行计划中出现任务卡片。
  > 你可以通过 ⓘ 查看任务说明，并在卡片中调整参数。

#### Step 3：运行控制

- 标题：**启动自动化**。
- 正文：

  > 配置完成后从这里开始。
  > 按钮会根据当前状态自动切换为准备环境、开始任务或停止任务。

- Target：RuntimeSidebar 第一行整张 Runtime Control Border，含标题及当前状态操作。
  不只框 32×32 图标，不改原有重试、进度环、禁用及状态投影逻辑。
- Preferred placement：Left。操作为“跳过”“上一步”“下一步”，计数 `3 / 4`。
- 不调用 Prepare、Start、Stop，也不要求用户实际点击运行入口。

#### Step 4：实时截图

- 标题：**查看挂机画面**。
- 正文：

  > 运行环境启动后，可以在这里实时查看游戏画面。
  > 也可以放大预览，或显示桌面分身进行操作。

- Target：RuntimeSidebar 第二行整张 Preview Border，包含标题、放大/折叠入口、图像或占位图，
  以及显示/隐藏分身入口。分身按钮沿用真实 enabled/visibility，不伪造运行环境状态。
- Preferred placement：Left。操作为“上一步”“开始使用”，计数 `4 / 4`。
  最后一步不显示“下一步”或单独“跳过”；Esc 仍等价 Skip。
- 无 Session、Worker 或游戏画面时用真实 placeholder 完成介绍，无需先准备环境。

### 6. Replay 和临时展示状态

- Settings 添加轻量“帮助”区，项目名“新手指引”，说明
  “快速了解任务配置、运行控制和实时截图。”，按钮“查看新手指引”。
- 入口在 Project 成功、非 busy、非退出且没有其他 Overlay 时可用；不可用时显示简短原因。
  若必要 target 仍缺失，停止并 WARN，不留下遮罩，帮助区可提示暂时无法显示。
- 手动开始先记录原焦点和原页面，再通过已有 SwitchSection 切到 Home，从 Step 1 开始。
  不调用 LoadProject，不切换配置、不新增/删除任务、不重排、不写 option 或 ActiveConfigurationId。
- 为保证介绍可见，replay 可以临时展开 Task Shelf、第一张现有任务卡和 Preview 内容，并调整工作区滚动位置。
  仅在进入相应步骤时调整；保存原展开值和滚动 offset，结束/失败后尽力恢复。
  首次自动 Tour 的默认展开不属于临时 replay 状态，不能在退出时回滚。
- replay 的卡片展开通过现有 UI 字段和渲染实现；现有 `_updatingOptionEditors` 保护必须覆盖重建过程，
  不因重建/焦点变化触发额外的 option 保存。Settings 点击之前的正常用户失焦提交仍按原规则处理。
- 运行中的 Configuration Edit Lock 继续有效。replay 只可改变 GUI 展开显示，不解除运行编辑锁。
  已有运行和 Preview 继续按原状态工作；用户始终能 Esc 返回，Tour 不持有应用操作门。
- 手动 Tour 结束留在首页，恢复可见且可用的原焦点，否则聚焦首页导航；不聚焦已隐藏的设置控件。
  如果期间真实退出，释放 UI 引用即可，不强行切回页面。
- 所有 View-only 恢复先核对窗口、配置和控件仍有效；不为恢复布局重建消失的配置或任务。

### 7. Target resolution、滚动与 placement

- 所有几何以共同 Overlay 根 Grid 为祖先，在 WPF DIP 中计算。
  使用真实 FrameworkElement 的 RenderSize/ActualWidth/ActualHeight，通过 TransformToAncestor 转换边界。
  不用屏幕绝对坐标、截图识别、UI Automation 定位或手写 DPI 比例。
- 每次进入步骤、恢复窗口、SizeChanged、DPI/layout 变化、工作区滚动或动态卡片重建后重新解析。
  合并同一 Dispatcher 周期内的重算，只在 Overlay 活跃时订阅布局变化，矩形不变时不重复更新。
- 首次显示前预检四个必需 target 存在；后续每步仍检查实际可见性、有效尺寸和共同视觉祖先。
  未完成布局可以延后一次 layout pass；已稳定后仍不存在、尺寸为零或已脱离视觉树属于 target 失败。
- BringIntoView/必要滚动必须先发生，再等待 layout，再计算几何；不能算完矩形后才滚动。
  超高卡片优先让标题和说明入口可见，尽量展示参数；不得缩小文字以硬塞整张卡。
- 卡片的逻辑 target 始终是完整 Border；实际 Spotlight 取其与滚动 viewport、祖先裁剪和 Overlay 边界的交集。
  在可见部分外扩 8 DIP 后再次约束到合法显示范围，不能亮起被 ScrollViewer 裁掉的屏幕外内容。
  这是物理空间不足时对“整张卡尽量可见”的实现，不把 Step 2 拆成两步或换成参数子控件。
- 使用实际 popover 测量尺寸，默认宽 312 DIP，允许 300–320 DIP 范围内调整。
  Overlay 内保留至少 16 DIP 安全边距；target 与 popover 留约 12 DIP 间隔。
- 优先 Right/Left；不足时依次试对侧、Bottom、Top，每个候选都按可用空间校验并沿另一轴限制边界。
  若均不能完全避开 target，选可用空间最大的方向，popover 限宽/限高并约束到窗口内，允许最少必要遮挡。
  正文内部可滚动，标题、步骤和 footer 固定可见；不能让“跳过”或完成按钮落到窗口外。
- 小箭头指向可见 target，锚点限制在圆角之间；极端重叠无法合理指向时隐藏箭头，不画误导连线。
- 必须验证 920×640 和 1180×760、导航展开/收起、100%/150%/200% DPI，以及移动到不同 DPI 的显示器。
  离屏 DIP 测量不能替代真实 DPI 验收。

### 8. 视觉、动画与 Pulse

- Popover 使用现有 Surface 背景、字体、正文/次要文字色、Radius.Section 和轻量 Surface 阴影。
  保留标题、正文、step counter、footer 和小箭头；不引入 Web Tour 样式或第三方大框架。
- 2026-09-20 实测反馈调整：跳过/上一步使用无常驻描边的中性色轻按钮，保留 hover 和键盘焦点反馈；
  下一步/开始使用保留蓝底白字，固定 88 DIP 宽，按钮统一 34 DIP 高，不使用字符箭头。
  Popover 顶部至少位于导航内容起点下方 16 DIP，避免覆盖标题栏。
- backdrop 使用 26% 黑色透明度作为 24–28% 的默认值，目标保持正常亮度，不 Blur 背景。
  视觉 hole 可用排除圆角矩形的几何；另设完整透明输入拦截层，hole 不成为 hit-test hole。
- 卡片高亮圆角跟随真实 target，矩形工作区使用相近的轻量圆角；可用 1 DIP 浅蓝细边框。
  不采用重度聚光、霓虹、持续 glow 或 breathing；Spotlight 自身不可聚焦或操作。
- 蓝色采用现有 Brush.Primary/Primary.Surface。当前 Brush.TasksAccent 是绿色，不直接拿来作为蓝色 Pulse。
  动画只作用于 Overlay 自己的 visual，不修改或动画化共享 frozen brush，也不改目标控件的业务状态。
- 切步默认约 200 ms、ease-out：旧文案淡出、Spotlight 平滑移动/缩放、新文案淡入。
  保持同一个 Overlay 存活，不整屏隐藏重建。首次自动出现等待约 400 ms 后轻量淡入。
- Step 2 完成定位后约 450 ms 开始 Pulse，每次约 850 ms，中间间隔 200 ms，默认 3 次。
  仅提供内部 1–3 次常量/参数，不增加用户设置。蓝色细圆环以说明图标为中心，从直径 24 DIP 扩散至
  32 DIP，不填充背景；先轻量淡入再淡出，结束后完全移除，不跟随按钮的长方形点击区域拉伸。
- 离开步骤、Skip、Esc、Finish、暂停、失败或窗口销毁立即取消延时和动画；迟到回调检查当前展示代次。
  Resize 只重定位，不重放 Pulse；从最小化恢复同一步也不重新播放，重新进入 Step 2 可再播放一次有限序列。
- SystemParameters.ClientAreaAnimation 为 false 时无 Pulse、无移动/淡入淡出，直接切换最终布局。
  若系统设置在展示期间变化，停止当前动画并落到最终态，不依赖动画 Completed 事件推进步骤。

### 9. Overlay、输入与焦点

- OnboardingOverlay 覆盖整个客户区，pointer、滚轮、拖放都不能穿到高亮区域或其他底层控件。
  点击背景不关闭、不推进；只有 footer 操作和 Esc 控制 Tour。
- 不直接禁用 MainWindowContent 来实现阻塞，否则目标可能套用 disabled 样式而失去正常亮度。
  用 Overlay 命中拦截、焦点限制和窗口预览输入处理阻止底层访问键/快捷键，保留既有运行锁决定的显示状态。
- 焦点进入当前 popover 的主操作；Tab/Shift+Tab/Control+Tab 限制在可见 Tour 操作中。
  Enter/Space 激活当前按钮，Previous/Next 都有键盘路径；不额外要求左右键作为快捷键。
- 每步给出明确 accessible title、可读的“第 N 步，共 4 步”和操作 AutomationProperties.Name。
  切步后保持可见操作中的焦点；Esc 优先由已显示的 Tour 消费，语义等于 Skip。
- 自动关闭后尽力恢复进入前仍可见、启用且可聚焦的元素，否则聚焦 Home 的稳定导航入口。
  关闭/暂停时不能留下 Keyboard.FocusedElement 指向隐藏 Overlay。
- 自动开始前除检查 IsGlobalModalOpen，还检查 Task Description Drawer；任一打开就延后。
  不自动打开 Drawer，不叠加 Preview/Update，不为此增加 Modal Coordinator。
- Onboarding 单独维护活跃/可见状态。不要直接把它并入现有 IsGlobalModalOpen 后复用原生拦截，
  因为这会破坏必须支持的最小化、resize 和关闭到托盘。
  Preview/Update 继续使用原来的 Blur、关闭限制及 native hook；Tour 不安装这个拦截钩子。
- Preview/Update/Drawer 的打开入口加入最小的 Tour 互斥检查；正常输入不可到达底层入口，
  程序性请求也不能同时显示两层。确有退出或安装交接需要时先中止 Tour，再沿用原流程。
- 窗口的原生最小化、最大化、resize、标题栏关闭和 Alt+F4 继续遵守原窗口行为；
  关闭到托盘算 Pause，真正退出算 Abort，都不算 Skip。客户区其余底层动作被 Tour 拦截。
- 后台更新检查结果可继续更新红点/文案；不因 Tour 取消更新或额外等待网络返回。

### 10. 生命周期与启动门槛

使用少量内存状态表达 Dormant、Pending、Showing(step)、Paused(step)、Ended 即可，
另记录来源 Auto/Replay 和一次性展示代次；名称仅用于说明，不要求状态机框架。
CurrentStep 只存在于 MainWindow 内存，四步均由用户按钮推进，没有每步操作完成条件。

自动开始必须同时满足：

1. 新用户资格成立、未处理当前版本、本进程未主动结束或失败中止过自动 Tour。
2. MainWindow Loaded、可见且非最小化，当前 Home 可见。
3. Project 成功加载、TaskWorkspacePanel 可见，初始配置处理完成且没有损坏/保存失败阻塞。
4. 启动检测和已有 Child Session 的恢复尝试已经结束，而不只是某个中间时刻 `_busy == false`。
   用 MainWindow 启动流程末尾的简单完成标志覆盖无 Session 的提前返回及恢复的成功/失败路径。
   不改 Child Session 的探测、RDP、COM 或注销实现；恢复失败可沿用正常错误处理后结束启动等待。
5. `_busy == false`、`_exitInProgress == false`，Session 当前不在 Connecting/Disconnecting。
6. PreviewOverlay、UpdateOverlay、Task Description Drawer 均不显示，没有尚未返回的启动提示交互。
7. 四步必需控件已生成，当前 target 完成布局并能解析可见边界。

满足后通过 Dispatcher 延后一个布局周期，再等待约 400 ms，期间任何条件变化都取消当前延时。
淡入前再次校验；只允许一个待执行的开始操作，不用高频轮询。
在启动完成、Home 返回、visibility/state、busy 结束及其他 Overlay 关闭等既有事件处重新评估。

| 事件 | 显示状态 | 持久化与后续 |
| --- | --- | --- |
| Pending 时 busy/其他 Overlay/不在 Home | 继续等待 | 不写完成，不强制抢回 Home |
| Next / Previous | 切到相邻步骤 | 不写文件；不得越过缺失步骤 |
| Auto 的 Skip / Esc / Finish | 清理并结束 | 实际显示后才写完成版本 |
| Replay 的 Skip / Esc / Finish | 清理、恢复展示并留在首页 | 所有 Onboarding 文件不变 |
| 最小化或 Hide 到托盘 | 隐藏 Overlay，取消动画，保留步骤 | 不记完成，不取消核心运行 |
| 同窗口恢复 | 等待布局与门槛，重定位同一步 | 不重播被中断的 Pulse |
| Showing 时开始外部 busy 操作 | 暂停并释放输入限制 | busy 结束后满足条件再恢复，不阻塞操作门 |
| 真实退出/窗口销毁 | 取消回调、释放引用 | 不记完成，不等待 Tour 清理 I/O 才退出 |
| 初始化/绘制/必需 target 失败 | 关闭并 WARN | 本次自动尝试结束；下次启动可用资格重试 |
| 外部流程必须打开互斥 Overlay | 先中止 Tour | 不记完成，既有流程继续 |

Paused 恢复不强行关闭其他 Overlay；待其关闭后再恢复。暂停期间仍保留本次 mode/step，
但不跨进程保存；退出后再次自动展示始终从 Step 1 开始。
任何残留 Dispatcher 回调都必须在真正关闭后失效，不能重开窗口、写完成版本或保留可命中的透明遮罩。

### 11. 失败语义与核心流程隔离

- 自动 Tour 缺少任一必需 target：有限布局重试后取消本次 Tour，记录包含 mode、step、target 身份的 WARN。
  不显示 `1 / 4 → 3 / 4`，不把失败算用户处理。
- 手动 Tour 仅允许“空计划的 Step 2”合法 fallback；其他必需 target 缺失同样停止并 WARN。
  可选 Pulse 图标不存在不算必需 target 缺失。
- 异常处理覆盖初始化、延迟回调、geometry、动画和清理，不能让 optional Onboarding 异常冒泡破坏 MainWindow。
  一次展示失败最多记录一次主要诊断，避免 LayoutUpdated 导致日志刷屏。
- 所有结束路径都取消 timer/storyboard/Dispatcher 操作，移除布局订阅和输入拦截，清除临时 visual/focus 引用。
  清理失败也必须尽力移除 Overlay 的 hit testing，防止透明遮罩卡住应用。
- Tour 不调用 Session 创建/恢复/注销、Worker prepare/replace、Run Start/Stop 或 Update Engine install。
  自动 Tour 等待的是现有启动流程的完成，不是发起第二条启动链路。
- 运行控制卡和 Preview 内容继续反映原快照。展开 Preview 只沿用原可见性订阅规则，
  不增加新 Controller、IPC 请求类型或 Onboarding 专用预览生命周期。

## Testing Decisions

### 测试边界与现有先例

测试边界已于 2026-09-20 经用户确认：真实 MainWindow/WPF 控件层、ProjectPlanModule 公共接口与临时文件，
DPI、动画观感和真实托盘交互保留人工验收。不引入新的分层 mock 或通用注入框架：

1. **主要边界：真实 MainWindow/WPF 展示行为。** 复用 GUI SelfTestRunner 的配置 Tab 和运行控制自检方式，
   在隔离 Project/存储目录下实例化真实窗口、进行 Measure/Arrange/Dispatcher 布局、触发控件动作，
   检查显示内容、目标边界、焦点、文件副作用和结束后的可用性。
   新启动接入需有覆盖生产初始化顺序的场景，不能只反射设置“启动完成”就声称启动竞态已经验证。
2. **次要边界：ProjectPlanModule 公共接口及磁盘结果。** 复用已有 PI fixture、临时配置、
   V1 迁移、损坏保护和保存失败测试。围绕 Open/CreateConfiguration/重开观察实际结果，
   不单独为 Store 的私有分支建立新测试入口。

好测试验证用户能看到、操作和持久化的行为；不固定 private helper 名称、字段数量、Storyboard 帧数，
不把本规格的 if 条件逐句复制进测试。必要时沿用现有反射搭建场景，但断言必须落在外部结果。
自动 Tour/replay 文件测试均使用临时目录，不读取或改写真实安装目录配置。
真正 DPI、系统标题栏与托盘、键盘路由和动画观感需要真实桌面补充，不以离屏结果替代。

### 自动化验收矩阵

| ID | 场景 | 必须观察到的结果 |
| --- | --- | --- |
| A01 | 无配置、有效 PI | 配置 1 仅保存 PI 第一项，ExplicitOptions 空，重开 Id/任务稳定 |
| A02 | PI 首项名称/顺序更换 | 跟随新的第一项，不出现硬编码 AccountLeveling |
| A03 | 点击 `+` | 新配置为空；原配置及参数不变 |
| A04 | 已有正常/空配置，缺完成文件 | 不新增资格、不补任务、不自动弹 |
| A05 | V1 迁移、V2 空列表/无效 active | 原迁移/修正规则有效，不被误识别为新用户 |
| A06 | 配置损坏/不可读，包括有资格残留 | 无默认任务、无自动 Tour；原文件不因加载被覆盖 |
| A07 | 损坏 fallback 后用户真实编辑 | 原始 bytes 备份；备份失败仍不覆盖 |
| A08 | PI 缺失、invalid、task 空数组 | 原错误 UI 可用，无完成版本；修复后资格仍能使用 |
| A09 | 初始默认配置保存失败 | 不冒充保存成功，空工作区和错误提示保留，不显示自动 Tour |
| A10 | 正常首次渲染且还没进入 Step 2 | 第一张真实卡已展开；Tour 未显示也成立 |
| A11 | 无已有 Session 的 Loaded 分支 | 检测结束、布局稳定和延时后只开始一次 |
| A12 | 恢复已有 Session 尚在等待 | 不提前展示；恢复完成或错误交互结束后再判断 |
| A13 | busy、隐藏、非 Home、Overlay 打开 | 不展示、不写完成；条件解除后可开始 |
| A14 | 延时中条件改变或窗口销毁 | 迟到回调不显示 Overlay、不写版本 |
| A15 | 四步前进与返回 | 文案/计数/按钮和真实 target 按顺序一致，最后为开始使用 |
| A16 | Auto 在 Step 1–3 Skip、各步 Esc、Step 4 Finish | 写 1，资格清除或被完成版本覆盖；重启不自动弹 |
| A17 | Tour 未显示、target 失败、异常/真正退出 | 无完成版本；资格保留，重启从 Step 1 开始 |
| A18 | 首次配置已保存后进程重开 | pending 资格允许重试；已有老配置无 pending 仍不弹 |
| A19 | 完成文件与 pending 共存/未来版本 | 完成优先，不降级版本，不重复展示 |
| A20 | 资格/完成读取或写入失败 | WARN、核心启动可用；完成写失败关闭后不在本进程重弹 |
| A21 | replay，完成文件存在/缺失 | 均能手动开始；结束前后版本和资格文件 bytes/存在性完全不变 |
| A22 | replay 有任务、原卡折叠 | 临时展示首卡；结束恢复 UI，全部配置/顺序/参数 bytes 不变 |
| A23 | replay 空计划、Task Shelf 收起 | Step 2 合法 fallback，无 Pulse、无插入任务；恢复原展示 |
| A24 | 必需 target 缺失/动态重建 | 重新解析有效控件或失败关闭；不使用旧 Border、不跳步 |
| A25 | 首项无 Description/无参数 | 使用真实卡片，无伪造按钮/参数，无错误 Pulse |
| A26 | Resize、滚动、超高任务卡 | viewport 裁剪正确，重算后 popover/操作不出界 |
| A27 | 首选侧空间不足 | 对侧/上下 fallback 可用；最坏情况 footer 仍可操作 |
| A28 | 最小化/隐藏后恢复 | 同一步重定位，动画取消，无完成写入，不重放中断 Pulse |
| A29 | 底层点击、滚轮、快捷键 | 参数、配置、Drawer、运行入口均无意外动作 |
| A30 | Tab/Shift+Tab/Esc/关闭 | 焦点限制有效，关闭后聚焦可见控件，不留透明输入墙 |
| A31 | Pulse 与步切/快速连续点击 | 默认最多三次，切步立即终止；旧回调不画在下一步 |
| A32 | 系统动画关闭/运行中关闭 | 无 Pulse 或移动，立即展示正确最终态，导航正常 |
| A33 | Preview/Update/Drawer 互斥 | 不叠层；原焦点、Blur、Esc 和原生钩子行为可恢复 |
| A34 | 原有 Active Run 或 Preview 展示 | 不产生额外 Prepare/Start/Stop/安装调用，不修改运行快照 |

### 人工验收与发布门槛

- 首次完整包从配置不存在开始，确认默认第一任务和实际参数/说明入口、四步真实高亮、完成后的正常使用。
- 1180×760、920×640，导航展开/收起，100%/150%/200% DPI 和跨显示器切换：
  观察 target、箭头、Popover 边界、超高参数卡和小窗口 fallback。
- 用鼠标、键盘实测空洞不可点击、Tab 限制、Esc、标题栏最小化/最大化/关闭到托盘、恢复同一步及真实退出重开。
- 观察有限 Pulse、200 ms 切换不闪屏、关闭系统动画后的静态显示。
- Tour 前后打开 Preview/Update/Task Description，确认各自焦点/遮罩/native hook 未回归。
  有现成运行环境时观察 Preview 和运行状态即可，不为教程验收修改游戏参数或自动执行任务。
- 实现时运行 GUI Release 构建、相关 GUI/ProjectModel 自检和 diff/120 列检查；
  若修改公共初始化令旧自检依赖“缺文件为空”，调整 fixture 为显式空配置，不放宽原有回归断言。
- 未修改 Child Session 原生流程，无需重做全部 RDP/COM baseline；若实现意外触及这些流程，
  先重新说明必要性，并使用 STATUS 记录的可重复 baseline 验证，不以 Onboarding 测试覆盖它们。
- 规格定稿阶段未执行实现构建、桌面或游戏测试；后续实现阶段的实际结果以验证记录为准。

## Out of Scope

- MaaFramework welcome、Project Interface/interface 扩展、MaaNOP 资源教程协议或资源内容修改。
- interactive tutorial、click-through、必须实际点击才能推进、自动打开说明 Drawer、自动准备或开始任务。
- 修改已有配置、演示任务注入、配置迁移到新 schema、保存展开状态或跨进程步骤恢复。
- 通用 Tour DSL、JSON step 配置、第三方大型框架、状态机框架、Modal Coordinator、事件总线或新 DI 层。
- UI Automation 定位、截图坐标映射、多个独立窗口、通用动画包和未来几十步的扩展设计。
- Worker/Protocol/IPC/Child Session/RDP/COM/Task Scheduler 流程变更。
- 自动修复损坏配置、教程进度云同步、统计遥测、在运行环境中执行入门任务。

## Further Notes

### 依据与设计冲突记录

依据为用户提供的《NarutoAutoGUI 新手指引 / Onboarding Tour — Grill-to-Docs》38 节确认材料，
并于 2026-09-20 对照工作区 HEAD `e7e8f20`。本地规格是实施入口，不是已实现状态。

- [多配置规格](multiple-task-configurations-spec.md)、
  [ADR 0003](../adr/0003-separate-application-settings-maanop-config-and-run-plan.md) 和
  [ADR 0017](../adr/0017-persist-explicit-maanop-intent-and-block-meaning-changing-invalid-config.md)
  当前约定缺文件时为空。本需求明确增加“真正缺失的首次配置预置第一项”这一限定例外；
  只预置 SelectedTasks，不向 ExplicitOptions 写默认值，不改已有/损坏文件。
  实施该切片时同步对应 ADR 的这条例外及架构现状，不提前把它记作已实现。
- [CONTEXT](../../CONTEXT.md) 中 Application Settings 定义仍含历史路径；当前源码与 ADR 已删除该机制，
  本需求不依照旧术语恢复 settings.json，不顺带改写无关领域文档。
- 跨启动重试增加空资格标记；依据与写入顺序详见上文第 4 节。只读完成版本无法无歧义区分新旧用户。
- 现有 native modal hook 与 Tour 暂停/resize 冲突；方案为保持 Preview/Update 钩子语义，
  Tour 独立输入限制和最小互斥检查，不扩建全局 modal 服务。
- 无说明/参数的首项、超高卡片、replay 时已折叠区域是现有控件允许的状态，
  按上述可选 Pulse、viewport 裁剪、临时 UI 恢复处理，不改变 PI 第一项规则。
- 实时截图遵循 [ADR 0024](../adr/0024-preview-is-independent-of-active-runs.md) 与
  [连续 Preview 规格](preview-live-spec.md)，不能按旧“仅 Active Run 有截图”的历史记录设计文案或启动条件。

### 持久化位置与代码导航

以下路径是当前导航快照和文件格式契约，不要求在实现时保留私有 helper 名称。

| 用途 | 当前路径 |
| --- | --- |
| 原配置存在性、V2 意图 | `<程序目录>/config/maanop-config.json` |
| 已处理版本，内容为单个整数 `1` | `<程序目录>/config/onboarding.txt` |
| 新用户资格，只以空文件存在表达 | `<程序目录>/config/onboarding-new-user.pending` |
| 现有轻量偏好参考 | `<程序目录>/config/update-check.txt` |
| 根布局、工作区、Settings、Overlay | [MainWindow.xaml](../../src/NarutoAutoGUI/Views/MainWindow.xaml) |
| 启动、任务卡、说明、Preview、焦点和钩子 | [MainWindow](../../src/NarutoAutoGUI/Views/MainWindow.xaml.cs) |
| 配置 Tab、失焦提交、运行编辑锁 | [Configurations partial](../../src/NarutoAutoGUI/Views/MainWindow.Configurations.cs) |
| 更新偏好及 Overlay 互斥参考 | [Updates partial](../../src/NarutoAutoGUI/Views/MainWindow.Updates.cs) |
| 配置 Store、缺失/损坏与原子保存 | [MaaNopConfig](../../src/NarutoAutoGUI.ProjectModel/MaaNopConfig.cs) |
| 首次项目语义和公共测试边界 | [ProjectPlanModule](../../src/NarutoAutoGUI.ProjectModel/ProjectPlanModule.cs) |
| PI 顺序、空 task 及结构验证 | [PI Loader](../../src/NarutoAutoGUI.ProjectModel/ProjectInterfaceLoader.cs) |
| Surface、蓝色、圆角、阴影 | [DesignSystem](../../src/NarutoAutoGUI/Themes/DesignSystem.xaml) |
| 模块测试先例 | [SelfTestRunner](../../src/NarutoAutoGUI/Infrastructure/SelfTestRunner.cs) |
| 真实 MainWindow 测试先例 | [配置自检](../../src/NarutoAutoGUI/Infrastructure/SelfTestRunner.Configurations.cs) |
| 启动恢复只读依据 | [ChildSessionManager](../../src/NarutoAutoGUI/ChildSession/ChildSessionManager.cs) |

### 建议实施切片

以下是规格定稿时的实施顺序建议，未创建独立工单；后续用户已通过 `implement` 请求授权实施。

1. **首次初始化与资格**：区分配置加载来源，模块预置真实第一项、原子保存及 GUI 初始展开；
   GUI 偏好 helper、完成版本/资格写入顺序和跨启动测试。同步初始化例外的 ADR 与相关 fixture。
2. **四步 Overlay**：真实 targets、精确文案、viewport 几何、placement、按钮/计数、输入和焦点；
   从手动入口可完整走通，确认不写任何现有配置。
3. **生命周期集成**：启动完成信号、延迟自动进入、replay 临时 UI 恢复、最小化/托盘、互斥和全部失败清理。
   以正常/损坏/旧配置和未完成重启矩阵检验，不触碰 Child Session 原生逻辑。
4. **动效与收口验收**：有限 Pulse、切换动效和动画关闭；完成小窗口、DPI、键盘、托盘的实机矩阵，
   记录实际结果后更新 STATUS，未执行的验收项保持待验证。

依赖为 1 → 2 → 3 → 4；不拆出独立 Worker、Protocol 或通用框架工单。
