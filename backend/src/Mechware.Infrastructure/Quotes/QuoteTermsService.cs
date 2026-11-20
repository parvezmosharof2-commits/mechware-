using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// The default terms of the quote PDF (D4.20). One row, seeded by the migration from <see cref="QuotePdfOptions"/>. A save compares the row's
/// <c>xmin</c> with the editor's <c>rowVersion</c> (and EF's own token catches a race): 409 <c>QUOTE_TERMS_CHANGED</c>. The send of a quote
/// copies the lines into the version (<see cref="SnapshotAsync"/>), so a later change never alters a quote that was sent.
/// </summary>
public sealed class QuoteTermsService(MechwareDbContext db, ICurrentUserService user, TimeProvider clock) : IQuoteTermsService
{
    private static ConflictException Stale() => new("QUOTE_TERMS_CHANGED", "Someone changed the default terms since you loaded them. Reload and edit again.");

    public async Task<QuoteTermsDto> GetAsync(CancellationToken ct = default)
    {
        var row = await RowAsync(db, ct);
        return await ToDtoAsync(row, db.Entry(row).Property<uint>("xmin").CurrentValue, ct);
    }

    public async Task<QuoteTermsDto> UpdateAsync(UpdateQuoteTermsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = user.UserId ?? throw new UnauthorizedAccessException("Sign in to change the quote terms.");
        if (request.RowVersion is not { } rowVersion) throw new ValidationException("rowVersion", "Send the rowVersion of the terms you edited.");
        var en = QuoteTermsSettings.Normalize(request.TermsEn, "termsEn");
        var bn = QuoteTermsSettings.Normalize(request.TermsBn, "termsBn");

        var row = await RowAsync(db, ct);
        if (db.Entry(row).Property<uint>("xmin").CurrentValue != rowVersion) throw Stale();
        if (en.SequenceEqual(row.TermsEn) && bn.SequenceEqual(row.TermsBn)) return await ToDtoAsync(row, rowVersion, ct);

        var before = JsonSerializer.Serialize(new { row.TermsEn, row.TermsBn });
        var now = clock.GetUtcNow().UtcDateTime;
        row.Change(en, bn, actor, now);
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, user.IpAddress, "quote.terms_changed", "quote_terms_settings", row.Id.ToString(), before,
            JsonSerializer.Serialize(new { row.TermsEn, row.TermsBn }), user.CorrelationId, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Stale();
        }

        return await ToDtoAsync(row, db.Entry(row).Property<uint>("xmin").CurrentValue, ct);
    }

    public Task<QuoteTermsSnapshot> CurrentAsync(CancellationToken ct = default) => SnapshotAsync(db, ct);

    /// <summary>The lines now; the caller (a send) freezes them into the version inside its own transaction.</summary>
    internal static async Task<QuoteTermsSnapshot> SnapshotAsync(MechwareDbContext db, CancellationToken ct) => (await RowAsync(db, ct)).ToSnapshot();

    /// <summary>The one row; created from the pad defaults of <see cref="QuotePdfOptions"/> on first use (insert-if-absent, so two first readers write one row).</summary>
    private static async Task<QuoteTermsSettings> RowAsync(MechwareDbContext db, CancellationToken ct)
    {
        // Tracked: the shadow xmin (the row version) is only kept for a tracked row.
        var rows = db.QuoteTermsSettings;
        if (await rows.SingleOrDefaultAsync(ct) is { } row) return row;
        var (id, en, bn, now) = (QuoteTermsSettings.SingletonId, new QuotePdfOptions().TermsEn.ToArray(), new QuotePdfOptions().TermsBn.ToArray(), DateTime.UtcNow);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO quote_terms_settings (id, terms_en, terms_bn, created_at) VALUES ({id}, {en}, {bn}, {now}) ON CONFLICT (id) DO NOTHING", ct);
        return await rows.SingleAsync(ct);
    }

    private async Task<QuoteTermsDto> ToDtoAsync(QuoteTermsSettings row, uint rowVersion, CancellationToken ct)
    {
        var by = row.UpdatedBy is { } id ? await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync(ct) : null;
        return new QuoteTermsDto(row.TermsEn, row.TermsBn, rowVersion, row.UpdatedAtUtc, row.UpdatedBy, by);
    }
}
