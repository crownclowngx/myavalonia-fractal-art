# Workbench Command 开发说明

Plugin SDK `3.3.0` 允许插件把少量高价值用户意图声明为 Workbench Command，再由 Host 菜单、快捷键或
Command Palette 投影。Command 不是 Avalonia `ICommand` 的替代品；只对当前 Document 有意义的按钮、表单、
拖放和参数修改应继续使用插件内部命令。

## 当前产品决策（G0001–G0011）

模板阶段的 `ApplyWorkbenchMessage` 已在 G0001 移除，且不复用原身份包装其他语义。当前 Module 不登记
Workbench Command、菜单贡献或快捷键：

```text
当前作品内部按钮
    → FractalArtworkDocument 的异步命令
    → 窄应用服务
    → 当前 Document Scope 的状态与取消令牌
```

撤销、重做、九宫格生成、候选采用/收藏、重新预览、取消和 PNG 导出都只针对当前作品，不具备跨整个工作台的稳定语义，因此保留为
Document 内部命令。后续只有经过单独评审的高价值意图，才可获得新的稳定 Command ID。

## 必须保持的边界

1. 注册阶段只保存稳定身份、展示元数据和目标 `DocumentTypeId`，不保存实例、回调、Provider 或 `ICommand`；
2. 状态和执行属于当前 `FractalArtworkDocument` 实例，同类型的两个文档必须隔离；
3. 异步执行返回真实可等待任务并首先观察取消，不能用 `async void` 隐藏未完成工作；
4. 若未来登记命令，Host 仍负责活动 Document 路由、UI 线程切换、菜单排序和快捷键冲突。

## 为什么默认不注册快捷键

快捷键是整个工作台共享的稀缺资源。当前插件不调用 `AddDocumentCommand`、
`AddMenuCommandContribution` 或 `AddKeyBindingContribution`。只有命令语义稳定、冲突政策清楚且用户确实
需要高频访问时，才应增加全局贡献。

## 未来适配局部命令

可以让 Workbench Target 委托现有应用用例，但公共身份始终是 `CommandId`。Target 不得解析 Host 或插件根
容器；依赖由 Document Scope 构造注入，Target 也不承担 Catalog、Dock 或菜单生命周期。

## 测试清单

- Module 只为自己拥有的 Document 注册命令；
- 当前阶段命令、菜单和快捷键数量均为零；
- 新增后验证已知/未知身份、当前实例隔离、取消、并发与定向状态通知；
- Standalone 不模拟 Host 的活动 Document 路由或菜单投影；
- 正式发布阶段再使用真实 ZIP 和 Host 验证独立 ALC。
