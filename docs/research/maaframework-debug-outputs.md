# MaaFramework debug 输出定义核对

核对日期：2026-09-21。只研究日志与调试产物，不修改执行代码或开启额外调试选项。

## 核对版本与结论

安装包 Python distribution 为 `maafw 5.12.3`；Worker 历史 readiness 信息为 Binding `5.10.0.0`、
Runtime `v5.12.3`。因此以下规则以官方 MaaFramework `v5.12.3` 为准，
对应 commit `0c3f6454902b8ff9f7697cc6b09a7a935a41cdbb`，其 MaaUtils 子模块固定为
`0c2556cfcff85eab8c2fa4529d71e37c310a3b78`。[版本源码][framework]

**不能从当前目录只有一个 `maafw.log`，推断框架只产生这一份日志。**
当前版还会产生 `maafw.bak.<时间戳>.log`。`maa.log`、`maa.bak.log` 都不是此版本的日志命名。
官方故障反馈文档仍写 `maafw.bak.log`，但该 tag 对应的日志实现已经使用带时间戳的备份名；
诊断导出应以实际版本的源码定义为准。[日志文件名][logger-names] [旧文档表述][troubleshooting]

## 输出目录与触发条件

下表假设 `LogDir` 已设置为 `./debug`；相对路径以进程工作目录为基准。

| 文件或目录 | 何时产生 | 是否属于纯日志 |
| --- | --- | --- |
| `debug/maafw.log` | 设置非空 LogDir 后开启文件日志 | 是 |
| `debug/maafw.bak.<时间戳>.log` | 日志轮转检查时，当前日志达到 16 MiB | 是 |
| `debug/vision/*.jpg` | `SaveDraw=true`，执行识别或相关图像比较并生成绘图 | 否，识别绘图 |
| `debug/on_error/*.png` | `SaveOnError=true`，Pipeline 错误分支且有缓存截图 | 否，游戏截图 |
| `debug/screencap/*.<format>` | Pipeline 显式执行 `Screencap` action，且有缓存图像 | 否，游戏截图 |

文件日志由 `set_log_dir` 启动；`StdoutLevel` 只控制 stdout 级别，不等同关闭文件中的调试日志。
[设置入口][set-options] [Logger 输出通道][logger-names]

`vision` 文件名由时间戳、识别节点名和 reco id 等组成；保存逻辑显式检查 `SaveDraw`。
`on_error` 为 `<时间戳>_<节点名>.png`，覆盖动作失败、错误处理循环和没有有效识别节点的错误分支。
`Screencap` action 可指定文件名与格式，默认名含时间戳和节点名；这不是打开 DebugMode 自动产生的文件。
[绘图保存][vision] [识别命名][recognizer-name] [错误截图触发][error-trigger]
[错误截图保存][error-save] [Screencap action][screencap]

## DebugMode 与默认值

这些选项相互独立，**设置 LogDir 不等于开启 DebugMode，DebugMode 也不会自动打开 SaveDraw**。

- 底层 `OptionMgr` 初始 LogDir 为空；`save_draw`、`debug_mode`、`save_on_error` 都是 `false`。
- DebugMode 使识别结果提供内存中的 `raw/draws`；绘图生成条件为 `SaveDraw || DebugMode`，
  但落盘条件仍然只有 `SaveDraw`。未发现该开关额外写 Pipeline JSON 或自动生成 dump 的路径。
- 官方接口文档还描述 DebugMode 的 focus 回调行为；这与磁盘输出不是同一件事。
- MaaToolkit 的配置入口另有默认值：`logging=true`、`save_draw=false`、`save_on_error=true`。
  只有确实调用该配置初始化入口时，才能把这组默认值用于集成应用。

[底层默认值][defaults] [识别原图缓存][recognizer] [绘图生成与落盘][vision]
[接口说明][options-doc] [Toolkit 默认值][toolkit-defaults] [Toolkit 应用选项][toolkit-apply]

本机 MaaNOP 的 `agent/main.py` 仅调用 `Tasker.set_log_dir("./debug")`，未显式开启上述三个开关。
当前 Worker 源码未找到对应的全局日志选项初始化或 Toolkit 配置初始化调用。
因此现存 `maafw.log` 不能直接证明 Worker 和 Python Agent 两侧的框架诊断都已完整写入；
此处记录接入缺口，需单独验证进程内初始化与真实执行日志，不能通过导出文件存在性断言完整覆盖。

## 日志轮转的精确规则

- 当前日志名固定为 `maafw.log`，备份格式为 `maafw.bak.{}.log`。
- 时间戳格式为 `yyyy.MM.dd-HH.mm.ss.<毫秒>`，使用本地时间；毫秒没有补零要求，通常为 1–3 位。
  示例：`maafw.bak.2026.09.21-09.35.00.123.log`。不是递增编号。
