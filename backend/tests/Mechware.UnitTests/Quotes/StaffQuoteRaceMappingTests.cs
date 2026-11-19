using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Infrastructure.Quotes;

namespace Mechware.UnitTests.Quotes;

/// <summary>
/// D4.6: what the staff desk turns into a 409 instead of a 500. The one-live exclusion constraint is DEFERRED (D4.4), so its 23P01
/// arrives from the commit as a bare <see cref="PostgresException"/>; a unique index (one open version, version number) arrives
/// from SaveChanges wrapped in a <see cref="DbUpdateException"/>. Anything else is not a race and is left alone.
/// </summary>
public class StaffQuoteRaceMappingTests
{
    private static PostgresException Pg(string state) => new("violation", "ERROR", "ERROR", state);

    [Theory]
    [InlineData(PostgresErrorCodes.ExclusionViolation, false)]   // 23P01 at commit
    [InlineData(PostgresErrorCodes.ExclusionViolation, true)]
    [InlineData(PostgresErrorCodes.UniqueViolation, true)]       // 23505 from SaveChanges
    [InlineData(PostgresErrorCodes.UniqueViolation, false)]
    public void UniqueAndExclusionViolations_AreRaces(string state, bool wrapped)
    {
        Exception ex = wrapped ? new DbUpdateException("save failed", Pg(state)) : Pg(state);
        StaffQuoteService.IsRace(ex, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(PostgresErrorCodes.IntegrityConstraintViolation)]   // 23000: a guard trigger refused, a bug, not a race
    [InlineData(PostgresErrorCodes.CheckViolation)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    public void OtherDatabaseErrors_AreNotRaces(string state)
    {
        StaffQuoteService.IsRace(new DbUpdateException("save failed", Pg(state)), out _).Should().BeFalse();
        StaffQuoteService.IsRace(new InvalidOperationException("other"), out _).Should().BeFalse();
    }
}
