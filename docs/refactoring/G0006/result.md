# G0006 实施结果

> 状态：代码、v6 迁移、专用文档和本地自动化门禁完成；真实 Host 与人工视觉验收待完成

## 已完成

- 四种生成器全部经过版本化、类型安全的内部 DAG，不再存在生成器专用整链旁路；
- 增加只读 `ImageSurface`、`Mask`，并收紧 `ScalarField` 缓冲区所有权；
- 图验证覆盖节点、端口、类型、连接、拓扑、版本及效果链诊断；
- 每个 Document Scope 独占 128 MiB / 256 项线程安全 LRU，错误、取消和超大项不会污染缓存；
- 修改渐变只失效着色下游，修改线宽不重新生成路径，路径可以跨尺寸复用；
- 九宫格、预览和导出共用创作图与缓存，原独立缩略图缓存已删除；
- 作品格式升级到 v6，显式保存图与空效果链，v1–v5 自动迁移；
- 保持一个 Persistable Document、零 Tool、零 Workbench Command、零菜单贡献和零默认快捷键。

## 自动化结果

- Debug/Release 全量测试：均为 113/113 通过；
- 新增图验证、不可变缓冲区、节点缓存、LRU、并发、取消/异常、Scope 隔离、v5→v6 和损坏 v6 测试；
- Julia、Mandelbrot、递归树、L-System 四类代表 RGBA 指纹已固定；五个经典 L-System 指纹未变化；
- Debug/Release 构建均为零警告，`dotnet format --verify-no-changes` 通过；
- G0003 Release 基准的四个数值指纹保持 `7e48c474df9a9443`、`129fcaf38794df7a`、
  `87f88b15c42a85a1`、`6e243eb8828cc0d3`；
- Standalone 进程响应正常，主窗口句柄非零，标题为 `Fractal Art · Standalone Preview`。

## 尚待人工验收

- 在真实 Host 中分别打开、保存、关闭并恢复 v1–v6 作品，核对 Document Scope 与缓存释放；
- 人工比较 Julia、Mandelbrot、递归树和 L-System 迁移前后的画面；
- 在不同窗口尺寸与 DPI 下确认界面没有暴露节点、缓存或空效果链概念。

本阶段没有使用 AIFLOW，没有增加 Windows CI，没有生成或验收正式 ZIP，也没有执行部署、签名或发布门禁。
