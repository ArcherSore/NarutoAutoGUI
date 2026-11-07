# Application Settings Definition

本协议只描述 NarutoAutoGUI 自身设置页面。定义源为 src/NarutoAutoGUI/Assets/settings.json，作为程序集内置资源发布；它不是用户配置，也不参与 MaaNOP Project Interface 解析。

## 格式

根对象只包含必填 sections；数组顺序决定展示顺序。section 包含 id、title、icon、items 和可选 description，且不嵌套。section 与 item 的 id 在整个定义中唯一。icon 是 WPF-UI 的 SymbolRegular 名称。

| item type | 必填字段（除 id/type） | 可选字段 |
| --- | --- | --- |
| toggle | icon、title、settingKey | description、joinPrevious |
| action | icon、title、buttonText、actionId | description、joinPrevious |
| info | icon；title / description / valueKey 至少一个非空 | 其余展示字段、joinPrevious |
| about | title | description |

每项默认独占一张卡片；joinPrevious 为 true 时并入上一项的卡片，section 的首项不能使用。

解析拒绝未知字段、类型和图标名，缺少必填内容、重复 id 与不属于该类型的绑定字段。绑定只接受显式注册的 key；错误包含 item id 和资源位置。除 joinPrevious 外不提供布局配置，也不提供表达式、handler 自动发现或静默回退。

## 绑定边界

SettingsDefinition 负责页面定义，SettingsRegistry 将 toggle、action、value key 显式绑定到业务对象，SettingsView 按四种行类型展示，并用左侧目录在连续滚动的分类间跳转和跟随。ApplicationSettings 负责应用偏好及行为入口，不持有 MaaNOP Config 或 Run Plan。新增同类设置行只需修改定义；新增行为或动态值还需注册代码。

| 类型 | 当前 key | 含义 |
| --- | --- | --- |
| setting | update.checkOnStartup | 启动时检查更新 |
| setting | application.closeToTray | 关闭窗口时隐藏到托盘 |
| setting | framework.saveOnError | 保存失败截图 |
| setting | framework.saveDraw | 保存识别标注图，不持久化 |
| setting | framework.debugMode | 框架调试模式 |
| setting | diagnostics.clearOnStartup | 启动时自动清理日志和调试文件 |
| action | update.check | 检查完整包更新 |
| action | diagnostics.export | 导出诊断包 |
| action | diagnostics.clear | 清理日志和调试文件 |
| action | onboarding.replay | 重看新手指引 |
| action | support.openAfdian | 打开支持项目页面 |
| value | update.currentVersion | 展示当前版本 |

偏好存于安装目录的 config 下；MaaNOP 用户配置、Project Interface 与指引进度各自独立。设置定义不保存这些状态。
