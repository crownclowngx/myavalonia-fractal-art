# FractalArtPlugin 开发快速开始

本解决方案用于开发 `myavalonia.plugin.fractal.art` Managed Plugin。它把真实插件、独立 Avalonia 开发窗口和
自动化测试放在同一个解决方案中，使界面与业务代码既能快速预览，也能由 MyAvaloniaManagement Host
按正式插件协议加载。

## 项目结构

```text
FractalArtPlugin/
├─ FractalArtPlugin.slnx
├─ src/
│  ├─ FractalArtPlugin.Plugin/       # 唯一真实插件程序集和正式交付内容
│  └─ FractalArtPlugin.Standalone/   # 只供本地开发的 Avalonia 窗口
├─ tests/
│  └─ FractalArtPlugin.Tests/        # 插件业务、状态和注册行为测试
└─ docs/
   └─ refactoring/             # 按 G 编号归档的计划、实施方案、结果与质量门禁
```

`FractalArtPlugin.Plugin` 是唯一正式插件项目。Standalone 和 Tests 都直接引用它，不能各自复制一套 View、
ViewModel、服务或贡献清单。

## 最短开发流程

在解决方案根目录打开 PowerShell：

```powershell
dotnet restore
dotnet build -c Debug -warnaserror
dotnet test -c Debug --no-build
dotnet run --project src/FractalArtPlugin.Standalone
```

Standalone 适合快速检查 AXAML、编译绑定、命令和插件自身对象图。写到可以联调时，再把干净的插件目录
部署到真实 Host；发布前则必须生成正式 ZIP。不要把 Standalone 能运行当成 Host 验收已经通过。

## 接下来阅读

1. [Fractal Art 插件形态、产品定位与闭环实施计划](product-shape-and-implementation-plan.md)
2. [项目、Host 与 Standalone 窗口职责](project-and-window-responsibilities.md)
3. [临时部署、正式发布与验收](deployment-and-release.md)
4. [Workflow Action Provider 与 Consumer 接入](workflow-actions.md)
5. [Workbench Command 开发说明](workbench-commands.md)
6. [G0001–G0003 重构实施档案](refactoring/README.md)
7. [G0003 高精度性能与领域模块化专项档案](refactoring/G0003/precision-performance/result.md)

## 开发前记住

- `myavalonia.plugin.fractal.art` 是持久身份，发布后不要因为显示名、项目名或文件夹改名而改变它。
- manifest 由 Build 包生成，不要手写或复制一份长期维护。
- 插件只通过公开 Plugin SDK 接入 Host，不引用 Host 内部项目。
- 新增插件运行时 NuGet 包时，要同时更新根目录 `Directory.Packages.props`、Plugin 项目的
  `PackageReference` 和 `ManagedPluginPrivatePackage`；完整示例见部署文档。
- 当前交付目标是 Windows x64；插件替换后必须完整重启 Host，不支持热更新。
- Workflow Action Provider 与 Consumer 是两种互斥角色，选择前先阅读专项文档，不要在同一插件中同时注册。
- Workbench Command 只提升跨工作台有价值的用户意图；G0001–G0003 当前保持零全局命令和零快捷键。
