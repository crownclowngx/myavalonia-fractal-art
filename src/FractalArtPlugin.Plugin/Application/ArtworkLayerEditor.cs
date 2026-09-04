namespace FractalArtPlugin.Application;

public interface IArtworkLayerEditor
{
    ArtworkDefinition AddFractal(ArtworkDefinition artwork, FractalGeneratorKind kind);
    ArtworkDefinition AddGroup(ArtworkDefinition artwork);
    ArtworkDefinition Update(ArtworkDefinition artwork, ArtworkLayerDefinition replacement);
    ArtworkDefinition Delete(ArtworkDefinition artwork, string layerId);
    ArtworkDefinition Move(ArtworkDefinition artwork, string layerId, int direction);
    ArtworkDefinition MoveIntoGroup(ArtworkDefinition artwork, string layerId, string groupId);
    ArtworkDefinition MoveOutOfGroup(ArtworkDefinition artwork, string layerId);
}

/// <summary>
/// 图层树修改的唯一入口。服务只返回新的不可变作品，不触碰历史、Dirty、预览或 UI；
/// Document 因而可以把一次树操作作为一条原子历史记录提交。
/// </summary>
internal sealed class ArtworkLayerEditor(IArtworkValidator validator) : IArtworkLayerEditor
{
    public ArtworkDefinition AddFractal(ArtworkDefinition artwork, FractalGeneratorKind kind)
    {
        var id = NextId(artwork, "layer");
        var layer = ArtworkDefinition.CreateDefaultLayer(id, kind) with
        {
            Name = NextName(artwork, kind switch
            {
                FractalGeneratorKind.Julia => "Julia",
                FractalGeneratorKind.Mandelbrot => "Mandelbrot",
                FractalGeneratorKind.LSystem => "L-System",
                FractalGeneratorKind.RecursiveTree => "递归树",
                _ => "分形层"
            })
        };
        return Validate(artwork with
        {
            Layers = new ArtworkLayerDefinition[] { layer }.Concat(artwork.Layers).ToArray(),
            Presentation = artwork.Presentation with { SelectedLayerId = id }
        });
    }

    public ArtworkDefinition AddGroup(ArtworkDefinition artwork)
    {
        var id = NextId(artwork, "group");
        var group = new LayerGroupDefinition(
            id, NextName(artwork, "分组"), true, 1, LayerBlendMode.Normal,
            LayerTransformDefinition.Identity, null, []);
        return Validate(artwork with
        {
            Layers = new ArtworkLayerDefinition[] { group }.Concat(artwork.Layers).ToArray(),
            Presentation = artwork.Presentation with { SelectedLayerId = id }
        });
    }

    public ArtworkDefinition Update(ArtworkDefinition artwork, ArtworkLayerDefinition replacement)
    {
        if (ArtworkLayerTree.Find(artwork.Layers, replacement.Id) is null)
        {
            throw new InvalidOperationException($"图层 {replacement.Id} 不存在。");
        }

        var layers = artwork.Layers.Select(layer => layer switch
        {
            FractalLayerDefinition fractal when fractal.Id == replacement.Id => replacement,
            LayerGroupDefinition group when group.Id == replacement.Id => replacement,
            LayerGroupDefinition group when group.Children.Any(child => child.Id == replacement.Id) &&
                                            replacement is not LayerGroupDefinition => group with
                                            {
                                                Children = Array.AsReadOnly(group.Children.Select(current =>
                                                    current.Id == replacement.Id ? replacement : current).ToArray())
                                            },
            _ => layer
        }).ToArray();
        return Validate(artwork with { Layers = layers });
    }

    public ArtworkDefinition Delete(ArtworkDefinition artwork, string layerId)
    {
        var target = ArtworkLayerTree.Find(artwork.Layers, layerId) ??
            throw new InvalidOperationException($"图层 {layerId} 不存在。");
        if (target is LayerGroupDefinition { Children.Count: > 0 })
        {
            throw new InvalidOperationException($"分组 {target.Name} 仍包含子图层，请先移出或删除子图层。");
        }

        if (target is FractalLayerDefinition && ArtworkLayerTree.EnumerateFractals(artwork.Layers).Count() == 1)
        {
            throw new InvalidOperationException("作品必须保留至少一个分形层。");
        }

        var references = artwork.Layers
            .Concat(artwork.Layers.OfType<LayerGroupDefinition>().SelectMany(group => group.Children))
            .Where(layer => layer.Mask?.SourceLayerId == layerId)
            .Select(layer => layer.Name)
            .ToArray();
        if (references.Length > 0)
        {
            throw new InvalidOperationException(
                $"图层 {target.Name} 正被以下遮罩引用：{string.Join("、", references)}。请先解除引用。");
        }

        var layers = artwork.Layers.Where(layer => layer.Id != layerId).Select(layer => layer switch
        {
            LayerGroupDefinition group => group with
            {
                Children = Array.AsReadOnly(group.Children.Where(child => child.Id != layerId).ToArray())
            },
            _ => layer
        }).ToArray();
        var nextSelection = layers.SelectMany(layer => layer is LayerGroupDefinition group
                ? new[] { layer.Id }.Concat(group.Children.Select(child => child.Id))
                : [layer.Id])
            .First();
        return Validate(artwork with
        {
            Layers = layers,
            Presentation = artwork.Presentation with { SelectedLayerId = nextSelection }
        });
    }

