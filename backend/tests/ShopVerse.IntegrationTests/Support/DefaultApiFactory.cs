namespace ShopVerse.IntegrationTests.Support;

/// <summary>Development host (demo accounts seeded) over its own database; xUnit needs one parameterless constructor.</summary>
public sealed class DefaultApiFactory : DbBackedFactory;
