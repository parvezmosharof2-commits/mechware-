using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;

namespace Mechware.Infrastructure.Persistence.Configurations;

/// <summary>
/// The credit ledger (ADR-0010 §2). The CHECKs repeat <see cref="CreditLedgerEntry.Validate"/>; <see cref="CreditLedgerSql"/>
/// adds the append-only trigger and the deferred chain / cached-total check.
/// </summary>
internal sealed class CreditLedgerEntryConfiguration : IEntityTypeConfiguration<CreditLedgerEntry>
{
    private const string Kinds = "'charge','payment_applied','payment_on_account','on_account_applied','payment_reversed','charge_reduced','write_off','on_account_correction','on_account_payout','withholding_applied'";

    public void Configure(EntityTypeBuilder<CreditLedgerEntry> b)
    {
        b.ToTable("credit_ledger_entries", t =>
        {
            t.HasCheckConstraint("ck_credit_ledger_kind", $"kind IN ({Kinds})");
            t.HasCheckConstraint("ck_credit_ledger_seq", "seq >= 1");
            t.HasCheckConstraint("ck_credit_ledger_totals", "receivable_after_minor >= 0 AND on_account_after_minor >= 0");
            t.HasCheckConstraint("ck_credit_ledger_moves", "receivable_delta_minor <> 0 OR on_account_delta_minor <> 0");
            t.HasCheckConstraint("ck_credit_ledger_currency", "currency = 'BDT'");
            t.HasCheckConstraint("ck_credit_ledger_key", "length(idempotency_key) > 0");
            t.HasCheckConstraint("ck_credit_ledger_due_on", "(kind = 'charge') = (due_on IS NOT NULL)");
            // A debt is created only by an order, never by a staff adjustment (§2.2).
            t.HasCheckConstraint("ck_credit_ledger_receivable_order", "receivable_delta_minor = 0 OR order_id IS NOT NULL");
            t.HasCheckConstraint("ck_credit_ledger_no_adjustment_debt", "receivable_delta_minor <= 0 OR adjustment_id IS NULL");
            t.HasCheckConstraint("ck_credit_ledger_signs", """
                CASE kind
                WHEN 'charge' THEN receivable_delta_minor > 0 AND on_account_delta_minor = 0
                WHEN 'payment_applied' THEN receivable_delta_minor < 0 AND on_account_delta_minor = 0 AND payment_id IS NOT NULL
                WHEN 'payment_on_account' THEN receivable_delta_minor = 0 AND on_account_delta_minor > 0 AND payment_id IS NOT NULL
                WHEN 'on_account_applied' THEN receivable_delta_minor < 0 AND on_account_delta_minor = receivable_delta_minor
                    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)
                WHEN 'payment_reversed' THEN payment_id IS NOT NULL AND ((receivable_delta_minor > 0 AND on_account_delta_minor = 0)
                    OR (receivable_delta_minor = 0 AND on_account_delta_minor < 0))
                WHEN 'charge_reduced' THEN order_id IS NOT NULL AND ((receivable_delta_minor < 0 AND on_account_delta_minor = 0)
                    OR (receivable_delta_minor = 0 AND on_account_delta_minor > 0 AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)))
                WHEN 'write_off' THEN receivable_delta_minor < 0 AND on_account_delta_minor = 0 AND adjustment_id IS NOT NULL
                WHEN 'on_account_correction' THEN receivable_delta_minor = 0 AND adjustment_id IS NOT NULL
                    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)
                WHEN 'on_account_payout' THEN receivable_delta_minor = 0 AND on_account_delta_minor < 0 AND adjustment_id IS NOT NULL
                    AND (payment_id IS NOT NULL OR credit_note_id IS NOT NULL)
                WHEN 'withholding_applied' THEN receivable_delta_minor < 0 AND on_account_delta_minor = 0 AND withholding_id IS NOT NULL
                    AND num_nonnulls(payment_id, adjustment_id, credit_note_id) = 0
                ELSE false END
                """);
            t.HasCheckConstraint("ck_credit_ledger_withholding", "withholding_id IS NULL OR kind = 'withholding_applied'");
        });
        b.MapAudit(updated: false);
        b.Property(e => e.CompanyId).HasColumnName("company_id");
        b.Property(e => e.Seq).HasColumnName("seq");
        b.Property(e => e.EventId).HasColumnName("event_id");
        b.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<CreditLedgerKind>());
        b.Property(e => e.OrderId).HasColumnName("order_id");
        // Credit notes come with E6; their foreign key comes with them.
        b.Property(e => e.PaymentId).HasColumnName("payment_id");
        b.Property(e => e.AdjustmentId).HasColumnName("adjustment_id");
        b.Property(e => e.CreditNoteId).HasColumnName("credit_note_id");
        b.Property(e => e.WithholdingId).HasColumnName("withholding_id");
        b.Property(e => e.ReceivableDeltaMinor).HasColumnName("receivable_delta_minor");
        b.Property(e => e.OnAccountDeltaMinor).HasColumnName("on_account_delta_minor");
        b.Property(e => e.ReceivableAfterMinor).HasColumnName("receivable_after_minor");
        b.Property(e => e.OnAccountAfterMinor).HasColumnName("on_account_after_minor");
        b.Property(e => e.DueOn).HasColumnName("due_on");
        b.Property(e => e.OccurredOn).HasColumnName("occurred_on");
        b.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(CreditLedgerKeys.MaxLength).IsRequired();
        b.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
        b.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasMaxLength(CreditLedgerEntry.MaxCorrelationIdLength);
        b.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(CreditLedgerEntry.MaxReasonLength);

