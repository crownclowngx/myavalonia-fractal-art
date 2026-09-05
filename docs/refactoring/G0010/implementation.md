# G0010 实施说明

## 领域与应用

- 增加 `MathLensAnalysis`、`MathLensFrame`、归一化点/线段及 `IMathLensService`；
- 增加 EscapeTime、Path、Attractor 三个 Provider，并在组合根显式登记；
- 逃逸时间生产内核改为调用共享 `EscapeOrbitMath`，保留原运算顺序和标量写入；
- 图层栅格变换和点云密度取景分别抽出共享投影值对象；
- 吸引子轨道初值派生成为生成器与透镜共用入口；
- L-System 透镜逐轮调用生产展开器和 Turtle 解释器，不复制规则语言。

## Document 与界面

- `MathLensSession` 是每个 Document 独占的会话控制器，负责分析、播放、取消和迟到提交保护；
- 工具栏增加“数学透镜”，底部候选区在打开时切换为公式、说明、播放和滑块面板；
- 画布上层用两个 `StreamGeometry` 批量绘制线和点，避免为点云创建大量控件；
- 逃逸时间模式下点击图片重新选点；打开期间禁用平移/缩放，关闭后恢复；
- 参数或层变化只重新分析透镜，不把透镜状态写入作品或渲染图。

## 兼容性

作品格式保持 v8，Document Content schema、Plugin/Document ID、Workflow Recipe 与 File Artifact 均不变。
单一 Persistable Document、零 Tool、零全局命令、零菜单和零快捷键不变。IFS 尚未实现，因此本阶段以已有
奇异吸引子完成点云类透镜。

## 注释策略

详细中文注释集中说明共享数学语义、任意精度一致性、坐标投影、展示帧压缩、会话态不持久化、取消和
generation 提交边界；简单属性和显然的 UI 绑定不堆砌逐行注释。
