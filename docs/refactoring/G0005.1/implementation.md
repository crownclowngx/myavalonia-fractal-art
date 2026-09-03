# G0005.1 实施方案：逃逸时间与 L-System

> 文档状态：已实施；本文件同时记录计划基线与最终落地差异

## 总体结构

G0005.1 不引入通用节点框架。两类生成器通过现有窄渲染策略汇入两种已经验证的数据形态：

```text
Escape Time
    ├─ Julia kernel
    └─ Mandelbrot kernel
             ↓
         ScalarField → GradientMapper ─┐
                                       ├→ RgbaImage → Preview / PNG
L-System                               │
    → RuleExpander → TurtleInterpreter │
    → PathGeometry → PathStrokeRenderer┘
```

`FractalArtworkDocument` 继续拥有作品快照、历史、Dirty、异步 generation、保存和关闭语义。公式内核、规则展开和
Turtle 解释都不进入 Document。

## v5 生成器定义

v5 使用稳定的 `FractalGeneratorKind` 数值标签，并为四种受支持生成器保存明确的不可变定义：

```text
ArtworkDefinition
├─ GeneratorKind: Julia | RecursiveTree | Mandelbrot | LSystem
├─ Julia
├─ Mandelbrot
├─ RecursiveTree
└─ LSystem
```

这里刻意保留普通 record 和追加式枚举，不引入多态 JSON、反射扫描或通用节点框架。所有定义均被验证，当前标签只决定
哪条渲染策略生效；这样 v4 的 Julia/递归树配方可以精确迁移，候选切换也不会丢掉另一类自定义内容。快照 DTO 仍由
`ArtworkSnapshotCodec` 独占，v5 新字段缺失时会整体拒绝，不用默认值掩盖损坏。

### 逃逸时间定义

建议字段：

```text
EscapeTimeDefinition
├─ Formula: Julia | Mandelbrot
├─ CenterX / CenterY / Scale
├─ JuliaConstant?       # 只有 Julia 必须存在
├─ MaxIterations
├─ ForceHighPrecision
└─ PrecisionDigits
```

Julia 与 Mandelbrot 共用复平面视口、精度政策和标量场颜色映射；差异只位于迭代初值与常量来源：

- Julia：`z₀ = pixel`，`c = 用户常量`；
- Mandelbrot：`z₀ = 0`，`c = pixel`。

首版为 Mandelbrot 提供双精度内核和任意精度参考内核。Julia 扰动优化不应通过条件分支硬套到 Mandelbrot；
只有在独立基准证明必要后，再增加 Mandelbrot 专用扰动策略。

### L-System 定义

建议字段：

```text
LSystemDefinition
├─ Axiom
├─ Rules[]: Symbol + Replacement
├─ Iterations
├─ TurnAngleDegrees
├─ InitialHeadingDegrees
├─ StepLength
├─ LengthDecay
├─ StrokeWidth
└─ StrokeWidthDecay
```

颜色继续使用作品级渐变，背景继续使用画布定义。规则、绘制参数与外观保持分离，避免规则编辑器承担描边职责。

## L-System 领域管线

### 规则验证器

`ILSystemValidator` 负责：

- 公理、规则数量和文本长度；
- 左侧符号唯一且只包含一个允许符号；
- 右侧只包含已声明的绘制、控制或变量符号；
- `[` 与 `]` 静态平衡；
- 角度、步长、衰减、线宽和迭代范围；
- 预测展开符号数、线段数和最大栈深度。

验证结果应包含结构化错误码、字段路径和中文消息，使 UI 不需要解析异常文本。

### 受预算展开器

`ILSystemExpander` 只执行确定性并行替换。实现按迭代逐轮写入受限缓冲区，每次追加前检查 250,000 符号预算，
并周期性观察取消。不使用无界递归，也不把完整展开内容写入作品快照。

### Turtle 路径解释器

`ITurtlePathInterpreter` 消费符号序列并输出 `PathGeometry`：

- `F/G`：前进并产生线段；
- `f`：前进但不绘制；
- `+/-`：按固定角度旋转；
- `[`：压入位置、方向、长度、线宽和层级；
- `]`：恢复完整状态；
- 变量符号不绘制。

解释器使用显式有界栈，最多生成 50,000 条线段。输出沿用 G0005 的归一化方形逻辑画板和层级字段，
`PathStrokeRenderer` 无需知道路径来自递归树还是 L-System。

## 示例目录

`IGeneratorExampleCatalog` 是只读领域目录，按家族和公式返回不可变定义。应用示例等价于一次普通作品修改：

```text
ExampleDefinition → 当前 GeneratorDefinition → 验证 → 历史记录 → 预览
```

渲染器不读取示例 ID。用户修改后由当前定义决定画面，目录不会在渲染时再次覆盖参数。界面可以通过与目录定义
做值比较显示“示例名”或“自定义”，不增加第二份持久化参数。

