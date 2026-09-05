# G0013 编排与恢复设计

## 角色与 SOLID

| 职责 | 实现边界 |
| --- | --- |
| 渲染和临时产物所有权 | Fractal 已有 Render、Batch、Release；本阶段不修改生产渲染行为 |
| 艺术后处理 | ImageLab 两个文件 Action 共用 Prepare 用例、效果流水线与独占 PNG 提交端口 |
| 定义生成和兼容检查 | Studio ArtWorkflowDefinitionBuilder；使用真实目录 revision 和现有验证器 |
| 会话恢复 | Studio ArtWorkflowRecoverySession；仅接受冻结定义对应的白名单终态 |
| 执行与授权 | 原 Runner 顺序执行，Host Gateway 继续负责身份、风险、确认、Scope 和超时 |
| 界面与选择文件 | ArtWorkflowPanel 适配表单；ArtWorkflowFilePicker 在 View 附着期间持有 StorageProvider |

Handler 的变化停留在协议适配边界；效果算法没有新增 UI、JSON、SDK、DI 或文件系统依赖。
新观察接口只同步投影一次调用的开始和终态，不构建事件总线，也不把结果正文写入通用运行历史。
生产程序集仍不存在跨插件引用；测试工程可以同时引用三个真实 Module。

## 完整定义的生成

在 Studio 展开“Fractal → ImageLab”，选择已保存配方和已存在输出目录，设置效果，然后生成并验证。
生成仅准备定义；点击主工具栏“执行”才通过 Host 调用 Provider。定义可直接导出到原 JSON 区。

单张使用旧 Render → 旧 apply-art-effects-file → Release。
多张使用 export-artwork-batch → ForEach apply-art-effects-file-to-directory → ForEach Release。
批量来源为 `${render-batch.result.results}`，图像字段为 `${item.artifact}`，文件名字段为 `${item.itemId}`。
不用任意数组下标，也不扩展字符串插值。文件名由批次 GUID 和序号组成，重复配方仍具有独立身份。

根 schemaVersion 为 2，contractRevision 与 presentationRevision 来自同一次真实目录捕获。
静态检查还验证风险、确认频率、敏感字段和 File Artifact 输出身份/版本；
ImageLab 最终输出没有下游读取者，因此必须额外验证其输出协议，不能只依靠步骤引用检查。
缺失 Action、旧 ImageLab 缺少目录动作或不兼容 Schema 均阻止生成。运行前契约漂移仍由原 Runner 拒绝；
展示修订漂移只提示。本文不提供带占位 revision 的伪导入文件。

## ImageLab 新动作

稳定 ID：`myavalonia.plugin.image.lab.workflow.apply-art-effects-file-to-directory`。

| 字段 | 语义 |
| --- | --- |
| source | 原有 File Artifact v1；只读取 transient/run PNG |
| blur / bloom / grain | 沿用旧动作必填参数与取值范围 |
| outputDirectory | 已存在的绝对目录，拒绝 Workflow 临时根及重解析点 |
| fileStem | 1–64 个小写字母、数字或连字符，应用层追加 .png |
| 输出 | 原有 artifact + image；lifetime 固定 persistent |

风险 ReadsLocalFiles、WritesLocalFiles、LongRunning，OncePerRun 确认。
旧 apply-art-effects-file 的 Schema 与固定 G0007 夹具逐字段对照；不修改 Fractal 一键导出调用。
原始文件/配方不覆盖、不删除，输出冲突失败。单图 4096 单边、256 MiB 编码预算继续生效，
在解码前读取 PNG/IHDR 尺寸，解码后再验证尺寸。

处理顺序：严格解析 → 路径与参数预检 → 有界读取和所有权/摘要验证 → Decode → Blur/Bloom/Grain →
Encode → 构造响应 → committing 通知 → 最后取消检查 → 无覆盖原子提交 → 直接返回。
提交后不发送可能失败的进度通知或重新序列化；失败时清理尚未提交的 partial。
100% 进度不是成功凭证，界面以 Host 终态为准。

## 恢复与所有权

台账属于当前 Document Scope，不落盘，只保存预期项目的文件字段、状态和不确定输出位置。
步骤内容、顺序、引用或契约 revision 与冻结定义不符时作为普通工作流运行，不更新旧台账。
同一已消费的示例定义不能重复运行；必须重新准备续跑、重新生成或新批次。

| 情况 | 行为 |
| --- | --- |
| 已确认成功 | 保留 persistent PNG，续跑跳过此项 |
| 后处理失败/取消/结果无效 | 标为需核对，记录预期输出位置，不把文件存在当作成功 |
| 可复用源 | 校验 marker、24 小时期限、路径、长度与摘要，准备只含未完成项的定义 |
| 续跑 | 使用新输出名称；至多 16 次后处理 + 16 次释放，满足 32 步预算 |
| 源失效或被清理 | 明确要求重新生成；读取原路径下的当前配方，已成功项保留 |
| 重新生成 | 新操作身份、新输出名称；旧源仍登记在台账中供显式清理 |
| 释放返回 released=false | 保持延迟清理状态，不能显示已释放 |
| 显式清理 | 逐项创建受 Host 治理的 Release Run；一项失败不阻断其他项，取消结束整轮 |
| 关闭/放弃恢复 | 清空台账；persistent 输出保留，合法临时 marker 交由既有 TTL |

为避免无限累积旧操作，重新生成限制会话最多保留 256 个源身份；达到限制后需清理并结束该批次。
跨插件所有权仍由生产者 marker 和受治理调用共同约束，不是 Host 原生 Artifact 句柄。

原子文件提交与 Host 最终成功之间没有 SDK 确认回调。进程崩溃或 Host 拒绝结果后，
可能已经存在一个未确认的 persistent PNG；再次执行使用新名称，用户自行核对旧文件。
同权限进程并发替换路径的系统级竞态不宣称已被完全消除。

## 产品边界

这里处理导出文件，不进入 Fractal ArtworkSnapshot v8、Dirty、Undo/Redo，也不改变实时 Master Effects。
Standalone 仍采用既有 Fake 目录，所以新示例会正确提示缺少真实 Provider；
完整跨插件链由独立本地集成工程验证。真实 Host 目录刷新、授权 UI、ALC 卸载、关闭排空和 ZIP 部署仍待发布阶段验收。
