# G0001 实施方案

## 身份决策

插件 ID 和首个 Document ID 都保持原值。只把代码符号改为产品语义名称，因为持久 ID 是外部兼容契约，
类名是内部实现细节。模板 `ApplyWorkbenchMessage` 被完整删除，不将旧 ID 改造成导出或渲染命令。

## 组合与生命周期

`FractalArtPluginModule` 只登记：

```text
FractalArtworkDocument（Scoped）
    ↔ FractalArtworkView（Transient）
```

业务服务统一从 `AddFractalArtPluginServices` 注册。Standalone 建立自己的根 Provider 和 Document Scope，提供
`IDocumentLifetime` 与 `IPluginWindowInteraction` 的开发期实现；关闭窗口时释放 Scope，让 Document 和取消源
按真实生命周期回收。

## 视图形态

一个 Document 内部包含顶栏、创作导航、中央画布、当前属性和状态栏。G0003 以前尚未实现的入口保持禁用并
带明确说明，不用可点击占位制造错误承诺。所有局部操作使用 Document 自身命令。

## SOLID 约束

- Module 只做声明式组合，不处理运行时业务；
- Standalone 只做承载与 Host 端口适配，不复制业务对象；
- Document 不持有 Window、Dock、Catalog 或根容器；
- 以后新增能力必须通过应用服务接入，不能重新把算法塞回 View code-behind。
