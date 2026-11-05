using ShopVerse.Domain.Identity;

namespace ShopVerse.IntegrationTests.Support;

public static class TestUsers
{
    /// <summary>Inserts a real, verified (active) account; company memberships and orders must reference one.</summary>
    public static async Task<Guid> CreateAsync(PostgresDatabaseFixture fixture) => await CreateAsync(fixture, verified: true);

    /// <summary>Inserts an account; <paramref name="verified"/> false leaves it waiting for e-mail verification.</summary>
    public static async Task<Guid> CreateAsync(PostgresDatabaseFixture fixture, bool verified)
    {
        await using var db = fixture.CreateContext();
        var user = User.Create($"member.{Guid.NewGuid():N}@example.test", null, null, "Test Member");
        if (verified) user.VerifyEmail(DateTime.UtcNow);
        db.Users.Add(user);
        // A real buyer has somewhere to deliver to; orders without an address are refused.
        db.Addresses.Add(DefaultAddress(user.Id));
        await db.SaveChangesAsync();
        return user.Id;
    }

    public static Address DefaultAddress(Guid userId) => new(
        Guid.NewGuid(), userId, "Factory", "Engr. Test Receiver", "+8801711999999", "Plot 1, Test Industrial Area", null, null,
        "Tongi", "Gazipur", "Dhaka", "1710", isDefaultShipping: true, isDefaultBilling: true);
}
