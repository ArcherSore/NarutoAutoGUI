# 只实现 MaaNOP 使用的 Project Interface 语义

NarutoAutoGUI 是 MaaNOP 的专用前端，不作为通用 MaaFramework Project Interface 客户端。影响执行的未知或不受支持的语义应拒绝，而不是猜测后继续运行；这牺牲对其他项目或未来 PI 字段的自动兼容性，换取用户配置和实际 Run Plan 之间可验证的一致性。新增语义以 MaaNOP 的真实需要为依据。
