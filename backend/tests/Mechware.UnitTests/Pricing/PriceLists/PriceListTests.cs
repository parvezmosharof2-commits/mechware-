using System.Reflection;
using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Pricing.PriceLists;
using static Mechware.UnitTests.Pricing.PriceLists.PriceListBuilders;

namespace Mechware.UnitTests.Pricing.PriceLists;

/// <summary>D2.3: the <see cref="PriceList"/> aggregate (ADR-0008 §1, §3; threat model §৮ PL1, PL3–PL5, PL8–PL10).</summary>
public class PriceListTests
{
    private static readonly Guid Outsider = Guid.Parse("00000000-0000-0000-0000-0000000000a9");

    // ---- exit tests: four-eyes (PL1) ---------------------------------------------------------------------------------

    [Fact]
    public void Approve_BySameUserWhoSubmitted_IsRefused()
    {
        var list = Pending(); // created by Author, submitted by Submitter, who never edited it

        var act = () => Approve(list, approver: Submitter);

        act.Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_FOUR_EYES");
        list.Status.Should().Be(PriceListStatus.PendingApproval);
        list.ApprovedBy.Should().BeNull();
        Approve(list, approver: Approver);
        list.Status.Should().Be(PriceListStatus.Approved, "a different person may approve");
    }

    [Fact]
    public void Approve_ByTheCreator_IsRefused()
    {
        var list = Pending();
        FluentActions.Invoking(() => Approve(list, approver: Author))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_FOUR_EYES");
    }

    [Theory]
    [InlineData("header")]
    [InlineData("add")]
    [InlineData("change")]
    [InlineData("remove")]
    public void Approve_ByAUserWhoEditedTheDraft_IsRefused(string edit)
    {
        var list = Draft();
        var first = list.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        var second = list.AddEntry(Guid.NewGuid(), V2, 1, 100_000, null, Author, Now);
        switch (edit)
        {
            case "header": list.ChangeHeader("Acme 2027 rev", "CTR-2027-01", Now.AddDays(1), Now.AddDays(91), Editor, Now); break;
            case "add": list.AddEntry(Guid.NewGuid(), V1, 10, 90_000, null, Editor, Now); break;
            case "change": list.ChangeEntry(first.Id, 99_000, null, Editor, Now); break;
            default: list.RemoveEntry(second.Id, Editor, Now); break;
        }

        list.Submit(Submitter, Floors(list), false, Now);

        list.Contributors.Select(c => c.UserId).Should().BeEquivalentTo([Author, Editor]);
        FluentActions.Invoking(() => Approve(list, approver: Editor))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_FOUR_EYES");
        Approve(list, approver: Approver);
        list.ApprovedBy.Should().Be(Approver);
    }

    // ---- exit tests: frozen once submitted (PL3, PL4) ----------------------------------------------------------------

    [Fact]
    public void EditingAPendingList_IsRefused()
    {
        var list = Pending();
        var hash = list.ContentHash;

        AssertEveryEditIsRefused(list);
        FluentActions.Invoking(() => list.Submit(Submitter, Floors(list), false, Now))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_NOT_EDITABLE");
        list.ContentHash.Should().Be(hash).And.Be(list.ComputeContentHash());
        list.Contributors.Select(c => c.UserId).Should().Equal(Author);
    }

    [Fact]
    public void EditingAnApprovedList_IsRefused()
    {
        var list = Approved();
        AssertEveryEditIsRefused(list);
        list.ComputeContentHash().Should().Be(list.ContentHash);

        list.Withdraw(Approver, "Wrong prices for Acme", Now.AddHours(1));
        AssertEveryEditIsRefused(list);
    }

