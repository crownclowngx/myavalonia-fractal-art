# G0001–G0005 实施与 G0005.1 设计档案

本目录独立保存 Fractal Art 产品化重构的专业文档，避免把阶段性决策、实施细节和验收证据散落在产品愿景中。
产品长期路线仍以 [`product-shape-and-implementation-plan.md`](../product-shape-and-implementation-plan.md) 为基线。

## 目录

| 阶段 | 当下计划 | 实施方案 | 实施结果 |
| --- | --- | --- | --- |
| G0001 模板清理与产品身份冻结 | [计划](G0001/plan.md) | [方案](G0001/implementation.md) | [结果](G0001/result.md) |
| G0002 作品领域模型与空持久化闭环 | [计划](G0002/plan.md) | [方案](G0002/implementation.md) | [结果](G0002/result.md) |
| G0003 第一条 Julia 纵向渲染切片 | [计划](G0003/plan.md) | [方案](G0003/implementation.md) | [结果](G0003/result.md) |
| G0004 变体探索与艺术化参数 | [计划](G0004/plan.md) | [方案](G0004/implementation.md) | [结果](G0004/result.md) |
| G0005 第二类数据形态——递归路径 | [计划](G0005/plan.md) | [方案](G0005/implementation.md) | [结果](G0005/result.md) |
| G0005.1 双生成器入口与可编辑 L-System | [计划](G0005.1/plan.md) | [交互](G0005.1/interaction-design.md) / [方案](G0005.1/implementation.md) | 待实施 |

G0003 后续的高精度性能与 Domain 模块化工作已独立实施并完成本地自动化，见
[专项计划](G0003/precision-performance/plan.md)、[优化设计](G0003/precision-performance/optimization-design.md)、
[实施说明](G0003/precision-performance/implementation.md)、[基准](G0003/precision-performance/baseline.md)和
[结果](G0003/precision-performance/result.md)。它不占用 G0004 阶段编号，真实 Host 人工验收仍待完成。

共同工程约束、SOLID 落点、测试矩阵和本轮明确排除的 CI/发布门禁见
[质量基线与门禁](quality-baseline.md)。

## 状态口径

- “已实施”表示代码、文档及本地自动化门禁已经完成；
- “已封板”还要求对应阶段声明的真实 Host 或人工视觉验收全部完成；
- “待实施”表示需求、交互和技术方案已归档，但代码与验证尚未开始；
- 本轮按要求不增加 AIFLOW、不增加 Windows CI、不执行发布与正式 ZIP 门禁；
- 结果文档明确区分自动化已通过与留给集成环境的人工验收，不用降低措辞掩盖缺口。
