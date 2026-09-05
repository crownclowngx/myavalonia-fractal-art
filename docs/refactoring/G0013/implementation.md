# G0013 实施说明

## 生产变更

- Fractal：复用既有三个 Provider Action 和配方导出接口，生产渲染代码保持不变。
- ImageLab：新增目录输出适配与注册；把单图处理拆成准备结果和最终提交，保留旧 Action Schema。
  实际流预算、marker 白名单、祖先路径检查和 PNG/IHDR 前置校验集中在文件适配层。
- Studio：新增示例构建器、Artifact 白名单投影、只读源校验与 Scoped 恢复台账；
  Runner 增加同步开始/终态观察端口，默认仍是失败停止和运行后清空普通输出。
- UI：新增可折叠面板；文件多选、排序、移除、目录选择和效果编辑使用独立端口。
  生成/续跑/重新生成仅准备定义，主工具栏执行继续走同一 RunSession。
  显式清理逐项调用 Release；原取消命令也取消整轮清理或在途文件选择。
- 生命周期：关闭立即清空台账，迟到结果不重新写入；同一 Document 的操作防重入，不跨 Scope 共享恢复状态。

## 自动化组织

ImageLab 单元测试覆盖协议、旧 Schema 兼容、有界读取、真实 PNG、错误输入、重解析点与取消。
Studio 单元测试覆盖冻结快照、表单、预算、白名单、防泄漏、迟到选择/恢复、逐项清理和兼容检查。

Fractal 新增 `FractalArtPlugin.WorkflowIntegration.Tests`，通过 SDK 注册接口取得真实 Module 的描述符和 Handler。
每个 Provider 使用独立服务容器，每次调用创建并释放独立 Scope；计数探针验证释放，不直接引用私有 DTO。
Studio 测试目录夹具与真实注册 Schema 对照，避免两侧各自维护的测试数据悄悄漂移。

Headless 测试在独立 UI 线程运行真实面板，验证编译绑定并生成 `studio-panel.png`。
本地 Gate 使用独立 TRX 文件，防止多个测试项目覆盖同名证据。

## 本地命令

在 Fractal 仓库运行：

```powershell
./tools/verify-g0013.ps1
```

可通过 FractalRoot、ImageLabRoot、StudioRoot 指定三个已有仓库位置。
脚本执行 locked restore、Debug 零警告构建、四个测试项目、三个解决方案的只读格式校验、
Fractal 既有指纹基准、Studio Fake 自检和三个新启动的空白 Standalone 烟雾。
原始日志、TRX、基准和 summary.json 位于忽略目录 `artifacts/test-results/G0013/local-gate/`。

ImageLab 旧文件中已有全量格式检查差异，本阶段只统一其空白格式；不改变这些算法的语义。
已有用户修改继续保留。没有执行 AIFLOW、Windows CI 或任何发布/ZIP/安装门禁。

## 关联文档

- [编排与恢复设计](workflow-orchestration-design.md)
- [ImageLab 专项](../../../../myavalonia-image-lab/docs/refactoring/G0013/README.md)
- [Studio 专项](../../../../myavalonia-workflow-studio/docs/refactoring/G0013/README.md)
- [实施结果与待验收项](result.md)
