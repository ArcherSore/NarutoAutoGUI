# Worker 随发布包携带固定 MaaFramework runtime

Worker 使用与发布包一同交付的 MaaFramework Binding 和原生运行时，不从 MaaNOP 或 MFAAvalonia 安装目录借用 DLL。共用外部 runtime 看似节省体积，却会让自动化结果依赖另一产品的升级和路径；固定组合增加发布与兼容性维护成本，换取可复现的支持边界。MaaNOP 仍提供自己的 Project Interface、资源和 Agent；完整包内的 Python 不再带第二份原生库，MaaNOP 打包负责让 Agent 和前置任务加载这份共享库，并匹配语言绑定的版本；Worker 不配置项目的语言运行时。Worker 只控制 Win32 窗口，不携带 Android 用的 AgentBinary。
