using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mechware.Application.Catalog;
using Mechware.Application.Identity;
using Mechware.Application.Pdf;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;
using S = Mechware.Domain.Quotes.QuoteVersionStatus;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// <see cref="IQuotePdfService"/> (ADR-0011 §10, D4.11; threat model QT1, QT9, QT17). The model comes from the frozen version only
/// (<see cref="QuotePdfComposer"/>). The first request renders the file (outside any row lock), stores it under a key built here from the quote
/// id, version and locale (never from the request) and records key and SHA-256 on the version, null to value once: the first writer wins and a
/// loser serves the winner's file. Every download recomputes the SHA-256 of the stored bytes; a mismatch is never sent, it is logged and audited.
/// </summary>
public sealed class QuotePdfService(MechwareDbContext db, IBuyerQuoteService buyers, IPdfRenderer renderer, IObjectStorage storage, QuotePdfOptions options, IQuoteTermsService terms,
    ICurrentUserService user, TimeProvider clock, ILogger<QuotePdfService> log) : IQuotePdfService
{
    private static KeyNotFoundException NotFound() => new(QuoteAcceptanceGate.NotFoundMessage);

    private static string Locale(string? locale)
    {
        var l = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim().ToLowerInvariant();
        return l is "en" or "bn" ? l : throw new ValidationException("locale", "The language must be en or bn.");
    }

    public async Task<QuotePdfFile> BuyerPdfAsync(Guid actorUserId, Guid companyId, Guid quoteId, int versionNo, string? locale, CancellationToken ct = default)
    {
        var lang = Locale(locale);
        // The same access rules as the detail read (membership of the URL's company, visibility, a version Sent or later): one 404 for anything else.
        var detail = await buyers.GetAsync(actorUserId, companyId, quoteId, ct);
        if (detail.Versions.All(v => v.VersionNo != versionNo)) throw NotFound();
        return await IssuedAsync(quoteId, versionNo, lang, ct) ?? throw NotFound();
    }

    public async Task<QuotePdfFile> StaffPdfAsync(Guid quoteId, int versionNo, string? locale, CancellationToken ct = default)
    {
        var lang = Locale(locale);
        var status = await db.QuoteVersions.AsNoTracking().Where(v => v.QuoteId == quoteId && v.VersionNo == versionNo).Select(v => (S?)v.Status).FirstOrDefaultAsync(ct)
                     ?? throw NotFound();
        if (status is S.Draft or S.PendingApproval) return await PreviewAsync(quoteId, versionNo, lang, ct);
        return await IssuedAsync(quoteId, versionNo, lang, ct) ?? throw NotFound();
    }

    public async Task<QuoteDocumentCheckDto> CheckAsync(string? sha256, CancellationToken ct = default)
    {
        var sha = sha256?.Trim().ToLowerInvariant();
        if (!QuoteVersion.IsHash(sha)) throw NotFound();
        var hit = await db.QuoteVersions.AsNoTracking()
            .Where(v => EF.Property<string?>(v, "PdfEnSha256") == sha || EF.Property<string?>(v, "PdfBnSha256") == sha)
            .Select(v => new { v.QuoteId, v.VersionNo, En = EF.Property<string?>(v, "PdfEnSha256") == sha }).FirstOrDefaultAsync(ct) ?? throw NotFound();
        var number = await db.Quotes.AsNoTracking().Where(q => q.Id == hit.QuoteId).Select(q => q.Number).SingleAsync(ct);
        return new QuoteDocumentCheckDto(hit.QuoteId, number, hit.VersionNo, hit.En ? "en" : "bn");
    }

    // ---- preview: rendered, never stored, never recorded ----------------------------------------------------------------

    private async Task<QuotePdfFile> PreviewAsync(Guid quoteId, int versionNo, string lang, CancellationToken ct)
    {
        var (v, number, company) = await LoadAsync(quoteId, versionNo, ct) ?? throw NotFound();
        // A draft has no frozen lines yet: it previews the defaults of this moment. A version waiting for approval prints its own.
        var live = v.TermsSnapshot is null ? await terms.CurrentAsync(ct) : null;
        var result = renderer.Render(QuotePdfComposer.Compose(QuotePdfComposer.Model(number, v, lang, draft: true, company, live), options, Logo()));
        return new QuotePdfFile(result.Bytes, FileName(number, versionNo, lang) + ".preview.pdf", result.Sha256Hex, Preview: true);
    }

    // ---- issued: lazy render, first writer wins, verified on every download ---------------------------------------------

    private async Task<QuotePdfFile?> IssuedAsync(Guid quoteId, int versionNo, string lang, CancellationToken ct)
    {
        if (await LoadAsync(quoteId, versionNo, ct) is not { } loaded || loaded.V.Status is S.Draft or S.PendingApproval) return null;
        var (v, number, _) = loaded;
        string name = lang == "en" ? "En" : "Bn";
        var (key, sha) = await RecordedAsync(v.Id, name, ct);

        if (key is not null && sha is not null && await ReadAsync(key, ct) is { } stored)
        {
            if (Hex(stored) != sha) throw await IntegrityFailedAsync(quoteId, number, versionNo, lang, sha, Hex(stored), "stored file", ct);
            return new QuotePdfFile(stored, FileName(number, versionNo, lang), sha);
        }

        // Nothing recorded yet, or the stored file is gone: render from the snapshot.
        var rendered = renderer.Render(QuotePdfComposer.Compose(QuotePdfComposer.Model(number, v, lang, draft: false, null), options, Logo()));
        if (key is not null && sha is not null)
        {
            // A missing file is rendered again; only the same hash may be put back (a changed renderer is an alert, ADR-0011 §10).
            if (rendered.Sha256Hex != sha) throw await IntegrityFailedAsync(quoteId, number, versionNo, lang, sha, rendered.Sha256Hex, "re-render", ct);
            await StoreAsync(key, rendered, ct);
            return new QuotePdfFile(rendered.Bytes, FileName(number, versionNo, lang), sha);
        }

        key = $"quotes/{quoteId:N}/v{versionNo}-{lang}.pdf";
        await StoreAsync(key, rendered, ct);
        var (hash, now, id) = (rendered.Sha256Hex, clock.GetUtcNow().UtcDateTime, v.Id);
        var won = lang == "en"
            ? await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE quote_versions SET pdf_en_key = {key}, pdf_en_sha256 = {hash}, updated_at = {now} WHERE id = {id} AND pdf_en_key IS NULL", ct)
            : await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE quote_versions SET pdf_bn_key = {key}, pdf_bn_sha256 = {hash}, updated_at = {now} WHERE id = {id} AND pdf_bn_key IS NULL", ct);
        if (won == 1)
        {
            db.AuditLogs.Add(Audit("quote.pdf_rendered", quoteId, JsonSerializer.Serialize(new { Number = number, VersionNo = versionNo, Locale = lang, Sha256 = rendered.Sha256Hex })));
            await db.SaveChangesAsync(ct);
            return new QuotePdfFile(rendered.Bytes, FileName(number, versionNo, lang), rendered.Sha256Hex);
        }

        // Another request recorded the file first: serve theirs, verified like any download.
        var (wKey, wSha) = await RecordedAsync(v.Id, name, ct);
        if (wKey is null || wSha is null || await ReadAsync(wKey, ct) is not { } winner) throw Unavailable();
        if (Hex(winner) != wSha) throw await IntegrityFailedAsync(quoteId, number, versionNo, lang, wSha, Hex(winner), "stored file", ct);
        return new QuotePdfFile(winner, FileName(number, versionNo, lang), wSha);
    }

    private async Task<(QuoteVersion V, string Number, string? Company)?> LoadAsync(Guid quoteId, int versionNo, CancellationToken ct)
    {
        var v = await db.QuoteVersions.AsNoTracking().Include(x => x.Lines).AsSplitQuery().FirstOrDefaultAsync(x => x.QuoteId == quoteId && x.VersionNo == versionNo, ct);
        if (v is null) return null;
        var q = await db.Quotes.AsNoTracking().Where(x => x.Id == quoteId).Select(x => new { x.Number, Company = db.Companies.Where(c => c.Id == x.CompanyId).Select(c => c.LegalName).FirstOrDefault() })
            .SingleAsync(ct);
        return (v, q.Number, q.Company);
    }

    private async Task<(string? Key, string? Sha)> RecordedAsync(Guid versionId, string name, CancellationToken ct)
    {
        var row = await db.QuoteVersions.AsNoTracking().Where(v => v.Id == versionId)
            .Select(v => new { Key = EF.Property<string?>(v, "Pdf" + name + "Key"), Sha = EF.Property<string?>(v, "Pdf" + name + "Sha256") }).SingleAsync(ct);
        return (row.Key, row.Sha);
    }

    private async Task<byte[]?> ReadAsync(string key, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(key, ct);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>Puts the file under its key; a file already there (a lost race, an orphan) stays when it is the same bytes, else it is replaced.</summary>
    private async Task StoreAsync(string key, PdfResult file, CancellationToken ct)
    {
        try
        {
            await storage.PutAsync(key, new MemoryStream(file.Bytes), ct);
        }
        catch (IOException)
        {
            if (await ReadAsync(key, ct) is { } existing && Hex(existing) == file.Sha256Hex) return;
            await storage.DeleteAsync(key, ct);
            await storage.PutAsync(key, new MemoryStream(file.Bytes), ct);
        }
    }

    private async Task<Exception> IntegrityFailedAsync(Guid quoteId, string number, int versionNo, string lang, string expected, string actual, string what, CancellationToken ct)
    {
        log.LogError("Quote PDF integrity check failed for {Number} v{Version} {Locale} ({What}): expected {Expected}, got {Actual}.", number, versionNo, lang, what, expected, actual);
        db.AuditLogs.Add(Audit("quote.pdf_integrity_failed", quoteId, JsonSerializer.Serialize(new { Number = number, VersionNo = versionNo, Locale = lang, Expected = expected, Actual = actual, What = what })));
        await db.SaveChangesAsync(ct);
        return Unavailable();
    }

    private static ServiceUnavailableException Unavailable() =>
        new("QUOTE_PDF_UNAVAILABLE", "This document cannot be downloaded right now. Please contact us and quote the number on your quotation.");

    private AuditLog Audit(string action, Guid quoteId, string newValues) =>
        new(Guid.NewGuid(), user.UserId, user.IpAddress, action, "quotes", quoteId.ToString(), null, newValues, user.CorrelationId, clock.GetUtcNow().UtcDateTime);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string FileName(string number, int versionNo, string lang) => $"{number}-r{versionNo}-{lang}.pdf";

    private byte[]? Logo() => string.IsNullOrWhiteSpace(options.LogoPath) || !File.Exists(options.LogoPath) ? null : File.ReadAllBytes(options.LogoPath);
}