    public ArtworkDefinition Move(ArtworkDefinition artwork, string layerId, int direction)
    {
        if (direction is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "移动方向只能是 -1 或 1。");
        }

        var root = artwork.Layers.ToArray();
        var rootIndex = Array.FindIndex(root, layer => layer.Id == layerId);
        if (rootIndex >= 0)
        {
            Swap(root, rootIndex, direction);
            return Validate(artwork with { Layers = root });
        }

        for (var index = 0; index < root.Length; index++)
        {
            if (root[index] is not LayerGroupDefinition group)
            {
                continue;
            }

            var children = group.Children.ToArray();
            var childIndex = Array.FindIndex(children, child => child.Id == layerId);
            if (childIndex < 0)
            {
                continue;
            }

            Swap(children, childIndex, direction);
            root[index] = group with { Children = Array.AsReadOnly(children) };
            return Validate(artwork with { Layers = root });
        }

        throw new InvalidOperationException($"图层 {layerId} 不存在。");
    }

    public ArtworkDefinition MoveIntoGroup(ArtworkDefinition artwork, string layerId, string groupId)
    {
        var layer = ArtworkLayerTree.FindFractal(artwork.Layers, layerId) ??
            throw new InvalidOperationException("只有分形层可以移入分组。");
        var group = artwork.Layers.OfType<LayerGroupDefinition>().FirstOrDefault(candidate => candidate.Id == groupId) ??
            throw new InvalidOperationException($"分组 {groupId} 不存在。");
        if (group.Children.Any(child => child.Id == layerId))
        {
            return artwork;
        }

        var removed = RemoveFractal(artwork.Layers, layerId);
        var layers = removed.Select(item => item is LayerGroupDefinition candidate && candidate.Id == groupId
            ? candidate with
            {
                Children = Array.AsReadOnly(new ArtworkLayerDefinition[] { layer }
                    .Concat(candidate.Children).ToArray())
            }
            : item).ToArray();
        return Validate(artwork with { Layers = layers });
    }

    public ArtworkDefinition MoveOutOfGroup(ArtworkDefinition artwork, string layerId)
    {
        var parent = artwork.Layers.OfType<LayerGroupDefinition>()
            .FirstOrDefault(group => group.Children.Any(child => child.Id == layerId)) ??
            throw new InvalidOperationException("当前图层不在分组中。");
        var layer = parent.Children.First(child => child.Id == layerId) as FractalLayerDefinition ??
            throw new InvalidOperationException("只有分形层可以移出分组。");
        var removed = RemoveFractal(artwork.Layers, layerId).ToList();
        var parentIndex = removed.FindIndex(item => item.Id == parent.Id);
        removed.Insert(parentIndex, layer);
        return Validate(artwork with { Layers = removed });
    }

    private ArtworkDefinition Validate(ArtworkDefinition artwork)
    {
        validator.Validate(artwork);
        return artwork;
    }

    private static IReadOnlyList<ArtworkLayerDefinition> RemoveFractal(
        IReadOnlyList<ArtworkLayerDefinition> layers,
        string layerId) => layers.Where(layer => layer.Id != layerId).Select(layer => layer switch
    {
        LayerGroupDefinition group => group with
        {
            Children = Array.AsReadOnly(group.Children.Where(child => child.Id != layerId).ToArray())
        },
        _ => layer
    }).ToArray();

    private static void Swap<T>(T[] items, int index, int direction)
    {
        var target = index + direction;
        if (target < 0 || target >= items.Length)
        {
            return;
        }

        (items[index], items[target]) = (items[target], items[index]);
    }

    private static string NextId(ArtworkDefinition artwork, string prefix)
    {
        var used = artwork.Layers.Concat(artwork.Layers.OfType<LayerGroupDefinition>().SelectMany(group => group.Children))
            .Select(layer => layer.Id).ToHashSet(StringComparer.Ordinal);
        for (var number = 1; ; number++)
        {
            var candidate = $"{prefix}-{number}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string NextName(ArtworkDefinition artwork, string prefix)
    {
        var names = artwork.Layers.Concat(artwork.Layers.OfType<LayerGroupDefinition>().SelectMany(group => group.Children))
            .Select(layer => layer.Name).ToHashSet(StringComparer.Ordinal);
        for (var number = 1; ; number++)
        {
            var candidate = $"{prefix} {number}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
