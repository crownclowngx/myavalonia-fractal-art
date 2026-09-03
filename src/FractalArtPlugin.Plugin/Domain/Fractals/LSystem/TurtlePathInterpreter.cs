using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;

namespace FractalArtPlugin.Domain.Fractals.LSystem;

/// <summary>
/// 将已展开符号解释为矢量路径。解释器使用显式有界栈，完成后再把原始坐标等比归一化到逻辑画板；
/// 它不读取颜色、画布像素或 Avalonia 类型。
/// </summary>
internal sealed class TurtlePathInterpreter : ITurtlePathInterpreter
{
    private const double Margin = 0.06;

    public PathGeometry Interpret(
        LSystemDefinition definition,
        string symbols,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(symbols);
        var state = new TurtleState(0, 0, definition.InitialHeadingDegrees, definition.StepLength, 0);
        var stack = new Stack<TurtleState>();
        var raw = new List<RawSegment>();

        for (var index = 0; index < symbols.Length; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            switch (symbols[index])
            {
                case 'F':
                case 'G':
                    state = Move(state, true, raw);
                    break;
                case 'f':
                    state = Move(state, false, raw);
                    break;
                case '+':
                    state = state with { HeadingDegrees = state.HeadingDegrees + definition.TurnAngleDegrees };
                    break;
                case '-':
                    state = state with { HeadingDegrees = state.HeadingDegrees - definition.TurnAngleDegrees };
                    break;
                case '[':
                    if (stack.Count >= LSystemValidator.MaximumStackDepth)
                    {
                        throw new InvalidDataException("L-System Turtle 状态栈超过 1,024 层。");
                    }

                    stack.Push(state);
                    state = state with
                    {
                        Length = state.Length * definition.LengthDecay,
                        Level = state.Level + 1
                    };
                    break;
                case ']':
                    if (!stack.TryPop(out state))
                    {
                        throw new InvalidDataException($"L-System 在第 {index + 1} 个符号遇到没有对应 '[' 的 ']'.");
                    }

                    break;
            }
        }

        if (stack.Count != 0)
        {
            throw new InvalidDataException("L-System 展开结束时仍有未闭合的 '['。");
        }

        if (raw.Count == 0)
        {
            throw new InvalidDataException("L-System 没有产生可绘制线段。");
        }

        return Normalize(raw);
    }

    private static TurtleState Move(TurtleState state, bool draw, ICollection<RawSegment> segments)
    {
        var radians = state.HeadingDegrees * Math.PI / 180d;
        var endX = state.X + Math.Cos(radians) * state.Length;
        var endY = state.Y + Math.Sin(radians) * state.Length;
        if (draw)
        {
            if (segments.Count >= LSystemValidator.MaximumSegments)
            {
                throw new InvalidDataException("L-System 绘制线段超过 50,000 条。");
            }

            segments.Add(new RawSegment(state.X, state.Y, endX, endY, state.Level));
        }

        return state with { X = endX, Y = endY };
    }

    private static PathGeometry Normalize(IReadOnlyList<RawSegment> raw)
    {
        var minimumX = raw.Min(segment => Math.Min(segment.StartX, segment.EndX));
        var maximumX = raw.Max(segment => Math.Max(segment.StartX, segment.EndX));
        var minimumY = raw.Min(segment => Math.Min(segment.StartY, segment.EndY));
        var maximumY = raw.Max(segment => Math.Max(segment.StartY, segment.EndY));
        var width = maximumX - minimumX;
        var height = maximumY - minimumY;
        var span = Math.Max(width, height);
        if (!double.IsFinite(span) || span <= 0)
        {
            throw new InvalidDataException("L-System 路径边界退化或包含非有限坐标。");
        }

        var usable = 1d - Margin * 2;
        var offsetX = Margin + (span - width) / span * usable / 2d;
        var offsetY = Margin + (span - height) / span * usable / 2d;
        var segments = raw.Select(segment => new PathSegment(
            new PathPoint(offsetX + (segment.StartX - minimumX) / span * usable,
                offsetY + (segment.StartY - minimumY) / span * usable),
            new PathPoint(offsetX + (segment.EndX - minimumX) / span * usable,
                offsetY + (segment.EndY - minimumY) / span * usable),
            segment.Level)).ToArray();
        return new PathGeometry(segments, segments.Max(segment => segment.Level));
    }

    private readonly record struct TurtleState(
        double X,
        double Y,
        double HeadingDegrees,
        double Length,
        int Level);

    private readonly record struct RawSegment(
        double StartX,
        double StartY,
        double EndX,
        double EndY,
        int Level);
}
