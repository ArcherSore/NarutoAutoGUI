# Worker 随发布包携带固定 MaaFramework runtime

Worker 使用与发布包一同交付的 MaaFramework Binding 和原生运行时，不从 MaaNOP 或 MFAAvalonia 安装目录借用 DLL。共用外部 runtime 看似节省体积，却会让自动化结果依赖另一产品的升级和路径；固定组合增加发布与兼容性维护成本，换取可复现的支持边界。MaaNOP 仍提供自己的 Project Interface、资源和 Agent；完整包内的 Python 不再带第二份原生库，Agent 通过 `MAAFW_BINARY_PATH` 使用 Worker 这一份，MaaNOP 打包时按 Worker 的版本安装 `maafw`。Worker 只控制 Win32 窗口，不携带 Android 用的 AgentBinary。
