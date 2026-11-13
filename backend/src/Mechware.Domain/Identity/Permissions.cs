namespace Mechware.Domain.Identity;

/// <summary>
/// Granular RBAC permission constants and role definitions for MECHWARE.
/// </summary>
public static class Permissions
{
    public const string OrdersViewOwn = "orders.view_own";
    public const string OrdersViewAll = "orders.view_all";
    public const string OrdersRefund = "orders.refund";

    /// <summary>Approves a refund someone else asked for (four eyes, threat model v2 P3).</summary>
    public const string PaymentsRefundApprove = "payments.refund_approve";

    public const string CatalogCreate = "catalog.create";
    public const string CatalogUpdateOwn = "catalog.update_own";
    public const string CatalogModerate = "catalog.moderate";
    /// <summary>Edit any product's product-information data (supply status, successor, alternatives, order units).</summary>
    public const string CatalogManage = "catalog.manage";

    public const string SellersManageOwn = "sellers.manage_own";
    public const string SellersKycReview = "sellers.kyc_review";

    public const string UsersManage = "users.manage";
    public const string FinancePayouts = "finance.payouts";
    public const string AuditView = "audit.view";

    public const string CompaniesView = "companies.view";
    public const string CompaniesManage = "companies.manage";
    public const string CompaniesCredit = "companies.credit";

    // Price lists and margin floors (ADR-0008 §1, owner decision 3). Approval is four-eyes: holding both edit and approve
    // never lets anyone approve a list they created, changed or submitted (domain + database).
    public const string PriceListsView = "pricelists.view";
    /// <summary>Create, change, submit and revise a price list.</summary>
    public const string PriceListsEdit = "pricelists.edit";
    /// <summary>Approve, reject and withdraw a price list (with step-up).</summary>
    public const string PriceListsApprove = "pricelists.approve";
    public const string PriceListsApproveBelowFloor = "pricelists.approve_below_floor";
    /// <summary>See cost, floor and margin figures; no buyer-facing response ever carries them.</summary>
    public const string PricingViewCost = "pricing.view_cost";
    public const string PricingManageFloors = "pricing.manage_floors";

    public static class Roles
    {
        public const string Buyer = "buyer";
        public const string Seller = "seller";
        public const string Support = "support";
        public const string Moderator = "moderator";
        public const string Finance = "finance";
        public const string Admin = "admin";

        /// <summary>Every staff role signs in with a password and an authenticator (brief §11.1). Buyers and sellers may opt in.</summary>
        public static bool RequiresMandatoryTwoFactor(string role) =>
            string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, Finance, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, Support, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, Moderator, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> GetDefaultPermissionsForRole(string role) =>
        role.ToLowerInvariant() switch
        {
            Roles.Buyer => [OrdersViewOwn],
            Roles.Seller => [OrdersViewOwn, CatalogCreate, CatalogUpdateOwn, SellersManageOwn],
            // Support is the sales-facing staff role: it prepares price lists but never approves them.
            Roles.Support => [OrdersViewOwn, OrdersViewAll, OrdersRefund, CompaniesView, PriceListsView, PriceListsEdit],
            Roles.Moderator => [CatalogModerate, SellersKycReview],
            Roles.Finance =>
            [
                OrdersViewAll, OrdersRefund, PaymentsRefundApprove, FinancePayouts, AuditView, CompaniesView, CompaniesCredit,
                PriceListsView, PriceListsApprove, PricingViewCost
            ],
            Roles.Admin =>
            [
                OrdersViewOwn,
                OrdersViewAll,
                OrdersRefund,
                PaymentsRefundApprove,
                CatalogCreate,
                CatalogUpdateOwn,
                CatalogModerate,
                CatalogManage,
                SellersManageOwn,
                SellersKycReview,
                UsersManage,
                FinancePayouts,
                AuditView,
                CompaniesView,
                CompaniesManage,
                CompaniesCredit,
                PriceListsView,
                PriceListsEdit,
                PriceListsApprove,
                PriceListsApproveBelowFloor,
                PricingViewCost,
                PricingManageFloors
            ],
            _ => []
        };
}
