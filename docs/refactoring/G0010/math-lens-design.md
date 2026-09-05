# G0010 数学透镜设计

## 数据链路

```text
当前 ArtworkDefinition + 当前选中层 + 可选画布选点
→ MathLensService
→ EscapeTime / Path / Attractor Provider
→ 不可变 MathLensAnalysis + MathLensFrame
→ Document Scope MathLensSession
→ 底部解释面板 + 画布 Overlay
```

`MathLensService` 只做策略分派。Provider 读取作品快照并调用已有数值/路径/点云服务；`MathLensSession`
只管理打开状态、帧游标、60ms 播放时钟、取消和 generation。Overlay 只把 0–1 坐标画到当前图片矩形，
不参与数学计算。

## 三类分析

### 逃逸时间

画布点击先排除 Uniform 信箱边，再经过当前层逆变换定位生产预览像素。Julia 与 Mandelbrot 的 double 和
定点内核共用 `EscapeOrbitMath` 单点核心：生产渲染不收集轨迹，透镜才记录 `z₀…zₙ`。深缩放采用权威
定点路径；轨迹最终写入同一平滑标量和线性渐变逻辑，因此逃逸次数、归一化值和基础颜色可以互相核对。

### 路径构造

递归树直接按生产 `PathGeometry.Level` 逐层显露。L-System 第 0 帧展示公理，后续每轮调用同一展开器，
再调用同一 Turtle 解释器取得几何；最终符号按最多 120 个连续批次播放。批次只控制可见线段数量，不改变
替换、符号顺序或绘制结果。

### 吸引子

透镜复用公式策略、轨道 0 的 Seed 初值和预热递推，并按当前预览质量生成真实点云。`PointCloudProjection`
同时服务密度累积和 Overlay：完整点云决定边界，Overlay 最多均匀展示 20,000 点。预热和形成过程合计最多
240 帧，展示采样不改变生产点云。

## 坐标与生命周期

- `LayerCoordinateProjection` 是图层正/逆变换的唯一公式来源；
- `UniformImageProjection` 负责控件尺寸、图片宽高比、信箱边和归一化选点；
- 逃逸与吸引子最多 240 帧，始终保留首尾；递归树按层级，L-System 按派生轮和动作批次；
- 切层、改参、撤销/重做、关闭和 Dispose 都取消旧分析；只有当前 generation 可以提交；
- 暂停保留当前帧，取消回到首帧，关闭清空整个会话并恢复原画布交互；
- 选中分组/不可用层时返回说明帧；隐藏分形层仍可解释，但明确标注已隐藏。

## SOLID 落点

| 原则 | 落点 |
| --- | --- |
| 单一职责 | 数学递推、Provider、会话、坐标和绘制分别实现 |
| 开闭原则 | 新家族通过新增 `IMathLensProvider` 扩展，Document 不增加公式分支 |
| 里氏替换 | Provider 统一遵守确定性、取消和不可变返回契约 |
| 接口隔离 | 分析服务与播放时钟只暴露所需操作 |
| 依赖倒置 | Document 依赖会话控制器；会话依赖分析端口和时钟端口 |
