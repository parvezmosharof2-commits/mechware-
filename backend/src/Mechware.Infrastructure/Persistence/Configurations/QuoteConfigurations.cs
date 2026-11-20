using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Mechware.Domain.Catalog;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using Mechware.Domain.Sellers;
using Mechware.Infrastructure.Security;

namespace Mechware.Infrastructure.Persistence.Configurations;

/// <summary>
/// RFQs and versioned quotes (ADR-0011 §11). Enums are text with a CHECK on the list, every foreign key is RESTRICT, nothing is
/// ever deleted and every table has an <c>xmin</c> token. The CHECKs repeat the aggregate's shapes; what needs the previous row
/// or another table (frozen versions, transitions, four-eyes with contributors, append-only RFQ lines, one live version) lives
/// in <see cref="QuoteSql"/>. <c>ship_to_site_id</c> has no foreign key: company sites are deleted for real, and a frozen
/// version must keep the id it was quoted for (§8.3 answers <c>QUOTE_DELIVERY_SITE_GONE</c>).
/// </summary>
internal static class QuoteColumns
{
    public const string Open = "'Draft', 'PendingApproval'";
    public const string Issued = "'Sent', 'Accepted', 'Rejected', 'Withdrawn', 'Superseded'";

    public static string NumberCheck(string prefix) => $"number ~ '^{prefix}-[0-9]{{6}}-[A-Z2-7]{{6}}$'";

    public static void Xmin<T>(this EntityTypeBuilder<T> b) where T : class =>
        b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

    /// <summary>A value stored as jsonb; changes are detected on <paramref name="same"/>, so an unchanged value is never rewritten.</summary>
    public static PropertyBuilder<T> Jsonb<T>(this PropertyBuilder<T> p, string column, Func<T, string> write, Func<string, T> read, Func<T, T, bool> same) =>
        p.HasColumnName(column).HasColumnType("jsonb").HasConversion(
            new ValueConverter<T, string>(v => write(v), v => read(v)),
            new ValueComparer<T>((x, y) => Same<T>(x!, y!, same), v => 0, v => v));

    private static bool Same<T>(T x, T y, Func<T, T, bool> same) => ReferenceEquals(x, y) || (x is not null && y is not null && same(x, y));

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Web);

    public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Web) ?? throw new InvalidOperationException("Empty jsonb value.");
}

