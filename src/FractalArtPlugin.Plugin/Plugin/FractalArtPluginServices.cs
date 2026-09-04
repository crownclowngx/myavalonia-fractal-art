using Microsoft.Extensions.DependencyInjection;
using FractalArtPlugin.Application;
using FractalArtPlugin.Infrastructure;
using FractalArtPlugin.Application.Workflow;
using FractalArtPlugin.Infrastructure.Workflow;

namespace FractalArtPlugin.Plugin;

public static class FractalArtPluginServices
{
    /// <summary>
    /// 登记无状态领域服务、Document Scope 内的创作图/缓存，以及基础设施适配器；Standalone 复用同一入口。
    /// 数学内核保持单例，所有持有可重算作品数据的服务保持 Scoped，确保两个作品不会共享缓存生命周期。
    /// </summary>
    public static IServiceCollection AddFractalArtPluginServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IArtworkGraphValidator, ArtworkGraphValidator>();
        services.AddSingleton<IArtworkValidator, ArtworkValidator>();
        services.AddSingleton<IArtworkSnapshotCodec, ArtworkSnapshotCodec>();
        services.AddSingleton<IJuliaFieldGenerator, JuliaFieldGenerator>();
        services.AddSingleton<IMandelbrotFieldGenerator, MandelbrotFieldGenerator>();
        services.AddSingleton<IGradientMapper, LinearGradientMapper>();
        services.AddSingleton<ILSystemValidator, LSystemValidator>();
        services.AddSingleton<ILSystemExpander, LSystemExpander>();
        services.AddSingleton<ITurtlePathInterpreter, TurtlePathInterpreter>();
        services.AddSingleton<IRecursiveTreePathGenerator, RecursiveTreePathGenerator>();
        services.AddSingleton<IPathStrokeRenderer, PathStrokeRenderer>();
        services.AddSingleton<IArtworkGraphNodeExecutor, JuliaFieldNodeExecutor>();
        services.AddSingleton<IArtworkGraphNodeExecutor, MandelbrotFieldNodeExecutor>();
        services.AddSingleton<IArtworkGraphNodeExecutor, RecursiveTreePathNodeExecutor>();
        services.AddSingleton<IArtworkGraphNodeExecutor, LSystemPathNodeExecutor>();
        services.AddSingleton<IArtworkGraphNodeExecutor, ScalarGradientNodeExecutor>();
        services.AddSingleton<IArtworkGraphNodeExecutor, PathStrokeNodeExecutor>();
        services.AddSingleton<IArtworkGraphNodeExecutor, EffectChainNodeExecutor>();
        services.AddSingleton<IArtworkGraphNodeExecutor, SingleLayerCompositionNodeExecutor>();
        services.AddSingleton<IArtworkGraphNodeExecutor, OutputNodeExecutor>();
        services.AddScoped<IArtworkGraphCache, ArtworkGraphCache>();
        services.AddScoped<IArtworkGraphExecutor, ArtworkGraphExecutor>();
        services.AddScoped<IArtworkRenderPipeline, ArtworkRenderPipeline>();
        services.AddSingleton<IArtisticParameterMapper, ArtisticParameterMapper>();
        services.AddSingleton<IVariationGenerator, VariationGenerator>();
        services.AddScoped<IVariationExplorer, VariationExplorer>();
        services.AddSingleton<IArtworkPresetCatalog, ArtworkPresetCatalog>();
        services.AddSingleton<IPngEncoder, PngEncoder>();
        services.AddSingleton<IAtomicFileWriter, AtomicFileWriter>();
        services.AddScoped<IArtworkExporter, ArtworkExporter>();
        services.AddSingleton<IPreviewImageFactory, AvaloniaPreviewImageFactory>();
        services.AddSingleton<IArtworkExportDialog, ArtworkExportDialog>();
        services.AddTransient<IArtworkHistory, ArtworkHistory>();
        services.AddSingleton<IWorkflowRecipeCodec, WorkflowRecipeCodec>();
        services.AddSingleton<IWorkflowRecipeFiles, WorkflowRecipeFiles>();
        // Artifact 创建依赖当前 Document/Action Scope 的最终质量渲染管线，不能提升为单例。
        // 过期清理由无 Scope 依赖的生命周期适配器执行，避免根作用域捕获 Scoped 服务。
        services.AddScoped<IFractalWorkflowArtifactStore, FractalWorkflowArtifactStore>();
        services.AddSingleton<IImageLabActionClient, ImageLabActionClient>();
        services.AddScoped<IImageLabArtEffectExportCoordinator, ImageLabArtEffectExportCoordinator>();
        services.AddSingleton<IWorkflowRecipeDialog, WorkflowRecipeDialog>();
        services.AddSingleton<IImageLabExportDialog, ImageLabExportDialog>();
        return services;
    }
}