    private static void AssertEveryEditIsRefused(PriceList list)
    {
        var entry = list.Entries.First();
        var edits = new Action[]
        {
            () => list.ChangeHeader("New name", "CTR-X", Now.AddDays(2), Now.AddDays(20), Editor, Now),
            () => list.AddEntry(Guid.NewGuid(), V2, 1, 100_000, null, Editor, Now),
            () => list.ChangeEntry(entry.Id, 1, null, Editor, Now),
            () => list.RemoveEntry(entry.Id, Editor, Now),
        };
        foreach (var edit in edits)
            edit.Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_NOT_EDITABLE");
        entry.UnitPriceMinor.Should().Be(100_000);
        list.Entries.Should().ContainSingle();
        list.Contributors.Select(c => c.UserId).Should().NotContain(Editor);
    }

    [Fact]
    public void ScopeAndCompany_CannotChangeAfterCreation()
    {
        foreach (var name in new[] { nameof(PriceList.Scope), nameof(PriceList.CompanyId), nameof(PriceList.SeriesId), nameof(PriceList.Version) })
            typeof(PriceList).GetProperty(name)!.SetMethod!.IsPublic.Should().BeFalse($"{name} has no public setter");

        var mutators = typeof(PriceList).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(PriceList))
            .SelectMany(m => m.GetParameters())
            .Where(p => p.ParameterType == typeof(PriceListScope) || p.Name!.Contains("company", StringComparison.OrdinalIgnoreCase));
        mutators.Should().BeEmpty("no instance method takes a scope or a company");

        var list = Approved();
        var next = list.Revise(Guid.NewGuid(), Editor, Now.AddHours(1));
        (next.Scope, next.CompanyId, next.SeriesId).Should().Be((PriceListScope.Company, (Guid?)CompanyA, list.SeriesId));

