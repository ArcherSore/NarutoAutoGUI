# Triage labels

本仓库使用 GitHub Issues 的以下标签表达工程 skills 的五种分诊状态：

| 角色 | 标签 |
| --- | --- |
| 待分诊 | needs-triage |
| 等待补充信息 | needs-info |
| Agent 可开始 | ready-for-agent |
| 需要人工设备、权限或决策 | ready-for-human |
| 不计划处理 | wontfix |

spec 和 to-tickets 发布的可执行工作使用 ready-for-agent；真实桌面或完整包人工验收使用 ready-for-human。完成工作后关闭 Issue，不把状态复制进长期文档。
