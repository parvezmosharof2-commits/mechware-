using FluentAssertions;
using ShopVerse.Infrastructure.Workers;
using Xunit;

namespace ShopVerse.UnitTests.Audit;

/// <summary>The anchor check the worker runs every minute: the chain only grows, and a sealed position never changes.</summary>
public class AuditChainHeadTests
{
    [Theory]
    [InlineData(10, "a", 10, "a", false)]   // nothing new
    [InlineData(10, "a", 12, "c", false)]   // grew
    [InlineData(10, "a", 7, "x", true)]     // rows removed from the end
    [InlineData(10, "a", 10, "b", true)]    // the last row rewritten and re-hashed
    [InlineData(0, null, 3, "c", false)]    // first rows sealed
    public void TheHeadMayOnlyMoveForward(long seenPosition, string? seenHash, long position, string? hash, bool alarm) =>
        AuditChainWorker.HeadWentBack((seenPosition, seenHash), (position, hash)).Should().Be(alarm);
}
