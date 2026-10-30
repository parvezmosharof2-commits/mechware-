namespace ShopVerse.Domain.Identity;

/// <summary>
/// Granular RBAC permission constants and role definitions for MECHWARE.
/// </summary>
public static class Permissions
{
    public const string OrdersViewOwn = "orders.view_own";
    public const string OrdersViewAll = "orders.view_all";
    public const string OrdersRefund = "orders.refund";

    public const string CatalogCreate = "catalog.create";
    public const string CatalogUpdateOwn = "catalog.update_own";
    public const string CatalogModerate = "catalog.moderate";

    public const string SellersManageOwn = "sellers.manage_own";
    public const string SellersKycReview = "sellers.kyc_review";

    public const string UsersManage = "users.manage";
    public const string FinancePayouts = "finance.payouts";
    public const string AuditView = "audit.view";

    public const string CompaniesView = "companies.view";
    public const string CompaniesManage = "companies.manage";
    public const string CompaniesCredit = "companies.credit";

    public static class Roles
    {
        public const string Buyer = "buyer";
        public const string Seller = "seller";
        public const string Support = "support";
        public const string Moderator = "moderator";
        public const string Finance = "finance";
        public const string Admin = "admin";

        public static bool RequiresMandatoryTwoFactor(string role) =>
            string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, Finance, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> GetDefaultPermissionsForRole(string role) =>
        role.ToLowerInvariant() switch
        {
            Roles.Buyer => [OrdersViewOwn],
            Roles.Seller => [OrdersViewOwn, CatalogCreate, CatalogUpdateOwn, SellersManageOwn],
            Roles.Support => [OrdersViewOwn, OrdersViewAll, OrdersRefund, CompaniesView],
            Roles.Moderator => [CatalogModerate, SellersKycReview],
            Roles.Finance => [OrdersViewAll, OrdersRefund, FinancePayouts, AuditView, CompaniesView, CompaniesCredit],
            Roles.Admin =>
            [
                OrdersViewOwn,
                OrdersViewAll,
                OrdersRefund,
                CatalogCreate,
                CatalogUpdateOwn,
                CatalogModerate,
                SellersManageOwn,
                SellersKycReview,
                UsersManage,
                FinancePayouts,
                AuditView,
                CompaniesView,
                CompaniesManage,
                CompaniesCredit
            ],
            _ => []
        };
}
