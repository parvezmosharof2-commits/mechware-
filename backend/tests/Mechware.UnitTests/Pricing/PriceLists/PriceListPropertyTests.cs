using System.Numerics;
using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Pricing.PriceLists;
using static Mechware.UnitTests.Pricing.PriceLists.PriceListBuilders;
using static Mechware.UnitTests.Properties.PropertyRunner;

namespace Mechware.UnitTests.Pricing.PriceLists;

/// <summary>
/// D2.3 property tests (threat model §৮ PL1, PL3, PL10). Seeded, so a failure names its seed and iteration. The floor
/// oracle is exact integer arithmetic (<see cref="BigInteger"/>, markup and VAT in hundredths/basis points) and shares no
/// code with <see cref="MarginFloorPolicy"/>; the four-eyes oracle tracks the editors itself.
/// </summary>
public class PriceListPropertyTests
{
    private const int Rounds = 2_000;
    private const long Max = PriceListLimits.MaxUnitPriceMinor;
    private static readonly Guid[] Users = Enumerable.Range(1, 5).Select(i => Guid.Parse($"00000000-0000-0000-0000-0000000001{i:D2}")).ToArray();

    [Fact]
    public void FloorEvaluation_NeverLetsAPriceBelowTheExactFloorPass()
    {
        ForAll(2331, Rounds, (rng, _) =>
        {
            var cost = rng.Next(4) switch { 0 => rng.NextInt64(1, 1_000), 1 => rng.NextInt64(1, 1_000_000), 2 => rng.NextInt64(1, Max / 11), _ => rng.NextInt64(1, Max + 1) };
            var hundredths = rng.Next(3) == 0 ? rng.Next(0, 100_001) : rng.Next(0, 5_001); // markup 0 … 1,000 %, often ≤ 50 %
            var vatBp = rng.Next(3) == 0 ? 0 : rng.Next(0, 5_001);
            var general = rng.Next(2) == 0;

            // Aim at the exact boundary: the exact floor (net), times 1 + VAT for a General (gross) entry, plus a small offset.
            var exactNet = new BigInteger(cost) * (10_000 + hundredths);
            var target = general ? exactNet * (10_000 + vatBp) / 100_000_000 : exactNet / 10_000;
            var price = (long)BigInteger.Clamp(target + rng.Next(-3, 4) + (rng.Next(5) == 0 ? rng.NextInt64(-1_000_000, 1_000_000) : 0), 1, Max);

            var list = Draft(general ? PriceListScope.General : PriceListScope.Company);
            list.AddEntry(Guid.NewGuid(), V1, general ? 2 : 1, price, null, Author, Now);
            var facts = new VariantCostFacts(V1, cost, null, [], vatBp / 10_000m);
            var rows = new[] { new MarginFloorRow(MarginFloorScope.Global, null, null, hundredths / 100m) };
            var e = MarginFloorPolicy.Evaluate(list, [facts], rows).Entries.Single();

            // Oracle: the net price (General: floor(gross / (1 + VAT)), no 1-poisha minimum) against ceil(cost × (1 + m)).
            var net = general ? new BigInteger(price) * 10_000 / (10_000 + vatBp) : price;
            var floor = (exactNet + 9_999) / 10_000;
            e.BelowFloor.Should().Be(net < floor, $"cost {cost}, markup {hundredths / 100m} %, VAT {vatBp} bp, price {price}, general {general}");
            e.BelowCost.Should().Be(net < cost);
            e.FloorMinor.Should().Be((long)floor);
            if (!e.BelowFloor)
            {
                // Never passes below the exact floor: net × 100 ≥ cost × (100 + m), and the gross price covers it with VAT.
                (net * 10_000).Should().BeGreaterThanOrEqualTo(exactNet);
                if (general) (new BigInteger(price) * 100_000_000).Should().BeGreaterThanOrEqualTo(exactNet * (10_000 + vatBp));
            }
        });
    }

    [Fact]
    public void Approve_NeverSucceedsForAContributorOrTheSubmitter()
    {
        var refused = 0;
        ForAll(2332, Rounds, (rng, _) =>
        {
            var creator = Pick(rng);
            var contributors = new HashSet<Guid> { creator };
            var list = PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "Random", PriceListScope.Company, CompanyA, "CTR-R",
                Now.AddDays(1), Now.AddDays(30), creator, Now);
            list.AddEntry(Guid.NewGuid(), Guid.NewGuid(), 1, 100_000, null, creator, Now);

            var rounds = rng.Next(1, 3); // sometimes a rejection and a second submit
            Guid submitter = default;
            for (var r = 0; r < rounds; r++)
            {
                for (var edits = rng.Next(0, 4); edits > 0; edits--)
                {
                    var editor = Pick(rng);
                    contributors.Add(editor);
                    switch (rng.Next(3))
                    {
                        case 0: list.AddEntry(Guid.NewGuid(), Guid.NewGuid(), 1, rng.NextInt64(60_000, 1_000_000), null, editor, Now); break;
                        case 1: list.ChangeEntry(list.Entries.First().Id, rng.NextInt64(60_000, 1_000_000), null, editor, Now); break;
                        default: list.ChangeHeader($"Random {rng.Next()}", "CTR-R", Now.AddDays(1), Now.AddDays(30), editor, Now); break;
                    }
                }

                submitter = Pick(rng);
                list.Submit(submitter, Floors(list), false, Now);
                if (r < rounds - 1) list.Reject(Pick(rng), "Please check the prices", Now);
            }

            var approver = Pick(rng);
            var act = () => Approve(list, approver: approver);
            if (contributors.Contains(approver) || approver == submitter)
            {
                act.Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_FOUR_EYES");
                list.Status.Should().Be(PriceListStatus.PendingApproval);
                list.ApprovedBy.Should().BeNull();
                refused++;
            }
            else
            {
                act();
                list.ApprovedBy.Should().Be(approver);
            }
        });
        refused.Should().BeInRange(Rounds / 4, Rounds - Rounds / 10, "both outcomes are exercised");
    }

    [Fact]
    public void ContentHash_DoesNotDependOnEntryOrder_ButOnEveryPrice()
    {
        ForAll(2333, Rounds / 4, (rng, _) =>
        {
            var (id, series) = (Guid.NewGuid(), Guid.NewGuid());
            var rows = Enumerable.Range(0, rng.Next(1, 12))
                .Select(i => (Id: Guid.NewGuid(), Variant: Guid.NewGuid(), MinQty: rng.Next(1, 10_001), Price: rng.NextInt64(1, Max + 1)))
                .ToList();
            var a = Draft(id: id, series: series);
            var b = Draft(id: id, series: series);
            foreach (var r in rows) a.AddEntry(r.Id, r.Variant, r.MinQty, r.Price, null, Author, Now);
            foreach (var r in rows.OrderBy(_ => rng.Next())) b.AddEntry(r.Id, r.Variant, r.MinQty, r.Price, null, Author, Now);
            b.ComputeContentHash().Should().Be(a.ComputeContentHash());

            var changed = rows[rng.Next(rows.Count)];
            b.ChangeEntry(changed.Id, changed.Price == Max ? Max - 1 : changed.Price + 1, null, Editor, Now);
            b.ComputeContentHash().Should().NotBe(a.ComputeContentHash());
        });
    }

    private static Guid Pick(Random rng) => Users[rng.Next(Users.Length)];
}
