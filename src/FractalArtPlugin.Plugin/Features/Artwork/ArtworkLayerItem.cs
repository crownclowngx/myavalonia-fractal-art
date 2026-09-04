namespace FractalArtPlugin.Features.Artwork;

/// <summary>图层面板的只读投影；每次作品快照切换后整体重建，不让 UI 项成为第二份可编辑状态。</summary>
public sealed record ArtworkLayerItem(
    string Id,
    string Name,
    bool IsVisible,
    bool IsGroup,
    bool IsChild,
    string KindName,
    string BlendModeName)
{
    public string DisplayName => IsChild ? $"↳ {Name}" : Name;
    public string VisibilityMark => IsVisible ? "●" : "○";
}

public sealed record MaskSourceOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record LayerGroupOption(string Id, string Name)
{
    public override string ToString() => Name;
}