        FluentActions.Invoking(() => PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "x", PriceListScope.Company, null, "CTR", Now, Now.AddDays(1), Author, Now))
            .Should().Throw<ValidationException>("a company list needs a company");
        FluentActions.Invoking(() => PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "x", PriceListScope.General, CompanyA, null, Now, Now.AddDays(1), Author, Now))
            .Should().Throw<ValidationException>("a public list must not have a company");
        FluentActions.Invoking(() => PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "x", (PriceListScope)9, null, null, Now, Now.AddDays(1), Author, Now))
            .Should().Throw<ValidationException>();
    }

    // ---- exit tests: validity (PL5) ----------------------------------------------------------------------------------

    [Fact]
    public void ValidityLongerThanTheMaximum_IsRefused()
    {
        var from = Now.AddDays(1);
        FluentActions.Invoking(() => Draft(from: from, to: from.AddDays(PriceListLimits.MaxValidityDays).AddSeconds(1)))
            .Should().Throw<ValidationException>();
        Draft(from: from, to: from.AddDays(PriceListLimits.MaxValidityDays)).ValidToUtc.Should().Be(from.AddDays(366));

        var list = Draft();
        FluentActions.Invoking(() => list.ChangeHeader("n", "CTR", from, from.AddDays(367), Editor, Now)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => list.ChangeHeader("n", "CTR", from, from, Editor, Now)).Should().Throw<ValidationException>("validTo > validFrom");
        FluentActions.Invoking(() => list.ChangeHeader("n", "CTR", from, from.AddDays(-1), Editor, Now)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => list.ChangeHeader("n", "CTR", from.ToLocalTime(), from.AddDays(2), Editor, Now))
            .Should().Throw<ValidationException>("local times are refused");
        list.Contributors.Select(c => c.UserId).Should().Equal([Author], "a refused edit records nobody");
    }

    [Fact]
    public void ApprovingAListWhoseWindowHasPassed_IsRefused()
    {
        var list = Draft(from: Now.AddDays(-10), to: Now.AddDays(1));
        list.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        list.Submit(Submitter, Floors(list), false, Now);

        foreach (var at in new[] { Now.AddDays(2), Now.AddDays(1), Now.AddDays(1).AddSeconds(-30) }) // the last: start = at + 60 s ≥ validTo
            FluentActions.Invoking(() => Approve(list, at: at))
                .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_WINDOW_PASSED");
        list.Status.Should().Be(PriceListStatus.PendingApproval);

        Approve(list, at: Now.AddDays(1).AddSeconds(-61));
        list.EffectiveFromUtc.Should().Be(Now.AddDays(1).AddSeconds(-1));
    }

    [Fact]
    public void Submit_AfterTheWindowEnded_IsRefused()
    {
        var list = Draft(from: Now.AddDays(-10), to: Now.AddDays(-1));
        list.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        FluentActions.Invoking(() => list.Submit(Submitter, Floors(list), false, Now)).Should().Throw<ValidationException>();
    }

    // ---- exit tests: entries (PL8, PL9) ------------------------------------------------------------------------------

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Entry_WithZeroOrNegativePrice_IsRefused(long price)
    {
        var list = Draft();
        FluentActions.Invoking(() => list.AddEntry(Guid.NewGuid(), V1, 1, price, null, Editor, Now)).Should().Throw<ValidationException>();
        var entry = list.AddEntry(Guid.NewGuid(), V1, 1, 1, null, Author, Now);
        FluentActions.Invoking(() => list.ChangeEntry(entry.Id, price, null, Editor, Now)).Should().Throw<ValidationException>();
        entry.UnitPriceMinor.Should().Be(1);
        list.Contributors.Select(c => c.UserId).Should().Equal(Author);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Entry_WithZeroOrNegativeMinQty_IsRefused(int minQty)
    {
        FluentActions.Invoking(() => Draft().AddEntry(Guid.NewGuid(), V1, minQty, 100, null, Author, Now)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Draft().AddEntry(Guid.NewGuid(), V1, 10_001, 100, null, Author, Now)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Draft(PriceListScope.General).AddEntry(Guid.NewGuid(), V1, 1, 100, null, Author, Now))
            .Should().Throw<ValidationException>("a public price for one unit is the catalogue's job");
        Draft(PriceListScope.General).AddEntry(Guid.NewGuid(), V1, 2, 100, null, Author, Now).MinQty.Should().Be(2);
        Draft().AddEntry(Guid.NewGuid(), V1, 10_000, 100, null, Author, Now).MinQty.Should().Be(10_000);
    }

    [Fact]
    public void Entry_AboveTheMaximumUnitPrice_IsRefused()
    {
        var list = Draft();
        foreach (var price in new[] { PriceListLimits.MaxUnitPriceMinor + 1, long.MaxValue })
            FluentActions.Invoking(() => list.AddEntry(Guid.NewGuid(), V1, 1, price, null, Author, Now)).Should().Throw<ValidationException>();
        list.AddEntry(Guid.NewGuid(), V1, 1, PriceListLimits.MaxUnitPriceMinor, null, Author, Now).UnitPriceMinor
            .Should().Be(PriceListLimits.MaxUnitPriceMinor);
    }

    [Fact]
    public void ListWithMoreThanTheMaximumEntries_IsRefused()
    {
        var list = Draft();
        for (var q = 1; q <= PriceListLimits.MaxEntriesPerList; q++)
            list.AddEntry(Guid.NewGuid(), V1, q, 1_000_000 - q, null, Author, Now);

        FluentActions.Invoking(() => list.AddEntry(Guid.NewGuid(), V2, 1, 100_000, null, Author, Now))
            .Should().Throw<ValidationException>().Which.Errors.Should().ContainSingle(e => e.Field == "entries");
        list.Entries.Should().HaveCount(PriceListLimits.MaxEntriesPerList);
    }

    [Fact]
    public void Entry_DuplicateVariantAndMinQty_IsRefused()
    {
        var list = Draft();
        var id = Guid.NewGuid();
        list.AddEntry(id, V1, 5, 100_000, null, Author, Now);
        FluentActions.Invoking(() => list.AddEntry(Guid.NewGuid(), V1, 5, 90_000, null, Author, Now)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => list.AddEntry(id, V2, 5, 90_000, null, Author, Now)).Should().Throw<ValidationException>("ids are unique");
        list.AddEntry(Guid.NewGuid(), V2, 5, 90_000, null, Author, Now);
        list.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void Breaks_MustNotGetDearerAsQuantityGrows()
    {
        var list = Draft();
        list.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        list.AddEntry(Guid.NewGuid(), V1, 10, 95_000, null, Author, Now);
        var dearer = list.AddEntry(Guid.NewGuid(), V1, 50, 95_001, null, Author, Now);
        list.AddEntry(Guid.NewGuid(), V2, 1, 60_000, null, Author, Now); // another variant does not count
        list.AddEntry(Guid.NewGuid(), V2, 5, 60_000, null, Author, Now);

        FluentActions.Invoking(() => list.Submit(Submitter, Floors(list), false, Now)).Should().Throw<ValidationException>();
        list.Status.Should().Be(PriceListStatus.Draft);

        list.ChangeEntry(dearer.Id, 95_000, null, Author, Now); // equal is not dearer
        list.Submit(Submitter, Floors(list), false, Now);
        list.Status.Should().Be(PriceListStatus.PendingApproval);
    }

    // ---- exit tests: margin floor in the aggregate (PL10) ------------------------------------------------------------

    [Fact]
    public void Submit_WithBelowFloorEntryWithoutReason_IsRefused()
    {
        var list = Draft();
        var entry = list.AddEntry(Guid.NewGuid(), V1, 1, 54_999, null, Author, Now); // floor 55,000

        FluentActions.Invoking(() => list.Submit(Submitter, Floors(list), false, Now))
            .Should().Throw<ValidationException>().Which.Errors.Should().ContainSingle(e => e.Field == "belowFloorReason");
        list.Status.Should().Be(PriceListStatus.Draft);

        list.ChangeEntry(entry.Id, 54_999, "Strategic pilot buyer, 2027", Author, Now);
        list.Submit(Submitter, Floors(list), false, Now);
        list.Status.Should().Be(PriceListStatus.PendingApproval);
    }

    [Fact]
    public void EntryBelowCost_IsRefused_UnlessConfigAllows()
    {
        var list = Draft();
        list.AddEntry(Guid.NewGuid(), V1, 1, Cost - 1, "Clearance agreed with the buyer", Author, Now);

        FluentActions.Invoking(() => list.Submit(Submitter, Floors(list), allowBelowCost: false, Now)).Should().Throw<ValidationException>();
        list.Submit(Submitter, Floors(list), allowBelowCost: true, Now);

        var ids = list.Entries.Select(e => e.Id).ToList();
        FluentActions.Invoking(() => Approve(list, acknowledged: ids, mayApproveBelowFloor: true, allowBelowCost: false))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_BELOW_COST");
        Approve(list, acknowledged: ids, mayApproveBelowFloor: true, allowBelowCost: true);
        list.Status.Should().Be(PriceListStatus.Approved);
        list.Entries.Single().Should().BeEquivalentTo(new { CostMinorAtApproval = (long?)Cost, FloorMinorAtApproval = (long?)55_000, BelowFloorAtApproval = (bool?)true });
    }

    [Fact]
    public void EntryBelowCost_AtApproval_IsRefused_WhenTheCostRoseAfterSubmit()
    {
        var list = Pending(price: 100_000);
        FluentActions.Invoking(() => Approve(list, cost: 100_001, acknowledged: [], mayApproveBelowFloor: true))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_BELOW_COST");
    }

    [Fact]
    public void EntryForAVariantWithoutCost_IsTreatedAsBelowFloor_InTheAggregate()
    {
        var list = Draft();
        var entry = list.AddEntry(Guid.NewGuid(), V1, 1, PriceListLimits.MaxUnitPriceMinor, null, Author, Now);
        FluentActions.Invoking(() => list.Submit(Submitter, Floors(list, cost: null), false, Now)).Should().Throw<ValidationException>();

        list.ChangeEntry(entry.Id, PriceListLimits.MaxUnitPriceMinor, "Cost not entered yet", Author, Now);
        list.Submit(Submitter, Floors(list, cost: null), false, Now);
        FluentActions.Invoking(() => Approve(list, cost: null, acknowledged: [])).Should().Throw<ForbiddenException>();
        Approve(list, cost: null, acknowledged: [entry.Id], mayApproveBelowFloor: true);
        list.Status.Should().Be(PriceListStatus.Approved);
    }

    // ---- approval: below floor needs reason, permission and the exact acknowledgement ---------------------------------

    [Fact]
    public void Approve_BelowFloor_NeedsTheExtraPermissionAndTheExactAcknowledgement()
    {
        var list = Draft();
        var low = list.AddEntry(Guid.NewGuid(), V1, 1, 54_000, "Pilot price", Author, Now);
        var lower = list.AddEntry(Guid.NewGuid(), V1, 10, 53_000, "Pilot price", Author, Now);
        list.AddEntry(Guid.NewGuid(), V2, 1, 80_000, null, Author, Now);
        list.Submit(Submitter, Floors(list), false, Now);
        Floors(list).BelowFloorEntryIds.Should().BeEquivalentTo([low.Id, lower.Id]);

        FluentActions.Invoking(() => Approve(list, acknowledged: [low.Id, lower.Id], mayApproveBelowFloor: false))
            .Should().Throw<ForbiddenException>().Which.Code.Should().Be("BELOW_FLOOR_PERMISSION_REQUIRED");
        foreach (var ack in new[] { Array.Empty<Guid>(), [low.Id], [low.Id, lower.Id, Guid.NewGuid()], [low.Id, list.Entries.Last().Id] })
            FluentActions.Invoking(() => Approve(list, acknowledged: ack, mayApproveBelowFloor: true))
                .Should().Throw<ConflictException>().Which.Code.Should().Be("BELOW_FLOOR_NOT_ACKNOWLEDGED");
        list.Status.Should().Be(PriceListStatus.PendingApproval);

        Approve(list, acknowledged: [lower.Id, low.Id], mayApproveBelowFloor: true);
        list.Status.Should().Be(PriceListStatus.Approved);
    }

    [Fact]
    public void Approve_AcknowledgingEntriesThatAreNotBelowFloor_IsRefused()
    {
        var list = Pending();
        FluentActions.Invoking(() => Approve(list, acknowledged: [list.Entries.Single().Id], mayApproveBelowFloor: true))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("BELOW_FLOOR_NOT_ACKNOWLEDGED");
    }

    [Fact]
    public void Approve_WhenAnEntryFellBelowTheFloorAfterSubmit_WithoutReason_IsRefused()
    {
        var list = Pending(price: 60_000); // floor 55,000 at submit
        var id = list.Entries.Single().Id;
        FluentActions.Invoking(() => Approve(list, cost: 56_000, acknowledged: [id], mayApproveBelowFloor: true)) // floor now 61,600
            .Should().Throw<ConflictException>().Which.Code.Should().Be("BELOW_FLOOR_REASON_REQUIRED");
    }

    // ---- approval: content hash (PL3) ---------------------------------------------------------------------------------

    [Fact]
    public void Approve_WithAStaleContentHash_IsRefused()
    {
        var list = Draft();
        var entry = list.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        list.Submit(Submitter, Floors(list), false, Now);
        var shown = list.ContentHash!;
        list.Reject(Approver, "Price too low for this buyer", Now);
        list.ChangeEntry(entry.Id, 90_000, null, Author, Now);
        list.Submit(Submitter, Floors(list), false, Now);
        list.ContentHash.Should().NotBe(shown);

        foreach (var stale in new[] { shown, shown.ToUpperInvariant(), "", "x" })
            FluentActions.Invoking(() => list.Approve(Approver, false, stale, [], Floors(list), false, Now.AddMinutes(5)))
                .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_CHANGED");
        FluentActions.Invoking(() => list.Approve(Approver, false, null!, [], Floors(list), false, Now.AddMinutes(5)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_CHANGED");

        list.Approve(Approver, false, list.ContentHash!, [], Floors(list), false, Now.AddMinutes(5));
        list.Status.Should().Be(PriceListStatus.Approved);
    }

    [Fact]
    public void FloorEvaluation_ForOtherContentOrAnotherList_IsRefused()
    {
        var list = Draft();
        list.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        var before = Floors(list);
        list.AddEntry(Guid.NewGuid(), V1, 10, 40_000, null, Author, Now); // below floor, evaluated before it existed
        FluentActions.Invoking(() => list.Submit(Submitter, before, false, Now))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_FLOOR_STALE");

        var other = Draft();
        other.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        FluentActions.Invoking(() => list.Submit(Submitter, Floors(other), false, Now))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_FLOOR_STALE");
        FluentActions.Invoking(() => list.Submit(Submitter, null!, false, Now)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ContentHash_IsIndependentOfEntryOrder_AndChangesWithAnyContentChange()
    {
        var (id, series) = (Guid.NewGuid(), Guid.NewGuid());
        var e = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        PriceList Build(bool reversed)
        {
            var l = Draft(id: id, series: series);
            var rows = new (Guid Id, Guid V, int Q, long P, string? R)[] { (e[0], V1, 1, 100_000, null), (e[1], V1, 10, 90_000, "Pilot buyer"), (e[2], V2, 1, 70_000, null), (e[3], V2, 3, 70_000, null) };
            foreach (var r in reversed ? Enumerable.Reverse(rows) : rows) l.AddEntry(r.Id, r.V, r.Q, r.P, r.R, Author, Now);
            return l;
        }

        var hash = Build(false).ComputeContentHash();
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
        Build(true).ComputeContentHash().Should().Be(hash, "entry order does not matter");
        Build(false).ComputeContentHash().Should().Be(hash, "the hash is stable");

        var changes = new Action<PriceList>[]
        {
            l => l.ChangeEntry(e[0], 99_999, null, Author, Now),
            l => l.ChangeEntry(e[1], 90_000, "Pilot buyer!", Author, Now),
            l => l.ChangeEntry(e[1], 90_000, null, Author, Now),
            l => l.RemoveEntry(e[3], Author, Now),
            l => l.AddEntry(Guid.NewGuid(), V2, 5, 60_000, null, Author, Now),
            l => l.ChangeHeader("Acme 2027", "CTR-2027-02", Now.AddDays(1), Now.AddDays(91), Author, Now),
            l => l.ChangeHeader("Acme 2027", "CTR-2027-01", Now.AddDays(1).AddSeconds(1), Now.AddDays(91), Author, Now),
            l => l.ChangeHeader("Acme 2027", "CTR-2027-01", Now.AddDays(1), Now.AddDays(92), Author, Now),
            l => l.ChangeHeader("Acme 2028", "CTR-2027-01", Now.AddDays(1), Now.AddDays(91), Author, Now),
        };
        foreach (var change in changes)
        {
            var l = Build(false);
            change(l);
            l.ComputeContentHash().Should().NotBe(hash);
        }

        var sameContentOtherEntryIds = Draft(id: id, series: series);
        sameContentOtherEntryIds.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        var single = Draft(id: id, series: series);
        single.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        single.ComputeContentHash().Should().NotBe(sameContentOtherEntryIds.ComputeContentHash(), "entry ids are part of what the approver acknowledges");

        var unchanged = Build(false);
        unchanged.ChangeHeader("Acme 2027", "CTR-2027-01", Now.AddDays(1).AddTicks(1234), Now.AddDays(91), Editor, Now);
        unchanged.ComputeContentHash().Should().Be(hash, "the window is kept in whole seconds, so the hash survives the database");
    }

    // ---- approve, reject, withdraw, revise ---------------------------------------------------------------------------

    [Fact]
    public void Approve_StoresTheResolversEffectiveFrom_AndTheAuditValues()
    {
        var backdated = Draft(from: Now.AddDays(-5), to: Now.AddDays(30));
        backdated.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        backdated.Submit(Submitter, Floors(backdated), false, Now);
        var at = Now.AddMinutes(5);
        Approve(backdated, at: at);

        (backdated.Status, backdated.ApprovedBy, backdated.ApprovedAtUtc).Should().Be((PriceListStatus.Approved, (Guid?)Approver, (DateTime?)at));
        backdated.EffectiveFromUtc.Should().Be(PriceResolver.EffectiveFromUtc(backdated.ValidFromUtc, at)).And.Be(at.AddSeconds(60));
        backdated.Entries.Single().Should().BeEquivalentTo(new { CostMinorAtApproval = (long?)Cost, FloorMinorAtApproval = (long?)55_000, BelowFloorAtApproval = (bool?)false });

        var later = Pending();
        Approve(later, at: Now.AddMinutes(5));
        later.EffectiveFromUtc.Should().Be(later.ValidFromUtc, "a future validFrom is kept");

        FluentActions.Invoking(() => Approve(later, approver: Outsider))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_NOT_PENDING");
        FluentActions.Invoking(() => Approve(Draft(), approver: Outsider))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_NOT_PENDING");
    }

    [Fact]
    public void Reject_ReturnsTheListToDraft_WithTheReason()
    {
        var list = Pending();
        FluentActions.Invoking(() => list.Reject(Approver, "no", Now)).Should().Throw<ValidationException>("a reason is required");
        list.Status.Should().Be(PriceListStatus.PendingApproval);

        list.Reject(Approver, "  Prices above the agreed rate  ", Now.AddMinutes(1));
        (list.Status, list.RejectedBy, list.RejectedAtUtc, list.RejectionReason)
            .Should().Be((PriceListStatus.Draft, (Guid?)Approver, (DateTime?)Now.AddMinutes(1), "Prices above the agreed rate"));
        list.SubmittedBy.Should().BeNull();
        list.SubmittedAtUtc.Should().BeNull();
        list.ContentHash.Should().BeNull();

        list.ChangeEntry(list.Entries.Single().Id, 95_000, null, Editor, Now);
        FluentActions.Invoking(() => list.Reject(Approver, "Again, too high", Now)).Should().Throw<ConflictException>();
        FluentActions.Invoking(() => list.Withdraw(Approver, "Not approved yet", Now)).Should().Throw<ConflictException>();
        list.Submit(Submitter, Floors(list), false, Now);
        FluentActions.Invoking(() => Approve(list, approver: Editor)).Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_FOUR_EYES");
    }

    [Fact]
    public void Withdraw_OnlyAnApprovedList_WithAReason_ByOnePerson()
    {
        FluentActions.Invoking(() => Draft().Withdraw(Approver, "Wrong prices", Now)).Should().Throw<ConflictException>();
        FluentActions.Invoking(() => Pending().Withdraw(Approver, "Wrong prices", Now)).Should().Throw<ConflictException>();

        var list = Approved();
        FluentActions.Invoking(() => list.Withdraw(Author, " ", Now)).Should().Throw<ValidationException>();
        list.Withdraw(Author, "Wrong prices for Acme", Now.AddHours(2)); // the author alone may stop a list: it never lowers a price
        (list.Status, list.WithdrawnBy, list.WithdrawnAtUtc, list.WithdrawReason)
            .Should().Be((PriceListStatus.Withdrawn, (Guid?)Author, (DateTime?)Now.AddHours(2), "Wrong prices for Acme"));
        (list.ApprovedBy, list.EffectiveFromUtc).Should().Be(((Guid?)Approver, (DateTime?)list.ValidFromUtc), "approval facts stay for the resolver and the audit");
        FluentActions.Invoking(() => list.Withdraw(Approver, "Wrong prices again", Now)).Should().Throw<ConflictException>();
    }

    [Fact]
    public void Revise_CreatesTheNextVersion_AndClearsContributorsAndApproval()
    {
        var list = Draft();
        list.AddEntry(Guid.NewGuid(), V1, 1, 100_000, null, Author, Now);
        list.AddEntry(Guid.NewGuid(), V1, 10, 54_000, "Pilot volume", Editor, Now);
        list.Submit(Submitter, Floors(list), false, Now);
        Approve(list, acknowledged: [list.Entries.Last().Id], mayApproveBelowFloor: true);

        var next = list.Revise(Guid.NewGuid(), Outsider, Now.AddDays(1));

        (next.SeriesId, next.Version, next.Status, next.CreatedBy).Should().Be((list.SeriesId, 2, PriceListStatus.Draft, Outsider));
        (next.Name, next.ContractRef, next.ValidFromUtc, next.ValidToUtc).Should().Be((list.Name, list.ContractRef, list.ValidFromUtc, list.ValidToUtc));
        next.Contributors.Select(c => c.UserId).Should().Equal(Outsider);
        new object?[] { next.SubmittedBy, next.ContentHash, next.ApprovedBy, next.ApprovedAtUtc, next.EffectiveFromUtc }.Should().OnlyContain(v => v == null);
        next.Entries.Select(e => (e.VariantId, e.MinQty, e.UnitPriceMinor, e.BelowFloorReason))
            .Should().BeEquivalentTo(list.Entries.Select(e => (e.VariantId, e.MinQty, e.UnitPriceMinor, e.BelowFloorReason)));
        next.Entries.Select(e => e.Id).Should().NotIntersectWith(list.Entries.Select(e => e.Id));
        next.Entries.Should().OnlyContain(e => e.PriceListId == next.Id && e.CostMinorAtApproval == null && e.BelowFloorAtApproval == null);

        next.ChangeEntry(next.Entries.First().Id, 98_000, null, Outsider, Now);
        list.Entries.First().UnitPriceMinor.Should().Be(100_000, "the approved version never changes");
        next.Submit(Author, Floors(next), false, Now.AddDays(1));
        FluentActions.Invoking(() => Approve(next, approver: Outsider, acknowledged: [next.Entries.Last().Id], mayApproveBelowFloor: true))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("PRICE_LIST_FOUR_EYES");
        Approve(next, approver: Editor, at: Now.AddDays(1).AddMinutes(1), acknowledged: [next.Entries.Last().Id], mayApproveBelowFloor: true);
        next.Status.Should().Be(PriceListStatus.Approved, "who edited version 1 may approve version 2 they did not touch");

        list.Withdraw(Approver, "Superseded early", Now.AddDays(2));
        list.Revise(Guid.NewGuid(), Outsider, Now.AddDays(2)).Version.Should().Be(2, "a withdrawn version can be revised too");
        FluentActions.Invoking(() => Pending().Revise(Guid.NewGuid(), Outsider, Now)).Should().Throw<ConflictException>();
        FluentActions.Invoking(() => Draft().Revise(Guid.NewGuid(), Outsider, Now)).Should().Throw<ConflictException>();
        FluentActions.Invoking(() => next.Revise(next.Id, Outsider, Now)).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Header_ContractRefIsRequiredForACompanyList_AndForbiddenOtherwise()
    {
        FluentActions.Invoking(() => PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "x", PriceListScope.Company, CompanyA, " ", Now, Now.AddDays(1), Author, Now))
            .Should().Throw<ValidationException>();
        FluentActions.Invoking(() => PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "x", PriceListScope.Company, CompanyA, new string('c', 65), Now, Now.AddDays(1), Author, Now))
            .Should().Throw<ValidationException>();
        FluentActions.Invoking(() => PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "x", PriceListScope.Group, null, "CTR", Now, Now.AddDays(1), Author, Now))
            .Should().Throw<ValidationException>();
        FluentActions.Invoking(() => PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), " ", PriceListScope.Group, null, null, Now, Now.AddDays(1), Author, Now))
            .Should().Throw<ValidationException>();
        var list = Draft();
        (list.Version, list.Status, list.Currency.ToString(), list.ContractRef).Should().Be((1, PriceListStatus.Draft, "BDT", "CTR-2027-01"));
        list.Contributors.Select(c => c.UserId).Should().Equal(Author);
    }
}
