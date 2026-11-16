using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Orders;

namespace Mechware.Infrastructure.Persistence.Configurations;

/// <summary>
/// The credit ledger (ADR-0010 §2). The CHECKs repeat <see cref="CreditLedgerEntry.Validate"/>; <see cref="CreditLedgerSql"/>
/// adds the append-only trigger and the deferred chain / cached-total check.
/// </summary>
internal sealed class CreditLedgerEntryConfiguration : IEntityTypeConfiguration<CreditLedgerEntry>
{
    private const string Kinds = "'charge','payment_applied','payment_on_account','on_account_applied','payment_reversed','charge_reduced','write_off','on_account_correction','on_account_payout'";

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
                ELSE false END
                """);
        });
        b.MapAudit(updated: false);
        b.Property(e => e.CompanyId).HasColumnName("company_id");
        b.Property(e => e.Seq).HasColumnName("seq");
        b.Property(e => e.EventId).HasColumnName("event_id");
        b.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<CreditLedgerKind>());
        b.Property(e => e.OrderId).HasColumnName("order_id");
        // Source ids of tables that come with B3.4 (payments), B3.6 (adjustments) and E6 (credit notes); their foreign keys come with them.
        b.Property(e => e.PaymentId).HasColumnName("payment_id");
        b.Property(e => e.AdjustmentId).HasColumnName("adjustment_id");
        b.Property(e => e.CreditNoteId).HasColumnName("credit_note_id");
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
    }
}
