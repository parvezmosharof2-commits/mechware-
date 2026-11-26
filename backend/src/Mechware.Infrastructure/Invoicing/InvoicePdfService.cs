using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mechware.Application.Catalog;
using Mechware.Application.Common;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Application.Orders;
using Mechware.Application.Pdf;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Invoicing.Pdf;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Invoicing;

/// <summary>
/// <see cref="IInvoicePdfService"/> (ADR-0012 §12, E6.4b; the D4.11 pattern of <c>QuotePdfService</c>). The file is a rendering of the stored snapshot only
/// (<see cref="InvoicePdfComposer"/>). It is rendered outside any row lock, stored under a key built here from the invoice id and locale (never from the
/// request) and recorded on the invoice null to value once: the first writer wins and a loser serves the winner's file, verified. Every download
/// recomputes the SHA-256 of the stored bytes; a mismatch is never sent, it is logged and audited.
/// </summary>
public sealed class InvoicePdfService(MechwareDbContext db, IOrderService orders, IPdfRenderer renderer, IObjectStorage storage, ICurrentUserService user,
    TimeProvider clock, ILogger<InvoicePdfService> log) : IInvoicePdfService
{
    /// <summary>One message for everything the caller may not see: a foreign order, an invoice of another order, an unknown id.</summary>
    private static KeyNotFoundException NotFound() => new("Order not found.");

    private static bool IsHash(string? value) => value is { Length: 64 } && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static KeyNotFoundException DocumentNotFound() => new("Document not found.");

    private static string Locale(string? locale)
    {
        var l = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim().ToLowerInvariant();
        return l is "en" or "bn" ? l : throw new ValidationException("locale", "The language must be en or bn.");
    }

    public async Task<InvoicePdfFile> BuyerPdfAsync(Guid actorUserId, Guid orderId, Guid invoiceId, string? locale, CancellationToken ct = default)
    {
        var lang = Locale(locale);
        // The very rule of the buyer's order detail (the placer of the order); anything else is its 404.
        await orders.GetAsync(actorUserId, orderId, ct);
        var invoice = await LoadAsync(invoiceId, ct);
        if (invoice is null || invoice.OrderId != orderId) throw NotFound();
        return await ServeAsync(invoice, lang, ct);
    }

    public async Task<InvoicePdfFile> StaffPdfAsync(Guid invoiceId, string? locale, CancellationToken ct = default)
    {
        var lang = Locale(locale);
        return await ServeAsync(await LoadAsync(invoiceId, ct) ?? throw NotFound(), lang, ct);
    }

    public async Task<InvoiceDocumentCheckDto> CheckAsync(string? sha256, CancellationToken ct = default)
    {
        var sha = sha256?.Trim().ToLowerInvariant();
        if (!IsHash(sha)) throw DocumentNotFound();
        var hit = await db.Invoices.AsNoTracking()
            .Where(i => EF.Property<string?>(i, "PdfEnSha256") == sha || EF.Property<string?>(i, "PdfBnSha256") == sha)
            .Select(i => new { i.Id, i.Number, i.Kind, En = EF.Property<string?>(i, "PdfEnSha256") == sha }).FirstOrDefaultAsync(ct) ?? throw DocumentNotFound();
        return new InvoiceDocumentCheckDto(hit.Id, hit.Number, hit.Kind == InvoiceKind.CreditNote ? "credit_note" : "tax_invoice", hit.En ? "en" : "bn");
    }

    public async Task RenderBothAsync(Guid invoiceId, CancellationToken ct = default)
    {
        if (await LoadAsync(invoiceId, ct) is not { } invoice) return;
        foreach (var lang in new[] { "en", "bn" })
        {
            try
            {
                await ServeAsync(invoice, lang, ct);
            }
            catch (ServiceUnavailableException)
            {
                // Already logged and audited; a retry of the outbox message would only repeat the alert.
            }
        }
    }

    private async Task<InvoicePdfFile> ServeAsync(Invoice invoice, string lang, CancellationToken ct)
    {
        string name = lang == "en" ? "En" : "Bn";
        var (key, sha) = await RecordedAsync(invoice.Id, name, ct);

        if (key is not null && sha is not null && await ReadAsync(key, ct) is { } stored)
        {
            if (Hex(stored) != sha) throw await IntegrityFailedAsync(invoice, lang, sha, Hex(stored), "stored file", ct);
            return new InvoicePdfFile(stored, FileName(invoice, lang), sha);
        }

        // Nothing recorded yet, or the stored file is gone: render from the snapshot (outside any row lock).
        var rendered = InvoicePdfComposer.Render(renderer, invoice, lang, await OriginalNumberAsync(invoice, ct));
        if (key is not null && sha is not null)
        {
            // A missing file is rendered again; only the same hash may be put back (a changed renderer is an alert, ADR-0012 §12).
            if (rendered.Sha256Hex != sha) throw await IntegrityFailedAsync(invoice, lang, sha, rendered.Sha256Hex, "re-render", ct);
            await StoreAsync(key, rendered, ct);
            return new InvoicePdfFile(rendered.Bytes, FileName(invoice, lang), sha);
        }

        key = $"invoices/{invoice.Id:N}/{lang}.pdf";
        await StoreAsync(key, rendered, ct);
        var (hash, id) = (rendered.Sha256Hex, invoice.Id);
        var won = lang == "en"
            ? await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE invoices SET pdf_en_key = {key}, pdf_en_sha256 = {hash} WHERE id = {id} AND pdf_en_key IS NULL", ct)
            : await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE invoices SET pdf_bn_key = {key}, pdf_bn_sha256 = {hash} WHERE id = {id} AND pdf_bn_key IS NULL", ct);
        if (won == 1)
        {
            db.AuditLogs.Add(Audit("invoice.pdf_rendered", invoice.Id, JsonSerializer.Serialize(new { invoice.Number, Locale = lang, Sha256 = hash })));
            await db.SaveChangesAsync(ct);
            return new InvoicePdfFile(rendered.Bytes, FileName(invoice, lang), hash);
        }

        // Another request recorded the file first: serve theirs, verified like any download.
        var (wKey, wSha) = await RecordedAsync(invoice.Id, name, ct);
        if (wKey is null || wSha is null || await ReadAsync(wKey, ct) is not { } winner) throw Unavailable();
        if (Hex(winner) != wSha) throw await IntegrityFailedAsync(invoice, lang, wSha, Hex(winner), "stored file", ct);
        return new InvoicePdfFile(winner, FileName(invoice, lang), wSha);
    }

    private Task<Invoice?> LoadAsync(Guid invoiceId, CancellationToken ct) =>
        db.Invoices.AsNoTracking().Include(i => i.Lines).AsSplitQuery().FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

    private async Task<string?> OriginalNumberAsync(Invoice invoice, CancellationToken ct) =>
        invoice.OriginalInvoiceId is { } original ? await db.Invoices.AsNoTracking().Where(i => i.Id == original).Select(i => i.Number).FirstOrDefaultAsync(ct) : null;

    private async Task<(string? Key, string? Sha)> RecordedAsync(Guid invoiceId, string name, CancellationToken ct)
    {
        var row = await db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId)
            .Select(i => new { Key = EF.Property<string?>(i, "Pdf" + name + "Key"), Sha = EF.Property<string?>(i, "Pdf" + name + "Sha256") }).SingleAsync(ct);
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

    private async Task<Exception> IntegrityFailedAsync(Invoice invoice, string lang, string expected, string actual, string what, CancellationToken ct)
    {
        log.LogError("Invoice PDF integrity check failed for {Number} {Locale} ({What}): expected {Expected}, got {Actual}.", invoice.Number, lang, what, expected, actual);
        db.AuditLogs.Add(Audit("invoice.pdf_integrity_failed", invoice.Id, JsonSerializer.Serialize(new { invoice.Number, Locale = lang, Expected = expected, Actual = actual, What = what })));
        await db.SaveChangesAsync(ct);
        return Unavailable();
    }

    private static ServiceUnavailableException Unavailable() =>
        new("INVOICE_PDF_UNAVAILABLE", "This document cannot be downloaded right now. Please contact us and quote the number on your invoice.");

    private AuditLog Audit(string action, Guid invoiceId, string newValues) =>
        new(Guid.NewGuid(), user.UserId, user.IpAddress, action, "invoices", invoiceId.ToString(), null, newValues, user.CorrelationId, clock.GetUtcNow().UtcDateTime);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string FileName(Invoice invoice, string lang) => $"{invoice.Number}-{lang}.pdf";
}

/// <summary>Outbox consumer: renders both languages of a newly issued invoice after its COMMIT (the lazy download is the fallback).</summary>
public sealed class InvoicePdfPublisher(IInvoicePdfService pdfs) : IOutboxPublisher
{
    public async Task PublishAsync(OutboxEnvelope message, CancellationToken ct = default)
    {
        if (message.EventType != InvoiceIssuer.IssuedEvent) return;
        await pdfs.RenderBothAsync(message.AggregateId, ct);
    }
}
