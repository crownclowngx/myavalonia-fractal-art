namespace FractalArtPlugin.Features.Artwork;

/// <summary>
/// Document 当前可呈现状态。它只描述本次打开会话，不属于作品配方；保存、撤销和导出均不读取该枚举。
/// </summary>
public enum ArtworkWorkspacePhase
{
    Loading,
    Ready,
    Blocked,
    Failed
}
