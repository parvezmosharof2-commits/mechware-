using ShopVerse.Domain.Identity;

namespace ShopVerse.IntegrationTests.Support;

public static class TestUsers
{
    /// <summary>Inserts a real account; company memberships must reference one.</summary>
    public static async Task<Guid> CreateAsync(PostgresDatabaseFixture fixture)
    {
        await using var db = fixture.CreateContext();
        var user = User.Create($"member.{Guid.NewGuid():N}@example.test", null, null, "Test Member");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
