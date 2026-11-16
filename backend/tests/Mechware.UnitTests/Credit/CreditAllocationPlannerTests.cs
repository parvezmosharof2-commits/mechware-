using FluentAssertions;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;

namespace Mechware.UnitTests.Credit;

/// <summary>ADR-0010 §6 and threat model §১২ CS6, CS8, CS9: the pure allocation planner, and its lines through the ledger.</summary>
public class CreditAllocationPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 16);

    private static CreditOpenCharge Open(long receivable, int dueInDays = 0, int confirmedMinutesAgo = 0, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), receivable, Today.AddDays(dueInDays), Now.AddMinutes(-confirmedMinutesAgo));

    private static Company NewCompany(long limitTaka = 10_000_000_000)
    {
        var c = Company.Register(Guid.NewGuid(), "Square Textiles Ltd", "TL-2020-1234", "0012345678901", null, null);
        c.Activate();
        c.SetCredit(CreditTerms.Net30, new Money(limitTaka, Currency.BDT));
        return c;
    }

    [Fact]
    public void AnOverPayment_GoesOnAccount_AndDoesNotRaiseAvailableCredit()
    {
        // CS9: a company with a BDT 1,000 limit owes 600 on two orders and pays 1,000. The 400 extra is kept on account, named
        // after the payment; available credit comes back to the limit (1,000), never to 1,400.
        var company = NewCompany(limitTaka: 1_000);
        var ledger = new CreditLedger(company, []);
        var (older, newer) = (Guid.NewGuid(), Guid.NewGuid());
        ledger.Charge(older, 40_000, Now.AddDays(-20), Now.AddDays(-20));
        ledger.Charge(newer, 20_000, Now.AddDays(-5), Now.AddDays(-5));
        company.CreditAvailable.Should().Be(new Money(400, Currency.BDT));

        var plan = CreditAllocationPlanner.Plan(100_000, [Open(20_000, dueInDays: 25, id: newer), Open(40_000, dueInDays: 10, id: older)]);
        ShouldBe(plan, 40_000, (older, 40_000), (newer, 20_000));

        var payment = Guid.NewGuid();
        var lines = plan.Postings(payment, Today, Guid.NewGuid(), Guid.NewGuid(), "corr-1");
        lines.Select(l => (l.Kind, l.ReceivableDeltaMinor, l.OnAccountDeltaMinor, l.IdempotencyKey)).Should().Equal(
            (CreditLedgerKind.PaymentApplied, -40_000L, 0L, $"verify:{payment:N}:{older:N}"),
            (CreditLedgerKind.PaymentApplied, -20_000L, 0L, $"verify:{payment:N}:{newer:N}"),
            (CreditLedgerKind.PaymentOnAccount, 0L, 40_000L, $"verify:{payment:N}:on_account"));
        lines.Should().OnlyContain(l => l.PaymentId == payment && l.EventId == lines[0].EventId);
        foreach (var line in lines) ledger.Post(line, Now);

        (company.CreditUsedMinor, company.CreditOnAccountMinor).Should().Be((0, 40_000));
        company.CreditAvailable.Should().Be(new Money(1_000, Currency.BDT), "money on account is not credit until staff apply it to an order (§1, §6)");
        foreach (var line in lines) ledger.Post(line, Now).AlreadyApplied.Should().BeTrue("a replayed verification posts nothing twice");
        company.CreditLedgerSeq.Should().Be(5);
    }

    [Fact]
    public void ANamedOrder_AboveWhatItOwes_OrNotOpen_IsRefusedWithItsId()
    {
        var owes = Open(5_000);
        var paid = Open(0);
        var unknown = Guid.NewGuid();   // another company's order looks exactly like an unknown one: not among this company's charges

        var above = FluentActions.Invoking(() => CreditAllocationPlanner.Plan(10_000, [owes, paid], [new(owes.OrderId, 5_001)]))
            .Should().Throw<ConflictException>().Which;
        (above.Code, above.Message.Contains(owes.OrderId.ToString())).Should().Be(("ALLOCATION_EXCEEDS_OUTSTANDING", true));
        foreach (var notOpen in new[] { paid.OrderId, unknown })
        {
            var ex = FluentActions.Invoking(() => CreditAllocationPlanner.Plan(10_000, [owes, paid], [new(owes.OrderId, 100), new(notOpen, 100)]))
                .Should().Throw<ConflictException>().Which;
            (ex.Code, ex.Message.Contains(notOpen.ToString())).Should().Be(("ALLOCATION_ORDER_NOT_OPEN", true));
        }
        ShouldBe(CreditAllocationPlanner.Plan(10_000, [owes, paid], [new(owes.OrderId, 5_000)]), 5_000, (owes.OrderId, 5_000));   // exactly what it owes
    }

    [Fact]
    public void Allocations_NeverExceedThePayment()
    {
        var (a, b) = (Open(9_000), Open(9_000));
        CreditAllocationRequest[][] tooMuch =
        [
            [new(a.OrderId, 600), new(b.OrderId, 401)], [new(a.OrderId, 1_001)], [new(a.OrderId, 0)], [new(a.OrderId, -5)],
            [new(a.OrderId, 300), new(a.OrderId, 300)]
        ];
        foreach (var requests in tooMuch)
            FluentActions.Invoking(() => CreditAllocationPlanner.Plan(1_000, [a, b], requests)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => CreditAllocationPlanner.Plan(0, [a])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => CreditAllocationPlanner.Plan(-1, [a])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => CreditAllocationPlanner.Plan(1_000, [a, a])).Should().Throw<InvalidOperationException>("an order is loaded once");
        FluentActions.Invoking(() => CreditAllocationPlanner.Plan(1_000, [Open(-1)])).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void NoOpenCharges_EverythingGoesOnAccount()
    {
        ShouldBe(CreditAllocationPlanner.Plan(123_456, []), 123_456);
        ShouldBe(CreditAllocationPlanner.Plan(123_456, [Open(0), Open(0)]), 123_456);
    }

    [Fact]
    public void AnExactPayoff_LeavesNothingOnAccount_AndOnePoishaGoesToTheOldestDue()
    {
        var (late, soon) = (Open(7_000, dueInDays: -3), Open(3_000, dueInDays: 10));
        ShouldBe(CreditAllocationPlanner.Plan(10_000, [soon, late]), 0, (late.OrderId, 7_000), (soon.OrderId, 3_000));
        ShouldBe(CreditAllocationPlanner.Plan(1, [soon, late]), 0, (late.OrderId, 1));
        ShouldBe(CreditAllocationPlanner.Plan(7_001, [soon, late]), 0, (late.OrderId, 7_000), (soon.OrderId, 1));
        ShouldBe(CreditAllocationPlanner.Plan(10_001, [soon, late]), 1, (late.OrderId, 7_000), (soon.OrderId, 3_000));
    }

    [Fact]
    public void ManyOrders_AreFilledOldestDueFirst_AndTheLastOneOnlyInPart()
    {
        var charges = Enumerable.Range(0, 200).Select(i => Open(1_000 + i, dueInDays: -i)).ToList();   // the last is the oldest
        var plan = CreditAllocationPlanner.Plan(150_000, charges);
        plan.Allocations.Select(a => a.OrderId).Should().Equal(charges.AsEnumerable().Reverse().Take(plan.Allocations.Count).Select(c => c.OrderId));
        plan.Allocations.SkipLast(1).Should().OnlyContain(a => a.AmountMinor == charges.Single(c => c.OrderId == a.OrderId).ReceivableMinor);
        (plan.Allocations.Sum(a => a.AmountMinor), plan.OnAccountMinor).Should().Be((150_000, 0));
    }

    [Fact]
    public void NamedOrdersFirst_ThenTheOthersOldestDueFirst_ThenOnAccount()
    {
        var (oldest, middle, newest) = (Open(1_000, -30), Open(2_000, -10), Open(4_000, 5));
        // The recorder names the newest order for 1,500 of its 4,000: it gets exactly that, never topped up; the rest goes
        // to the other orders, oldest due first, and what is left is on account.
        ShouldBe(CreditAllocationPlanner.Plan(5_000, [middle, newest, oldest], [new(newest.OrderId, 1_500)]), 500,
            (newest.OrderId, 1_500), (oldest.OrderId, 1_000), (middle.OrderId, 2_000));
        ShouldBe(CreditAllocationPlanner.Plan(2_000, [middle, newest, oldest], [new(newest.OrderId, 1_500)]), 0,
            (newest.OrderId, 1_500), (oldest.OrderId, 500));
        ShouldBe(CreditAllocationPlanner.Plan(3_000, [middle, newest, oldest], [new(middle.OrderId, 2_000), new(oldest.OrderId, 1_000)]), 0,
            (middle.OrderId, 2_000), (oldest.OrderId, 1_000));   // in the order given
    }

    private static void ShouldBe(CreditAllocationPlan plan, long onAccount, params (Guid OrderId, long Amount)[] allocations)
    {
        plan.Allocations.Select(a => (a.OrderId, a.AmountMinor)).Should().Equal(allocations);
        plan.OnAccountMinor.Should().Be(onAccount);
    }

    [Fact]
    public void TieBreaks_DueOn_ThenTheOlderConfirmation_ThenTheSmallerOrderId_WhateverTheInputOrder()
    {
        var low = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var high = Guid.Parse("f0000000-0000-4000-8000-000000000001");
        var ffLast = Guid.Parse("00000000-0000-4000-8000-0000000000ff");   // PostgreSQL uuid order: byte by byte, as written
        var charges = new[]
        {
            Open(100, dueInDays: 0, confirmedMinutesAgo: 5, id: high),
            Open(100, dueInDays: 0, confirmedMinutesAgo: 5, id: low),
            Open(100, dueInDays: 0, confirmedMinutesAgo: 5, id: ffLast),
            Open(100, dueInDays: 0, confirmedMinutesAgo: 60),               // confirmed earlier: first among the same due date
            Open(100, dueInDays: -1, confirmedMinutesAgo: 0),               // due earlier: first of all
        };
        var expected = new[] { charges[4].OrderId, charges[3].OrderId, low, ffLast, high };

        var rng = new Random(7);
        for (var i = 0; i < 20; i++)
            CreditAllocationPlanner.Plan(500, charges.OrderBy(_ => rng.Next()).ToList()).Allocations.Select(a => a.OrderId)
                .Should().Equal(expected, "the order of the loaded rows never matters");
    }

    [Fact]
    public void AllocationPlanner_Property_SumsMatchExactly_AndNoOrderGoesNegative()
    {
        // 2,000 seeded rounds: random open charges (ties on due date and confirmation on purpose, some paid off), random
        // payments (exact, short, over), random named orders (valid, above what is owed, paid off, unknown, repeated, too much).
        // An independent brute-force oracle (selection order, one poisha at a time) says what must happen; the planner must
        // refuse with the same code or return the same plan. Every fifth round is scaled to crore-sized amounts, where the
        // oracle runs on the small numbers. Each plan is then posted through the real ledger: no order goes below zero, the
        // company owes exactly the applied amount less, and on account holds exactly the rest.
        for (var seed = 0; seed < 2_000; seed++)
        {
            var rng = new Random(seed);
            long scale = seed % 5 == 4 ? 10_000_019 : 1;
            var dueDays = new[] { -40, -10, -10, 0, 15 };
            var charges = Enumerable.Range(0, rng.Next(0, 9))
                .Select(_ => Open(rng.Next(4) == 0 ? 0 : rng.Next(1, 2_000), dueDays[rng.Next(dueDays.Length)], rng.Next(3) * 30)).ToList();
            var owed = charges.Sum(c => c.ReceivableMinor);
            var amount = (long)rng.Next(1, (int)owed + 2_000);
            var requests = new List<CreditAllocationRequest>();
            for (var i = rng.Next(0, 4); i > 0; i--)
            {
                var pick = charges.Count > 0 && rng.Next(6) > 0 ? charges[rng.Next(charges.Count)] : null;
                var id = pick?.OrderId ?? Guid.NewGuid();
                var x = rng.Next(5) switch
                {
                    0 when pick is not null => pick.ReceivableMinor + 1,                     // above what the order owes
                    1 => rng.Next(1, (int)amount + 1),
                    _ => pick is { ReceivableMinor: > 0 } ? rng.Next(1, (int)Math.Min(pick.ReceivableMinor, amount) + 1) : 1
                };
                requests.Add(new(id, x));
            }

            var expected = Oracle(amount, charges, requests);
            CreditAllocationPlan? plan = null;
            string outcome;
            try
            {
                plan = CreditAllocationPlanner.Plan(amount * scale, charges.Select(c => c with { ReceivableMinor = c.ReceivableMinor * scale }).OrderBy(_ => rng.Next()),
                    requests.Select(r => r with { AmountMinor = r.AmountMinor * scale }).ToList());
                outcome = "plan";
            }
            catch (DomainException ex) { outcome = ex.Code; }
            outcome.Should().Be(expected.Refusal ?? "plan", $"seed {seed}");
            if (plan is null) continue;

            plan.Allocations.Select(a => (a.OrderId, a.AmountMinor)).Should().Equal(expected.Allocations.Select(a => (a.OrderId, a.Amount * scale)), $"seed {seed}");
            (plan.Allocations.Sum(a => a.AmountMinor) + plan.OnAccountMinor).Should().Be(amount * scale, $"seed {seed}: Σ allocations + on account = the payment");
            plan.OnAccountMinor.Should().BeGreaterThanOrEqualTo(0);
            plan.Allocations.Select(a => a.OrderId).Should().OnlyHaveUniqueItems();
            plan.Allocations.Should().OnlyContain(a => a.AmountMinor > 0 && a.AmountMinor <= charges.Single(c => c.OrderId == a.OrderId).ReceivableMinor * scale);

            var company = NewCompany();
            var ledger = new CreditLedger(company, []);
            foreach (var c in charges.Where(c => c.ReceivableMinor > 0)) ledger.Charge(c.OrderId, c.ReceivableMinor * scale, Now, Now);
            foreach (var line in plan.Postings(Guid.NewGuid(), Today, Guid.NewGuid(), null, null)) ledger.Post(line, Now);
            foreach (var c in charges.Where(c => c.ReceivableMinor > 0))
            {
                var applied = plan.Allocations.Where(a => a.OrderId == c.OrderId).Sum(a => a.AmountMinor);
                ledger.BalanceOf(c.OrderId).ReceivableMinor.Should().Be(c.ReceivableMinor * scale - applied, $"seed {seed}").And.BeGreaterThanOrEqualTo(0);
            }
            (company.CreditUsedMinor, company.CreditOnAccountMinor).Should().Be((owed * scale - (amount * scale - plan.OnAccountMinor), plan.OnAccountMinor), $"seed {seed}");
        }
    }

    private sealed record Expected(string? Refusal, List<(Guid OrderId, long Amount)> Allocations);

    /// <summary>Written apart from the planner: no sorting library, no Math.Min; the earliest order is found by scanning.</summary>
    private static Expected Oracle(long amount, List<CreditOpenCharge> charges, List<CreditAllocationRequest> requests)
    {
        long total = 0;
        foreach (var r in requests) total += r.AmountMinor;
        var ids = new HashSet<Guid>();
        foreach (var r in requests)
            if (r.AmountMinor < 1 || r.AmountMinor > amount || total > amount || !ids.Add(r.OrderId) || requests.Count > 50)
                return new("VALIDATION_ERROR", []);
        var result = new List<(Guid, long)>();
        foreach (var r in requests)
        {
            var match = charges.FirstOrDefault(c => c.OrderId == r.OrderId && c.ReceivableMinor > 0);
            if (match is null) return new("ALLOCATION_ORDER_NOT_OPEN", []);
            if (r.AmountMinor > match.ReceivableMinor) return new("ALLOCATION_EXCEEDS_OUTSTANDING", []);
            result.Add((r.OrderId, r.AmountMinor));
        }

        var pending = charges.Where(c => !ids.Contains(c.OrderId) && c.ReceivableMinor > 0).ToList();
        var queue = new List<CreditOpenCharge>();
        while (pending.Count > 0)   // selection: take the earliest of what is left, again and again
        {
            var first = pending[0];
            foreach (var c in pending) if (Earlier(c, first)) first = c;
            queue.Add(first);
            pending.Remove(first);
        }
        var got = new long[queue.Count];
        var left = amount - total;
        for (var i = 0; i < queue.Count && left > 0;)   // one poisha at a time, to the earliest order that still owes something
        {
            if (got[i] == queue[i].ReceivableMinor) { i++; continue; }
            got[i]++;
            left--;
        }
        for (var i = 0; i < queue.Count; i++) if (got[i] > 0) result.Add((queue[i].OrderId, got[i]));
        return new(null, result);
    }

    private static bool Earlier(CreditOpenCharge a, CreditOpenCharge b) =>
        a.DueOn != b.DueOn ? a.DueOn < b.DueOn
        : a.ConfirmedAtUtc != b.ConfirmedAtUtc ? a.ConfirmedAtUtc < b.ConfirmedAtUtc
        : string.CompareOrdinal(a.OrderId.ToString("N"), b.OrderId.ToString("N")) < 0;   // PostgreSQL uuid order, written independently
}
