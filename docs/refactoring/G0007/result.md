# G0007 实施与测试结果

## 已完成

- Fractal Module 同时登记 caller-bound Gateway、Render Action、Release Action、Lifecycle 和一个 Persistable Document；
- Art 内 ImageLab 导出表单及协调器已接入，效果设置保持会话级；
- Workflow Recipe v1、File Artifact v1、Render/Release Handler 与 TTL 清理已实现；
- Standalone 使用不可用 Gateway，仍能独立启动而不伪造跨插件处理；
- ImageLab 缺失前置失败、finally 清理、Run Dispose 顺序、Artifact 所有权/摘要/幂等和 Descriptor 已覆盖。

当前 Fractal Debug/Release 全量结果均为 `126 passed, 0 failed, 0 skipped`；ImageLab Debug/Release 均为
`772 passed`；Host 核心 Debug/Release 均为 `292 passed`，Release 全解决方案各测试程序集全部通过。
三个仓库中 G0007 改动文件的 `dotnet format --verify-no-changes --no-restore --include ...` 均通过；
为避免扩大改动，没有顺带格式化 Host 与 ImageLab 的历史文件。

此外直接用 Host 的 `WorkflowSchemaValidator` 验证三个生产 Descriptor，均为 `valid=true, issues=0`；
用 `WorkflowReferenceTypeSystem` 验证 `render.artifact → effects.source` 与
`render.artifact → release.artifact`，两条 Studio 静态赋值均为 `valid=true, issues=0`。

## 未宣称完成

当前阶段未执行 Windows CI、正式 ZIP、签名、发布或 NuGet 门禁。真实 Host 中两条人工操作路径需要三个插件
按开发部署方式同时安装后验收；未完成前只称“代码与本地自动门禁已实施”，不称发布封板。
