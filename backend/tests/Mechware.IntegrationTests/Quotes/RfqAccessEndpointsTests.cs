using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Companies;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.RfqApiKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.5 over HTTP (threat model QT1, QT10): the real host, real sign-ins, real roles. Its own class, so its own host and login budget.
/// The matrix is the point: another company's Admin, a person with no company, platform Support staff (with a real authenticator code),
/// an anonymous caller, a Pending, Rejected or removed member, and a Requester reaching for a colleague's RFQ all get the 404 of a
/// company or RFQ that does not exist, with one body, on every route.
/// </summary>
public sealed class RfqAccessEndpointsTests(RfqApiFactory factory) : IClassFixture<RfqApiFactory>
{
    private readonly RfqApiKit _kit = new(factory);

    private static object Product(Guid variantId) => new { source = "product", variantId, quantity = 2, message = "Please quote" };

    private async Task<Guid> AskAsync(Person who, Guid company, Guid variantId)
    {
        var created = await DataAsync(await PostAsync(who.Client, company, Product(variantId), Key()), HttpStatusCode.Created);
        return created.GetProperty("rfq").GetProperty("id").GetGuid();
    }

    [PostgresFact]
    public async Task Idor_RfqsOfAnotherCompany_Are404()
    {
        var (ownerA, buyerA, requesterA, requester2A, approverA) = (await _kit.PersonAsync(), await _kit.PersonAsync(), await _kit.PersonAsync(), await _kit.PersonAsync(), await _kit.PersonAsync());
        var (ownerB, nobody, support) = (await _kit.PersonAsync(), await _kit.PersonAsync(), await _kit.PersonAsync(Permissions.Roles.Support));
        var companyA = await _kit.CompanyAsync(ownerA.Id, "Rfq Access A Ltd", true, (buyerA.Id, CompanyRoles.Buyer), (requesterA.Id, CompanyRoles.Requester),
            (requester2A.Id, CompanyRoles.Requester), (approverA.Id, CompanyRoles.Approver));
        var companyB = await _kit.CompanyAsync(ownerB.Id, "Rfq Access B Ltd");
        var variant = await _kit.VariantAsync("RFQA" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant());
        var rfqA = await AskAsync(requesterA, companyA, variant.VariantId);
        var rfqB = await AskAsync(ownerB, companyB, variant.VariantId);

        // Allowed: the people who may see it, each one only in their own company.
        foreach (var who in new[] { ownerA, buyerA, requesterA })
        {
            var ok = await who.Client.GetAsync(Url(companyA, "/" + rfqA));
            await DataAsync(ok);
            ShouldBePrivate(ok);
        }

        // Not allowed: the same 404 as for a company and an RFQ that do not exist, on every route.
        var baseline = await NotFoundShapeAsync(await ownerA.Client.GetAsync(Url(Guid.NewGuid())), "unknown company");
        (await NotFoundShapeAsync(await ownerA.Client.GetAsync(Url(companyA, "/" + Guid.NewGuid())), "unknown rfq")).Should().Be(baseline);
        var denied = new (Person Who, string Label)[] { (ownerB, "the Admin of ANOTHER company"), (nobody, "a person with no company"), (support, "platform Support staff") };
        foreach (var (who, label) in denied)
        {
            var routes = new (Func<Task<HttpResponseMessage>> Call, string Route)[]
            {
                (() => who.Client.GetAsync(Url(companyA)), "list"),
                (() => who.Client.GetAsync(Url(companyA, "/" + rfqA)), "detail"),
                (() => who.Client.PostAsync(Url(companyA, $"/{rfqA}/withdraw"), null), "withdraw"),
                (() => PostAsync(who.Client, companyA, Product(variant.VariantId), Key()), "create"),
            };
            foreach (var (call, route) in routes)
                (await NotFoundShapeAsync(await call(), $"{label}: {route}")).Should().Be(baseline, $"{label} on {route}");
        }

        // An id of another company's RFQ never works under one's own company, nor under the other company.
        (await NotFoundShapeAsync(await ownerA.Client.GetAsync(Url(companyA, "/" + rfqB)), "rfq of B under A")).Should().Be(baseline);
        (await NotFoundShapeAsync(await ownerA.Client.GetAsync(Url(companyB, "/" + rfqB)), "rfq of B under B")).Should().Be(baseline);
        (await NotFoundShapeAsync(await ownerB.Client.GetAsync(Url(companyB, "/" + rfqA)), "rfq of A under B")).Should().Be(baseline);
        (await NotFoundShapeAsync(await ownerA.Client.PostAsync(Url(companyA, $"/{rfqB}/withdraw"), null), "withdraw rfq of B")).Should().Be(baseline);

        // Nothing of the refused calls changed anything.
        await using (var db = _kit.Db())
        {
            (await db.Rfqs.AsNoTracking().SingleAsync(r => r.Id == rfqB)).Status.Should().Be(RfqStatus.Submitted);
            (await db.Rfqs.AsNoTracking().SingleAsync(r => r.Id == rfqA)).Status.Should().Be(RfqStatus.Submitted);
            (await db.Rfqs.CountAsync(r => r.CompanyId == companyA)).Should().Be(1, "none of the refused creates stored anything");
        }

        // Anonymous: 401 on every route, never the data.
        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync(Url(companyA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(Url(companyA, "/" + rfqA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync(Url(companyA, $"/{rfqA}/withdraw"), null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(anonymous, companyA, Product(variant.VariantId), Key())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // A malformed company id never reaches the service.
        (await ownerA.Client.GetAsync("/api/v1/companies/not-a-guid/rfqs")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task RfqRoleMatrix_RequesterSeesOnlyOwn_BuyerAndAdminSeeAll_AndWithdrawalFollowsTheSameRule()
    {
        var (owner, buyer, requester, requester2, approver) = (await _kit.PersonAsync(), await _kit.PersonAsync(), await _kit.PersonAsync(), await _kit.PersonAsync(), await _kit.PersonAsync());
        var company = await _kit.CompanyAsync(owner.Id, "Rfq Roles Ltd", true, (buyer.Id, CompanyRoles.Buyer), (requester.Id, CompanyRoles.Requester),
            (requester2.Id, CompanyRoles.Requester), (approver.Id, CompanyRoles.Approver));
        var variant = await _kit.VariantAsync(null);
        var byRequester = await AskAsync(requester, company, variant.VariantId);
        var byRequester2 = await AskAsync(requester2, company, variant.VariantId);
        var byBuyer = await AskAsync(buyer, company, variant.VariantId);

        async Task<Guid[]> ListedAsync(Person who)
        {
            var response = await who.Client.GetAsync(Url(company));
            ShouldBePrivate(response);
            return [.. (await DataAsync(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
        }

        (await ListedAsync(owner)).Should().BeEquivalentTo([byRequester, byRequester2, byBuyer]);
        (await ListedAsync(buyer)).Should().BeEquivalentTo([byRequester, byRequester2, byBuyer]);
        (await ListedAsync(requester)).Should().Equal(byRequester);
        (await ListedAsync(requester2)).Should().Equal(byRequester2);
        (await ListedAsync(approver)).Should().BeEmpty("an Approver is not in Quotes:VisibleTo and asked for nothing");

        var baseline = await NotFoundShapeAsync(await owner.Client.GetAsync(Url(Guid.NewGuid())), "unknown company");
        (await NotFoundShapeAsync(await requester.Client.GetAsync(Url(company, "/" + byRequester2)), "a colleague's rfq")).Should().Be(baseline);
        (await NotFoundShapeAsync(await approver.Client.GetAsync(Url(company, "/" + byRequester)), "an Approver reading a Requester's rfq")).Should().Be(baseline);
        (await NotFoundShapeAsync(await requester.Client.PostAsync(Url(company, $"/{byRequester2}/withdraw"), null), "withdrawing a colleague's rfq")).Should().Be(baseline);

        // The requester withdraws their own; the Buyer withdraws a colleague's; both then read as withdrawn.
        var own = await DataAsync(await requester.Client.PostAsync(Url(company, $"/{byRequester}/withdraw"), null));
        own.GetProperty("rfq").GetProperty("status").GetString().Should().Be("withdrawn");
        var others = await DataAsync(await buyer.Client.PostAsync(Url(company, $"/{byRequester2}/withdraw"), null));
        others.GetProperty("rfq").GetProperty("status").GetString().Should().Be("withdrawn");

        // Filter by status; a bad status is a 400, not a list.
        (await DataAsync(await owner.Client.GetAsync(Url(company) + "?status=withdrawn"))).GetProperty("total").GetInt32().Should().Be(2);
        (await DataAsync(await owner.Client.GetAsync(Url(company) + "?status=submitted"))).GetProperty("total").GetInt32().Should().Be(1);
        (await owner.Client.GetAsync(Url(company) + "?status=bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.Client.GetAsync(Url(company) + "?status=2")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task AnUnverifiedCompany_CannotRequestAQuote_NonActiveCompaniesAreInvisibleToReads_AndARemovedMemberIsAStranger()
    {
        var (pendingOwner, suspendedOwner, rejectedOwner, active, leaver) = (await _kit.PersonAsync(), await _kit.PersonAsync(), await _kit.PersonAsync(), await _kit.PersonAsync(), await _kit.PersonAsync());
        var variant = await _kit.VariantAsync(null);
        var pending = await _kit.CompanyAsync(pendingOwner.Id, "Rfq Pending Ltd", activate: false);
        var suspended = await _kit.CompanyAsync(suspendedOwner.Id, "Rfq Suspended Ltd");
        var rejected = await _kit.CompanyAsync(rejectedOwner.Id, "Rfq Rejected Ltd", activate: false);
        var withLeaver = await _kit.CompanyAsync(active.Id, "Rfq Leaver Ltd", true, (leaver.Id, CompanyRoles.Buyer));
        var suspendedRfq = await AskAsync(suspendedOwner, suspended, variant.VariantId);
        await using (var db = _kit.Db())
        {
            var companies = new CompanyService(db);
            await companies.AdminSuspendAsync(suspended);
            await companies.AdminRejectAsync(rejected, new RejectCompanyRequest("Not a real company"));
        }

        (await ErrorAsync(await PostAsync(pendingOwner.Client, pending, Product(variant.VariantId), Key()), HttpStatusCode.Conflict)).Code.Should().Be("COMPANY_NOT_VERIFIED");
        (await ErrorAsync(await PostAsync(suspendedOwner.Client, suspended, Product(variant.VariantId), Key()), HttpStatusCode.Conflict)).Code.Should().Be("COMPANY_SUSPENDED");
        var baseline = await NotFoundShapeAsync(await active.Client.GetAsync(Url(Guid.NewGuid())), "unknown company");
        (await NotFoundShapeAsync(await PostAsync(rejectedOwner.Client, rejected, Product(variant.VariantId), Key()), "create for a Rejected company")).Should().Be(baseline);
        (await NotFoundShapeAsync(await pendingOwner.Client.GetAsync(Url(pending)), "read of a Pending company")).Should().Be(baseline);
        (await NotFoundShapeAsync(await rejectedOwner.Client.GetAsync(Url(rejected)), "read of a Rejected company")).Should().Be(baseline);
        (await DataAsync(await suspendedOwner.Client.GetAsync(Url(suspended, "/" + suspendedRfq)))).GetProperty("rfq").GetProperty("number").GetString().Should().StartWith("MWR-");

        // A member removed from the company is a stranger from that moment, with the token they already hold.
        (await DataAsync(await leaver.Client.GetAsync(Url(withLeaver)))).GetProperty("total").GetInt32().Should().Be(0);
        await using (var db = _kit.Db())
        {
            var companies = new CompanyService(db);
            var memberId = (await companies.ListMembersAsync(active.Id, withLeaver)).Single(m => m.UserId == leaver.Id).Id;
            await companies.RemoveMemberAsync(active.Id, withLeaver, memberId);
        }

        (await NotFoundShapeAsync(await leaver.Client.GetAsync(Url(withLeaver)), "a removed member")).Should().Be(baseline);
        (await NotFoundShapeAsync(await PostAsync(leaver.Client, withLeaver, Product(variant.VariantId), Key()), "a removed member asking")).Should().Be(baseline);
        await using var check = _kit.Db();
        (await check.Rfqs.CountAsync(r => r.CompanyId == pending || r.CompanyId == rejected || r.CompanyId == withLeaver)).Should().Be(0);
    }
}
