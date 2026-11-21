namespace Mechware.Domain.Identity;

/// <summary>
/// Granular RBAC permission constants and role definitions for MECHWARE.
/// </summary>
public static class Permissions
{
    public const string OrdersViewOwn = "orders.view_own";
    public const string OrdersViewAll = "orders.view_all";
    public const string OrdersRefund = "orders.refund";

    /// <summary>Cancel a confirmed order that has not shipped (E7.2, always with step-up): restocks, releases credit, flags paid money for refund.</summary>
    public const string OrdersCancel = "orders.cancel";

    /// <summary>Move a confirmed order through processing, packed, shipped (courier + tracking number) and delivered (F1.1; Support, Admin).</summary>
    public const string OrdersFulfil = "orders.fulfil";

    /// <summary>Approve or reject a return, mark it received, inspect it and restock (F2.3a; Support, Admin). Moves no money.</summary>
    public const string ReturnsManage = "returns.manage";

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

    // Shipping zones and rate cards (E8.4a). Tariffs decide what every buyer pays and whether production orders are possible at all.
    public const string ShippingView = "shipping.view";
    /// <summary>Create, change and delete zones and rate cards (money-affecting configuration: Admin only).</summary>
    public const string ShippingManage = "shipping.manage";

    // Credit settlement (ADR-0010 §5). Finance holds both, like refunds: four-eyes is per payment (verifier ≠ recorder), not per role.
    /// <summary>Record a customer's payment against its credit; recording releases nothing.</summary>
    public const string CreditRecordPayment = "credit.record_payment";
    /// <summary>Verify or reject a payment someone else recorded (verify with step-up); only a verification releases credit.</summary>
    public const string CreditVerifyPayment = "credit.verify_payment";
    /// <summary>Request (or withdraw, or reject) a write-off, an on-account correction or a payout; a request moves no money.</summary>
    public const string CreditAdjust = "credit.adjust";
    /// <summary>Approve someone else's adjustment, with step-up (Admin only, ADR-0010 §5): only the approval writes the ledger line.</summary>
    public const string CreditAdjustApprove = "credit.adjust_approve";
    /// <summary>Lift the overdue credit hold for at most <c>Credit:HoldOverrideMaxDays</c>, with step-up and a reason (Admin only, ADR-0010 §5, §9).</summary>
    public const string CreditHoldOverride = "credit.hold_override";
    /// <summary>Read the payment and adjustment queues, aging, and company statements, and export them as CSV (Finance, Admin; ADR-0010 §5, §10). The all-company CSV also needs step-up.</summary>
    public const string CreditView = "credit.view";

    // RFQs and quotes (ADR-0011 §13). Four-eyes is per version (approver ≠ creator, submitter, contributor; domain + database),
    // so holding edit and approve never lets anyone approve a quote they wrote.
    /// <summary>The quote inbox, RFQs, quotes, PDFs and the document check (Support, Finance, Admin).</summary>
    public const string QuotesView = "quotes.view";
    /// <summary>Create, edit, send within authority, revise, withdraw, close and assign (Support, Admin).</summary>
    public const string QuotesEdit = "quotes.edit";
    /// <summary>Approve (and so send) or reject a pending version, never one's own; with step-up (Finance, Admin).</summary>
    public const string QuotesApprove = "quotes.approve";
    /// <summary>Needed in addition to approve a version with a line below the margin floor (Admin only).</summary>
    public const string QuotesApproveBelowFloor = "quotes.approve_below_floor";

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
            Roles.Support =>
            [
                OrdersViewOwn, OrdersViewAll, OrdersRefund, OrdersCancel, OrdersFulfil, ReturnsManage, CompaniesView, PriceListsView, PriceListsEdit, ShippingView,
                QuotesView, QuotesEdit
            ],
            Roles.Moderator => [CatalogModerate, SellersKycReview],
            Roles.Finance =>
            [
                OrdersViewAll, OrdersRefund, OrdersCancel, PaymentsRefundApprove, FinancePayouts, AuditView, CompaniesView, CompaniesCredit,
                PriceListsView, PriceListsApprove, PricingViewCost, ShippingView, CreditRecordPayment, CreditVerifyPayment, CreditAdjust, CreditView,
                QuotesView, QuotesApprove
            ],
            Roles.Admin =>
            [
                OrdersViewOwn,
                OrdersViewAll,
                OrdersRefund,
                OrdersCancel,
                OrdersFulfil,
                ReturnsManage,
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
                PricingManageFloors,
                ShippingView,
                ShippingManage,
                CreditRecordPayment,
                CreditVerifyPayment,
                CreditAdjust,
                CreditAdjustApprove,
                CreditHoldOverride,
                CreditView,
                QuotesView,
                QuotesEdit,
                QuotesApprove,
                QuotesApproveBelowFloor
            ],
            _ => []
        };
}
