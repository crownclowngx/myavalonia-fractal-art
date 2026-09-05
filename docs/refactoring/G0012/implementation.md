# G0012 实施说明

## 应用与传输边界

新增 `WorkflowBatchExporter`、`IWorkflowBoundedRecipeReader` 和批次输入/结果值对象。
批次服务先完成所有配方读取与导出计划预检，再按输入顺序调用存储；每项拥有独立操作 GUID。
`WorkflowBatchArtifacts` 跨越应用层返回和 Handler 序列化边界持有回滚责任，成功后显式移交。

新增 `ExportArtworkBatchWorkflowActionHandler`，由 Module 登记第三个 Action。它解析严格输入、转发同步进度、
输出与单张一致的 Artifact/image 结构。已有 Render/Release 的 Schema、ID、风险和确认策略保持不变。
共享 Workflow SDK 仅作为测试依赖加入 `1.0.0`，生产插件不新增跨插件私有程序集引用。

## 文件与兼容

RecipeFiles 从读取前 Length 检查改为有界流实际计数；其已有公开 `ReadAsync` 复用该实现。
ArtifactStore 的内部创建端口新增可选 origin 参数，marker 添加可选 InvocationId/itemId，旧 marker 兼容恢复。
存储补齐预先/最后取消、PNG 字节预算、重复目录拒绝、各级重解析点检查、独占 marker 创建和 marker 最后删除。
单张 Render 在返回 JSON 前也执行最后检查，并在失败后回滚当前产物。

没有改变 Artwork v8、Workflow Recipe v1、renderer v1、插件/Document 稳定身份，也没有增加 UI、Tool 或命令。
Scope 生命周期由 SDK 注册和 Host 调用负责，插件不创建嵌套 Run。

## 自动化分工

- `G0012WorkflowBatchTests`：参数形状、唯一身份、字节/像素预算、预检顺序、缺失能力、Schema 与 ForEach 引用、取消及回滚、输出预算。
- `G0012WorkflowFileTests`：真实五类生成器与多图层 PNG、摘要/marker、旧 Release 幂等、配方读取、流增长/取消、写入故障、文件占用恢复及路径保护。
- `PluginCompositionTests`：三个 Action 注册、严格 Scope 构造、服务/缓存隔离，以及 Scope 释放后的缓存不可用。

测试用实际依赖注入与真实 PNG/文件系统覆盖关键边界；需要注入失败或探测大图预算时使用窄端口替身，
不通过真实大图耗尽内存来证明预算拒绝。详细契约和限制见 [设计](workflow-provider-design.md)，命令与结果见 [结果](result.md)。
