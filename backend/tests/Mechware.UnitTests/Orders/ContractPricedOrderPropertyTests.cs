using FluentAssertions;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using static Mechware.UnitTests.Properties.PropertyRunner;

namespace Mechware.UnitTests.Orders;

/// <summary>
/// D2.5 / D2.6 (threat model §৮ PL18, owner decision 2): orders priced by <see cref="PriceResolver"/> and summed by
/// <see cref="OrderPricing"/> reconcile to the poisha, with contract prices, quantity breaks, sales, mixed VAT rates,
/// discounts on publicly priced lines only and a free-shipping threshold; and a company never pays more than a guest for
/// the same cart. Seeded, so a failure names its seed and iteration.
/// </summary>
public class ContractPricedOrderPropertyTests
{
    private static readonly DateTime T0 = new(2026, 11, 15, 6, 0, 0, DateTimeKind.Utc);
    private static readonly decimal[] VatRates = [0m, 0.05m, 0.075m, 0.10m, 0.15m, 0.5m];
    private static readonly Guid Company = Guid.Parse("00000000-0000-0000-0000-00000000cc01");

    private sealed record Line(Guid VariantId, Guid SellerId, int Quantity, PricingInput Catalogue);

    [Fact]
    public void RandomOrdersWithContractPrices_AlwaysReconcileToThePoisha()
    {
        ForAll(2511, 2_000, (rng, _) =>
        {
            var sellers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var lines = Enumerable.Range(0, rng.Next(1, 7)).Select(_ => RandomLine(rng, sellers)).ToList();
            var lists = new List<PriceListInput> { RandomList(rng, lines, PriceListScope.Company), RandomList(rng, lines, PriceListScope.General) };
            var threshold = rng.Next(3) == 0 ? 0 : rng.NextInt64(1, 5_000_000);
            var fee = rng.Next(0, 20_000);

            var company = Place(lines, lists, TaxMode.Exclusive, Company, threshold, fee, rng, discounts: true);
            AssertReconciles(company.Priced, TaxMode.Exclusive);
            company.Priced.SubOrders.SelectMany(s => s.Lines)
                .Where(l => l.Price!.Source is PriceSource.Contract or PriceSource.GroupList or PriceSource.Quote)
                .Should().OnlyContain(l => l.DiscountMinor == 0, "promotions never stack on a negotiated price (ADR-0008 §6)");

            var guest = Place(lines, lists, TaxMode.Inclusive, null, threshold, fee, rng, discounts: true);
            AssertReconciles(guest.Priced, TaxMode.Inclusive);
            guest.Priced.SubOrders.SelectMany(s => s.Lines).Select(l => l.Price!.Source)
                .Should().BeSubsetOf([PriceSource.List, PriceSource.Sale, PriceSource.QuantityBreak], "a guest never gets a negotiated price");

            // The same cart without promotions: the company never pays more than a guest (rule A + owner decision 2), its free-
            // shipping basis equals the guest's (so the same fee), and on publicly priced lines it pays at most (1 + VAT) poisha
            // per unit plus half a poisha per line less. Catalogue prices are at least 2 poisha (see RandomLine).
            var plainCompany = Place(lines, lists, TaxMode.Exclusive, Company, threshold, fee, rng, discounts: false);
            var plainGuest = Place(lines, lists, TaxMode.Inclusive, null, threshold, fee, rng, discounts: false);
            plainCompany.Basis.Should().Be(plainGuest.Basis, "a company line counts toward free shipping at the guest's VAT-inclusive value");
            plainCompany.Priced.ShippingMinor.Should().Be(plainGuest.Priced.ShippingMinor);
            plainCompany.Priced.GrandTotalMinor.Should().BeLessThanOrEqualTo(plainGuest.Priced.GrandTotalMinor, "a company never pays more than a guest");
            var guestLines = plainGuest.Priced.SubOrders.SelectMany(s => s.Lines).ToDictionary(l => l.VariantId);
            foreach (var l in plainCompany.Priced.SubOrders.SelectMany(s => s.Lines))
            {
                var g = guestLines[l.VariantId];
                l.TotalMinor.Should().BeLessThanOrEqualTo(g.TotalMinor);
                if (l.Price!.Source is PriceSource.List or PriceSource.Sale or PriceSource.QuantityBreak)
                    (g.TotalMinor - l.TotalMinor).Should().BeLessThanOrEqualTo((long)decimal.Ceiling(l.Quantity * (1m + l.VatRate) + 0.5m),
                        "the only difference on a public price is rounding it to net once per unit");
            }
        });
    }

