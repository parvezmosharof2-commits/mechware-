using Microsoft.EntityFrameworkCore;
using Mechware.Application.Audit;

namespace Mechware.Infrastructure.Persistence;

/// <summary>Calls the database functions installed by <see cref="AuditChainSql"/>.</summary>
public sealed class AuditChain(MechwareDbContext db) : IAuditChain
{
    public async Task<int> SealAsync(int maxRows, TimeSpan settle, CancellationToken ct = default) =>
        (await db.Database.SqlQuery<int>($"SELECT seal_audit_logs({maxRows}, {settle}) AS \"Value\"").ToListAsync(ct)).Single();

    public async Task<AuditChainStatus> VerifyAsync(CancellationToken ct = default)
    {
        var row = (await db.Database.SqlQuery<VerifyRow>(
                $"SELECT sealed AS \"Sealed\", first_broken_position AS \"FirstBrokenPosition\", head_hash AS \"HeadHash\" FROM verify_audit_chain()")
            .ToListAsync(ct)).Single();
        long unsealed = (await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM audit_logs WHERE chain_position IS NULL").ToListAsync(ct)).Single();
        return new AuditChainStatus(row.Sealed, row.FirstBrokenPosition, row.HeadHash, unsealed);
    }

    public async Task<(long Position, string? Hash)> HeadAsync(CancellationToken ct = default)
    {
        var head = (await db.Database.SqlQuery<HeadRow>(
                $"SELECT chain_position AS \"Position\", hash AS \"Hash\" FROM audit_logs WHERE chain_position IS NOT NULL ORDER BY chain_position DESC LIMIT 1")
            .ToListAsync(ct)).SingleOrDefault();
        return head is null ? (0, null) : (head.Position, head.Hash);
    }

    private sealed record HeadRow(long Position, string? Hash);

    private sealed record VerifyRow(long Sealed, long? FirstBrokenPosition, string? HeadHash);
}
