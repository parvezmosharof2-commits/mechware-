using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;

namespace Mechware.UnitTests.Quotes;

/// <summary>Shared builders for the D4.2 / D4.3 tests. <see cref="Now"/> is 10:00 in Dhaka on 17 November 2026.</summary>
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
    public static readonly Guid CompanyB = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    public static readonly Guid Seller1 = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    public static readonly Guid Seller2 = Guid.Parse("00000000-0000-0000-0000-0000000000d2");
    public static readonly Guid Site = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    public static readonly Guid V1 = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    public static readonly Guid V2 = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    public static readonly Guid V3 = Guid.Parse("00000000-0000-0000-0000-0000000000b3");
    public static readonly QuoteOptions Options = new();
    public static readonly QuoteCompanySnapshot Company = new("Acme Engineering Ltd", "000123456-0101", "12 Tejgaon I/A, Dhaka 1208");

    /// <summary>A global floor of 0 % on cost: with <see cref="Cost"/> 1 every price of 1 poisha or more is on or above the floor.</summary>
    public static readonly MarginFloorRow[] ZeroFloor = [new(MarginFloorScope.Global, null, null, 0m)];

    /// <summary>The owner's 10 % global floor (ADR-0008 decision 3).</summary>
    public static readonly MarginFloorRow[] TenPercentFloor = [new(MarginFloorScope.Global, null, null, 10m)];

    public static string Hash(char c = 'a') => new(c, 64);

    /// <summary>A staff user who is a member of no buyer company (no conflict of interest).</summary>
    public static QuoteActor Staff(Guid id) => new(id, []);

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
        quote.EditDraft(1, Content(Line(V1), Line(V2, qty: 4, price: 250_000)), Staff(Author), Options, at ?? Now);
        return quote;
    }

    public static VariantCostFacts Cost(Guid variant, long? cost = 1) => new(variant, cost, null, [], 0.15m);

    /// <summary>The buyer's own resolver price (Exclusive mode, net), as <c>IPriceResolver</c> gives it for the quote's company.</summary>
    public static PriceResolution Reference(Guid variant, long price, PriceSource source = PriceSource.List) =>
        new(variant, price, source, PriceBasis.NetOfVat, null, null, null, null, price);

    /// <summary>
    /// The policy run of a version: cost 1 and a 0 % floor (never below floor), the reference equal to the quote price (within
    /// authority), except that <paramref name="overAuthority"/> doubles the reference of line 1 (a 50 % discount).
    /// </summary>
    public static QuoteApprovalCheck Check(Quote quote, int versionNo = 1, bool overAuthority = false, bool allowBelowCost = false)
    {
        var v = quote.Version(versionNo);
        return QuoteApprovalPolicy.Evaluate(v, v.Lines.Select(l => Cost(l.VariantId)).ToList(), ZeroFloor,
            v.Lines.Select(l => Reference(l.VariantId, overAuthority && l.LineNo == 1 ? l.UnitPriceMinor * 2 : l.UnitPriceMinor)).ToList(),
            Options, allowBelowCost);
    }

    /// <summary>The policy run again at approval with the reference and cost recorded at the send (nothing moved since).</summary>
    public static QuoteApprovalCheck Recheck(Quote quote, int versionNo = 1)
    {
        var v = quote.Version(versionNo);
        return QuoteApprovalPolicy.Evaluate(v, v.Lines.Select(l => Cost(l.VariantId, l.CostMinorAtCheck ?? 1)).ToList(), ZeroFloor,
            v.Lines.Select(l => Reference(l.VariantId, l.ReferenceUnitPriceMinor ?? l.UnitPriceMinor)).ToList(), Options, false);
    }

    public static QuoteIssue Issue(Quote quote, int versionNo = 1, bool overAuthority = false) =>
        new(Company, "Rahim Uddin", quote.Version(versionNo).Lines.Select(l => Facts(l.VariantId, l.SellerId)).ToList(),
            Check(quote, versionNo, overAuthority));

    /// <summary>An approval that acknowledges exactly the flags of <paramref name="check"/> and returns the stored hash.</summary>
    public static QuoteApproval Ack(Quote quote, QuoteApprovalCheck check, int versionNo = 1, string? hash = null, bool belowFloorPermission = false,
        string? comment = null) =>
        new(belowFloorPermission, hash ?? quote.Version(versionNo).ContentHash ?? Hash(), check.Flags.BelowFloorLineIds, check.Flags.OverAuthorityLineIds,
            comment);

    /// <summary><see cref="Approver"/> (or <paramref name="approver"/>) approves with nothing changed since the send.</summary>
    public static void ApproveAs(Quote quote, int versionNo, DateTime at, Guid? approver = null, string? hash = null, string? comment = null,
        bool lapsed = false)
    {
        var check = Recheck(quote, versionNo);
        quote.Approve(versionNo, Staff(approver ?? Approver), Ack(quote, check, versionNo, hash, comment: comment), check, lapsed, at);
    }

    public static string HashOf(Quote quote, int versionNo = 1) => quote.Version(versionNo).ContentHash ?? Hash();

    public static Quote Sent(DateTime? at = null)
    {
        var quote = Drafted(at);
        quote.Send(1, Issue(quote), Staff(Sender), false, Options, at ?? Now);
        return quote;
    }

    public static Quote Pending(DateTime? at = null)
    {
        var quote = Drafted(at);
        quote.Send(1, Issue(quote, overAuthority: true), Staff(Sender), false, Options, at ?? Now);
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
            case QuoteVersionStatus.Accepted: quote.Accept(1, HashOf(quote), Guid.NewGuid(), Buyer, false, Now.AddHours(1)); break;
            case QuoteVersionStatus.Rejected: quote.Reject(1, Buyer, "Too expensive", Now.AddHours(1)); break;
            case QuoteVersionStatus.Withdrawn: quote.Withdraw(1, Sender, "Wrong prices", Now.AddHours(1)); break;
            case QuoteVersionStatus.Superseded:
                quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(1));
                quote.Send(2, Issue(quote, 2), Staff(Sender), false, Options, Now.AddHours(2));
                break;
        }

        return quote;
    }
}