    [Fact]
    public void ASnapshotThatDoesNotMatchItsLine_IsRefused()
    {
        var line = new Line(Guid.NewGuid(), Guid.NewGuid(), 2, Catalogue(105_000, 0.05m));
        var price = PriceResolver.Resolve(new PriceResolutionRequest(line.VariantId, 2, line.Catalogue, TaxMode.Inclusive, null, null, T0, []));
        var draft = new OrderLineDraft(Guid.NewGuid(), line.VariantId, line.SellerId, "S", "T", 2, price.UnitPriceMinor - 1, 0.05m, 0, price);

        var act = () => OrderPricing.Price([draft], [new SellerTerms(line.SellerId, 0, 0m)], TaxMode.Inclusive);

        act.Should().Throw<ValidationException>("an order line must never carry a snapshot of a price it was not charged");
    }

    private static (PricedOrder Priced, long Basis) Place(
        List<Line> lines, List<PriceListInput> lists, TaxMode mode, Guid? company, long threshold, long fee, Random rng, bool discounts)
    {
        var drafts = new List<OrderLineDraft>();
        var guestUnit = new Dictionary<Guid, long>();
        foreach (var l in lines)
        {
            var price = PriceResolver.Resolve(new PriceResolutionRequest(l.VariantId, l.Quantity, l.Catalogue, mode, company, null, T0, lists));
            var guest = PriceResolver.Resolve(new PriceResolutionRequest(l.VariantId, l.Quantity, l.Catalogue, TaxMode.Inclusive, null, null, T0, lists));
            var negotiated = price.Source is PriceSource.Contract or PriceSource.GroupList or PriceSource.Quote;
            var gross = checked(l.Quantity * price.UnitPriceMinor);
            var discount = discounts && !negotiated && rng.Next(3) == 0 ? rng.NextInt64(0, gross + 1) : 0;
            var id = Guid.NewGuid();
            guestUnit[id] = guest.UnitPriceMinor;
            drafts.Add(new OrderLineDraft(id, l.VariantId, l.SellerId, "SKU", "Item", l.Quantity, price.UnitPriceMinor, l.Catalogue.VatRate, discount, price));
        }

        var basis = OrderPricing.FreeShippingBasisMinor(drafts, guestUnit, mode);
        var terms = drafts.Select(d => d.SellerId).Distinct().Select(s => new SellerTerms(s, basis >= threshold ? 0 : fee, 0.1m)).ToList();
        return (OrderPricing.Price(drafts, terms, mode), basis);
    }

    private static void AssertReconciles(PricedOrder o, TaxMode mode)
    {
        var lines = o.SubOrders.SelectMany(s => s.Lines).ToList();
        foreach (var l in lines)
        {
            l.GrossMinor.Should().Be(checked(l.Quantity * l.UnitPriceMinor));
            l.TotalMinor.Should().Be(l.NetMinor + l.TaxMinor);
            if (mode == TaxMode.Exclusive)
            {
                l.NetMinor.Should().Be(l.GrossMinor - l.DiscountMinor);
                l.TaxMinor.Should().Be((long)decimal.Round(l.NetMinor * l.VatRate, 0, MidpointRounding.ToEven), "VAT is rounded once per line");
            }
            else l.TotalMinor.Should().Be(l.GrossMinor - l.DiscountMinor, "a VAT-inclusive line never adds VAT");
            l.Price!.UnitPriceMinor.Should().Be(l.UnitPriceMinor, "the snapshot is the price charged");
            l.Price.Basis.Should().Be(mode == TaxMode.Inclusive ? PriceBasis.GrossInclVat : PriceBasis.NetOfVat);
        }

        foreach (var s in o.SubOrders)
        {
            s.TotalMinor.Should().Be(s.Lines.Sum(l => l.TotalMinor) + s.ShippingMinor);
            (s.CommissionMinor + s.SellerEarningMinor).Should().Be(s.GoodsNetMinor);
        }

        o.GrandTotalMinor.Should().Be(lines.Sum(l => l.TotalMinor) + o.ShippingMinor, "lines + shipping = grand total, to the poisha");
        o.GrandTotalMinor.Should().Be(lines.Sum(l => l.NetMinor) + o.TaxMinor + o.ShippingMinor, "net + VAT + shipping = grand total");
        (o.SubtotalMinor, o.DiscountMinor, o.TaxMinor).Should().Be((lines.Sum(l => l.GrossMinor), lines.Sum(l => l.DiscountMinor), lines.Sum(l => l.TaxMinor)));
    }