        b.HasIndex(e => new { e.CompanyId, e.Seq }).IsUnique().HasDatabaseName("ux_credit_ledger_company_seq");
        b.HasIndex(e => e.IdempotencyKey).IsUnique().HasDatabaseName("ux_credit_ledger_idempotency_key");
        b.HasIndex(e => new { e.CompanyId, e.OrderId }).HasDatabaseName("ix_credit_ledger_company_order");
        b.HasIndex(e => e.OrderId).HasDatabaseName("ix_credit_ledger_order");
        // A company or an order with a ledger line is never deleted (§12).
        b.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Order>().WithMany().HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CreditPayment>().WithMany().HasForeignKey(e => e.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CreditAdjustment>().WithMany().HasForeignKey(e => e.AdjustmentId).OnDelete(DeleteBehavior.Restrict);
        // E6.6: at most one line per verified withholding (its key is withholding:{id}; this index is the backstop).
        b.HasOne<Mechware.Domain.Invoicing.TaxWithholding>().WithMany().HasForeignKey(e => e.WithholdingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => e.WithholdingId).IsUnique().HasFilter("withholding_id IS NOT NULL").HasDatabaseName("ux_credit_ledger_withholding");
    }
}

/// <summary>
/// Customer payments (ADR-0010 §4): the CHECKs repeat the domain's shape per method and status and the four-eyes rule (§2.3);
/// partial unique indexes refuse a reference recorded twice, a rejected entry excepted (CS4). <see cref="CreditLedgerSql"/>
/// refuses every change but the next step, and any delete.
/// </summary>
internal sealed class CreditPaymentConfiguration : IEntityTypeConfiguration<CreditPayment>
{
    /// <summary>A bank or wallet transaction id: one payment across every company and all these methods.</summary>
    public const string TransferMethods = "'beftn','rtgs','npsb','bkash','nagad'";
    /// <summary>An instrument number: one payment per company and drawer bank (cheque numbers repeat across banks).</summary>
    public const string InstrumentMethods = "'cheque','pay_order','bank_deposit'";
    /// <summary>The highest <c>Credit:MaxPaymentMinor</c> the options accept; the configured limit is the domain's.</summary>
    public const string MaxMinor = "1000000000000";

