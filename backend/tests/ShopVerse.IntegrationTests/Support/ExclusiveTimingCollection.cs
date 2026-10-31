namespace ShopVerse.IntegrationTests.Support;

/// <summary>Tests in this collection run alone: xUnit runs every other collection in parallel but waits for this one on its own.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExclusiveTimingCollection
{
    public const string Name = "exclusive-timing";
}