    private static Line RandomLine(Random rng, Guid[] sellers)
    {
        // At least 2 poisha: a 1-poisha catalogue price is the one case where the 1-poisha net minimum lifts a company above a guest.
        var price = rng.Next(4) == 0 ? rng.NextInt64(2, 1_000) : rng.NextInt64(2, 20_000_000);
        var vat = VatRates[rng.Next(VatRates.Length)];
        var quantity = rng.Next(5) == 0 ? rng.Next(1, 10_001) : rng.Next(1, 60);
        var catalogue = Catalogue(price, vat);
        if (rng.Next(4) == 0 && price > 2)
            catalogue = catalogue with
            {
                SalePrice = Money.FromMinorUnits(rng.NextInt64(2, price), Currency.BDT),
                SaleStartsAtUtc = T0.AddDays(-1),
                SaleEndsAtUtc = rng.Next(2) == 0 ? T0.AddDays(1) : T0.AddHours(-1), // running, or already over
            };
        return new Line(Guid.NewGuid(), sellers[rng.Next(sellers.Length)], quantity, catalogue);
    }

    private static PricingInput Catalogue(long priceMinor, decimal vat) =>
        new(Money.FromMinorUnits(priceMinor, Currency.BDT), null, null, null, null, vat);

    /// <summary>
    /// An approved list with entries for some of the lines: a contract (net, around the public net price, sometimes above it)
    /// or public breaks (gross, from 2 units). Breaks never get dearer as the quantity grows, like an approved list.
    /// </summary>
    private static PriceListInput RandomList(Random rng, List<Line> lines, PriceListScope scope)
    {
        var id = Guid.NewGuid();
        var entries = new List<PriceListEntryInput>();
        foreach (var l in lines.Where(_ => rng.Next(2) == 0))
        {
            var gross = l.Catalogue.Price.ToMinorUnits();
            var reference = scope == PriceListScope.Company ? PriceResolver.ToOrderBasis(gross, l.Catalogue.VatRate, TaxMode.Exclusive) : gross;
            // Public (gross) prices keep the 2-poisha minimum of RandomLine; a contract (net) price may be 1 poisha.
            var lowest = scope == PriceListScope.Company ? 1 : 2;
            var price = Math.Max(lowest, (long)(reference * (0.7 + rng.NextDouble() * 0.4)));
            var minQty = scope == PriceListScope.Company ? 1 : 2;
            var steps = rng.Next(1, 4);
            for (var step = 0; step < steps; step++)
            {
                entries.Add(new PriceListEntryInput(Guid.NewGuid(), l.VariantId, minQty, price));
                minQty += rng.Next(1, 50);
                price = Math.Max(lowest, price - rng.NextInt64(0, price / 10 + 1));
            }
        }

        var company = scope == PriceListScope.Company ? Company : (Guid?)null;
        return new PriceListInput(id, Guid.NewGuid(), 1, scope, company, company is null ? null : "CN-PROP-1", Currency.BDT,
            PriceListStatus.Approved, T0.AddDays(-10), T0.AddDays(10), T0.AddDays(-9), null, entries);
    }
}
