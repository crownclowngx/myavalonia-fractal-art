using Xunit;

namespace FractalArtPlugin.Tests.Architecture;

public sealed class DomainDependencyTests
{
    [Fact]
    public void 领域与数值类型位于职责命名空间且公共边界不暴露Avalonia()
    {
        var types = new[]
        {
            typeof(ArtworkDefinition),
            typeof(HighPrecisionViewport),
            typeof(RenderContext),
            typeof(ArbitraryDecimal),
            typeof(PrecisionDescriptor)
        };

        Assert.Equal("FractalArtPlugin.Domain.Artwork", typeof(ArtworkDefinition).Namespace);
        Assert.Equal("FractalArtPlugin.Domain.Viewport", typeof(HighPrecisionViewport).Namespace);
        Assert.Equal("FractalArtPlugin.Domain.Rendering", typeof(RenderContext).Namespace);
        Assert.Equal("FractalArtPlugin.Numerics", typeof(ArbitraryDecimal).Namespace);
        foreach (var type in types)
        {
            var publicSurfaceTypes = type.GetProperties().Select(property => property.PropertyType)
                .Concat(type.GetMethods().Select(method => method.ReturnType));
            Assert.DoesNotContain(publicSurfaceTypes, exposed =>
                exposed.Namespace?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
        }
    }
}
