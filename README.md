# FractalArtPlugin

这是 `myavalonia.plugin.fractal.art` 的 Managed Plugin 解决方案。当前已完成 G0001–G0003：
产品化插件壳、可持久化空作品闭环，以及 Julia → 标量场 → 线性渐变 → 预览/PNG 导出的首条纵向切片。
真实交付物是 `src/FractalArtPlugin.Plugin`；`Standalone` 只负责快速预览同一份 View、Document 与业务服务。

> 第一次开始开发前，请先阅读 [项目文档与快速开始](docs/README.md)。其中说明了三个子项目和
> Standalone 窗口的职责、接入真实 Host 的边界，以及临时部署和正式 ZIP 发布流程。

```powershell
dotnet restore
dotnet build
dotnet run --project src/FractalArtPlugin.Standalone
dotnet msbuild src/FractalArtPlugin.Plugin/FractalArtPlugin.Plugin.csproj -t:BuildManagedPluginPackage -p:Configuration=Release
```

要在真实 Host 中调试，请显式提供 Host 的 `Controls` 目录：

```powershell
dotnet msbuild src/FractalArtPlugin.Plugin/FractalArtPlugin.Plugin.csproj `
  -t:DeployManagedPlugin `
  -p:ManagedPluginDeployRoot=C:\Path\To\Host\Controls
```

Standalone 只能验证界面和插件自身对象图；manifest、加载上下文、Document Scope、Dock、Tool 和
生命周期必须使用真实 Host 做最终验收。

首版保持零 Tool、零 Workbench Command、零默认快捷键。G0001–G0003 的计划、实施方案、结果与门禁证据见
[重构实施档案](docs/refactoring/README.md)。
