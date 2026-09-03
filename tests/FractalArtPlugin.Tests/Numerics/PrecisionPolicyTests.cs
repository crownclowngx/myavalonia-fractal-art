using Xunit;

namespace FractalArtPlugin.Tests.Numerics;

public sealed class PrecisionPolicyTests
{
    [Fact]
    public void 普通强制高精度只使用所需位数而非全部配置上限()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            ForceHighPrecision = true,
            PrecisionDigits = 256,
            MaxIterations = 320
        };

        var descriptor = new PrecisionPolicy().Describe(definition, 800);

        Assert.Equal(256, descriptor.ConfiguredDigits);
        Assert.Equal(32, descriptor.EffectiveDigits);
        Assert.True(descriptor.RequiredDigits <= descriptor.ConfiguredDigits);
    }

    [Fact]
    public void 深度尺度超过配置上限时明确失败而非静默截断()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            Scale = "1e-90",
            PrecisionDigits = 96,
            ForceHighPrecision = true,
            MaxIterations = 4096
        };

        var exception = Assert.Throws<InsufficientPrecisionException>(() =>
            new PrecisionPolicy().Describe(definition, 1024));

        Assert.Equal(96, exception.ConfiguredDigits);
        Assert.True(exception.RequiredDigits > exception.ConfiguredDigits);
        Assert.Contains("配置精度不足", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", 32)]
    [InlineData("1e-40", 56)]
    [InlineData("1e-220", 236)]
    public void 精度推导覆盖普通尺度与深缩放(string scale, int expectedDigits)
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            Scale = scale,
            PrecisionDigits = 256,
            MaxIterations = 96,
            ForceHighPrecision = true
        };

        var descriptor = new PrecisionPolicy().Describe(definition, 256);

        Assert.Equal(expectedDigits, descriptor.EffectiveDigits);
    }
}
