using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// One consistent moment for several queries, without a single lock (MVCC, REPEATABLE READ): totals and the lines they add up
/// never differ. Shared by the credit reads (B3.8/B3.9) and the staff order reads (G5.5b); inside an open transaction it simply runs.
/// </summary>
internal static class ReadSnapshot
{
    public static async Task<T> RunAsync<T>(MechwareDbContext db, Func<Task<T>> read, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await read();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = await read();
        await tx.CommitAsync(ct);
        return result;
    }
}
