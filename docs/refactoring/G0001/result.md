# G0001 实施结果

> 实施日期：2026-09-03  
> 状态：代码与自动化门禁完成；真实 Host 人工验收待集成环境执行

## 已完成

- `MainDocument/MainView` 已替换为 `FractalArtworkDocument/FractalArtworkView`；
- 显示名冻结为“分形作品”，稳定 ID 保持不变；
- `ApplyWorkbenchMessage`、Command ID、菜单位置和全部模板命令测试已删除；
- Module 当前只登记一个 Persistable Document；Tool、命令、菜单和快捷键均为零；
- Standalone 使用真实 DI 对象图、独立 Document Scope、关闭令牌和保存窗口端口；
- 产品化三栏工作区已经落地，未实现入口明确禁用；
- 阶段档案和文档索引已经建立。

## 自动化证据

- 注册测试验证单一 Persistable Document 及零全局贡献；
- 身份测试冻结 Plugin ID 和 Document ID；
- 严格 Scope 测试成功构造两个相互独立的 Document；
- `dotnet build -warnaserror` 通过。

## 2026-09-03 Standalone 启动回归修正

首次烟雾检查只验证进程存活，遗漏了窗口构造函数同步等待异步 Document 初始化造成的 UI 线程死锁。
修正后构造函数只组装 Scope、Document 和 View；窗口 `Opened` 后再异步初始化，并由受观察任务捕获、展示
初始化失败。后续启动烟雾检查必须同时确认非零窗口句柄和窗口标题，不能再用“进程没有退出”判定成功。

修正完成后已由用户在实际桌面环境确认 Standalone 可以正常打开；基础启动验收通过。

## 待人工验收

- 在真实 Host 中确认 Descriptor 显示、Document Scope、Dock 创建和关闭释放；
- 在 Standalone 中人工检查不同 DPI、窗口最小尺寸和主题可读性；基础启动必须具有非零窗口句柄。

这些项目不影响代码实施完成，但在执行前 G0001 不标记为“全环境封板”。
