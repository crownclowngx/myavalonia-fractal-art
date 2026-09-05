# G0013 实施计划

> 2026-09-05；按确认方案实施。当前阶段仅进行本地开发验证，真实 Host 和发布验收另行执行。

## 目标与范围

在 Studio 内生成单张或 1–16 项配方的完整 Definition v2，按 Fractal 渲染 → ImageLab 文件后处理 → Fractal 释放执行。
保留已确认成功的 PNG，提供当前会话的未完成项续跑、重新生成和显式清理。输入继续是已保存的 Workflow Recipe v1，
不增加 Seed 覆盖、跨重启恢复、自动补偿或重试引擎。

## 实施顺序

1. ImageLab 新增目录输出 Action，保留旧文件 Action 的 Schema；补强有界读取、PNG 头部、路径与最后提交边界。
2. Studio 构造当前目录下的完整定义；新增冻结示例快照、白名单终态观察与恢复台账。
3. 接入 Document 内置面板、文件选择端口、取消、关闭、输入排序与状态显示。
4. 添加两侧单元测试与独立真实 Handler 集成工程，运行 Debug 构建、全量测试、格式只读检查、指纹与烟雾。
5. 同步产品计划、索引和专用文档，记录实际通过证据及仍待执行的真实 Host 验收。

## 工程约束

SOLID 第一顺位；只使用窄端口、适配器、不可变快照和应用用例。不共享插件私有 CLR DTO，
不让 Provider Handler 调用 Gateway，不更改 Host/SDK、File Artifact v1、Artwork v8 或 Studio Definition v2。
注释使用中文解释职责、提交窗口、取消与清理所有权。

保留工作区已有实现。无 AIFLOW、Windows CI、Release、ZIP、安装、部署、签名、上传或发布门禁。
