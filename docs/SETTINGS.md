# Application Settings Definition

本协议只描述 NarutoAutoGUI 自身设置页面。源文件为 `src/NarutoAutoGUI/Assets/settings.json`，
作为 EmbeddedResource 随 GUI 程序集发布；不是用户可编辑配置，也不参与 MaaNOP `interface.json` 解析。

## 格式

根对象包含必填 `title`、`sections` 及可选 `description`。数组顺序即展示顺序。
每个 section 包含必填 `id`、`title`、`items` 及可选 `description`；section 不嵌套。
section 与 item 的 `id` 在整个定义内唯一。

| item type | 必填字段（除 id/type） | 可选字段 |
| --- | --- | --- |
| `toggle` | `title`、`settingKey` | `description` |
| `action` | `title`、`buttonText`、`actionId` | `description` |
| `info` | `title` / `description` / `valueKey` 至少一个非空 | 其余展示字段 |

```json
{
  "title": "设置",
  "sections": [{
    "id": "updates",
    "title": "更新",
    "items": [
      { "id": "version", "type": "info", "valueKey": "update.currentVersion" },
      {
        "id": "startup", "type": "toggle", "title": "启动时检查更新",
        "settingKey": "update.checkOnStartup"
      },
      {
        "id": "check", "type": "action", "title": "检查更新",
        "buttonText": "检查更新", "actionId": "update.check"
      }
    ]
  }]
}
```

解析拒绝未知字段、未知类型、缺少必填内容、重复 id 和与类型不符的绑定字段。
绑定拒绝未注册的 key，并报告 item id。加载失败记录 `Assets/settings.json` 和异常后抛出。
不做恢复、兼容迁移、表达式、条件可见性、布局配置、任意命令参数或 handler 自动发现。

## 代码边界与绑定

- `SettingsDefinition` / `SettingsSectionDefinition` / `SettingsItemDefinition`：纯页面定义与基本校验。
- `SettingsRegistry`：显式的 toggle/action/value 三张表，将定义绑定为页面、分组和行模型。
- `SettingsToggle`：当前布尔值与保存回调；初始化只更新内存，不触发保存。
- `SettingsAction`：异步入口、可用性、提示和状态；通过 ICommand 接入按钮。业务入口负责忙状态和错误反馈。
- `SettingsValue`：代码更新的只读展示文本，用属性通知刷新界面，不轮询业务数据。
- `ApplicationSettings`：注册当前应用的 key，适配现有更新偏好文件；不持有 MaaNOP 配置或 RunPlan。
- `SettingsView`：独立 UserControl，按 item type 使用 Toggle/Action/Info 模板，共享既有颜色和控件样式。
  action 标题与按钮文字相同时只显示按钮，避免重复；空说明/状态不占空间。

| 类型 | 注册 key | 代码来源 / 入口 |
| --- | --- | --- |
| setting | `update.checkOnStartup` | `ApplicationSettings` 读写原更新偏好 |
| action | `update.check` | 原更新弹窗 + `CheckUpdateAsync` |
| action | `diagnostics.export` | 原 SaveFileDialog + `ExportDiagnosticsAsync` |
| action | `onboarding.replay` | 原 `ReplayOnboarding` 流程 |
| value | `update.currentVersion` | 启动时的项目版本、随后 Engine 检查结果 |

更新检查结果、导出结果和指引错误分别写入对应 action 的 `Status`，不需要再声明 status provider。
更新/导出忙状态和指引可用性由原 C# 流程更新。MainWindow 仍拥有弹窗、窗口生命周期和指引的真实目标，
但不再拥有 Settings 的具体行布局或更新偏好文件读写。

## 状态存储

- `config/update-check.txt`：保持原格式；缺失或内容不是精确 `false` 时开启，用户修改写 `true` / `false`。
  读取错误由原更新初始化报告，保存错误继续显示“无法保存更新设置，请重试。”并写日志。
- `config/onboarding.txt`、`config/onboarding-new-user.pending`：原指引组件继续管理，replay 不修改它们。
- `config/maanop-config.json`、MaaNOP Project Interface、RunPlan 均不参与本协议；不恢复旧统一 settings 文件。

## 扩展与验证

新增同类行时修改定义；新增行为或动态值时同时在 C# 显式注册。当前所有设置项都能用三种行表达，
不需要 custom view。未来遇到真正复杂的设置可在 View 边界增加专用视图，不应向 JSON 塞入布局或业务表达式。
现有更新弹窗和指引浮层保留专用 UI，不属于 Settings 行渲染协议。

新增 `SelfTestRunner.Settings.cs` 已接入普通自检和 `--support-only`，覆盖定义、绑定错误、三个动作路由、
toggle 读写/失败提示、版本/状态通知、真实 View 绑定、原更新流程联动及 Home/PI/配置/RunPlan 隔离。
更新测试只替换 Engine 检查的外部调用，仍经过原弹窗和完整检查状态处理，不访问网络。
既有诊断和指引自检继续验证原导出内容、后台状态、replay 行为与最小窗口滚动布局。
实际执行结果见 [Status](STATUS.md)。
