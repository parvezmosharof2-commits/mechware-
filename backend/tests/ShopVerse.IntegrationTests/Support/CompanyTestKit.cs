using ShopVerse.Application.Companies;

namespace ShopVerse.IntegrationTests.Support;

public static class CompanyTestKit
{
    /// <summary>
    /// Adds a member and has them accept, which is what a working membership is since invitations (threat model v1, M2).
    /// Tests about something else use this; the invitation itself is tested in MemberInvitationTests.
    /// </summary>
    public static async Task<MemberDto> AddAcceptedMemberAsync(this ICompanyService companies, Guid adminUserId, Guid companyId,
        AddMemberRequest request, CancellationToken ct = default)
    {
        MemberDto invited = await companies.AddMemberAsync(adminUserId, companyId, request, ct);
        await companies.AcceptInvitationAsync(request.UserId, companyId, ct);
        return invited with { Accepted = true };
    }
}
