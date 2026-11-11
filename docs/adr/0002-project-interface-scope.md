# 只实现 MaaNOP 使用的 Project Interface 语义

Bunshin 目前只服务 MaaNOP，不承诺兼容任意 MaaFramework Project Interface。影响执行的未知或不受支持的语义应拒绝，而不是猜测后继续运行；这牺牲对其他项目或未来 PI 字段的自动兼容性，换取用户配置和实际 Run Plan 之间可验证的一致性。新增语义以 MaaNOP 的真实需要为依据。

这是一项可替换的策略，与运行、观察、更新等机制分开：机制不写死项目名，项目名、更新来源和展示内容来自 interface.json；支持范围只在 ProjectModel 的 Project Interface 解析中，少量项目专属常量在 ProjectProfile。接入其他项目或放宽支持范围，只改这两处。
