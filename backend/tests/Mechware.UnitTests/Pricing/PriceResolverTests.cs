using FluentAssertions;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;

namespace Mechware.UnitTests.Pricing;

/// <summary>D2.2: the pure price resolver (ADR-0008 §5, threat model §৮ PL5, PL6, PL8, PL13–PL15, PL20).</summary>
public class PriceResolverTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Variant = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OtherVariant = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid CompanyA = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid CompanyB = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid GroupSeries = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly TimeSpan Delay = PriceListLimits.ActivationDelay;

    // ---- builders -------------------------------------------------------------------------------------------------

    /// <summary>An Approved list, approved 10 days before T0, valid from 20 days before to 20 days after T0.</summary>
    private static PriceListInput NewList(PriceListScope scope, Guid? company, params (int MinQty, long Price)[] entries) => new(
        Guid.NewGuid(), Guid.NewGuid(), 1, scope, company, scope == PriceListScope.Company ? "CTR-001" : null, Currency.BDT,
        PriceListStatus.Approved, T0.AddDays(-20), T0.AddDays(20), T0.AddDays(-10), null,
        entries.Select(e => new PriceListEntryInput(Guid.NewGuid(), Variant, e.MinQty, e.Price)).ToList());

    private static PriceListInput Contract(long price, Guid? company = null) => NewList(PriceListScope.Company, company ?? CompanyA, (1, price));
    private static PriceListInput Group(long price) => NewList(PriceListScope.Group, null, (1, price)) with { SeriesId = GroupSeries };
    private static PriceListInput Breaks(params (int MinQty, long Price)[] entries) => NewList(PriceListScope.General, null, entries);

    /// <summary>Catalogue price 1,050.00 VAT-inclusive at 5 % = 1,000.00 net, so net and gross numbers stay readable.</summary>
    private static PriceResolution Resolve(
        IEnumerable<PriceListInput> lists, int qty = 1, Guid? company = null, DateTime? asOf = null, long listPrice = 105_000,
        long? sale = null, decimal vat = 0.05m, Guid? groupSeries = null, TaxMode? mode = null)
    {
        var catalogue = new PricingInput(Money.FromMinorUnits(listPrice), null, sale is null ? null : Money.FromMinorUnits(sale.Value),
            sale is null ? null : T0.AddDays(-1), sale is null ? null : T0.AddDays(1), vat);
        return PriceResolver.Resolve(new PriceResolutionRequest(Variant, qty, catalogue,
            mode ?? (company is null ? TaxMode.Inclusive : TaxMode.Exclusive), company, groupSeries, asOf ?? T0, lists.ToList()));
    }

    // ---- exit tests (threat model §৮) ----------------------------------------------------------------------------------

    [Fact]
    public void Resolver_TieGoesToTheMoreSpecificSource()
    {
        // Every source at 1,000.00 net (public 1,050.00 gross at 5 %).
        var contract = Contract(100_000);
        var group = Group(100_000);
        var breaks = Breaks((2, 105_000));
        var all = new[] { contract, group, breaks };

        Resolve(all, qty: 2, company: CompanyA, sale: 105_000, groupSeries: GroupSeries).Source.Should().Be(PriceSource.Contract);
        Resolve([group, breaks], qty: 2, company: CompanyA, groupSeries: GroupSeries).Source.Should().Be(PriceSource.GroupList);
        Resolve([breaks], qty: 2, company: CompanyA, listPrice: 110_000, sale: 105_000).Source.Should().Be(PriceSource.QuantityBreak);
        // 1,050.01 and 1,050.00 gross both round down to 1,000.00 net: a tie, the sale names the source.
        Resolve([], company: CompanyA, listPrice: 105_001, sale: 105_000).Source.Should().Be(PriceSource.Sale);
        Resolve([], company: CompanyA).Source.Should().Be(PriceSource.List);

        // Same source, same price: the later approval wins, then the smaller list id.
        var older = Contract(90_000) with { ApprovedAtUtc = T0.AddDays(-12) };
        var later = Contract(90_000) with { ApprovedAtUtc = T0.AddDays(-11) };
        Resolve([older, later], company: CompanyA).PriceListId.Should().Be(later.Id);
        Resolve([later, older], company: CompanyA).PriceListId.Should().Be(later.Id);
        var idLow = Contract(90_000) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var idHigh = Contract(90_000) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        Resolve([idHigh, idLow], company: CompanyA).PriceListId.Should().Be(idLow.Id);
    }

    [Fact]
    public void Resolver_LowestValidWins_EvenAgainstAMoreSpecificSource()
    {
        // Rule A: a contract above the public sale price never applies (owner decision 1).
        var result = Resolve([Contract(97_000)], company: CompanyA, sale: 99_750); // sale 997.50 gross = 950.00 net
        result.Source.Should().Be(PriceSource.Sale);
        result.UnitPriceMinor.Should().Be(95_000);
        result.PriceListId.Should().BeNull();
    }

    [Fact]
    public void Resolver_PicksTheBreakWithTheLargestMinQtyNotAboveTheQuantity()
    {
        var contract = NewList(PriceListScope.Company, CompanyA, (50, 80_000), (1, 99_000), (10, 90_000));
        Resolve([contract], qty: 1, company: CompanyA).UnitPriceMinor.Should().Be(99_000);
        Resolve([contract], qty: 9, company: CompanyA).UnitPriceMinor.Should().Be(99_000);
        Resolve([contract], qty: 10, company: CompanyA).UnitPriceMinor.Should().Be(90_000);
        Resolve([contract], qty: 49, company: CompanyA).UnitPriceMinor.Should().Be(90_000);
        Resolve([contract], qty: 50, company: CompanyA).UnitPriceMinor.Should().Be(80_000);
        Resolve([contract], qty: 10_000, company: CompanyA).EntryId.Should().Be(contract.Entries.Single(e => e.MinQty == 50).Id);

        // Public breaks (gross) for a guest: below the smallest break only the catalogue applies.
        var breaks = Breaks((5, 100_000), (20, 95_000));
        Resolve([breaks], qty: 4).Source.Should().Be(PriceSource.List);
        Resolve([breaks], qty: 5).UnitPriceMinor.Should().Be(100_000);
        Resolve([breaks], qty: 20).UnitPriceMinor.Should().Be(95_000);

        // Entries of another variant, and invalid entries, are never used.
        var noise = NewList(PriceListScope.Company, CompanyA) with
        {
            Entries =
            [
                new(Guid.NewGuid(), OtherVariant, 1, 1_000), new(Guid.NewGuid(), Variant, 1, 0), new(Guid.NewGuid(), Variant, 1, -5),
                new(Guid.NewGuid(), Variant, 0, 2_000), new(Guid.NewGuid(), Variant, 1, PriceListLimits.MaxUnitPriceMinor + 1),
            ],
        };
        Resolve([noise], company: CompanyA).Source.Should().Be(PriceSource.List);
    }

    [Fact]
    public void Resolver_NeverUsesAListOutsideItsWindowOrNotApproved()
    {
        var good = Contract(50_000);
        Resolve([good], company: CompanyA).Source.Should().Be(PriceSource.Contract, "control: the list itself is valid");

        PriceListInput[] bad =
        [
            good with { Status = PriceListStatus.Draft },
            good with { Status = PriceListStatus.PendingApproval },
            good with { Status = PriceListStatus.Withdrawn, WithdrawnAtUtc = T0.AddDays(-1) },
            good with { ApprovedAtUtc = null },
            good with { ValidToUtc = T0 }, // expired exactly now
            good with { ValidToUtc = T0.AddDays(-1) },
            good with { ValidFromUtc = T0.AddSeconds(1) }, // scheduled
            good with { ApprovedAtUtc = T0.AddSeconds(-59) }, // approved, but not yet 60 s ago
            good with { Currency = Currency.USD },
            good with { CompanyId = null }, // inconsistent header: a contract without a company
        ];
        foreach (var list in bad)
            Resolve([list], company: CompanyA).Source.Should().Be(PriceSource.List, "{0} must not apply", list);

        var breaks = Breaks((1, 50_000));
        Resolve([breaks with { Status = PriceListStatus.PendingApproval }]).Source.Should().Be(PriceSource.List);
        Resolve([breaks with { CompanyId = CompanyA }], company: CompanyA).Source.Should().Be(PriceSource.List);
    }

    [Fact]
    public void Resolver_WithoutACompany_NeverReturnsANegotiatedSource()
    {
        var lists = new[] { Contract(10_000), Contract(10_000, CompanyB), Group(10_000) };

        var guest = Resolve(lists, groupSeries: GroupSeries);
        guest.Source.Should().Be(PriceSource.List);
        guest.UnitPriceMinor.Should().Be(105_000);
        guest.Basis.Should().Be(PriceBasis.GrossInclVat);

        Resolve(lists, groupSeries: GroupSeries, mode: TaxMode.Exclusive).Source.Should().Be(PriceSource.List,
            "a guest priced net still never sees a negotiated price");

        // A company sees its own contract only, never another company's, and a group list only through its own series.
        var a = Resolve([Contract(10_000, CompanyB), Group(20_000)], company: CompanyA);
        a.Source.Should().Be(PriceSource.List);
        Resolve([Contract(10_000, CompanyB), Group(20_000)], company: CompanyA, groupSeries: GroupSeries).Source.Should().Be(PriceSource.GroupList);
        Resolve([Contract(10_000, CompanyB), Group(20_000)], company: CompanyA, groupSeries: Guid.NewGuid()).Source.Should().Be(PriceSource.List);
        Resolve([Group(20_000) with { CompanyId = CompanyA }], company: CompanyA, groupSeries: GroupSeries).Source.Should().Be(PriceSource.List);
    }

    [Fact]
    public void BackdatedValidFrom_NeverAppliesBeforeApproval()
    {
        var approvedAt = T0;
        var list = Contract(50_000) with { ApprovedAtUtc = approvedAt, ValidFromUtc = approvedAt.AddDays(-30) };

        PriceResolver.EffectiveFromUtc(list.ValidFromUtc, approvedAt).Should().Be(approvedAt + Delay);
        Resolve([list], company: CompanyA, asOf: approvedAt.AddDays(-15)).Source.Should().Be(PriceSource.List, "inside validity but before approval");
        Resolve([list], company: CompanyA, asOf: approvedAt).Source.Should().Be(PriceSource.List);
        Resolve([list], company: CompanyA, asOf: approvedAt + Delay - TimeSpan.FromTicks(1)).Source.Should().Be(PriceSource.List);
        Resolve([list], company: CompanyA, asOf: approvedAt + Delay).Source.Should().Be(PriceSource.Contract, "approval + 60 s is the start, inclusive");

        // A validFrom later than approval + 60 s is the start itself.
        var future = list with { ValidFromUtc = approvedAt.AddHours(2) };
        PriceResolver.EffectiveFromUtc(future.ValidFromUtc, approvedAt).Should().Be(approvedAt.AddHours(2));
        Resolve([future], company: CompanyA, asOf: approvedAt.AddHours(1)).Source.Should().Be(PriceSource.List);
        PriceResolver.EffectiveFromUtc(T0, null).Should().BeNull();
    }

    [Fact]
    public void ValidityWindow_StartIsInclusive_EndIsExclusive()
    {
        var from = T0.AddDays(-1);
        var to = T0.AddDays(1);
        var list = Contract(50_000) with { ApprovedAtUtc = T0.AddDays(-5), ValidFromUtc = from, ValidToUtc = to };

        Resolve([list], company: CompanyA, asOf: from - TimeSpan.FromTicks(1)).Source.Should().Be(PriceSource.List);
        Resolve([list], company: CompanyA, asOf: from).Source.Should().Be(PriceSource.Contract);
        Resolve([list], company: CompanyA, asOf: to - TimeSpan.FromTicks(1)).Source.Should().Be(PriceSource.Contract);
        Resolve([list], company: CompanyA, asOf: to).Source.Should().Be(PriceSource.List);
    }

    [Fact]
    public void WithdrawingAVersion_NeverBringsBackAnOlderCheaperOne()
    {
        var series = Guid.NewGuid();
        var v1 = Contract(70_000) with { SeriesId = series, Version = 1, ApprovedAtUtc = T0.AddDays(-15) };
        var v2 = Contract(90_000) with { SeriesId = series, Version = 2, ApprovedAtUtc = T0.AddDays(-5), ValidFromUtc = T0.AddDays(-3) };
        Resolve([v1, v2], company: CompanyA).UnitPriceMinor.Should().Be(90_000, "v2 has started and supersedes the cheaper v1");

        // Withdrawn after it started: v2 keeps superseding v1, the buyer falls back to the public price.
        var withdrawn = v2 with { Status = PriceListStatus.Withdrawn, WithdrawnAtUtc = T0.AddDays(-1) };
        var result = Resolve([v1, withdrawn], company: CompanyA);
        result.Source.Should().Be(PriceSource.List);
        result.UnitPriceMinor.Should().Be(100_000);
        Resolve([v1, withdrawn with { WithdrawnAtUtc = null }], company: CompanyA).Source.Should().Be(PriceSource.List,
            "a withdrawal without a time is treated as after the start (fail closed)");
        Resolve([v1, withdrawn with { WithdrawnAtUtc = T0.AddDays(-3) }], company: CompanyA).Source.Should().Be(PriceSource.List,
            "withdrawn exactly at its start still counts as started");

        // An expired later version does not bring v1 back either.
        Resolve([v1, v2 with { ValidToUtc = T0.AddDays(-1) }], company: CompanyA).Source.Should().Be(PriceSource.List);

        // Withdrawn before it started: v2 never starts, v1 simply continues.
        var early = v2 with { ValidFromUtc = T0.AddDays(2), Status = PriceListStatus.Withdrawn, WithdrawnAtUtc = T0.AddDays(-1) };
        Resolve([v1, early], company: CompanyA, asOf: T0.AddDays(3)).UnitPriceMinor.Should().Be(70_000);
    }

    [Fact]
    public void OrderPricedBeforeEffectiveFrom_PaysTheOldPrice_AtOrAfterIt_TheNewPrice()
    {
        var series = Guid.NewGuid();
        var approvedAt = T0;
        var v1 = Contract(95_000) with { SeriesId = series, Version = 1 };
        var cheaper = Contract(90_000) with { SeriesId = series, Version = 2, ApprovedAtUtc = approvedAt, ValidFromUtc = approvedAt.AddDays(-1) };
        var dearer = cheaper with { Entries = [cheaper.Entries[0] with { UnitPriceMinor = 98_000 }] };

        foreach (var (v2, newPrice) in new[] { (cheaper, 90_000L), (dearer, 98_000L) })
        {
            var before = Resolve([v1, v2], company: CompanyA, asOf: approvedAt + Delay - TimeSpan.FromTicks(1));
            before.UnitPriceMinor.Should().Be(95_000);
            before.PriceListVersion.Should().Be(1);

            var at = Resolve([v1, v2], company: CompanyA, asOf: approvedAt + Delay);
            at.UnitPriceMinor.Should().Be(newPrice);
            at.PriceListVersion.Should().Be(2);
            at.PriceListId.Should().Be(v2.Id);

            Resolve([v2, v1], company: CompanyA, asOf: approvedAt + Delay).Should().Be(at, "resolving again at the same instant gives the same answer");
        }
    }

    // ---- snapshot fields and tax basis ---------------------------------------------------------------------------------

    [Fact]
    public void Result_CarriesEverythingTheOrderLineSnapshots()
    {
        var contract = Contract(92_000);
        var r = Resolve([contract, Breaks((2, 103_950))], qty: 3, company: CompanyA);

        r.Should().Be(new PriceResolution(Variant, 92_000, PriceSource.Contract, PriceBasis.NetOfVat, contract.Id, 1,
            contract.Entries[0].Id, "CTR-001", 99_000)); // public = the break, 1,039.50 gross → 990.00 net

        var guest = Resolve([contract], sale: 100_000);
        guest.Should().Be(new PriceResolution(Variant, 100_000, PriceSource.Sale, PriceBasis.GrossInclVat, null, null, null, null, 100_000));
    }

    [Fact]
    public void CompanyPrice_IsTheInclusiveCataloguePriceDividedByOnePlusVat_RoundedDown()
    {
        // 1,000.00 incl. 5 % → 952.380952… → 952.38
        Resolve([], company: CompanyA, listPrice: 100_000).UnitPriceMinor.Should().Be(95_238);
        // 1,000.10 incl. 5 % → 952.476190… → 952.47 (banker's rounding would give 952.48)
        Resolve([], company: CompanyA, listPrice: 100_010).UnitPriceMinor.Should().Be(95_247);
        // exact divisions stay exact; no VAT means no change; a guest gets the gross price unchanged
        Resolve([], company: CompanyA, listPrice: 105_000).UnitPriceMinor.Should().Be(100_000);
        Resolve([], company: CompanyA, listPrice: 100_010, vat: 0m).UnitPriceMinor.Should().Be(100_010);
        Resolve([], listPrice: 100_010).UnitPriceMinor.Should().Be(100_010);
        // 0.01 incl. 15 % would be 0.0087 net: never zero
        Resolve([], company: CompanyA, listPrice: 1, vat: 0.15m).UnitPriceMinor.Should().Be(1);
    }

    [Fact]
    public void ReAddingVat_NeverExceedsTheInclusivePrice_AndFallsShortByAtMostOnePoishaPerUnitAndLine()
    {
        // OrderPricing in Exclusive mode: total = q × net + round(q × net × vat). Bound per line:
        // q × gross − q × (1 + vat) − ½ < total ≤ q × gross  (for gross ≥ 2 poisha), i.e. a company never pays more than a guest.
        var rng = new Random(2201);
        for (var i = 0; i < 5_000; i++)
        {
            var gross = rng.NextInt64(2, 1_000_000_00L);
            var vat = rng.Next(0, 5_001) / 10_000m;
            var q = rng.Next(0, 4) == 0 ? rng.Next(1, 10_001) : rng.Next(1, 4);
            var net = Resolve([], company: CompanyA, listPrice: gross, vat: vat).UnitPriceMinor;

            var lineNet = q * net;
            var total = lineNet + (long)decimal.Round(lineNet * vat, 0, MidpointRounding.ToEven);
            total.Should().BeLessThanOrEqualTo(q * gross, "seed 2201 iteration {0}: gross {1}, vat {2}, q {3}", i, gross, vat, q);
            ((decimal)total).Should().BeGreaterThan(q * gross - q * (1m + vat) - 0.5m, "iteration {0}", i);
            if (q == 1) total.Should().BeGreaterThanOrEqualTo(gross - 1);
        }
    }

    [Fact]
    public void NegotiatedPrice_NeverLosesByRounding_InTheBuyersDisfavour()
    {
        // 1,000.00 incl. 5 % = 952.380952… net. A contract at 952.38 is not above it → it wins (tie, contract is more specific).
        Resolve([Contract(95_238)], company: CompanyA, listPrice: 100_000).Source.Should().Be(PriceSource.Contract);
        // A contract at 952.39 is above the exact net price → the public price wins, and the buyer pays less than the contract.
        var r = Resolve([Contract(95_239)], company: CompanyA, listPrice: 100_000);
        r.Source.Should().Be(PriceSource.List);
        r.UnitPriceMinor.Should().Be(95_238);

        var rng = new Random(2202);
        for (var i = 0; i < 5_000; i++)
        {
            var gross = rng.NextInt64(2, 10_000_000L);
            var vat = rng.Next(0, 5_001) / 10_000m;
            var exactNet = gross / (1m + vat);
            var contract = rng.Next(2) == 0 ? (long)decimal.Floor(exactNet) + rng.Next(-2, 3) : rng.NextInt64(1, 10_000_000L);
            if (contract < 1) contract = 1;
            var res = Resolve([Contract(contract)], company: CompanyA, listPrice: gross, vat: vat);

            res.UnitPriceMinor.Should().BeLessThanOrEqualTo(contract, "the buyer never pays more than the contract");
            if (res.Source == PriceSource.Contract) ((decimal)contract).Should().BeLessThanOrEqualTo(exactNet, "a contract wins only when it is really not dearer");
            else ((decimal)res.UnitPriceMinor).Should().BeLessThan(contract, "the contract loses only to a lower price");
        }
    }

    // ---- input guards ----------------------------------------------------------------------------------------------

    [Fact]
    public void InvalidRequests_AreRefused()
    {
        FluentActions.Invoking(() => Resolve([], qty: 0)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Resolve([], qty: 10_001)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Resolve([], listPrice: 0)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Resolve([], listPrice: PriceListLimits.MaxUnitPriceMinor + 1)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Resolve([], vat: 0.51m)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Resolve([], company: CompanyA, mode: TaxMode.Inclusive)).Should().Throw<ValidationException>(
            "negotiated prices are net; a company order is never compared on the gross basis");
        Resolve([], qty: 10_000, listPrice: PriceListLimits.MaxUnitPriceMinor).UnitPriceMinor.Should().Be(PriceListLimits.MaxUnitPriceMinor);
    }
}
