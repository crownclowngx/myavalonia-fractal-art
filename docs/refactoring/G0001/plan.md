# G0001 当下计划：模板清理与产品身份冻结

## 目标

把模板实例变成真实的 Fractal Art 插件壳，不提前伪造图层、效果、动画或 Tool 能力，并为 G0002/G0003
提供稳定的产品语言和组合入口。

## 开工基线

- 插件 ID 已是 `myavalonia.plugin.fractal.art`，必须保留；
- Document ID 已被模板登记为 `myavalonia.plugin.fractal.art.document.main`，保留值可避免无收益迁移；
- 类型仍叫 `MainDocument/MainView`，显示名和界面仍是“示例文档”；
- Module 登记了 `ApplyWorkbenchMessage` 模板命令和 Tools 菜单；
- Standalone 直接 `new MainDocument()`，没有验证真实依赖对象图或 Scope。

## 工作项

1. 删除模板业务命令、ID、菜单贡献及其测试，不复用旧 ID；
2. 把 Document/View 改为 `FractalArtworkDocument/FractalArtworkView`；
3. 保留稳定 Document ID，把显示名冻结为“分形作品”；
4. 建立画布、左侧创作导航、右侧属性和状态区的产品化工作区；
5. Module 冻结为一个 Persistable Document、零 Tool、零 Workbench Command、零快捷键；
6. Standalone 通过 DI Scope 承载真实 Document/View；
7. 建立 `docs/refactoring/G0001` 阶段档案。

## 验收标准

- 源码和文档中不再把模板消息描述为产品能力；
- 类型名、显示名和视图布局表达真实产品；
- 自动化注册测试冻结身份、数量和零全局贡献约束；
- Debug 构建以警告视为错误并通过；
- Standalone 可以承载真实对象图；真实 Host 人工验收单独记录。

## 明确不做

- 不增加 Tool、全局菜单、快捷键或 Workflow Action；
- 不增加 Windows CI；
- 不执行发布、打包或正式 ZIP 门禁；
- 不使用 AIFLOW。