    public void Configure(EntityTypeBuilder<CreditPayment> b)
    {
        b.ToTable("credit_payments", t =>
        {
            t.HasCheckConstraint("ck_credit_payments_amount", $"amount_minor BETWEEN 1 AND {MaxMinor} AND currency = 'BDT'");
            t.HasCheckConstraint("ck_credit_payments_method", $"method IN ({TransferMethods}, {InstrumentMethods}, 'cash')");
            t.HasCheckConstraint("ck_credit_payments_texts", "reference ~ '^[A-Z0-9]{4,40}$' AND (drawer_bank IS NULL OR drawer_bank ~ '^[A-Z0-9]{2,60}$')");
            t.HasCheckConstraint("ck_credit_payments_instrument",
                $"(method IN ({InstrumentMethods})) = (drawer_bank IS NOT NULL) AND (method IN ('cheque','pay_order')) = (cheque_date IS NOT NULL)");
            t.HasCheckConstraint("ck_credit_payments_allocations", "jsonb_typeof(allocations) = 'array' AND jsonb_array_length(allocations) <= 50");
            t.HasCheckConstraint("ck_credit_payments_four_eyes",
                "(verified_by IS NULL OR verified_by <> recorded_by) AND (rejected_by IS NULL OR rejected_by <> recorded_by)");
            t.HasCheckConstraint("ck_credit_payments_status", """
                CASE status
                WHEN 'recorded' THEN num_nonnulls(verified_by, verified_at, cleared_on, rejected_by, rejected_at, bounced_by, bounced_at, decision_reason) = 0
                WHEN 'verified' THEN num_nonnulls(verified_by, verified_at, cleared_on) = 3
                    AND num_nonnulls(rejected_by, rejected_at, bounced_by, bounced_at, decision_reason) = 0
                WHEN 'rejected' THEN num_nonnulls(rejected_by, rejected_at, decision_reason) = 3
                    AND num_nonnulls(verified_by, verified_at, cleared_on, bounced_by, bounced_at) = 0
                WHEN 'bounced' THEN num_nonnulls(verified_by, verified_at, cleared_on, bounced_by, bounced_at, decision_reason) = 6
                    AND num_nonnulls(rejected_by, rejected_at) = 0 AND method <> 'cash'
                ELSE false END
                """);
        });
        b.MapAudit();
        b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.Property(p => p.CompanyId).HasColumnName("company_id");
        b.Property(p => p.Method).HasColumnName("method").HasMaxLength(20).HasConversion(new SnakeCaseEnumConverter<CreditPaymentMethod>());
        b.Property(p => p.AmountMinor).HasColumnName("amount_minor");
        b.Property(p => p.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(p => p.Reference).HasColumnName("reference").HasMaxLength(CreditPayment.MaxReferenceLength).IsRequired();
        b.Property(p => p.DrawerBank).HasColumnName("drawer_bank").HasMaxLength(CreditPayment.MaxBankLength);
        b.Property(p => p.ReceivedOn).HasColumnName("received_on");
        b.Property(p => p.ChequeDate).HasColumnName("cheque_date");
        b.Property(p => p.LateEntryReason).HasColumnName("late_entry_reason").HasMaxLength(CreditPayment.MaxTextLength);
        b.Property(p => p.Allocations).HasColumnName("allocations").HasColumnType("jsonb").IsRequired().HasConversion(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<List<CreditAllocationRequest>>(v, JsonSerializerOptions.Default) ?? new List<CreditAllocationRequest>(),
            new ValueComparer<IReadOnlyList<CreditAllocationRequest>>((x, y) => x!.SequenceEqual(y!), v => v.Count, v => v.ToList()));
        b.Property(p => p.Note).HasColumnName("note").HasMaxLength(CreditPayment.MaxTextLength);
        b.Property(p => p.Status).HasColumnName("status").HasMaxLength(20).HasConversion(new SnakeCaseEnumConverter<CreditPaymentStatus>());
        b.Property(p => p.RecordedBy).HasColumnName("recorded_by");
        b.Property(p => p.VerifiedBy).HasColumnName("verified_by");
        b.Property(p => p.VerifiedAtUtc).HasColumnName("verified_at");
        b.Property(p => p.ClearedOn).HasColumnName("cleared_on");
        b.Property(p => p.RejectedBy).HasColumnName("rejected_by");
        b.Property(p => p.RejectedAtUtc).HasColumnName("rejected_at");
        b.Property(p => p.BouncedBy).HasColumnName("bounced_by");
        b.Property(p => p.BouncedAtUtc).HasColumnName("bounced_at");
        b.Property(p => p.DecisionReason).HasColumnName("decision_reason").HasMaxLength(CreditPayment.MaxTextLength);
        b.Property(p => p.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(CreditPayment.MaxIdempotencyKeyLength).IsRequired();
        b.Property(p => p.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsFixedLength().IsRequired();

        b.HasIndex(p => p.Reference).IsUnique().HasDatabaseName("ux_credit_payments_transfer_reference")
            .HasFilter($"status <> 'rejected' AND method IN ({TransferMethods})");
        b.HasIndex(p => new { p.CompanyId, p.DrawerBank, p.Reference }).IsUnique().HasDatabaseName("ux_credit_payments_instrument_reference")
            .HasFilter($"status <> 'rejected' AND method IN ({InstrumentMethods})");
        b.HasIndex(p => new { p.RecordedBy, p.IdempotencyKey }).IsUnique().HasDatabaseName("ux_credit_payments_idempotency");
        b.HasIndex(p => new { p.CompanyId, p.ReceivedOn }).HasDatabaseName("ix_credit_payments_company");
        b.HasIndex(p => new { p.Status, p.CreatedAtUtc }).HasDatabaseName("ix_credit_payments_status");
        // A user or company with a payment is never deleted (§12: erasure pseudonymises, ids stay); a verifier is a real user.
        b.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(p => p.RecordedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(p => p.VerifiedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Adjustments (ADR-0010 §8.3): the CHECKs repeat the shape per kind and status and approver ≠ requester (§2.3).</summary>
internal sealed class CreditAdjustmentConfiguration : IEntityTypeConfiguration<CreditAdjustment>
{
    public void Configure(EntityTypeBuilder<CreditAdjustment> b)
    {
        const string max = CreditPaymentConfiguration.MaxMinor;
        b.ToTable("credit_adjustments", t =>
        {
            t.HasCheckConstraint("ck_credit_adjustments_kind", """
                CASE kind
                WHEN 'write_off' THEN amount_minor > 0 AND order_id IS NOT NULL AND num_nonnulls(payment_id, credit_note_id, payout_reference) = 0
                WHEN 'on_account_correction' THEN amount_minor <> 0 AND order_id IS NULL AND num_nonnulls(payment_id, credit_note_id) = 1
                    AND payout_reference IS NULL
                WHEN 'on_account_payout' THEN amount_minor > 0 AND order_id IS NULL AND num_nonnulls(payment_id, credit_note_id) = 1
                    AND payout_reference ~ '^[A-Z0-9]{4,40}$'
                ELSE false END
                """);
            t.HasCheckConstraint("ck_credit_adjustments_amount", $"amount_minor BETWEEN -{max} AND {max} AND currency = 'BDT' AND length(reason) >= 10");
            t.HasCheckConstraint("ck_credit_adjustments_four_eyes", "approved_by IS NULL OR approved_by <> requested_by");
            t.HasCheckConstraint("ck_credit_adjustments_status", """
                CASE status
                WHEN 'requested' THEN num_nonnulls(approved_by, approved_at, rejected_by, rejected_at, decision_reason) = 0
                WHEN 'approved' THEN num_nonnulls(approved_by, approved_at) = 2 AND num_nonnulls(rejected_by, rejected_at, decision_reason) = 0
                WHEN 'rejected' THEN num_nonnulls(rejected_by, rejected_at, decision_reason) = 3 AND num_nonnulls(approved_by, approved_at) = 0
                ELSE false END
                """);
        });
        b.MapAudit();
        b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.Property(a => a.CompanyId).HasColumnName("company_id");
        b.Property(a => a.Kind).HasColumnName("kind").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<CreditAdjustmentKind>());
        b.Property(a => a.AmountMinor).HasColumnName("amount_minor");
        b.Property(a => a.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(a => a.OrderId).HasColumnName("order_id");
        b.Property(a => a.PaymentId).HasColumnName("payment_id");
        b.Property(a => a.CreditNoteId).HasColumnName("credit_note_id");
        b.Property(a => a.PayoutReference).HasColumnName("payout_reference").HasMaxLength(CreditPayment.MaxReferenceLength);
        b.Property(a => a.Reason).HasColumnName("reason").HasMaxLength(CreditAdjustment.MaxReasonLength).IsRequired();
        b.Property(a => a.Status).HasColumnName("status").HasMaxLength(20).HasConversion(new SnakeCaseEnumConverter<CreditAdjustmentStatus>());
        b.Property(a => a.RequestedBy).HasColumnName("requested_by");
        b.Property(a => a.ApprovedBy).HasColumnName("approved_by");
        b.Property(a => a.ApprovedAtUtc).HasColumnName("approved_at");
        b.Property(a => a.RejectedBy).HasColumnName("rejected_by");
        b.Property(a => a.RejectedAtUtc).HasColumnName("rejected_at");
        b.Property(a => a.DecisionReason).HasColumnName("decision_reason").HasMaxLength(CreditAdjustment.MaxReasonLength);

        b.HasIndex(a => new { a.CompanyId, a.CreatedAtUtc }).HasDatabaseName("ix_credit_adjustments_company");
        b.HasIndex(a => new { a.Status, a.CreatedAtUtc }).HasDatabaseName("ix_credit_adjustments_status");
        b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Order>().WithMany().HasForeignKey(a => a.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CreditPayment>().WithMany().HasForeignKey(a => a.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(a => a.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(a => a.ApprovedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