internal sealed class RfqConfiguration : IEntityTypeConfiguration<Rfq>
{
    public void Configure(EntityTypeBuilder<Rfq> b)
    {
        b.ToTable("rfqs", t =>
        {
            t.HasCheckConstraint("ck_rfqs_number", QuoteColumns.NumberCheck(QuoteNumber.RfqPrefix));
            t.HasCheckConstraint("ck_rfqs_source", "source IN ('Cart', 'Text', 'Csv', 'Product')");
            t.HasCheckConstraint("ck_rfqs_status", "status IN ('Submitted', 'Quoting', 'Answered', 'Closed', 'Withdrawn')");
            t.HasCheckConstraint("ck_rfqs_texts",
                $"(message IS NULL OR char_length(message) <= {QuoteLimits.MaxMessageLength}) AND char_length(idempotency_key) BETWEEN 8 AND 100 " +
                $"AND request_hash ~ '^[0-9a-f]{{64}}$' AND line_count BETWEEN 1 AND {QuoteLimits.MaxRfqLines}");
            t.HasCheckConstraint("ck_rfqs_answered", "(status IN ('Submitted', 'Quoting') AND first_quote_sent_at IS NULL) " +
                "OR (status = 'Answered' AND first_quote_sent_at IS NOT NULL) OR status IN ('Closed', 'Withdrawn')");
            t.HasCheckConstraint("ck_rfqs_closed", "(status = 'Closed') = (closed_at IS NOT NULL) AND (closed_at IS NULL) = (closed_by IS NULL) " +
                $"AND (closed_at IS NULL) = (close_reason IS NULL) AND (close_reason IS NULL OR char_length(close_reason) BETWEEN {QuoteLimits.MinReasonLength} AND {QuoteLimits.MaxReasonLength})");
            t.HasCheckConstraint("ck_rfqs_withdrawn", "(status = 'Withdrawn') = (withdrawn_at IS NOT NULL)");
        });
        b.MapAudit();
        b.IgnoreEvents();
        b.Xmin();
        b.Property(r => r.Number).HasColumnName("number").HasMaxLength(QuoteNumber.Length).IsRequired();
        b.Property(r => r.CompanyId).HasColumnName("company_id");
        b.Property(r => r.RequestedBy).HasColumnName("requested_by");
        b.Property(r => r.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(20);
        b.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(r => r.Message).HasColumnName("message").HasMaxLength(QuoteLimits.MaxMessageLength);
        b.Property(r => r.NeededBy).HasColumnName("needed_by");
        b.Property(r => r.ShipToSiteId).HasColumnName("ship_to_site_id");
        b.Property(r => r.LineCount).HasColumnName("line_count");
        b.Property(r => r.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(100).IsRequired();
        b.Property(r => r.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsFixedLength().IsRequired();
        b.Property(r => r.AssignedTo).HasColumnName("assigned_to");
        b.Property(r => r.FirstQuoteSentAtUtc).HasColumnName("first_quote_sent_at");
        b.Property(r => r.ClosedBy).HasColumnName("closed_by");
        b.Property(r => r.ClosedAtUtc).HasColumnName("closed_at");
        b.Property(r => r.CloseReason).HasColumnName("close_reason").HasMaxLength(QuoteLimits.MaxReasonLength);
        b.Property(r => r.WithdrawnAtUtc).HasColumnName("withdrawn_at");

        b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(r => r.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(r => r.AssignedTo).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.RfqId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(r => r.Number).IsUnique().HasDatabaseName("ux_rfqs_number");
        b.HasIndex(r => new { r.RequestedBy, r.IdempotencyKey }).IsUnique().HasDatabaseName("ux_rfqs_idempotency");
        b.HasIndex(r => new { r.CompanyId, r.CreatedAtUtc }).IsDescending(false, true).HasDatabaseName("ix_rfqs_company_created");
        b.HasIndex(r => new { r.RequestedBy, r.CreatedAtUtc }).IsDescending(false, true).HasDatabaseName("ix_rfqs_requester_created");
        b.HasIndex(r => r.CreatedAtUtc).HasFilter("status IN ('Submitted', 'Quoting')").HasDatabaseName("ix_rfqs_inbox");
    }
}

internal sealed class RfqLineConfiguration : IEntityTypeConfiguration<RfqLine>
{
    public void Configure(EntityTypeBuilder<RfqLine> b)
    {
        b.ToTable("rfq_lines", t =>
        {
            t.HasCheckConstraint("ck_rfq_lines_line", $"line_no BETWEEN 1 AND {QuoteLimits.MaxRfqLines} AND quantity BETWEEN 1 AND {QuoteLimits.MaxQuantityPerLine}");
            t.HasCheckConstraint("ck_rfq_lines_match_status", "match_status IN ('Matched', 'Ambiguous', 'NotFound', 'Obsolete')");
            t.HasCheckConstraint("ck_rfq_lines_variant", "(variant_id IS NOT NULL) = (match_status IN ('Matched', 'Obsolete')) " +
                "AND (successor_variant_id IS NULL OR match_status = 'Obsolete')");
            t.HasCheckConstraint("ck_rfq_lines_texts", $"char_length(raw_text) BETWEEN 1 AND {QuoteLimits.MaxRawTextLength} " +
                $"AND (part_key IS NULL OR char_length(part_key) <= {QuoteLimits.MaxPartKeyLength}) AND (note IS NULL OR char_length(note) <= {QuoteLimits.MaxRfqNoteLength})");
        });
        b.MapAudit(updated: false);
        b.Xmin();
        b.Property(l => l.RfqId).HasColumnName("rfq_id");
        b.Property(l => l.LineNo).HasColumnName("line_no");
        b.Property(l => l.RawText).HasColumnName("raw_text").HasMaxLength(QuoteLimits.MaxRawTextLength).IsRequired();
        b.Property(l => l.PartKey).HasColumnName("part_key").HasMaxLength(QuoteLimits.MaxPartKeyLength);
        b.Property(l => l.Quantity).HasColumnName("quantity");
        b.Property(l => l.MatchStatus).HasColumnName("match_status").HasConversion<string>().HasMaxLength(20);
        b.Property(l => l.VariantId).HasColumnName("variant_id");
        b.Property(l => l.SuccessorVariantId).HasColumnName("successor_variant_id");
        b.Property(l => l.Note).HasColumnName("note").HasMaxLength(QuoteLimits.MaxRfqNoteLength);

        b.HasOne<ProductVariant>().WithMany().HasForeignKey(l => l.VariantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductVariant>().WithMany().HasForeignKey(l => l.SuccessorVariantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(l => new { l.RfqId, l.LineNo }).IsUnique().HasDatabaseName("ux_rfq_lines_line_no");
    }
}

internal sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> b)
    {
        b.ToTable("quotes", t =>
        {
            t.HasCheckConstraint("ck_quotes_number", QuoteColumns.NumberCheck(QuoteNumber.QuotePrefix));
            t.HasCheckConstraint("ck_quotes_latest_version", "latest_version_no >= 1");
            t.HasCheckConstraint("ck_quotes_closed", "(closed_at IS NULL) = (closed_by IS NULL) AND (closed_at IS NULL) = (close_reason IS NULL) " +
                $"AND (close_reason IS NULL OR char_length(close_reason) BETWEEN {QuoteLimits.MinReasonLength} AND {QuoteLimits.MaxReasonLength})");
        });
        b.MapAudit();
        b.IgnoreEvents();
        b.Xmin();
        b.Ignore(q => q.IsClosed);
        b.Ignore(q => q.OpenVersion);
        b.Ignore(q => q.LiveVersion);
        b.Property(q => q.Number).HasColumnName("number").HasMaxLength(QuoteNumber.Length).IsRequired();
        b.Property(q => q.CompanyId).HasColumnName("company_id");
        b.Property(q => q.RfqId).HasColumnName("rfq_id");
        b.Property(q => q.ContactUserId).HasColumnName("contact_user_id");
        b.Property(q => q.AssignedTo).HasColumnName("assigned_to");
        b.Property(q => q.CreatedBy).HasColumnName("created_by");
        b.Property(q => q.LatestVersionNo).HasColumnName("latest_version_no");
        b.Property(q => q.ClosedBy).HasColumnName("closed_by");
        b.Property(q => q.ClosedAtUtc).HasColumnName("closed_at");
        b.Property(q => q.CloseReason).HasColumnName("close_reason").HasMaxLength(QuoteLimits.MaxReasonLength);

        b.HasOne<Company>().WithMany().HasForeignKey(q => q.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Rfq>().WithMany().HasForeignKey(q => q.RfqId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(q => q.ContactUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(q => q.AssignedTo).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(q => q.Versions).WithOne().HasForeignKey(v => v.QuoteId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(q => q.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(q => q.Number).IsUnique().HasDatabaseName("ux_quotes_number");
        b.HasIndex(q => new { q.CompanyId, q.CreatedAtUtc }).IsDescending(false, true).HasDatabaseName("ix_quotes_company_created");
        b.HasIndex(q => q.RfqId).HasDatabaseName("ix_quotes_rfq");
        b.HasIndex(q => q.AssignedTo).HasFilter("closed_at IS NULL").HasDatabaseName("ix_quotes_open_assigned");
    }
}

internal sealed class QuoteVersionConfiguration : IEntityTypeConfiguration<QuoteVersion>
{
    private sealed record FlagsJson(Guid[] BelowFloorLineIds, Guid[] OverAuthorityLineIds, bool LongValidity);

    /// <summary>The BIN is encrypted inside the snapshot as it is in <c>companies</c> (threat model v1, H4); name and address are plain.</summary>
    private sealed record CompanyJson(string Name, string? Bin, string? BillingAddress);

    public void Configure(EntityTypeBuilder<QuoteVersion> b)
    {
        const string fixedShipping = "ship_to_site_id IS NOT NULL AND shipping_fees IS NOT NULL";
        b.ToTable("quote_versions", t =>
        {
            t.HasCheckConstraint("ck_quote_versions_status", $"status IN ({QuoteColumns.Open}, {QuoteColumns.Issued})");
            t.HasCheckConstraint("ck_quote_versions_header", $"version_no >= 1 AND validity_days BETWEEN 1 AND {QuoteOptions.HardMaxValidityDays} " +
                "AND payment_terms IN ('AsAccount', 'Advance') AND shipping_mode IN ('Calculated', 'Fixed')");
            t.HasCheckConstraint("ck_quote_versions_texts", $"(terms_text IS NULL OR char_length(terms_text) <= {QuoteLimits.MaxTermsLength}) " +
                $"AND (buyer_note IS NULL OR char_length(buyer_note) <= {QuoteLimits.MaxNoteLength}) AND (internal_note IS NULL OR char_length(internal_note) <= {QuoteLimits.MaxNoteLength}) " +
                $"AND (preparer_name IS NULL OR char_length(preparer_name) BETWEEN 1 AND {QuoteLimits.MaxNameLength}) " +
                $"AND (approval_comment IS NULL OR char_length(approval_comment) <= {QuoteLimits.MaxReasonLength}) " +
                $"AND (reject_reason IS NULL OR char_length(reject_reason) <= {QuoteLimits.MaxRejectReasonLength}) " +
                $"AND (withdraw_reason IS NULL OR char_length(withdraw_reason) BETWEEN {QuoteLimits.MinReasonLength} AND {QuoteLimits.MaxReasonLength})");
            t.HasCheckConstraint("ck_quote_versions_money", "goods_net_minor >= 0 AND tax_minor >= 0 AND goods_total_minor = goods_net_minor + tax_minor " +
                "AND (shipping_total_minor IS NULL) = (grand_total_minor IS NULL) " +
                "AND (shipping_total_minor IS NULL OR (shipping_total_minor >= 0 AND grand_total_minor = goods_total_minor + shipping_total_minor))");
            // Calculated delivery carries nothing; fixed delivery a site and per-seller fees, and once frozen its shipping and grand total.
            t.HasCheckConstraint("ck_quote_versions_shipping",
                "(shipping_mode = 'Calculated' AND ship_to_site_id IS NULL AND shipping_fees IS NULL AND ship_to IS NULL AND shipping_total_minor IS NULL) " +
                $"OR (shipping_mode = 'Fixed' AND {fixedShipping} AND jsonb_typeof(shipping_fees) = 'array' " +
                "AND (status = 'Draft' OR grand_total_minor IS NOT NULL))");
            // Every status after Draft is frozen: hash, submitter, flags, company snapshot, preparer.
            t.HasCheckConstraint("ck_quote_versions_frozen", "content_hash IS NULL OR content_hash ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_quote_versions_frozen_shape",
                "(status = 'Draft') = (content_hash IS NULL) AND (content_hash IS NULL) = (submitted_by IS NULL) AND (submitted_by IS NULL) = (submitted_at IS NULL) " +
                "AND (content_hash IS NULL) = (flags IS NULL) AND (status = 'Draft' OR (company_snapshot IS NOT NULL AND preparer_name IS NOT NULL))");
            t.HasCheckConstraint("ck_quote_versions_sent_shape",
                $"(status IN ({QuoteColumns.Issued})) = (sent_at IS NOT NULL) AND (sent_at IS NULL) = (sent_by IS NULL) " +
                "AND (sent_at IS NULL) = (valid_until_utc IS NULL) AND (valid_until_utc IS NULL OR valid_until_utc > sent_at)");
            // Four eyes (Q8, QT7): never the creator, never the submitter. Contributors are checked by the guard trigger.
            t.HasCheckConstraint("ck_quote_versions_four_eyes",
                "approved_by IS NULL OR (submitted_by IS NOT NULL AND approved_by <> created_by AND approved_by <> submitted_by)");
            t.HasCheckConstraint("ck_quote_versions_approval_shape",
                $"(approved_by IS NULL) = (approved_at IS NULL) AND (approved_by IS NULL OR status IN ({QuoteColumns.Issued})) " +
                "AND (approval_rejected_by IS NULL) = (approval_rejected_at IS NULL)");
            t.HasCheckConstraint("ck_quote_versions_outcome_shape",
                "(accepted_order_id IS NULL) = (accepted_by IS NULL) AND (accepted_order_id IS NULL) = (accepted_at IS NULL) " +
                "AND (status <> 'Accepted' OR accepted_order_id IS NOT NULL) AND (accepted_order_id IS NULL OR status IN ('Accepted', 'Superseded')) " +
                "AND (status = 'Rejected') = (rejected_by IS NOT NULL) AND (rejected_by IS NULL) = (rejected_at IS NULL) " +
                "AND (status = 'Withdrawn') = (withdrawn_by IS NOT NULL) AND (withdrawn_by IS NULL) = (withdrawn_at IS NULL) AND (withdrawn_by IS NULL) = (withdraw_reason IS NULL) " +
                "AND (status = 'Superseded') = (superseded_at IS NOT NULL)");
            t.HasCheckConstraint("ck_quote_versions_pdf",
                $"(pdf_en_key IS NULL) = (pdf_en_sha256 IS NULL) AND (pdf_bn_key IS NULL) = (pdf_bn_sha256 IS NULL) " +
                $"AND (pdf_en_sha256 IS NULL OR pdf_en_sha256 ~ '^[0-9a-f]{{64}}$') AND (pdf_bn_sha256 IS NULL OR pdf_bn_sha256 ~ '^[0-9a-f]{{64}}$') " +
                $"AND (pdf_en_key IS NULL AND pdf_bn_key IS NULL OR status IN ({QuoteColumns.Issued}))");
        });
        b.MapAudit();
        b.Xmin();
        b.Property(v => v.QuoteId).HasColumnName("quote_id");
        b.Property(v => v.VersionNo).HasColumnName("version_no");
        b.Property(v => v.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(v => v.ValidityDays).HasColumnName("validity_days");
        b.Property(v => v.PaymentTerms).HasColumnName("payment_terms").HasConversion<string>().HasMaxLength(20);
        b.Property(v => v.ShippingMode).HasColumnName("shipping_mode").HasConversion<string>().HasMaxLength(20);
        b.Property(v => v.ShipToSiteId).HasColumnName("ship_to_site_id");
        // The delivery address snapshot of a fixed-delivery version (D4.7; frozen after Draft by the guard). Plain text, as orders.ship_to.
        b.Property(v => v.ShipTo).Jsonb("ship_to", v => QuoteColumns.Json(v!), s => QuoteColumns.FromJson<QuoteShipTo>(s), (x, y) => Equals(x, y));
        b.Property(v => v.ShippingFees).Jsonb("shipping_fees",
            v => QuoteColumns.Json(v!.OrderBy(f => f.SellerId).ToArray()),
            s => QuoteColumns.FromJson<QuoteShippingFee[]>(s),
            (x, y) => QuoteColumns.Json(x!.OrderBy(f => f.SellerId)) == QuoteColumns.Json(y!.OrderBy(f => f.SellerId)));
        b.Property(v => v.TermsText).HasColumnName("terms_text").HasMaxLength(QuoteLimits.MaxTermsLength);
        b.Property(v => v.BuyerNote).HasColumnName("buyer_note").HasMaxLength(QuoteLimits.MaxNoteLength);
        b.Property(v => v.InternalNote).HasColumnName("internal_note").HasMaxLength(QuoteLimits.MaxNoteLength);
        b.Property(v => v.CompanySnapshot).Jsonb("company_snapshot",
            v => QuoteColumns.Json(new CompanyJson(v!.Name, v.Bin is null ? null : FieldEncryption.Current.Protect(v.Bin), v.BillingAddress)),
            s => ReadCompany(QuoteColumns.FromJson<CompanyJson>(s)),
            (x, y) => Equals(x, y));
        b.Property(v => v.PreparerName).HasColumnName("preparer_name").HasMaxLength(QuoteLimits.MaxNameLength);
        b.Property(v => v.GoodsNetMinor).HasColumnName("goods_net_minor");
        b.Property(v => v.TaxMinor).HasColumnName("tax_minor");
        b.Property(v => v.GoodsTotalMinor).HasColumnName("goods_total_minor");
        b.Property(v => v.ShippingTotalMinor).HasColumnName("shipping_total_minor");
        b.Property(v => v.GrandTotalMinor).HasColumnName("grand_total_minor");
        b.Property(v => v.ContentHash).HasColumnName("content_hash").HasMaxLength(64).IsFixedLength();
        b.Property(v => v.Flags).Jsonb("flags",
            v => QuoteColumns.Json(new FlagsJson([.. v!.BelowFloorLineIds], [.. v.OverAuthorityLineIds], v.LongValidity)),
            s => ReadFlags(QuoteColumns.FromJson<FlagsJson>(s)),
            (x, y) => x!.LongValidity == y!.LongValidity && x.BelowFloorLineIds.SequenceEqual(y.BelowFloorLineIds)
                && x.OverAuthorityLineIds.SequenceEqual(y.OverAuthorityLineIds));
        b.Property(v => v.CreatedBy).HasColumnName("created_by");
        b.Property(v => v.SubmittedBy).HasColumnName("submitted_by");
        b.Property(v => v.SubmittedAtUtc).HasColumnName("submitted_at");
        b.Property(v => v.ApprovedBy).HasColumnName("approved_by");
        b.Property(v => v.ApprovedAtUtc).HasColumnName("approved_at");
        b.Property(v => v.ApprovalComment).HasColumnName("approval_comment").HasMaxLength(QuoteLimits.MaxReasonLength);
        b.Property(v => v.ApprovalRejectedBy).HasColumnName("approval_rejected_by");
        b.Property(v => v.ApprovalRejectedAtUtc).HasColumnName("approval_rejected_at");
        b.Property(v => v.SentBy).HasColumnName("sent_by");
        b.Property(v => v.SentAtUtc).HasColumnName("sent_at");
        b.Property(v => v.ValidUntilUtc).HasColumnName("valid_until_utc");
        b.Property(v => v.AcceptedOrderId).HasColumnName("accepted_order_id");
        b.Property(v => v.AcceptedBy).HasColumnName("accepted_by");
        b.Property(v => v.AcceptedAtUtc).HasColumnName("accepted_at");
        b.Property(v => v.RejectedBy).HasColumnName("rejected_by");
        b.Property(v => v.RejectedAtUtc).HasColumnName("rejected_at");
        b.Property(v => v.RejectReason).HasColumnName("reject_reason").HasMaxLength(QuoteLimits.MaxRejectReasonLength);
        b.Property(v => v.WithdrawnBy).HasColumnName("withdrawn_by");
        b.Property(v => v.WithdrawnAtUtc).HasColumnName("withdrawn_at");
        b.Property(v => v.WithdrawReason).HasColumnName("withdraw_reason").HasMaxLength(QuoteLimits.MaxReasonLength);
        b.Property(v => v.SupersededAtUtc).HasColumnName("superseded_at");
        // The lazily rendered PDFs (§10, D4.11): written null → value once by the guard; the domain gains them in D4.11.
        foreach (var (name, locale) in new[] { ("En", "en"), ("Bn", "bn") })
        {
            b.Property<string?>($"Pdf{name}Key").HasColumnName($"pdf_{locale}_key").HasMaxLength(200);
            b.Property<string?>($"Pdf{name}Sha256").HasColumnName($"pdf_{locale}_sha256").HasMaxLength(64).IsFixedLength();
        }

        b.HasOne<Order>().WithMany().HasForeignKey(v => v.AcceptedOrderId).OnDelete(DeleteBehavior.Restrict);
        // ClientCascade: a Draft edit replaces all lines, so EF deletes the removed ones; the database FK stays NO ACTION.
        b.HasMany(v => v.Lines).WithOne().HasForeignKey(l => l.VersionId).OnDelete(DeleteBehavior.ClientCascade);
        b.HasMany(v => v.Contributors).WithOne().HasForeignKey(c => c.VersionId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(v => v.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(v => v.Contributors).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(v => new { v.QuoteId, v.VersionNo }).IsUnique().HasDatabaseName("ux_quote_versions_version_no");
        // One open version per quote (§1). "One live version" is a deferred exclusion constraint in QuoteSql: a send supersedes
        // the live version in the same statement batch, in whatever order EF writes the two rows.
        b.HasIndex(v => v.QuoteId).IsUnique().HasFilter($"status IN ({QuoteColumns.Open})").HasDatabaseName("ux_quote_versions_one_open");
        b.HasIndex(v => v.ValidUntilUtc).HasFilter("status = 'Sent'").HasDatabaseName("ix_quote_versions_expiry");
        b.HasIndex(v => v.SubmittedAtUtc).HasFilter("status = 'PendingApproval'").HasDatabaseName("ix_quote_versions_approval_queue");
        b.HasIndex(v => v.AcceptedOrderId).HasFilter("accepted_order_id IS NOT NULL").HasDatabaseName("ix_quote_versions_accepted_order");
    }

    private static QuoteCompanySnapshot ReadCompany(CompanyJson c) =>
        new(c.Name, c.Bin is null ? null : FieldEncryption.Current.Unprotect(c.Bin), c.BillingAddress);

    private static QuoteFlags ReadFlags(FlagsJson f) => new(f.BelowFloorLineIds, f.OverAuthorityLineIds, f.LongValidity);
}

internal sealed class QuoteLineConfiguration : IEntityTypeConfiguration<QuoteLine>
{
    public void Configure(EntityTypeBuilder<QuoteLine> b)
    {
        b.ToTable("quote_lines", t =>
        {
            t.HasCheckConstraint("ck_quote_lines_line", $"line_no BETWEEN 1 AND {QuoteLimits.MaxLinesPerVersion} " +
                $"AND quantity BETWEEN 1 AND {QuoteLimits.MaxQuantityPerLine} AND unit_price_minor BETWEEN 1 AND {PriceListLimits.MaxUnitPriceMinor}");
            t.HasCheckConstraint("ck_quote_lines_snapshot", "vat_rate BETWEEN 0 AND 1 AND public_unit_price_minor > 0 " +
                $"AND char_length(sku) BETWEEN 1 AND {QuoteLimits.MaxSkuLength} AND (part_number IS NULL OR char_length(part_number) <= {QuoteLimits.MaxSkuLength}) " +
                $"AND char_length(title_en) BETWEEN 1 AND {QuoteLimits.MaxTitleLength} AND char_length(title_bn) BETWEEN 1 AND {QuoteLimits.MaxTitleLength} " +
                $"AND (line_note IS NULL OR char_length(line_note) <= {QuoteLimits.MaxLineNoteLength})");
            // OrderPricing in Exclusive mode with no discount: net = quantity × unit, total = net + VAT.
            t.HasCheckConstraint("ck_quote_lines_money", "line_net_minor = quantity::bigint * unit_price_minor AND line_tax_minor >= 0 " +
                "AND line_total_minor = line_net_minor + line_tax_minor");
            t.HasCheckConstraint("ck_quote_lines_check", "(reference_unit_price_minor IS NULL) = (reference_source IS NULL) " +
                "AND (reference_unit_price_minor IS NULL OR reference_unit_price_minor > 0) " +
                "AND (reference_source IS NULL OR reference_source IN ('List', 'Sale', 'QuantityBreak', 'GroupList', 'Contract')) " +
                "AND (cost_minor_at_check IS NULL OR cost_minor_at_check > 0) AND (floor_minor_at_check IS NULL OR floor_minor_at_check > 0)");
            t.HasCheckConstraint("ck_quote_lines_below_floor_reason", "(NOT below_floor OR below_floor_reason IS NOT NULL) " +
                $"AND (below_floor_reason IS NULL OR char_length(below_floor_reason) BETWEEN {QuoteLimits.MinReasonLength} AND {QuoteLimits.MaxReasonLength})");
        });
        b.MapAudit(updated: false);
        b.Xmin();
        b.Property(l => l.VersionId).HasColumnName("version_id");
        b.Property(l => l.LineNo).HasColumnName("line_no");
        b.Property(l => l.VariantId).HasColumnName("variant_id");
        b.Property(l => l.RfqLineId).HasColumnName("rfq_line_id");
        b.Property(l => l.Quantity).HasColumnName("quantity");
        b.Property(l => l.UnitPriceMinor).HasColumnName("unit_price_minor");
        b.Property(l => l.LineNote).HasColumnName("line_note").HasMaxLength(QuoteLimits.MaxLineNoteLength);
        b.Property(l => l.BelowFloorReason).HasColumnName("below_floor_reason").HasMaxLength(QuoteLimits.MaxReasonLength);
        b.Property(l => l.SellerId).HasColumnName("seller_id");
        b.Property(l => l.Sku).HasColumnName("sku").HasMaxLength(QuoteLimits.MaxSkuLength).IsRequired();
        b.Property(l => l.PartNumber).HasColumnName("part_number").HasMaxLength(QuoteLimits.MaxSkuLength);
        b.Property(l => l.TitleEn).HasColumnName("title_en").HasMaxLength(QuoteLimits.MaxTitleLength).IsRequired();
        b.Property(l => l.TitleBn).HasColumnName("title_bn").HasMaxLength(QuoteLimits.MaxTitleLength).IsRequired();
        // numeric(5,4): the hash prints the rate as "0.0000", so the stored scale must give back the same text (D4.3 handoff).
        b.Property(l => l.VatRate).HasColumnName("vat_rate").HasPrecision(5, 4);
        b.Property(l => l.PublicUnitPriceMinor).HasColumnName("public_unit_price_minor");
        b.Property(l => l.LineNetMinor).HasColumnName("line_net_minor");
        b.Property(l => l.LineTaxMinor).HasColumnName("line_tax_minor");
        b.Property(l => l.LineTotalMinor).HasColumnName("line_total_minor");
        b.Property(l => l.ReferenceUnitPriceMinor).HasColumnName("reference_unit_price_minor");
        b.Property(l => l.ReferenceSource).HasColumnName("reference_source").HasConversion<string>().HasMaxLength(20);
        b.Property(l => l.CostMinorAtCheck).HasColumnName("cost_minor_at_check");
        b.Property(l => l.FloorMinorAtCheck).HasColumnName("floor_minor_at_check");
        b.Property(l => l.BelowFloor).HasColumnName("below_floor");
        b.Property(l => l.OverAuthority).HasColumnName("over_authority");

        b.HasOne<ProductVariant>().WithMany().HasForeignKey(l => l.VariantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Seller>().WithMany().HasForeignKey(l => l.SellerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RfqLine>().WithMany().HasForeignKey(l => l.RfqLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(l => new { l.VersionId, l.LineNo }).IsUnique().HasDatabaseName("ux_quote_lines_line_no");
        b.HasIndex(l => new { l.VersionId, l.VariantId }).IsUnique().HasDatabaseName("ux_quote_lines_variant");
        b.HasIndex(l => l.VariantId).HasDatabaseName("ix_quote_lines_variant");
    }
}

internal sealed class QuoteVersionContributorConfiguration : IEntityTypeConfiguration<QuoteVersionContributor>
{
    public void Configure(EntityTypeBuilder<QuoteVersionContributor> b)
    {
        b.ToTable("quote_version_contributors");
        b.HasKey(c => new { c.VersionId, c.UserId });
        b.Xmin();
        b.Property(c => c.VersionId).HasColumnName("version_id");
        b.Property(c => c.UserId).HasColumnName("user_id");
        b.Property(c => c.FirstContributedAtUtc).HasColumnName("first_contributed_at");
    }
}
