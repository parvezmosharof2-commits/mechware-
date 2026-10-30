using FluentAssertions;
using ShopVerse.Domain.Approvals;
using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.UnitTests.Approvals;

public class ApprovalTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private static Guid[] Users(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();

    private static ApprovalRule Rule(long min = 0, Guid? cc = null, Guid[]? cats = null, params Guid[] approvers) =>
        new(Guid.NewGuid(), Company, "Rule", min, cc, cats, approvers.Length == 0 ? Users(1) : approvers);

    private static ApprovalRequest Request(ApprovalRule rule, Guid requester, long amount = 100_000) =>
        ApprovalRequest.Create(Guid.NewGuid(), requester, "order", Guid.NewGuid(), amount, null, null, rule);

    // ---------- rules ----------

    [Fact]
    public void Rule_RequiresAChainOfOneToFiveDistinctApprovers()
    {
        var a = Users(6);
        new Action(() => Rule(0, null, null, a[..1])).Should().NotThrow();
        new Action(() => Rule(0, null, null, a[..5])).Should().NotThrow();
        new Action(() => new ApprovalRule(Guid.NewGuid(), Company, "R", 0, null, null, [])).Should().Throw<ValidationException>();
        new Action(() => Rule(0, null, null, a[..6])).Should().Throw<ValidationException>();
        new Action(() => Rule(0, null, null, a[0], a[0])).Should().Throw<ValidationException>();
        new Action(() => new ApprovalRule(Guid.NewGuid(), Company, " ", 0, null, null, a[..1])).Should().Throw<ValidationException>();
        new Action(() => new ApprovalRule(Guid.NewGuid(), Company, "R", -1, null, null, a[..1])).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Matches_ChecksAmountCostCentreAndCategories()
    {
        var cc = Guid.NewGuid();
        var cat = Guid.NewGuid();
        var r = Rule(50_000, cc, [cat]);
        r.Matches(50_000, cc, [cat]).Should().BeTrue("the threshold is inclusive");
        r.Matches(49_999, cc, [cat]).Should().BeFalse();
        r.Matches(60_000, Guid.NewGuid(), [cat]).Should().BeFalse("wrong cost centre");
        r.Matches(60_000, null, [cat]).Should().BeFalse("no cost centre on the order");
        r.Matches(60_000, cc, [Guid.NewGuid()]).Should().BeFalse("no category overlap");
        r.Matches(60_000, cc, []).Should().BeFalse();

        var open = Rule(0);
        open.Matches(1, null, []).Should().BeTrue("a rule without conditions matches everything above its threshold");
    }

    [Fact]
    public void InactiveRule_NeverMatches()
    {
        var r = Rule(0);
        r.Update("R", 0, null, null, r.ApproverUserIds, isActive: false);
        r.Matches(1_000_000, null, []).Should().BeFalse();
    }

    [Fact]
    public void Select_PicksHighestThreshold_ThenMostConditions_Deterministically()
    {
        var cc = Guid.NewGuid();
        var low = Rule(10_000);
        var high = Rule(100_000);
        var highWithCc = Rule(100_000, cc);
        ApprovalRule.Select([low, high, highWithCc], 150_000, cc, []).Should().BeSameAs(highWithCc);
        ApprovalRule.Select([low, high, highWithCc], 150_000, null, []).Should().BeSameAs(high);
        ApprovalRule.Select([low, high, highWithCc], 20_000, cc, []).Should().BeSameAs(low);
        ApprovalRule.Select([low, high], 5_000, null, []).Should().BeNull("no rule matches, so no approval is needed");

        var rules = new[] { low, high, highWithCc };
        for (int i = 0; i < 50; i++)
            ApprovalRule.Select(rules.OrderBy(_ => Random.Shared.Next()), 150_000, cc, []).Should().BeSameAs(highWithCc, "input order must not matter");
    }

    // ---------- requests ----------

    [Fact]
    public void Create_BuildsOrderedSteps_AndSkipsTheRequester()
    {
        var u = Users(4);
        var rule = Rule(0, null, null, u[0], u[1], u[2]);
        var req = Request(rule, requester: u[1]);
        req.Steps.Select(s => s.ApproverUserId).Should().Equal(u[0], u[2]);
        req.Steps.Select(s => s.Position).Should().Equal(0, 1);
        req.Status.Should().Be(ApprovalStatus.Pending);
        req.CurrentStep!.ApproverUserId.Should().Be(u[0]);
        req.RuleName.Should().Be("Rule");
    }

    [Fact]
    public void Create_Fails_WhenTheRequesterIsTheOnlyApprover()
    {
        var u = Users(1);
        new Action(() => Request(Rule(0, null, null, u[0]), u[0]))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("NO_ELIGIBLE_APPROVER");
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Order", true)]
    [InlineData("bad type", false)]
    [InlineData("order-x", false)]
    [InlineData("purchase_request_2", true)]
    public void Create_ValidatesTheSubjectType(string subjectType, bool ok)
    {
        var act = () => ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), subjectType, Guid.NewGuid(), 100, null, null, Rule(0));
        if (ok) act.Should().NotThrow(); else act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void Create_RejectsBadAmountOrSubject()
    {
        var rule = Rule(0);
        new Action(() => ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), "order", Guid.NewGuid(), 0, null, null, rule)).Should().Throw<ValidationException>();
        new Action(() => ApprovalRequest.Create(Guid.NewGuid(), Guid.NewGuid(), "order", Guid.Empty, 5, null, null, rule)).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Chain_ApprovesOnlyWhenEveryStepApproved_InOrder()
    {
        var u = Users(4);
        var req = Request(Rule(0, null, null, u[0], u[1]), requester: u[3]);

        req.Invoking(r => r.Decide(u[1], true, null, Now)).Should().Throw<ForbiddenException>().Which.Code.Should().Be("NOT_CURRENT_APPROVER");
        req.Decide(u[0], true, null, Now);
        req.Status.Should().Be(ApprovalStatus.Pending);
        req.CurrentStep!.ApproverUserId.Should().Be(u[1]);
        req.Invoking(r => r.Decide(u[0], true, null, Now)).Should().Throw<ForbiddenException>("the first approver already decided");
        req.Decide(u[1], true, "ok", Now);
        req.Status.Should().Be(ApprovalStatus.Approved);
        req.CurrentStep.Should().BeNull();
    }

    [Fact]
    public void Rejection_NeedsAComment_AndEndsTheRequest()
    {
        var u = Users(3);
        var req = Request(Rule(0, null, null, u[0], u[1]), requester: u[2]);
        req.Invoking(r => r.Decide(u[0], false, "  ", Now)).Should().Throw<ValidationException>();
        req.Status.Should().Be(ApprovalStatus.Pending);
        req.Decide(u[0], false, "Over budget", Now);
        req.Status.Should().Be(ApprovalStatus.Rejected);
        req.Steps[0].Comment.Should().Be("Over budget");
        req.Steps[0].DecidedAtUtc.Should().Be(Now);
        req.Invoking(r => r.Decide(u[1], true, null, Now)).Should().Throw<ConflictException>().Which.Code.Should().Be("APPROVAL_NOT_PENDING");
    }

    [Fact]
    public void Comment_IsLimited()
    {
        var u = Users(2);
        var req = Request(Rule(0, null, null, u[0]), requester: u[1]);
        req.Invoking(r => r.Decide(u[0], true, new string('x', ApprovalRequest.MaxCommentLength + 1), Now)).Should().Throw<ValidationException>();
        req.Status.Should().Be(ApprovalStatus.Pending);
    }

    [Fact]
    public void Requester_CanNeverDecide_EvenIfTheyAreInTheRuleChain()
    {
        var u = Users(2);
        var req = Request(Rule(0, null, null, u[0], u[1]), requester: u[0]);
        req.Invoking(r => r.Decide(u[0], true, null, Now)).Should().Throw<ForbiddenException>().Which.Code.Should().Be("SELF_APPROVAL");
        req.Steps.Should().ContainSingle(s => s.ApproverUserId == u[1]);
    }

    [Fact]
    public void Cancel_IsRequesterOnly_AndOnlyWhilePending()
    {
        var u = Users(3);
        var req = Request(Rule(0, null, null, u[0]), requester: u[1]);
        req.Invoking(r => r.Cancel(u[0], Now)).Should().Throw<ForbiddenException>().Which.Code.Should().Be("NOT_REQUESTER");
        req.Cancel(u[1], Now);
        req.Status.Should().Be(ApprovalStatus.Cancelled);
        req.Invoking(r => r.Cancel(u[1], Now)).Should().Throw<ConflictException>();
        req.Invoking(r => r.Decide(u[0], true, null, Now)).Should().Throw<ConflictException>();
    }

    [Fact]
    public void RandomActions_NeverBreakTheStateMachine()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed);
            var u = Users(5);
            var chain = u.Take(rng.Next(1, 4)).ToArray();
            var requester = rng.Next(3) == 0 ? chain[0] : u[4];
            ApprovalRequest req;
            try { req = Request(Rule(0, null, null, chain), requester); } catch (ConflictException) { continue; }

            for (int i = 0; i < 40; i++)
            {
                var before = req.Status;
                var decidedBefore = req.Steps.Count(s => s.Decision != StepDecision.Pending);
                try
                {
                    switch (rng.Next(3))
                    {
                        case 0: req.Decide(u[rng.Next(5)], rng.Next(2) == 0, rng.Next(2) == 0 ? "c" : null, Now); break;
                        case 1: req.Cancel(u[rng.Next(5)], Now); break;
                        default: req.Decide(req.CurrentStep?.ApproverUserId ?? u[0], true, null, Now); break;
                    }
                }
                catch (DomainException) { /* refused moves must not change state */ }

                if (before != ApprovalStatus.Pending)
                    req.Status.Should().Be(before, $"a finished request never changes (seed {seed})");
                req.Steps.Count(s => s.Decision != StepDecision.Pending).Should().BeGreaterThanOrEqualTo(decidedBefore, $"decisions are never undone (seed {seed})");
                if (req.Status == ApprovalStatus.Approved) req.Steps.Should().OnlyContain(s => s.Decision == StepDecision.Approved);
                if (req.Status == ApprovalStatus.Rejected) req.Steps.Should().Contain(s => s.Decision == StepDecision.Rejected);
                req.Steps.Should().NotContain(s => s.ApproverUserId == requester, $"the requester is never in the chain (seed {seed})");
            }
        }
    }
}

public class ApprovalEligibilityTests
{
    [Fact]
    public void Create_SkipsApproversWhoAreNoLongerEligible()
    {
        var u = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var rule = new ApprovalRule(Guid.NewGuid(), Guid.NewGuid(), "R", 0, null, null, [u[0], u[1], u[2]]);
        var req = ApprovalRequest.Create(Guid.NewGuid(), u[3], "order", Guid.NewGuid(), 100, null, null, rule, new HashSet<Guid> { u[0], u[2] });
        req.Steps.Select(s => s.ApproverUserId).Should().Equal(u[0], u[2]);

        new Action(() => ApprovalRequest.Create(Guid.NewGuid(), u[3], "order", Guid.NewGuid(), 100, null, null, rule, new HashSet<Guid>()))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("NO_ELIGIBLE_APPROVER");
    }
}
