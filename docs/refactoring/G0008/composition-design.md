# G0008 合成、遮罩与缓存设计

## 唯一事实来源

`ArtworkDefinition v7` 持有 `Canvas + Layers + MasterEffects + Presentation`。顶层 `Layers` 和组内 `Children`
均按最上层在前排列；组只允许一层，子项可以是已知分形或不可用占位，但不能再次是组。

```text
ArtworkDefinition
├─ Canvas
├─ Layers（顶层，最上层在前）
│  ├─ FractalLayerDefinition
│  └─ LayerGroupDefinition
│     └─ FractalLayerDefinition / UnavailableLayerDefinition
├─ MasterEffects（Tone → Bloom）
└─ Presentation（含稳定 SelectedLayerId）
```

每个已知分形层拥有 Seed、四类生成器定义、渐变和独立探索状态。旧的 `Seed/Julia/...` 属性只是当前选中
分形层的兼容投影，不会进入 v7 JSON，因此不存在第二份可漂移参数。

## 固定像素顺序

```text
分形生成 → 着色/透明描边 → 图层变换 → 画布空间 Mask
→ 图层不透明度 → 组内/根级混合 → Tone → Bloom → 输出
```

根级先创建一次画布背景。每个组先在透明面上完成子层合成，再整体应用组变换、组遮罩、不透明度和混合。
默认单层且没有任何合成差异时走与 v6 像素等价的直通路径，避免透明往返取整，四类旧 RGBA 指纹保持不变。

## 变换与 Alpha

变换参数以画布百分比保存：位置 `-200%–200%`、缩放 `1%–800%`、旋转 `-180°–180°`、锚点
`0%–100%`。栅格变换从目标像素中心逆向映射到源图，图像采用预乘 Alpha 双线性插值，Mask 采用单通道
双线性插值；越界权重视为透明或 0。正旋转在屏幕坐标系中为顺时针。

混合遵循带 Alpha 的 source-over：Normal、Multiply、Screen、Add、Overlay 只改变重叠颜色函数，源 Alpha
仍同时乘图层不透明度与 Mask。全透明像素保持零 RGB，避免色调和 Bloom 制造隐藏颜色污染插值边缘。

## ScalarField 遮罩

Mask 只允许引用 Julia/Mandelbrot：

1. 内部点固定为 0；
2. 逃逸归一值在 `threshold ± softness/2` 内执行确定性 smoothstep；
3. `softness=0` 使用硬阈值；
4. 最后执行反相；
5. 使用遮罩源变换映射到目标共同画布空间。

隐藏且未引用的层完全跳过。隐藏遮罩源仅执行生成节点，直接复用完整渲染所用的节点缓存键，不执行渐变、
描边或可见合成分支。

## 缓存与资源释放

每个分形层的规范图节点 ID 带稳定层 ID 前缀。节点键只写入该节点真实读取的生成参数、输入摘要、尺寸、质量、
精度和渲染器版本；变换、混合、组或 Master Effects 变化不会让无关生成节点失效。每 Document 继续独占
128 MiB / 256 项 LRU，超大值、取消值与异常值不入缓存，候选最多 3 路并发。

合成中间面只保存在当前调用栈局部变量中，最后消费者完成后即可由运行时回收；跨渲染只缓存可复用的生成、
标量、路径、着色或描边节点值，不缓存整棵可漂移的低层执行图。

## 结构合法与可渲染性

`IArtworkValidator` 检查 ID、树结构、Mask 引用、参数和预算；`IArtworkRenderabilityValidator` 在其后检查
未知能力。未知层或效果可以解码、选择、改名并再次保存原始 JSON，但预览、普通 PNG、ImageLab Artifact
和 Workflow Render 都在昂贵计算前失败，并列出类型、版本、层名/ID。