- `rotate()` 仅在文件大小 **大于等于 16 × 1024 × 1024 字节** 时复制备份，并重新打开当前日志。
- 检查发生在日志初始化/重新初始化、`flush()`；Tasker 开始任务时会 flush，Logger 每累计
  1,000,000 条日志也会 flush。因此 16 MiB 是检查阈值，不是每份日志的严格大小上限。
- 没有固定“只保留一个备份”或“最多 N 份”规则。初始化会启动异步清理，递归删除 LogDir 内
  修改时间超过 7 天的 `.log`、`.jpg`、`.png` 普通文件；不是按备份数量清理。

[命名常量][logger-names] [时间格式][timestamp] [轮转与清理][logger-rotation]
[计数触发][logger-count] [任务开始 flush][task-flush]

## 其他产物与边界

- Record Controller 是显式创建的录制控制器，输出调用者指定的 recording 文件，以及相邻的
  `<recording文件stem>-Screenshot/screencap_<序号>.png`。不默认写 debug，也不由 DebugMode 自动开启。
  [录制输出][record]
- Command action 的图像参数使用系统临时目录中的 PNG，不属于 LogDir。[临时截图][command]
- Crash dump 不是此 DebugMode 开关的自动 debug 目录产物；官方故障文档的 Windows dump 示例是
  系统的 `AppData/Local/CrashDumps`。没有依据把 debug 中任意 dump 递归加入诊断包。[故障说明][troubleshooting]

## 对诊断包的直接影响

当前纯日志白名单应覆盖 `debug/maafw.log` 和 debug 顶层实际存在的
`maafw.bak.<时间戳>.log`；不能仅写死当前恰好存在的 `maafw.log`。
可按源码时间戳结构精确匹配，不应扩大成整个 debug 目录或任意文件。
历史兼容 `maafw.bak.log` 是否有必要，应依据支持版本决定，不能把它当 v5.12.3 必然产物。
动态轮转备份不存在时不需要虚构一个固定缺失备份文件名。
原需求排除截图的边界仍适用，`vision/`、`on_error/`、`screencap/`、录制截图不属于本轮日志包。
本笔记仅定义应修正的采集范围，不表示导出实现已经修正或验证了轮转文件。

补充核对：调查时可见的较新稳定 tag `v5.13.1` 使用不同 MaaUtils commit，
但比较上述 Logger、Time 相关文件未发现差异；本文不以浮动 master 或现行网页替代 v5.12.3 证据。

[framework]: https://github.com/MaaXYZ/MaaFramework/tree/v5.12.3
[logger-names]: https://github.com/MaaXYZ/MaaUtils/blob/0c2556cfcff85eab8c2fa4529d71e37c310a3b78/include/MaaUtils/Logger.h#L11-L12
[logger-rotation]: https://github.com/MaaXYZ/MaaUtils/blob/0c2556cfcff85eab8c2fa4529d71e37c310a3b78/source/Logger/Logger.cpp#L87-L184
[logger-count]: https://github.com/MaaXYZ/MaaUtils/blob/0c2556cfcff85eab8c2fa4529d71e37c310a3b78/source/Logger/Logger.cpp#L267-L281
[timestamp]: https://github.com/MaaXYZ/MaaUtils/blob/0c2556cfcff85eab8c2fa4529d71e37c310a3b78/include/MaaUtils/Time.hpp#L46-L53
[task-flush]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Tasker/Tasker.cpp#L317-L321
[defaults]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Global/OptionMgr.h#L47-L53
[set-options]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Global/OptionMgr.cpp#L33-L108
[vision]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Vision/VisionBase.cpp#L69-L97
[recognizer]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Task/Component/Recognizer.cpp#L97-L107
[recognizer-name]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Task/Component/Recognizer.cpp#L614-L618
[error-trigger]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Task/PipelineTask.cpp#L67-L86
[error-save]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Task/PipelineTask.cpp#L456-L478
[screencap]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Task/Component/Actuator.cpp#L547-L573
[record]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaRecordControlUnit/RecordController.cpp#L11-L115
[command]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaFramework/Task/Component/CommandAction.cpp#L104-L114
[toolkit-defaults]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaToolkit/Config/GlobalOptionConfig.h#L22-L34
[toolkit-apply]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/source/MaaToolkit/Config/GlobalOptionConfig.cpp#L65-L80
[options-doc]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/docs/zh_cn/2.2-%E9%9B%86%E6%88%90%E6%8E%A5%E5%8F%A3%E4%B8%80%E8%A7%88.md#L24-L41
[troubleshooting]: https://github.com/MaaXYZ/MaaFramework/blob/v5.12.3/docs/zh_cn/5.1-%E9%97%AE%E9%A2%98%E5%8F%8D%E9%A6%88.md#L26-L66
