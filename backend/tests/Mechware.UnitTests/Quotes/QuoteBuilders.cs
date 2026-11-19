using Mechware.Domain.Quotes;

namespace Mechware.UnitTests.Quotes;

/// <summary>Shared builders for the D4.2 tests. <see cref="Now"/> is 10:00 in Dhaka on 17 November 2026.</summary>
internal static class QuoteBuilders
{
    public static readonly DateTime Now = new(2026, 11, 17, 4, 0, 0, DateTimeKind.Utc);
    public static readonly Guid Author = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid Editor = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    public static readonly Guid Sender = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    public static readonly Guid Approver = Guid.Parse("00000000-0000-0000-0000-0000000000a4");
    public static readonly Guid Buyer = Guid.Parse("00000000-0000-0000-0000-0000000000a5");
    public static readonly Guid Contact = Guid.Parse("00000000-0000-0000-0000-0000000000a6");
    public static readonly Guid CompanyA = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    public static readonly Guid Seller1 = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    public static readonly Guid Seller2 = Guid.Parse("00000000-0000-0000-0000-0000000000d2");
    public static readonly Guid Site = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    public static readonly Guid V1 = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    public static readonly Guid V2 = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    public static readonly Guid V3 = Guid.Parse("00000000-0000-0000-0000-0000000000b3");
    public static readonly QuoteOptions Options = new();
    public static readonly QuoteCompanySnapshot Company = new("Acme Engineering Ltd", "000123456-0101", "12 Tejgaon I/A, Dhaka 1208");

    public static string Hash(char c = 'a') => new(c, 64);

    public static QuoteLineFacts Facts(Guid variant, Guid? seller = null, string? problem = null, bool active = true, decimal vat = 0.15m,
        long publicPrice = 120_000) =>
        new(variant, active, seller ?? Seller1, $"SKU-{variant.ToString("N")[^4..]}", "6ES7214-1AG40", "Siemens S7-1200 CPU",
            "সিমেন্স এস৭-১২০০ সিপিইউ", vat, publicPrice, problem);

    public static QuoteLineDraft Line(Guid variant, int qty = 10, long price = 100_000, string? note = null, string? reason = null,
        Guid? seller = null) =>
        new(variant, qty, price, Facts(variant, seller), note, reason);

    public static QuoteDraft Content(params QuoteLineDraft[] lines) => Content(15, lines);

    public static QuoteDraft Content(int validityDays, params QuoteLineDraft[] lines) =>
        new(validityDays, QuotePaymentTerms.AsAccount, QuoteShippingMode.Calculated, null, null, "Prices in BDT.", "As discussed on the phone.",
            "Margin is thin; check with finance.", lines);

    /// <summary>A new quote of <see cref="CompanyA"/> whose version 1 is a Draft with two lines (V1, V2), made by <see cref="Author"/>.</summary>
    public static Quote Drafted(DateTime? at = null)
    {
        var quote = Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(at ?? Now), CompanyA, null, Contact, Author, Options, at ?? Now);
        quote.EditDraft(1, Content(Line(V1), Line(V2, qty: 4, price: 250_000)), Author, Options, at ?? Now);
        return quote;
    }

    public static QuoteIssue Issue(Quote quote, int versionNo = 1, QuoteFlags? flags = null, string? hash = null) =>
        new(hash ?? Hash(), Company, "Rahim Uddin", quote.Version(versionNo).Lines.Select(l => Facts(l.VariantId, l.SellerId)).ToList(),
            flags ?? QuoteFlags.None);

    public static QuoteFlags OverAuthority(Quote quote, int versionNo = 1) =>
        new([], [quote.Version(versionNo).Lines.First().Id], false);

    public static Quote Sent(DateTime? at = null)
    {
        var quote = Drafted(at);
        quote.Send(1, Issue(quote), Sender, false, Options, at ?? Now);
        return quote;
    }

    public static Quote Pending(DateTime? at = null)
    {
        var quote = Drafted(at);
        quote.Send(1, Issue(quote, flags: OverAuthority(quote)), Sender, false, Options, at ?? Now);
        return quote;
    }

    /// <summary>A quote whose version 1 is in <paramref name="status"/> (Superseded: version 2 was sent after it).</summary>
    public static Quote WithVersion1In(QuoteVersionStatus status)
    {
        switch (status)
        {
            case QuoteVersionStatus.Draft: return Drafted();
            case QuoteVersionStatus.PendingApproval: return Pending();
        }

        var quote = Sent();
        switch (status)
        {
            case QuoteVersionStatus.Accepted: quote.Accept(1, Hash(), Guid.NewGuid(), Buyer, false, Now.AddHours(1)); break;
            case QuoteVersionStatus.Rejected: quote.Reject(1, Buyer, "Too expensive", Now.AddHours(1)); break;
            case QuoteVersionStatus.Withdrawn: quote.Withdraw(1, Sender, "Wrong prices", Now.AddHours(1)); break;
            case QuoteVersionStatus.Superseded:
                quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(1));
                quote.Send(2, Issue(quote, 2, hash: Hash('b')), Sender, false, Options, Now.AddHours(2));
                break;
        }

        return quote;
    }
}
