using MyAvaloniaManagement.PluginSdk;

namespace FractalArtPlugin.Standalone;

/// <summary>Standalone 没有 Host Action 目录；返回空目录让 ImageLab 导出明确降级为不可用。</summary>
internal sealed class UnavailableWorkflowActionGateway : IWorkflowActionGateway
{
    public IReadOnlyList<WorkflowActionDescriptor> GetAvailableActions() => [];

    public IWorkflowActionRun CreateRun() =>
        throw new InvalidOperationException("Standalone 不提供 Workflow Action Run。");
}
