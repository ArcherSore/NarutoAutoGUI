# Worker 随发布包携带固定 MaaFramework runtime

Worker 使用与发布包一同交付的 MaaFramework Binding、原生运行时和 AgentBinary，不从 MaaNOP 或 MFAAvalonia 安装目录借用 DLL。共用外部 runtime 看似节省体积，却会让自动化结果依赖另一产品的升级和路径；固定组合增加发布与兼容性维护成本，换取可复现的支持边界。MaaNOP 仍提供自己的 Project Interface、资源和 Agent。
