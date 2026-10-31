using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShopVerse.Domain.Identity;
using ShopVerse.Domain.Orders;
using ShopVerse.Domain.Payments;

namespace ShopVerse.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("orders", t =>
        {
            t.HasCheckConstraint("ck_orders_money", "subtotal >= 0 AND discount_total >= 0 AND tax_total >= 0 AND shipping_total >= 0 AND grand_total >= 0");
            t.HasCheckConstraint("ck_orders_discount", "discount_total <= subtotal");
        });
        b.MapAudit();
        b.IgnoreEvents();
        b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.Property(o => o.OrderNumber).HasColumnName("order_number").HasMaxLength(16).IsRequired();
        b.Property(o => o.UserId).HasColumnName("user_id");
        b.Property(o => o.CompanyId).HasColumnName("company_id");
        b.Property(o => o.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30);
        b.Property(o => o.TaxMode).HasColumnName("tax_mode").HasConversion<string>().HasMaxLength(10);
        b.Property(o => o.Currency).HasColumnName("currency").HasMaxLength(3);
        b.Property(o => o.SubtotalMinor).HasColumnName("subtotal");
        b.Property(o => o.DiscountMinor).HasColumnName("discount_total");
        b.Property(o => o.TaxMinor).HasColumnName("tax_total");
        b.Property(o => o.ShippingMinor).HasColumnName("shipping_total");
        b.Property(o => o.GrandTotalMinor).HasColumnName("grand_total");
        b.Property(o => o.ShipToJson).HasColumnName("ship_to").HasColumnType("jsonb");
        b.Property(o => o.PlacedAtUtc).HasColumnName("placed_at");
        b.Property(o => o.EventSequence).HasColumnName("event_seq");

        b.HasOne<User>().WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(o => o.SubOrders).WithOne().HasForeignKey(s => s.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(o => o.Events).WithOne().HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(o => o.SubOrders).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(o => o.Events).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(o => o.OrderNumber).IsUnique().HasDatabaseName("ux_orders_number");
        b.HasIndex(o => new { o.UserId, o.PlacedAtUtc }).HasDatabaseName("ix_orders_user_placed");
        b.HasIndex(o => o.Status).HasDatabaseName("ix_orders_status");
    }
}

internal sealed class SubOrderConfiguration : IEntityTypeConfiguration<SubOrder>
{
    public void Configure(EntityTypeBuilder<SubOrder> b)
    {
        b.ToTable("sub_orders", t =>
        {
            t.HasCheckConstraint("ck_sub_orders_money", "goods_net >= 0 AND discount_total >= 0 AND tax_total >= 0 AND shipping_total >= 0 AND total >= 0");
            t.HasCheckConstraint("ck_sub_orders_commission", "commission_rate BETWEEN 0 AND 1 AND commission >= 0 AND seller_earning >= 0 AND commission + seller_earning = goods_net");
        });
        b.MapAudit();
        b.Property(s => s.OrderId).HasColumnName("order_id");
        b.Property(s => s.SellerId).HasColumnName("seller_id");
        b.Property(s => s.SubOrderNumber).HasColumnName("sub_order_number").HasMaxLength(20).IsRequired();
        b.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30);
        b.Property(s => s.GoodsNetMinor).HasColumnName("goods_net");
        b.Property(s => s.DiscountMinor).HasColumnName("discount_total");
        b.Property(s => s.TaxMinor).HasColumnName("tax_total");
        b.Property(s => s.ShippingMinor).HasColumnName("shipping_total");
        b.Property(s => s.TotalMinor).HasColumnName("total");
        b.Property(s => s.CommissionRate).HasColumnName("commission_rate").HasPrecision(5, 4);
        b.Property(s => s.CommissionMinor).HasColumnName("commission");
        b.Property(s => s.SellerEarningMinor).HasColumnName("seller_earning");

