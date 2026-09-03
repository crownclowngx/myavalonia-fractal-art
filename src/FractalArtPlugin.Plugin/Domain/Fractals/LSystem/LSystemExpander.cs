using System.Text;
using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;

namespace FractalArtPlugin.Domain.Fractals.LSystem;

/// <summary>逐轮执行确定性并行替换；每次追加前检查硬预算并按有界间隔观察取消。</summary>
internal sealed class LSystemExpander(ILSystemValidator validator) : ILSystemExpander
{
    public string Expand(LSystemDefinition definition, CancellationToken cancellationToken)
    {
        validator.Validate(definition);
        cancellationToken.ThrowIfCancellationRequested();
        var rules = definition.Rules.ToDictionary(rule => rule.Symbol, rule => rule.Replacement);
        var current = definition.Axiom;
        for (var iteration = 0; iteration < definition.Iterations; iteration++)
        {
            var next = new StringBuilder(Math.Min(LSystemValidator.MaximumExpandedSymbols, current.Length * 2));
            for (var index = 0; index < current.Length; index++)
            {
                if ((index & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var replacement = rules.GetValueOrDefault(current[index]);
                next.Append(replacement ?? current[index].ToString());
                if (next.Length > LSystemValidator.MaximumExpandedSymbols)
                {
                    throw new InvalidDataException("L-System 展开超过 250,000 个符号。");
                }
            }

            current = next.ToString();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return current;
    }
}
