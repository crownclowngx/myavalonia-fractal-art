using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;

namespace FractalArtPlugin.Domain.Fractals.RecursiveTree;

/// <summary>
/// 确定性递归树生成器。它只把配方展开为归一化线段，不创建位图，也不知道预览或导出尺寸。
/// 随机源算法固定在类内；相同 Seed 与参数在不同进程中会产生逐段相同的路径。
/// </summary>
internal sealed class RecursiveTreePathGenerator : IRecursiveTreePathGenerator
{
    public PathGeometry Generate(
        RecursiveTreeDefinition definition,
        long seed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var capacity = CalculateSegmentCount(definition.Depth, definition.Branches);
        var segments = new List<PathSegment>(capacity);
        var stack = new Stack<BranchState>();
        var random = new StableRandom(unchecked((ulong)seed) ^ 0xA0761D6478BD642FUL);
        stack.Push(new BranchState(new PathPoint(0.5, 0.96), -90, definition.TrunkLength, 0));

        while (stack.Count > 0)
        {
            if ((segments.Count & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var branch = stack.Pop();
            var radians = branch.AngleDegrees * Math.PI / 180d;
            var end = new PathPoint(
                branch.Start.X + Math.Cos(radians) * branch.Length,
                branch.Start.Y + Math.Sin(radians) * branch.Length);
            segments.Add(new PathSegment(branch.Start, end, branch.Level));
            if (branch.Level + 1 >= definition.Depth)
            {
                continue;
            }

            // 逆序入栈保证最终几何始终按从左到右的分叉顺序输出，不能让 Stack 的 LIFO
            // 偶然改变路径序列；稳定序列是快照指纹和未来 SVG 输出可重现的基础。
            for (var index = definition.Branches - 1; index >= 0; index--)
            {
                var centered = definition.Branches == 1
                    ? 0d
                    : index / (double)(definition.Branches - 1) * 2d - 1d;
                var jitter = random.NextSigned() * definition.Randomness * definition.BranchAngleDegrees;
                stack.Push(new BranchState(
                    end,
                    branch.AngleDegrees + centered * definition.BranchAngleDegrees + jitter,
                    branch.Length * definition.LengthDecay,
                    branch.Level + 1));
            }
        }

        return new PathGeometry(segments, definition.Depth - 1);
    }

    private static int CalculateSegmentCount(int depth, int branches)
    {
        // 正常入口已经过 ArtworkValidator；这里仍保护独立的路径端口，避免未来 SVG 导出器
        // 直接调用时由恶意参数触发整数溢出或分配不可控的大集合。
        if (depth is < 1 or > 12 || branches is < 2 or > 3)
        {
            throw new InvalidDataException("递归树深度或分叉数超出路径生成预算。");
        }

        var total = 0;
        var level = 1;
        for (var index = 0; index < depth; index++)
        {
            total = checked(total + level);
            level = checked(level * branches);
        }

        if (total > 50_000)
        {
            throw new InvalidDataException("递归树线段总量不能超过 50,000。");
        }

        return total;
    }

    private readonly record struct BranchState(PathPoint Start, double AngleDegrees, double Length, int Level);

    private struct StableRandom(ulong state)
    {
        private ulong _state = state;

        public ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }

        public double NextSigned() => (NextUInt64() >> 11) * (1d / (1UL << 53)) * 2d - 1d;
    }
}