        b.HasMany(s => s.Items).WithOne().HasForeignKey(i => i.SubOrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(s => s.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasIndex(s => s.SubOrderNumber).IsUnique().HasDatabaseName("ux_sub_orders_number");
        b.HasIndex(s => new { s.SellerId, s.Status }).HasDatabaseName("ix_sub_orders_seller_status");
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> b)
    {
        b.ToTable("order_items", t =>
        {
            t.HasCheckConstraint("ck_order_items_qty", "quantity > 0");
            t.HasCheckConstraint("ck_order_items_money", "unit_price >= 0 AND gross >= 0 AND discount >= 0 AND discount <= gross AND net >= 0 AND tax >= 0 AND total = net + tax");
        });
        b.MapAudit(updated: false);
        b.Property(i => i.SubOrderId).HasColumnName("sub_order_id");
        b.Property(i => i.VariantId).HasColumnName("variant_id");
        b.Property(i => i.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        b.Property(i => i.Title).HasColumnName("title").HasMaxLength(255).IsRequired();
        b.Property(i => i.Quantity).HasColumnName("quantity");
        b.Property(i => i.UnitPriceMinor).HasColumnName("unit_price");
        b.Property(i => i.VatRate).HasColumnName("vat_rate").HasPrecision(5, 4);
        b.Property(i => i.GrossMinor).HasColumnName("gross");
        b.Property(i => i.DiscountMinor).HasColumnName("discount");
        b.Property(i => i.NetMinor).HasColumnName("net");
        b.Property(i => i.TaxMinor).HasColumnName("tax");
        b.Property(i => i.TotalMinor).HasColumnName("total");
        b.HasIndex(i => i.VariantId).HasDatabaseName("ix_order_items_variant");
    }
}

internal sealed class OrderEventRecordConfiguration : IEntityTypeConfiguration<OrderEventRecord>
{
    public void Configure(EntityTypeBuilder<OrderEventRecord> b)
    {
        b.ToTable("order_events");
        b.MapAudit(updated: false);
        b.Property(e => e.OrderId).HasColumnName("order_id");
        b.Property(e => e.Sequence).HasColumnName("seq");
        b.Property(e => e.FromStatus).HasColumnName("from_status").HasConversion<string>().HasMaxLength(30);
        b.Property(e => e.ToStatus).HasColumnName("to_status").HasConversion<string>().HasMaxLength(30);
        b.Property(e => e.ActorId).HasColumnName("actor_id");
        b.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(500);
        b.Property(e => e.AtUtc).HasColumnName("at");
        b.HasIndex(e => new { e.OrderId, e.Sequence }).IsUnique().HasDatabaseName("ux_order_events_order_seq");
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("idempotency_keys");
        b.MapAudit(updated: false);
        b.Property(r => r.UserId).HasColumnName("user_id");
        b.Property(r => r.Key).HasColumnName("key").HasMaxLength(100).IsRequired();
        b.Property(r => r.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsRequired();
        b.Property(r => r.OrderId).HasColumnName("order_id");
        b.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(r => new { r.UserId, r.Key }).IsUnique().HasDatabaseName("ux_idempotency_user_key");
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments", t => t.HasCheckConstraint("ck_payments_amount", "amount > 0"));
        b.MapAudit();
        b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.Property(p => p.OrderId).HasColumnName("order_id");
        b.Property(p => p.Provider).HasColumnName("provider").HasMaxLength(30).IsRequired();
        b.Property(p => p.ProviderRef).HasColumnName("provider_ref").HasMaxLength(150).IsRequired();
        b.Property(p => p.AmountMinor).HasColumnName("amount");
        b.Property(p => p.Currency).HasColumnName("currency").HasMaxLength(3);
        b.Property(p => p.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(p => p.Note).HasColumnName("note").HasMaxLength(500);
        b.Property(p => p.CapturedAtUtc).HasColumnName("captured_at");
        b.HasOne<Order>().WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(p => new { p.Provider, p.ProviderRef }).IsUnique().HasDatabaseName("ux_payments_provider_ref");
        b.HasIndex(p => p.OrderId).HasDatabaseName("ix_payments_order");
    }
}

internal sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> b)
    {
        b.ToTable("payment_events");
        b.MapAudit(updated: false);
        b.Property(e => e.Provider).HasColumnName("provider").HasMaxLength(30).IsRequired();
        b.Property(e => e.EventId).HasColumnName("event_id").HasMaxLength(150).IsRequired();
        b.Property(e => e.ProviderRef).HasColumnName("provider_ref").HasMaxLength(150).IsRequired();
        b.Property(e => e.Outcome).HasColumnName("outcome").HasMaxLength(30);
        b.HasIndex(e => new { e.Provider, e.EventId }).IsUnique().HasDatabaseName("ux_payment_events_provider_event");
    }
}

internal sealed class PromotionDefinitionConfiguration : IEntityTypeConfiguration<ShopVerse.Domain.Pricing.PromotionDefinition>
{
    public void Configure(EntityTypeBuilder<ShopVerse.Domain.Pricing.PromotionDefinition> b)
    {
        b.ToTable("promotions", t =>
        {
            t.HasCheckConstraint("ck_promotions_percent", "percent BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_promotions_limits", "(global_limit IS NULL OR global_limit >= 1) AND (per_user_limit IS NULL OR per_user_limit >= 1)");
            t.HasCheckConstraint("ck_promotions_redeemed", "redeemed_count >= 0");
        });
        b.MapAudit();
        b.Property(p => p.Name).HasColumnName("name").HasMaxLength(150).IsRequired();
        b.Property(p => p.Code).HasColumnName("code").HasMaxLength(40);
        b.Property(p => p.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20);
        b.Property(p => p.ScopeKind).HasColumnName("scope_kind").HasConversion<string>().HasMaxLength(20);
        b.Property(p => p.ScopeTargetId).HasColumnName("scope_target_id");
        b.Property(p => p.Priority).HasColumnName("priority");
        b.Property(p => p.Exclusive).HasColumnName("exclusive");
        b.Property(p => p.Percent).HasColumnName("percent").HasPrecision(5, 2);
        b.Property(p => p.FixedMinor).HasColumnName("fixed_amount");
        b.Property(p => p.MaxDiscountMinor).HasColumnName("max_discount");
        b.Property(p => p.MinSubtotalMinor).HasColumnName("min_subtotal");
        b.Property(p => p.VolumeBreaksJson).HasColumnName("volume_breaks").HasColumnType("jsonb");
        b.Property(p => p.SpendBreaksJson).HasColumnName("spend_breaks").HasColumnType("jsonb");
        b.Property(p => p.StartsAtUtc).HasColumnName("starts_at");
        b.Property(p => p.EndsAtUtc).HasColumnName("ends_at");
        b.Property(p => p.GlobalLimit).HasColumnName("global_limit");
        b.Property(p => p.PerUserLimit).HasColumnName("per_user_limit");
        b.Property(p => p.IsActive).HasColumnName("is_active");
        b.Property(p => p.RedeemedCount).HasColumnName("redeemed_count");
        b.Ignore(p => p.IsAutomatic);
        b.HasIndex(p => p.Code).IsUnique().HasFilter("code IS NOT NULL").HasDatabaseName("ux_promotions_code");
        b.HasIndex(p => new { p.IsActive, p.Code }).HasDatabaseName("ix_promotions_active");
    }
}

internal sealed class PromotionUserUsageConfiguration : IEntityTypeConfiguration<ShopVerse.Domain.Pricing.PromotionUserUsage>
{
    public void Configure(EntityTypeBuilder<ShopVerse.Domain.Pricing.PromotionUserUsage> b)
    {
        b.ToTable("promotion_user_usage", t => t.HasCheckConstraint("ck_promotion_user_usage_uses", "uses >= 0"));
        b.HasKey(u => new { u.PromotionId, u.UserId });
        b.Property(u => u.PromotionId).HasColumnName("promotion_id");
        b.Property(u => u.UserId).HasColumnName("user_id");
        b.Property(u => u.Uses).HasColumnName("uses");
        b.HasOne<ShopVerse.Domain.Pricing.PromotionDefinition>().WithMany().HasForeignKey(u => u.PromotionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PromotionRedemptionConfiguration : IEntityTypeConfiguration<ShopVerse.Domain.Pricing.PromotionRedemption>
{
    public void Configure(EntityTypeBuilder<ShopVerse.Domain.Pricing.PromotionRedemption> b)
    {
        b.ToTable("promotion_redemptions", t => t.HasCheckConstraint("ck_promotion_redemptions_discount", "discount > 0"));
        b.MapAudit(updated: false);
        b.Property(r => r.OrderId).HasColumnName("order_id");
        b.Property(r => r.PromotionId).HasColumnName("promotion_id");
        b.Property(r => r.UserId).HasColumnName("user_id");
        b.Property(r => r.DiscountMinor).HasColumnName("discount");
        b.HasOne<Order>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ShopVerse.Domain.Pricing.PromotionDefinition>().WithMany().HasForeignKey(r => r.PromotionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(r => new { r.OrderId, r.PromotionId }).IsUnique().HasDatabaseName("ux_promotion_redemptions_order_promo");
    }
}
