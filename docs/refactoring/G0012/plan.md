# G0012：Workflow Action Provider 批量作品渲染计划

> 2026-09-05；按确认方案实施。实施事实与待验收项见 [结果](result.md)。

## 目标

在 G0007 Render/Release 基础上增加 1–16 个保存配方的批量最终质量 PNG 渲染，沿用 File Artifact v1。
成功移交整批 run 产物；失败或取消停止后续项并回滚自有文件。不实现自动变体、持久文件输出或 Host 原生 Artifact 服务。

## 实现约束

- SOLID 优先，普通组合和窄接口注入；Handler 负责 JSON/SDK，应用用例负责预检和批次事务，存储负责文件所有权。
- 新增 `myavalonia.plugin.fractal.art.workflow.export-artwork-batch`；保留原 Render/Release 身份、Schema 和确认策略。
- 输入只包含 `items[{itemId,recipePath}]`，路径必须绝对，ID 唯一、非空且不超过 64 字符。
- 输出为有序 `results[{itemId,artifact,image}]`；读取全部固定快照并预检后顺序渲染，复用作品尺寸、背景和完整图层。
- 单配方 4 MiB、整批 16 MiB；单边不超过 4096，单图 16,777,216 像素、整批 67,108,864 像素，继续执行既有领域预算。
- 每个 Artifact 使用独立操作 GUID，marker 兼容补充 InvocationId/itemId；取消检查覆盖读取、预检、逐项渲染和提交。
- 暂存所有权延伸到 Handler 序列化之后；回滚独立于已取消令牌，逐项尽力清理，不掩盖原始异常，marker 最后删除。
- 详细中文注释解释生命周期、兼容和失败边界；不引入通用任务引擎、事件总线或跨插件私有依赖。

## 验收及交付

新增契约、预算、取消、真实 PNG、所有权和 Scope 自动化；执行本地 Debug restore/build/test/format、性能基准和 Standalone 烟雾。
同步 README、产品路线、接入文档、质量基线及本目录四份专用文档。

不使用 AIFLOW，不增加 Windows CI，不执行 Release、正式 ZIP、部署、签名或发布门禁。
真实 Host 的自调用/嵌套治理、独立 Provider Scope、跨 ALC 与 Studio 联调列入发布阶段待验收。
