using MyAvaloniaManagement.PluginSdk.UI;
using FractalArtPlugin.Constants;
using FractalArtPlugin.Features.Artwork;
using FractalArtPlugin.Infrastructure.Workflow;

namespace FractalArtPlugin.Plugin;

public sealed class FractalArtPluginModule : IPluginModule
{
    public void Configure(IPluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        registration.Services.AddFractalArtPluginServices();
        registration.UseWorkflowActionGateway();
        registration.UseLifecycle<FractalArtifactCleanupLifecycle>();
        registration.AddWorkflowAction<RenderArtworkFileWorkflowActionHandler>(
            FractalWorkflowActions.CreateRenderDescriptor());
        registration.AddWorkflowAction<ReleaseArtifactWorkflowActionHandler>(
            FractalWorkflowActions.CreateReleaseDescriptor());
        registration.AddPersistableDocument<FractalArtworkDocument, FractalArtworkView>(
            new DocumentDescriptor(
                PluginIds.FractalArtworkDocument,
                "分形作品",
                "创建、保存并导出可重复生成的 Julia 分形作品",
                "生成艺术"));

        // G0001 明确采用零 Tool、零 Workbench Command、零默认快捷键策略；G0007 的两个
        // Provider Action 和 caller-bound Consumer Gateway 都通过专用 Workflow 注册面声明。
        // 参数编辑、撤销、重做、预览和导出都是当前作品内部的意图，不占用 Host 的全局入口。
    }
}
