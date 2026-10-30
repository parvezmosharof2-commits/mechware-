namespace ShopVerse.Infrastructure.Identity;

/// <summary>
/// Decides whether the in-memory identity store creates its demo accounts. Those accounts (admin, sellers, buyer)
/// use passwords and a TOTP secret that are published in this repository, so they must never exist anywhere a real
/// person could reach: only the <c>Development</c> environment seeds them. Production, Staging and anything else get
/// an empty store. The decision comes from the environment name alone; no setting can switch it on elsewhere.
/// </summary>
/// <param name="SeedDemoAccounts">True only for local development.</param>
public sealed record IdentitySeedPolicy(bool SeedDemoAccounts)
{
    /// <summary>No demo accounts: the safe default for every real environment.</summary>
    public static IdentitySeedPolicy None { get; } = new(false);

    public static IdentitySeedPolicy ForEnvironment(string? environmentName) =>
        string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) ? new(true) : None;
}
