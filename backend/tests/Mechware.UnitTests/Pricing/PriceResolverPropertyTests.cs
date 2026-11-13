using FluentAssertions;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using static Mechware.UnitTests.Properties.PropertyRunner;

namespace Mechware.UnitTests.Pricing;

/// <summary>
/// D2.2 property tests (threat model §৮ PL8, PL9, PL20). Seeded, so a failure names its seed and iteration. The oracle is
/// a separate brute-force reading of ADR-0008 §5 written for the test: it shares no code with <see cref="PriceResolver"/>
/// and converts gross to net with integer basis points instead of decimals.
/// </summary>
public class PriceResolverPropertyTests
{
    private const int Rounds = 2_000;
    private const long Max = PriceListLimits.MaxUnitPriceMinor;
    private static readonly DateTime T0 = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);
    private static readonly Guid V = Guid.Parse("00000000-0000-0000-0000-00000000aa01");
    private static readonly Guid W = Guid.Parse("00000000-0000-0000-0000-00000000aa02");
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000cc01");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000cc02");
    private static readonly Guid G1 = Guid.Parse("00000000-0000-0000-0000-00000000ff01");
    private static readonly Guid G2 = Guid.Parse("00000000-0000-0000-0000-00000000ff02");
    private static readonly Guid S1 = Guid.Parse("00000000-0000-0000-0000-00000000ee01");
    private static readonly Guid S2 = Guid.Parse("00000000-0000-0000-0000-00000000ee02");
    private static readonly Guid S3 = Guid.Parse("00000000-0000-0000-0000-00000000ee03");

    /// <summary>Few series per scope, so versions of one series meet often (supersession, withdrawal); now and then a stray one.</summary>
    private static Guid SeriesFor(Random rng, PriceListScope scope) => rng.Next(10) == 0
        ? Pick(rng, G1, G2, S1, S2, S3)
        : scope switch { PriceListScope.Company => Pick(rng, S1, S2), PriceListScope.General => S3, _ => Pick(rng, G1, G2) };

    private static readonly TimeSpan[] Offsets =
    [
        TimeSpan.FromDays(-20), TimeSpan.FromDays(-1), TimeSpan.FromSeconds(-61), TimeSpan.FromSeconds(-60), TimeSpan.FromSeconds(-59),
        TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), TimeSpan.FromDays(5),
    ];

    private static readonly TimeSpan[] Past = [TimeSpan.FromDays(-30), TimeSpan.FromDays(-20), TimeSpan.FromDays(-9), TimeSpan.FromDays(-2)];

    private static readonly long[] TiePrices = [95_000, 99_750, 100_000, 104_999, 105_000];

    // ---- the four exit properties --------------------------------------------------------------------------------------

    [Fact]
    public void Resolver_AlwaysReturnsTheLowestValidCandidate()
    {
        ForAll(2301, Rounds, (rng, _) =>
        {
            var (request, vatBp) = RandomRequest(rng);
            var actual = PriceResolver.Resolve(request);
            var expected = Oracle(request, vatBp);

            actual.UnitPriceMinor.Should().Be(expected.Price, "the lowest valid candidate wins (rule A)");
            actual.Source.Should().Be(expected.Source);
            actual.PriceListId.Should().Be(expected.ListId);
            actual.EntryId.Should().Be(expected.EntryId);
            actual.PublicUnitPriceMinor.Should().Be(expected.Public);
            actual.Basis.Should().Be(request.Mode == TaxMode.Inclusive ? PriceBasis.GrossInclVat : PriceBasis.NetOfVat);
            if (request.VerifiedCompanyId is null) actual.Source.Should().BeOneOf(PriceSource.List, PriceSource.Sale, PriceSource.QuantityBreak);
        });
    }

    [Fact]
    public void Resolver_NeverReturnsZeroNegativeOrOverflows()
    {
        ForAll(2302, Rounds, (rng, _) =>
        {
            var (request, vatBp) = RandomRequest(rng, extreme: true);
            var actual = PriceResolver.Resolve(request); // never throws for a valid request, whatever the lists hold
            var listOnBasis = OnBasis(request.Catalogue.Price.ToMinorUnits(), request.Mode, vatBp);

            actual.UnitPriceMinor.Should().BeInRange(1, Max);
            actual.PublicUnitPriceMinor.Should().BeInRange(actual.UnitPriceMinor, listOnBasis, "no candidate is ever dearer than the catalogue price");
            actual.UnitPriceMinor.Should().Be(Oracle(request, vatBp).Price);
        });
    }

    [Fact]
    public void OrderingMore_NeverRaisesTheUnitPrice()
    {
        ForAll(2303, Rounds, (rng, _) =>
        {
            var (request, _) = RandomRequest(rng, monotoneBreaks: true);
            var q1 = rng.Next(1, 10_001);
            var q2 = rng.Next(0, 3) == 0 ? q1 : rng.Next(q1, 10_001);
            if (rng.Next(4) == 0) (q1, q2) = (1, 10_000);

            var fewer = PriceResolver.Resolve(request with { Quantity = q1 });
            var more = PriceResolver.Resolve(request with { Quantity = q2 });
            more.UnitPriceMinor.Should().BeLessThanOrEqualTo(fewer.UnitPriceMinor, "q {0} → {1}", q1, q2);
            more.PublicUnitPriceMinor.Should().BeLessThanOrEqualTo(fewer.PublicUnitPriceMinor);
        });
    }

    [Fact]
    public void Resolver_IsDeterministic_InputOrderDoesNotMatter()
    {
        ForAll(2304, Rounds, (rng, _) =>
        {
            var (request, _) = RandomRequest(rng);
            var expected = PriceResolver.Resolve(request);

            for (var k = 0; k < 3; k++)
            {
                var shuffled = Shuffle(rng, request.Lists.Select(l => l with { Entries = Shuffle(rng, l.Entries) }).ToList());
                PriceResolver.Resolve(request with { Lists = shuffled }).Should().Be(expected);
            }
        });
    }

    // ---- independent oracle (brute force over ADR-0008 §5) -----------------------------------------------------------

    private static long OnBasis(long gross, TaxMode mode, int vatBp) =>
        mode == TaxMode.Inclusive ? gross : Math.Max(1, gross * 10_000 / (10_000 + vatBp));

    private static DateTime? Start(PriceListInput l)
    {
        if (l.ApprovedAtUtc is null) return null;
        var earliest = l.ApprovedAtUtc.Value.AddSeconds(60);
        return l.ValidFromUtc > earliest ? l.ValidFromUtc : earliest;
    }

    private static bool Started(PriceListInput l, DateTime asOf) =>
        Start(l) is { } s && s <= asOf && s < l.ValidToUtc
        && (l.Status == PriceListStatus.Approved || (l.Status == PriceListStatus.Withdrawn && (l.WithdrawnAtUtc is null || l.WithdrawnAtUtc >= s)));

    private static (long Price, PriceSource Source, Guid? ListId, Guid? EntryId, long Public) Oracle(PriceResolutionRequest r, int vatBp)
    {
        var asOf = r.AsOfUtc;
        var cat = r.Catalogue;
        var cands = new List<(long Price, int Rank, PriceSource Source, DateTime Approved, Guid? ListId, Guid? EntryId)>
        {
            (OnBasis(cat.Price.ToMinorUnits(), r.Mode, vatBp), 1, PriceSource.List, DateTime.MinValue, null, null),
        };
        if (cat.SalePrice is { } sale && sale.ToMinorUnits() > 0 && sale.ToMinorUnits() < cat.Price.ToMinorUnits()
            && cat.SaleStartsAtUtc <= asOf && asOf < cat.SaleEndsAtUtc)
            cands.Add((OnBasis(sale.ToMinorUnits(), r.Mode, vatBp), 2, PriceSource.Sale, DateTime.MinValue, null, null));

        foreach (var l in r.Lists)
        {
            var applicable = l.Status == PriceListStatus.Approved && Start(l) is { } st && st <= asOf && asOf < l.ValidToUtc
                && l.Currency == cat.Price.Currency
                && !r.Lists.Any(o => o.SeriesId == l.SeriesId && o.Version > l.Version && Started(o, asOf));
            if (!applicable) continue;

            (int Rank, PriceSource Source) kind;
            if (l.Scope == PriceListScope.General && l.CompanyId is null) kind = (3, PriceSource.QuantityBreak);
            else if (r.VerifiedCompanyId is not null && l.Scope == PriceListScope.Group && l.CompanyId is null && l.SeriesId == r.GroupSeriesId) kind = (4, PriceSource.GroupList);
            else if (r.VerifiedCompanyId is not null && l.Scope == PriceListScope.Company && l.CompanyId == r.VerifiedCompanyId) kind = (5, PriceSource.Contract);
            else continue;

            var eligible = l.Entries.Where(e => e.VariantId == r.VariantId && e.MinQty >= 1 && e.MinQty <= r.Quantity
                && e.UnitPriceMinor >= 1 && e.UnitPriceMinor <= Max).ToList();
            if (eligible.Count == 0) continue;
            var largest = eligible.Max(e => e.MinQty);
            var entry = eligible.Where(e => e.MinQty == largest).OrderBy(e => e.UnitPriceMinor).ThenBy(e => e.Id).First();
            var price = kind.Rank == 3 ? OnBasis(entry.UnitPriceMinor, r.Mode, vatBp) : entry.UnitPriceMinor;
            cands.Add((price, kind.Rank, kind.Source, l.ApprovedAtUtc!.Value, l.Id, entry.Id));
        }

        var best = cands.OrderBy(c => c.Price).ThenByDescending(c => c.Rank).ThenByDescending(c => c.Approved)
            .ThenBy(c => c.ListId).ThenBy(c => c.EntryId).First();
        return (best.Price, best.Source, best.ListId, best.EntryId, cands.Where(c => c.Rank <= 3).Min(c => c.Price));
    }

    // ---- generators ---------------------------------------------------------------------------------------------------

    private static T Pick<T>(Random rng, params T[] options) => options[rng.Next(options.Length)];

    private static Guid NewId(Random rng)
    {
        var bytes = new byte[16];
        rng.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static long RandomPrice(Random rng, bool extreme)
    {
        if (extreme && rng.Next(3) == 0) return Pick(rng, 1L, 2L, Max - 1, Max, Max + 1, 0L, -1L, long.MaxValue, long.MinValue);
        if (rng.Next(20) == 0) return Pick(rng, 0L, -1L, Max + 1, long.MaxValue);
        return rng.Next(2) == 0 ? Pick(rng, TiePrices) : rng.NextInt64(1, 2_000_000);
    }

    private static int RandomMinQty(Random rng, bool extreme) => extreme && rng.Next(5) == 0
        ? Pick(rng, int.MinValue, -1, 0, 10_001, int.MaxValue)
        : Pick(rng, 1, 1, 2, 5, 10, 50, 100, 1_000, 10_000, rng.Next(1, 10_001));

    private static (PriceResolutionRequest Request, int VatBp) RandomRequest(Random rng, bool extreme = false, bool monotoneBreaks = false)
    {
        var company = Pick<Guid?>(rng, null, A, B);
        var mode = company is not null || rng.Next(2) == 0 ? TaxMode.Exclusive : TaxMode.Inclusive;
        var vatBp = Pick(rng, 0, 500, 750, 1_000, 1_500, 5_000, rng.Next(0, 5_001));

        var listGross = extreme && rng.Next(2) == 0 ? Pick(rng, 1L, 2L, Max) : rng.Next(2) == 0 ? Pick(rng, TiePrices) : rng.NextInt64(1, 2_000_000);
        long? saleGross = rng.Next(2) == 0 ? null : rng.Next(2) == 0 ? Pick(rng, TiePrices) : rng.NextInt64(1, 2_000_000);
        var saleStart = T0 + Pick(rng, Offsets);
        var catalogue = new PricingInput(Money.FromMinorUnits(listGross), null,
            saleGross is null ? null : Money.FromMinorUnits(saleGross.Value), saleStart, saleStart + Pick(rng, Offsets).Duration(), vatBp / 10_000m);

        var lists = new List<PriceListInput>();
        for (var i = rng.Next(0, 7); i > 0; i--)
        {
            var scope = Pick(rng, PriceListScope.General, PriceListScope.Group, PriceListScope.Company, PriceListScope.Company);
            Guid? listCompany = scope switch
            {
                PriceListScope.Company => rng.Next(10) == 0 ? null : Pick(rng, A, A, B),
                _ => rng.Next(10) == 0 ? Pick(rng, A, B) : null,
            };
            // Half the instants are boundary offsets, half lie well in the past / future, so most lists have started.
            var approvedAt = rng.Next(10) == 0 ? (DateTime?)null : T0 + (rng.Next(2) == 0 ? Pick(rng, Offsets) : Pick(rng, Past));
            var validFrom = T0 + (rng.Next(2) == 0 ? Pick(rng, Offsets) : Pick(rng, Past));
            var validTo = rng.Next(3) switch
            {
                0 => T0 + Pick(rng, Offsets),
                1 => validFrom + Pick(rng, Offsets).Duration(),
                _ => T0 + TimeSpan.FromDays(rng.Next(1, 30)),
            };
            var status = Pick(rng, PriceListStatus.Approved, PriceListStatus.Approved, PriceListStatus.Approved,
                PriceListStatus.Withdrawn, PriceListStatus.Draft, PriceListStatus.PendingApproval);
            DateTime? withdrawnAt = status == PriceListStatus.Withdrawn && rng.Next(5) != 0 ? T0 + Pick(rng, Offsets) : null;

            lists.Add(new PriceListInput(
                NewId(rng), SeriesFor(rng, scope), rng.Next(1, 4), scope, listCompany, $"CTR-{i}",
                rng.Next(10) == 0 ? Currency.USD : Currency.BDT, status, validFrom, validTo, approvedAt, withdrawnAt,
                monotoneBreaks ? MonotoneEntries(rng) : RandomEntries(rng, extreme)));
        }

        var request = new PriceResolutionRequest(V, Pick(rng, 1, 2, 5, 9, 10, 49, 50, 100, 999, 1_000, 10_000, rng.Next(1, 10_001)),
            catalogue, mode, company, Pick<Guid?>(rng, null, G1, G1, G2), T0, lists);
        return (request, vatBp);
    }

    private static List<PriceListEntryInput> RandomEntries(Random rng, bool extreme)
    {
        var entries = new List<PriceListEntryInput>();
        for (var i = rng.Next(0, 6); i > 0; i--)
            entries.Add(new(NewId(rng), rng.Next(5) == 0 ? W : V, RandomMinQty(rng, extreme), RandomPrice(rng, extreme)));
        return entries;
    }

    /// <summary>Breaks that never get dearer as minQty grows (what D2.3 enforces), plus noise for another variant.</summary>
    private static List<PriceListEntryInput> MonotoneEntries(Random rng)
    {
        var minQtys = Enumerable.Range(0, rng.Next(0, 5)).Select(_ => RandomMinQty(rng, false)).Distinct().Order().ToList();
        var price = rng.Next(2) == 0 ? Pick(rng, TiePrices) : rng.NextInt64(1, 2_000_000);
        var entries = new List<PriceListEntryInput>();
        foreach (var minQty in minQtys)
        {
            entries.Add(new(NewId(rng), V, minQty, price));
            price = Math.Max(1, price - rng.NextInt64(0, price / 5 + 1));
        }

        if (rng.Next(3) == 0) entries.Add(new(NewId(rng), W, 1, rng.NextInt64(1, 2_000_000)));
        return entries;
    }

    private static List<T> Shuffle<T>(Random rng, IEnumerable<T> items)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
