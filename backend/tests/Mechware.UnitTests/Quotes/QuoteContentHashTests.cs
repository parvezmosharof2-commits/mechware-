using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Properties.PropertyRunner;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>
/// D4.3: the content hash of ADR-0011 §6 (threat model QT3). The server computes it from the version and the quote; it covers
/// everything the buyer sees and accepts, and nothing that only staff see.
/// </summary>
public class QuoteContentHashTests
{
    private const int Seeds = 2_000;

    private static readonly Guid QuoteId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void TheCanonicalJson_IsFixed_AndTheHashIsItsLowerHexSha256()
    {
        var quote = Quote.Create(QuoteId, Guid.NewGuid(), "MWQ-261117-K7Q2MX", CompanyA, null, Contact, Author, Options, Now);
        quote.EditDraft(1, Content(Line(V1, note: "Lead time 3 weeks"), Line(V2, qty: 4, price: 250_000)), Staff(Author), Options, Now);
        quote.Send(1, Issue(quote), Staff(Sender), false, Options, Now);
        var v = quote.Version(1);

        // One line per token group here; the canonical text has no whitespace outside strings.
        var expected = """
            {"schema":"mechware.quote.v1","quoteId":"11111111-2222-3333-4444-555555555555","number":"MWQ-261117-K7Q2MX","versionNo":1,
            "currency":"BDT","companyId":"00000000-0000-0000-0000-0000000000c1","company":{"name":"Acme Engineering Ltd","bin":"000123456-0101",
            "billingAddress":"12 Tejgaon I/A, Dhaka 1208"},"contactUserId":"00000000-0000-0000-0000-0000000000a6","paymentTerms":"AsAccount",
            "validityDays":15,"termsText":"Prices in BDT.","buyerNote":"As discussed on the phone.",
            "shipping":{"mode":"Calculated","shipToSiteId":null,"fees":[]},"lines":[
            {"lineNo":1,"variantId":"00000000-0000-0000-0000-0000000000b1","sellerId":"00000000-0000-0000-0000-0000000000d1","sku":"SKU-00b1",
            "partNumber":"6ES7214-1AG40","titleEn":"Siemens S7-1200 CPU","titleBn":"সিমেন্স এস৭-১২০০ সিপিইউ","quantity":10,"unitPriceMinor":100000,
            "vatRate":"0.1500","lineNetMinor":1000000,"lineTaxMinor":150000,"lineTotalMinor":1150000,"publicUnitPriceMinor":120000,
            "lineNote":"Lead time 3 weeks"},
            {"lineNo":2,"variantId":"00000000-0000-0000-0000-0000000000b2","sellerId":"00000000-0000-0000-0000-0000000000d1","sku":"SKU-00b2",
            "partNumber":"6ES7214-1AG40","titleEn":"Siemens S7-1200 CPU","titleBn":"সিমেন্স এস৭-১২০০ সিপিইউ","quantity":4,"unitPriceMinor":250000,
            "vatRate":"0.1500","lineNetMinor":1000000,"lineTaxMinor":150000,"lineTotalMinor":1150000,"publicUnitPriceMinor":120000,
            "lineNote":null}],
            "totals":{"goodsNetMinor":2000000,"taxMinor":300000,"goodsTotalMinor":2300000,"shippingTotalMinor":null,"grandTotalMinor":null}}
            """.ReplaceLineEndings(string.Empty);

        var json = QuoteContentHash.CanonicalJson(quote, v);
        Encoding.UTF8.GetString(json).Should().Be(expected);
        v.ContentHash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expected))));
        Encoding.UTF8.GetString(json).Should().NotContain("Margin is thin", "the internal note is never in it");
    }

    [Fact]
    public void TheSameContent_InANewVersion_HasANewHash_AndEachVersionKeepsItsOwn()
    {
        var quote = Sent();
        var h1 = HashOf(quote);
        quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(1));
        quote.Send(2, Issue(quote, 2), Staff(Sender), false, Options, Now.AddHours(1));
        HashOf(quote, 2).Should().NotBe(h1, "the version number is part of what the buyer accepts");
        HashOf(quote).Should().Be(h1);
        QuoteContentHash.Compute(quote, quote.Version(1)).Should().Be(h1);
    }

    // ---- the property -------------------------------------------------------------------------------------------------

    private sealed record LineSpec(Guid Variant, Guid Seller, string Sku, string? PartNumber, string TitleEn, string TitleBn, int Quantity, long Price,
        decimal Vat, long PublicPrice, string? Note, string? Reason);

    /// <summary>Everything a test quote is built from: buyer-visible content first, then what only staff see.</summary>
    private sealed record Spec(
        Guid QuoteId, string Number, Guid CompanyId, Guid ContactId, QuoteCompanySnapshot Company, QuotePaymentTerms Terms, int ValidityDays,
        string? TermsText, string? BuyerNote, QuoteShippingMode Mode, Guid? Site, IReadOnlyList<QuoteShippingFee>? Fees, IReadOnlyList<LineSpec> Lines,
        string? InternalNote, Guid AuthorId, Guid SenderId, string Preparer, DateTime SentAt, bool OverAuthority, long Cost);

    private static readonly string[] Words = ["Siemens", "ABB", "Schneider", "relay", "সিমেন্স", "রিলে", "contactor", "ঢাকা", "PLC", "drive", "&<>'\"+", "১২০০"];
    private static readonly decimal[] Vats = [0m, 0.05m, 0.075m, 0.1m, 0.15m, 0.1234m];

    private static string Text(Random rng, int words) => string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[rng.Next(Words.Length)]));

    private static string? MaybeText(Random rng) => rng.Next(3) == 0 ? null : Text(rng, rng.Next(1, 6));

    private static LineSpec RandomLine(Random rng, Guid[] sellers) =>
        new(Guid.NewGuid(), sellers[rng.Next(sellers.Length)], $"SKU-{rng.Next(100_000):D5}", rng.Next(3) == 0 ? null : $"PN-{rng.Next(1_000_000)}",
            Text(rng, rng.Next(1, 5)), Text(rng, rng.Next(1, 5)), rng.Next(1, 10_001), rng.NextInt64(1_000, 50_000_000), Vats[rng.Next(Vats.Length)],
            rng.NextInt64(1, 60_000_000), MaybeText(rng), rng.Next(4) == 0 ? "Agreed with the owner" : null);

    private static Spec RandomSpec(Random rng)
    {
        var sellers = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => Guid.NewGuid()).ToArray();
        var lines = Enumerable.Range(0, rng.Next(1, 7)).Select(_ => RandomLine(rng, sellers)).ToList();
        var mode = rng.Next(2) == 0 ? QuoteShippingMode.Calculated : QuoteShippingMode.Fixed;
        var spec = new Spec(Guid.NewGuid(), QuoteNumber.NewQuote(Now, rng.Next), Guid.NewGuid(), Guid.NewGuid(),
            new QuoteCompanySnapshot(Text(rng, 3), rng.Next(3) == 0 ? null : $"{rng.Next(100_000_000):D9}-0101", MaybeText(rng)),
            rng.Next(2) == 0 ? QuotePaymentTerms.AsAccount : QuotePaymentTerms.Advance, rng.Next(1, 91), MaybeText(rng), MaybeText(rng), mode,
            mode == QuoteShippingMode.Fixed ? Guid.NewGuid() : null, null, lines,
            MaybeText(rng), Guid.NewGuid(), Guid.NewGuid(), Text(rng, 2), Now.AddMinutes(rng.Next(0, 100_000)), rng.Next(2) == 0, 1);
        return WithFees(spec, rng);
    }

    /// <summary>Fixed delivery needs exactly one fee per seller of the lines; Calculated has no site and no fees.</summary>
    private static Spec WithFees(Spec s, Random rng)
    {
        if (s.Mode == QuoteShippingMode.Calculated) return s with { Site = null, Fees = null };
        var old = (s.Fees ?? []).ToDictionary(f => f.SellerId, f => f.ShippingMinor);
        var fees = s.Lines.Select(l => l.Seller).Distinct()
            .Select(seller => new QuoteShippingFee(seller, old.TryGetValue(seller, out var fee) ? fee : rng.NextInt64(0, 2_000_000))).ToList();
        return s with { Site = s.Site ?? Guid.NewGuid(), Fees = fees };
    }

    private static QuoteLineFacts FactsOf(LineSpec l) =>
        new(l.Variant, true, l.Seller, l.Sku, l.PartNumber, l.TitleEn, l.TitleBn, l.Vat, l.PublicPrice, null);

    /// <summary>Builds the quote of <paramref name="s"/>, sends version 1 and returns its stored hash (Sent or PendingApproval).</summary>
    private static string Build(Spec s)
    {
        var quote = Quote.Create(s.QuoteId, Guid.NewGuid(), s.Number, s.CompanyId, null, s.ContactId, s.AuthorId, Options, s.SentAt.AddDays(-1));
        quote.EditDraft(1, new QuoteDraft(s.ValidityDays, s.Terms, s.Mode, s.Site, s.Fees, s.TermsText, s.BuyerNote, s.InternalNote,
                s.Lines.Select(l => new QuoteLineDraft(l.Variant, l.Quantity, l.Price, FactsOf(l), l.Note, l.Reason)).ToList()),
            Staff(s.AuthorId), Options, s.SentAt.AddDays(-1));
        var v = quote.Version(1);
        var check = QuoteApprovalPolicy.Evaluate(v, v.Lines.Select(l => Cost(l.VariantId, s.Cost)).ToList(), ZeroFloor,
            v.Lines.Select(l => Reference(l.VariantId, s.OverAuthority ? l.UnitPriceMinor * 2 : l.UnitPriceMinor)).ToList(), Options, false);
        quote.Send(1, new QuoteIssue(s.Company, s.Preparer, s.Lines.Select(FactsOf).ToList(), check), Staff(s.SenderId), false, Options, s.SentAt);
        v.ContentHash.Should().Be(QuoteContentHash.Compute(quote, v));
        return v.ContentHash!;
    }

    private static Spec ChangeLine(Spec s, Random rng, Func<LineSpec, LineSpec> change)
    {
        var k = rng.Next(s.Lines.Count);
        return s with { Lines = s.Lines.Select((l, i) => i == k ? change(l) : l).ToList() };
    }

    private static readonly (string Field, Func<Spec, Random, Spec> Change)[] BuyerVisible =
    [
        ("quote id", (s, _) => s with { QuoteId = Guid.NewGuid() }),
        ("number", (s, rng) => s with { Number = Different(s.Number, () => QuoteNumber.NewQuote(Now, rng.Next)) }),
        ("company id", (s, _) => s with { CompanyId = Guid.NewGuid() }),
        ("contact", (s, _) => s with { ContactId = Guid.NewGuid() }),
        ("company name", (s, _) => s with { Company = s.Company with { Name = s.Company.Name + " Ltd" } }),
        ("company BIN", (s, _) => s with { Company = s.Company with { Bin = s.Company.Bin is null ? "000999999-0101" : null } }),
        ("billing address", (s, _) => s with { Company = s.Company with { BillingAddress = s.Company.BillingAddress is null ? "Dhaka" : s.Company.BillingAddress + "," } }),
        ("payment terms", (s, _) => s with { Terms = s.Terms == QuotePaymentTerms.AsAccount ? QuotePaymentTerms.Advance : QuotePaymentTerms.AsAccount }),
        ("validity days", (s, _) => s with { ValidityDays = s.ValidityDays == 90 ? 89 : s.ValidityDays + 1 }),
        ("terms text", (s, _) => s with { TermsText = s.TermsText is null ? "Net 30" : s.TermsText + "." }),
        ("buyer note", (s, _) => s with { BuyerNote = s.BuyerNote is null ? "Thanks" : null }),
        ("shipping mode", (s, rng) => WithFees(s with { Mode = s.Mode == QuoteShippingMode.Fixed ? QuoteShippingMode.Calculated : QuoteShippingMode.Fixed }, rng)),
        ("ship-to site", (s, rng) => WithFees(s with { Mode = QuoteShippingMode.Fixed, Site = Guid.NewGuid() }, rng)),
        ("a shipping fee", (s, rng) => s.Mode == QuoteShippingMode.Calculated ? WithFees(s with { Mode = QuoteShippingMode.Fixed }, rng)
            : s with { Fees = s.Fees!.Select((f, i) => i == 0 ? f with { ShippingMinor = f.ShippingMinor + 1 } : f).ToList() }),
        ("line order", (s, rng) => s.Lines.Count > 1 ? s with { Lines = [.. s.Lines.Skip(1), s.Lines[0]] } : WithFees(s with { Lines = [.. s.Lines, RandomLine(rng, [s.Lines[0].Seller])] }, rng)),
        ("a line added", (s, rng) => WithFees(s with { Lines = [.. s.Lines, RandomLine(rng, [.. s.Lines.Select(l => l.Seller)])] }, rng)),
        ("a line removed", (s, rng) => s.Lines.Count > 1 ? WithFees(s with { Lines = s.Lines.Skip(1).ToList() }, rng)
            : s with { Lines = [s.Lines[0] with { Variant = Guid.NewGuid() }] }),
        ("variant", (s, rng) => ChangeLine(s, rng, l => l with { Variant = Guid.NewGuid() })),
        ("seller", (s, rng) => WithFees(ChangeLine(s, rng, l => l with { Seller = Guid.NewGuid() }), rng)),
        ("sku", (s, rng) => ChangeLine(s, rng, l => l with { Sku = l.Sku + "X" })),
        ("part number", (s, rng) => ChangeLine(s, rng, l => l with { PartNumber = l.PartNumber is null ? "PN-1" : null })),
        ("English title", (s, rng) => ChangeLine(s, rng, l => l with { TitleEn = l.TitleEn + " 2" })),
        ("Bangla title", (s, rng) => ChangeLine(s, rng, l => l with { TitleBn = l.TitleBn + " ২" })),
        ("quantity", (s, rng) => ChangeLine(s, rng, l => l with { Quantity = l.Quantity == 10_000 ? 9_999 : l.Quantity + 1 })),
        ("unit price", (s, rng) => ChangeLine(s, rng, l => l with { Price = l.Price + 1 })),
        ("VAT rate", (s, rng) => ChangeLine(s, rng, l => l with { Vat = l.Vat == 0.15m ? 0.075m : 0.15m })),
        ("public price", (s, rng) => ChangeLine(s, rng, l => l with { PublicPrice = l.PublicPrice + 1 })),
        ("line note", (s, rng) => ChangeLine(s, rng, l => l with { Note = l.Note is null ? "Ex stock" : null })),
    ];

    private static readonly (string Field, Func<Spec, Random, Spec> Change)[] StaffOnly =
    [
        ("internal note", (s, _) => s with { InternalNote = s.InternalNote is null ? "Check with finance" : null }),
        ("below-floor reason", (s, rng) => ChangeLine(s, rng, l => l with { Reason = l.Reason is null ? "Strategic account" : null })),
        ("author", (s, _) => s with { AuthorId = Guid.NewGuid() }),
        ("sender", (s, _) => s with { SenderId = Guid.NewGuid() }),
        ("preparer name", (s, _) => s with { Preparer = s.Preparer + " Khan" }),
        ("send time", (s, rng) => s with { SentAt = s.SentAt.AddMinutes(rng.Next(1, 10_000)) }),
        ("flags (status)", (s, _) => s with { OverAuthority = !s.OverAuthority }),
        ("cost and floor", (s, _) => s with { Cost = s.Lines.Min(l => l.Price) }),
    ];

    private static string Different(string old, Func<string> next)
    {
        string value;
        do value = next();
        while (value == old);
        return value;
    }

    [Fact]
    public void ContentHash_ChangesWithEveryBuyerVisibleField_AndNotWithStaffOnlyOnes()
    {
        ForAll(4303, Seeds, (rng, _) =>
        {
            var spec = RandomSpec(rng);
            var hash = Build(spec);
            Build(spec).Should().Be(hash, "new line and version ids do not change it, and it is deterministic");
            foreach (var (field, change) in BuyerVisible)
                Build(change(spec, rng)).Should().NotBe(hash, $"{field} is buyer-visible");
            foreach (var (field, change) in StaffOnly)
                Build(change(spec, rng)).Should().Be(hash, $"{field} is staff-only");
        });
    }
}
