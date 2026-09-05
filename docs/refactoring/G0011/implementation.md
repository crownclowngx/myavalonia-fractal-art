# G0011 实施说明

## 应用与领域边界

- 增加不可变 `ArtworkCompatibilityIssue/Report` 与 `IArtworkCompatibilityService`；
- 增加 `ArtworkExportRequest/Plan` 与 `IArtworkExportPlanner`，导出器只执行已验证计划；
- 增加统一 `RenderContext.ForThumbnail`，删除缩略图临时改写 Canvas 的重复推导；
- 保持现有验证器作为像素、图层、Bloom、吸引子和高精度预算的唯一领域门禁；
- Workflow Artifact 明确创建原尺寸、不透明背景的导出计划，保持既有 Action 契约。

## Document 与界面

- Document 增加四态工作区、新建会话快速开始、错误重试和最后成功画面保留；
- 恢复时先生成兼容报告，Blocked 状态不调用渲染管线；
- 缺失能力逐项显示并允许显式删除，删除走普通历史与 Dirty 语义；
- 导出宽高、等比锁定、透明背景和恢复画布尺寸均为会话态；
- 预检失败不打开文件框，成功后使用预检时捕获的作品修订完成原子导出。

## 编码、迁移与证据

- PNG 增加 sRGB/gAMA，逐行编码时清理全透明像素隐藏 RGB；
- 新增 v1–v8 嵌入式固定作品夹具，验证全部迁移到 v8，v3–v8 默认作品一致；
- 性能工具增加四类静态闭环场景和工作集字段，仍不建立机器相关的毫秒 CI 门槛；
- 详细中文注释集中解释失败关闭、显式修复、导出快照、Alpha 和状态生命周期。

## 门禁边界

本阶段只执行本地 Debug restore/build/test/format、性能证据和 Standalone 烟雾。不使用 AIFLOW，不新增
Windows CI，不执行 Release、部署、正式 ZIP、签名、安装或发布动作。
