using FluentAssertions;
using Mechware.Infrastructure.Seed;

namespace Mechware.UnitTests.Catalog;

/// <summary>
/// Semgrep (unsafe-path-combine, first blocking run on 2026-11-05) flagged the seeder writing artwork to
/// Path.Combine(mediaRoot, relativePath). The seeder writes files, so a path must never land outside the media root.
/// </summary>
public class SeedMediaPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "mw-seed-media-root");

    [Theory]
    [InlineData("products/plc/s7-1200.svg")]
    [InlineData("reviews/abc-1.svg")]
    [InlineData("a/./b/../c.svg")]
    public void APathInsideTheRoot_ResolvesUnderIt(string relative)
    {
        var full = CatalogSeeder.MediaSink.ResolveInside(Root, relative);

        full.Should().StartWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar);
    }

    [Theory]
    [InlineData("../outside.svg")]
    [InlineData("products/../../outside.svg")]
    [InlineData("/etc/passwd")]
    public void APathThatLeavesTheRoot_IsRefused(string relative)
    {
        var act = () => CatalogSeeder.MediaSink.ResolveInside(Root, relative);

        act.Should().Throw<InvalidOperationException>().WithMessage("*media root*");
    }

    [Fact]
    public void ASiblingDirectoryWithTheSamePrefix_IsRefused()
    {
        // "<root>-evil" starts with "<root>" as a string; the trailing separator in the check stops it.
        var act = () => CatalogSeeder.MediaSink.ResolveInside(Root, "../mw-seed-media-root-evil/x.svg");

        act.Should().Throw<InvalidOperationException>();
    }
}
