using Microsoft.Extensions.DependencyInjection;
using FractalArtPlugin.Application;
using FractalArtPlugin.Infrastructure;

namespace FractalArtPlugin.Plugin;

public static class FractalArtPluginServices
{
    /// <summary>登记无状态领域服务、应用用例及基础设施适配器；Standalone 复用同一个组合入口。</summary>
    public static IServiceCollection AddFractalArtPluginServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IArtworkValidator, ArtworkValidator>();
        services.AddSingleton<IArtworkSnapshotCodec, ArtworkSnapshotCodec>();
        services.AddSingleton<IJuliaFieldGenerator, JuliaFieldGenerator>();
        services.AddSingleton<IGradientMapper, LinearGradientMapper>();
        services.AddSingleton<IArtworkRenderPipeline, ArtworkRenderPipeline>();
        services.AddSingleton<IArtisticParameterMapper, ArtisticParameterMapper>();
        services.AddSingleton<IVariationGenerator, VariationGenerator>();
        services.AddSingleton<IVariationExplorer, VariationExplorer>();
        services.AddSingleton<IArtworkPresetCatalog, ArtworkPresetCatalog>();
        services.AddSingleton<IPngEncoder, PngEncoder>();
        services.AddSingleton<IAtomicFileWriter, AtomicFileWriter>();
        services.AddSingleton<IArtworkExporter, ArtworkExporter>();
        services.AddSingleton<IPreviewImageFactory, AvaloniaPreviewImageFactory>();
        services.AddSingleton<IArtworkExportDialog, ArtworkExportDialog>();
        services.AddTransient<IArtworkHistory, ArtworkHistory>();
        return services;
    }
}
