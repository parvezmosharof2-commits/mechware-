namespace Mechware.Application.Audit;

/// <param name="FirstBrokenPosition">Null when every sealed row is intact and in place; otherwise where the chain breaks.</param>
public sealed record AuditChainStatus(long Sealed, long? FirstBrokenPosition, string? HeadHash, long Unsealed)
{
    public bool Intact => FirstBrokenPosition is null;
}

/// <summary>The tamper-evident audit log (threat model v1, H3): sealing rows into a hash chain and checking it.</summary>
public interface IAuditChain
{
    /// <summary>Seals rows older than <paramref name="settle"/>, oldest first; returns how many were sealed.</summary>
    Task<int> SealAsync(int maxRows, TimeSpan settle, CancellationToken ct = default);

    /// <summary>Recomputes every sealed row: work grows with the log, so it runs daily and on request, not every minute.</summary>
    Task<AuditChainStatus> VerifyAsync(CancellationToken ct = default);

    /// <summary>The last sealed position and its hash: one indexed lookup, cheap enough to log as the anchor every run.</summary>
    Task<(long Position, string? Hash)> HeadAsync(CancellationToken ct = default);
}
