using MyAvaloniaManagement.PluginSdk;
namespace FractalArtPlugin.Constants;

public static class PluginIds
{
    public static readonly PluginId Plugin = new("myavalonia.plugin.fractal.art");

    /// <summary>
    /// 首个分形作品 Document 的持久身份。为兼容模板阶段已经产生的 Host 记录，
    /// 身份值保持不变；代码名称改为产品语义名称。
    /// </summary>
    public static readonly DocumentTypeId FractalArtworkDocument =
        new("myavalonia.plugin.fractal.art.document.main");
}