## 上下文编辑器拆分

交互层没有为首批字段引入新的消息总线或通用表单框架。当前由一个 Document Scope 内的主呈现模型暴露三组明确属性：

- 生成器导航：当前家族、公式和示例选择；
- 逃逸时间编辑：共用视口/精度，以及 Julia 专属常量；
- L-System 编辑：规则文本、绘制参数、预算和诊断。

所有 setter 仍只替换同一个不可变 `ArtworkDefinition`，统一经过验证、历史、Dirty 和预览 generation；没有编辑器缓存第二份
可渲染状态。AXAML 用上下文可见性与页签组合两组检查器。等 G0006 出现第三个新家族时，再基于真实重复抽取独立子模型，
避免现在为两组表单提前建立抽象层级。

## 变体规则

变体保持当前家族和具体公式：

| 分组 | Julia | Mandelbrot | L-System |
| --- | --- | --- | --- |
| 构图 | 中心、尺度 | 中心、尺度 | 初始方向、整体尺度 |
| 形态 | 复常量、迭代 | 迭代 | 迭代、角度、步长、长度衰减 |
| 颜色 | 渐变 | 渐变 | 层级渐变 |
| Seed | 保留现有语义 | 不参与确定性公式 | 首版不参与确定性规则 |

L-System 的公理和产生式文本不会被随机修改。候选中若迭代变化导致预算溢出，按现有确定性重采样规则生成合法值，
仍失败则回退源配方。

## v5 迁移

迁移必须先构造局部 v5 对象并完整验证，再交给 Document：

| 来源 | v5 目标 |
| --- | --- |
| v1/v2/v3 | `EscapeTime / Julia`，保留原 Julia 与渐变 |
| v4 Julia | `EscapeTime / Julia`，候选和收藏逐项迁移 |
| v4 RecursiveTree | `LSystem` 家族下的 `LegacyRecursiveTree`，继续调用原路径生成器 |

旧递归树不会自动翻译成 L-System 规则，因为随机角度、固定分叉和长度衰减无法用首版确定性规则逐像素等价表达。
未来若提供“转换为分形植物”，必须是用户主动的可撤销操作，并清楚提示视觉可能改变。

## SOLID 对照

| 原则 | G0005.1 落点 |
| --- | --- |
| 单一职责 | 公式内核、规则验证、规则展开、Turtle 解释、路径描边、示例目录、编辑器和 Document 编排分离 |
| 开闭原则 | 逃逸时间公式通过窄内核扩展；L-System 与逃逸时间通过既有渲染策略汇入统一应用管线 |
| 里氏替换 | 各公式内核遵守相同标量场、取消和诊断契约；路径来源遵守相同 `PathGeometry` 契约 |
| 接口隔离 | 展开器不描边，Turtle 不读取 UI，示例目录不渲染，编辑器不保存或调度任务 |
| 依赖倒置 | Document 依赖作品编辑和渲染端口；具体公式、规则及 View 由组合入口提供 |

## 测试与门禁

### 领域测试

- Julia 与 Mandelbrot 已知点、内部点、逃逸值、渐变端点和确定性；
- Mandelbrot double/任意精度选路、极深尺度预算和取消；
- 5 个内置 L-System 示例的固定展开、线段数、边界和指纹；
- 公理、重复规则、未知符号、括号失衡、栈上下溢、符号/线段超预算；
- 展开器与 Turtle 解释器的运行中取消；
- 示例应用后任意字段可编辑，且目录对象不被修改；
- 各家族变体分组、锁定、预算和公式/规则保持语义。

### 应用与 Document 测试

- 两个 Document 的家族、公式、规则和页签互相隔离；
- 切换家族、公式和示例进入统一历史与 Dirty；
- 快速编辑规则时迟到路径不能覆盖新预览；
- 非法编辑保留上一成功帧并显示结构化诊断；
- 九宫格、收藏、恢复、续变和 PNG 导出贯通两个家族；
- v1–v5 作品、候选和收藏迁移及完整往返；
- 真实 View 的编译绑定、上下文页签和隐藏参数不会被误改。

### 本地门禁

```powershell
dotnet restore
dotnet build FractalArtPlugin.slnx -c Debug -warnaserror
dotnet test FractalArtPlugin.slnx -c Debug --no-build
dotnet build FractalArtPlugin.slnx -c Release -warnaserror
dotnet test FractalArtPlugin.slnx -c Release --no-build
dotnet format FractalArtPlugin.slnx --verify-no-changes --no-restore
```

还要执行 Standalone 启动烟雾并确认窗口句柄和标题。真实视觉、不同 DPI、真实 Host v4→v5 恢复属于人工验收。
本阶段不使用 AIFLOW，不增加 Windows CI，不执行部署、发布或正式 ZIP 门禁。
