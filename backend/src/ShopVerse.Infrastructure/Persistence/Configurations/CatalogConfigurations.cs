using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Primitives;
using ShopVerse.Domain.Inventory;
using ShopVerse.Domain.Reviews;
using ShopVerse.Domain.Sellers;
using ShopVerse.Infrastructure.Persistence.Outbox;

namespace ShopVerse.Infrastructure.Persistence.Configurations;

internal static class EntityBaseMapping
{
    public static void MapAudit<T>(this EntityTypeBuilder<T> b, bool updated = true) where T : Entity<Guid>
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(e => e.CreatedAtUtc).HasColumnName("created_at");
        if (updated) b.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at");
        else b.Ignore(e => e.UpdatedAtUtc);
    }

    public static void IgnoreEvents<T>(this EntityTypeBuilder<T> b) where T : AggregateRoot<Guid> => b.Ignore(e => e.DomainEvents);
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("categories", t =>
        {
            t.HasCheckConstraint("ck_categories_vat_rate", "vat_rate BETWEEN 0.0000 AND 0.5000");
            t.HasCheckConstraint("ck_categories_depth", "depth BETWEEN 0 AND 2");
        });
        b.MapAudit();
        b.IgnoreEvents();
        b.Property(c => c.ParentId).HasColumnName("parent_id");
        b.Property(c => c.NameEn).HasColumnName("name_en").HasMaxLength(150).IsRequired();
        b.Property(c => c.NameBn).HasColumnName("name_bn").HasMaxLength(150).IsRequired();
        b.Property(c => c.Slug).HasColumnName("slug").HasMaxLength(150).IsRequired();
        b.Property(c => c.MaterializedPath).HasColumnName("materialized_path").HasMaxLength(255).IsRequired();
        b.Property(c => c.Depth).HasColumnName("depth");
        b.Property(c => c.SortOrder).HasColumnName("sort_order");
        b.Property(c => c.ImageUrl).HasColumnName("image_url");
        b.Property(c => c.CommissionRate).HasColumnName("commission_rate").HasPrecision(5, 4);
        b.Property(c => c.VatRate).HasColumnName("vat_rate").HasPrecision(5, 4);
        b.Property(c => c.AttributeSchemaJson).HasColumnName("attribute_schema").HasColumnType("jsonb");
        b.Property(c => c.SeoTitle).HasColumnName("seo_title").HasMaxLength(255);
        b.Property(c => c.SeoDescription).HasColumnName("seo_description");

        b.HasIndex(c => c.Slug).IsUnique().HasDatabaseName("ux_categories_slug");
        b.HasIndex(c => c.MaterializedPath).HasDatabaseName("ix_categories_path").HasOperators("varchar_pattern_ops");
        b.HasIndex(c => c.ParentId).HasDatabaseName("ix_categories_parent_id");
        b.HasOne<Category>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> b)
    {
        b.ToTable("brands");
        b.MapAudit(updated: false);
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        b.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(100).IsRequired();
        b.Property(x => x.LogoUrl).HasColumnName("logo_url");
        b.Property(x => x.IsVerified).HasColumnName("is_verified");
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_brands_name");
        b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_brands_slug");
    }
}

internal sealed class SellerConfiguration : IEntityTypeConfiguration<Seller>
{
    public void Configure(EntityTypeBuilder<Seller> b)
    {
        b.ToTable("sellers");
        b.MapAudit();
        b.Property(s => s.UserId).HasColumnName("user_id");
        b.Property(s => s.StoreName).HasColumnName("store_name").HasMaxLength(150).IsRequired();
        b.Property(s => s.Slug).HasColumnName("slug").HasMaxLength(150).IsRequired();
        b.Property(s => s.LogoUrl).HasColumnName("logo_url");
        b.Property(s => s.BannerUrl).HasColumnName("banner_url");
        b.Property(s => s.Description).HasColumnName("description");
        b.Property(s => s.Status).HasColumnName("status").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<SellerStatus>());
        b.Property(s => s.CommissionOverrideRate).HasColumnName("commission_override_rate").HasPrecision(5, 4);
        b.Property(s => s.ApprovedListingsCount).HasColumnName("approved_listings_count");
        b.Property(s => s.RatingAvg).HasColumnName("rating_avg").HasPrecision(3, 2);
        b.Property(s => s.RatingCount).HasColumnName("rating_count");
        b.Property(s => s.ResponseRatePercent).HasColumnName("response_rate_percent").HasPrecision(5, 2);
        b.Property(s => s.JoinedAtUtc).HasColumnName("joined_at");
        b.Property(s => s.ApprovedAtUtc).HasColumnName("approved_at");
        b.HasIndex(s => s.Slug).IsUnique().HasDatabaseName("ux_sellers_slug");
        b.HasIndex(s => s.Status).HasDatabaseName("ix_sellers_status");
        b.HasOne<ShopVerse.Domain.Identity.User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("products");
        b.MapAudit();
        b.IgnoreEvents();
        b.Property(p => p.SellerId).HasColumnName("seller_id");
        b.Property(p => p.CategoryId).HasColumnName("category_id");
        b.Property(p => p.BrandId).HasColumnName("brand_id");
        b.Property(p => p.TitleEn).HasColumnName("title_en").HasMaxLength(255).IsRequired();
        b.Property(p => p.TitleBn).HasColumnName("title_bn").HasMaxLength(255).IsRequired();
        b.Property(p => p.Slug).HasColumnName("slug").HasMaxLength(255).IsRequired();
        b.Property(p => p.ShortDescriptionEn).HasColumnName("short_description_en").IsRequired();
        b.Property(p => p.ShortDescriptionBn).HasColumnName("short_description_bn").IsRequired();
        b.Property(p => p.DescriptionHtml).HasColumnName("description_html").IsRequired();
        b.Property(p => p.HighlightsJson).HasColumnName("highlights").HasColumnType("jsonb");
        b.Property(p => p.SpecsJson).HasColumnName("specs").HasColumnType("jsonb");
        b.Property(p => p.DatasheetUrl).HasColumnName("datasheet_url");
        b.Property(p => p.CadModelUrl).HasColumnName("cad_model_url");
        b.Property(p => p.Status).HasColumnName("status").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<ProductStatus>());
        b.Property(p => p.RejectionReason).HasColumnName("rejection_reason");
        b.Property(p => p.Condition).HasColumnName("condition").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<ProductCondition>());
        b.Property(p => p.WarrantyMonths).HasColumnName("warranty_months");
        b.Property(p => p.ReturnPolicyDays).HasColumnName("return_policy_days");
        b.Property(p => p.WeightKg).HasColumnName("weight_kg").HasPrecision(8, 3);
        b.Property(p => p.LengthCm).HasColumnName("length_cm").HasPrecision(8, 2);
        b.Property(p => p.WidthCm).HasColumnName("width_cm").HasPrecision(8, 2);
        b.Property(p => p.HeightCm).HasColumnName("height_cm").HasPrecision(8, 2);
        b.Property(p => p.RatingAvg).HasColumnName("rating_avg").HasPrecision(3, 2);
        b.Property(p => p.RatingCount).HasColumnName("rating_count");
        b.Property(p => p.SoldCount).HasColumnName("sold_count");
        b.Property(p => p.DeletedAtUtc).HasColumnName("deleted_at");

        b.HasIndex(p => p.Slug).IsUnique().HasDatabaseName("ux_products_slug").HasFilter("deleted_at IS NULL");
        b.HasIndex(p => new { p.SellerId, p.Status }).HasDatabaseName("ix_products_seller_status");
        b.HasIndex(p => new { p.CategoryId, p.Status }).HasDatabaseName("ix_products_category_status");
        b.HasIndex(p => p.BrandId).HasDatabaseName("ix_products_brand_id");
        b.HasIndex(p => p.SpecsJson).HasDatabaseName("ix_products_specs_gin").HasMethod("gin");

        b.HasOne<Seller>().WithMany().HasForeignKey(p => p.SellerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Category>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Brand>().WithMany().HasForeignKey(p => p.BrandId).OnDelete(DeleteBehavior.SetNull);

        b.HasMany(p => p.Variants).WithOne().HasForeignKey(v => v.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(p => p.Media).WithOne().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(p => p.AttributeValues).WithOne().HasForeignKey(a => a.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(p => p.Variants).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(p => p.Media).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(p => p.AttributeValues).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    public void Configure(EntityTypeBuilder<ProductVariant> b)
    {
        b.ToTable("product_variants", t =>
        {
            t.HasCheckConstraint("ck_variants_price", "price_minor > 0");
            t.HasCheckConstraint("ck_variants_sale_price", "sale_price_minor IS NULL OR sale_price_minor > 0");
        });
        b.MapAudit();
        b.Property(v => v.ProductId).HasColumnName("product_id");
        b.Property(v => v.Sku).HasColumnName("sku").HasMaxLength(100).IsRequired();
        b.Property(v => v.Barcode).HasColumnName("barcode").HasMaxLength(100);

        var comparer = new ValueComparer<SortedDictionary<string, string>>(
            (a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
            v => JsonSerializer.Serialize(v, Json).GetHashCode(),
            v => new SortedDictionary<string, string>(v, StringComparer.Ordinal));
        b.Property(v => v.OptionValues).HasColumnName("option_values").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => new SortedDictionary<string, string>(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(v, Json) ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal))
            .Metadata.SetValueComparer(comparer);

        b.Property(v => v.PriceMinor).HasColumnName("price_minor");
        b.Property(v => v.SalePriceMinor).HasColumnName("sale_price_minor");
        b.Property(v => v.SaleStartsAtUtc).HasColumnName("sale_starts_at");
        b.Property(v => v.SaleEndsAtUtc).HasColumnName("sale_ends_at");
        b.Property(v => v.CompareAtPriceMinor).HasColumnName("compare_at_price_minor");
        b.Property(v => v.CostPriceMinor).HasColumnName("cost_price_minor");
        b.Property(v => v.WeightKg).HasColumnName("weight_kg").HasPrecision(8, 3);
        b.Property(v => v.Currency).HasColumnName("currency").HasMaxLength(3)
            .HasConversion(c => c.ToIsoCode(), s => CurrencyExtensions.Parse(s));
        b.Property(v => v.IsDefault).HasColumnName("is_default");
        b.Property(v => v.Status).HasColumnName("status").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<VariantStatus>());
        b.Ignore(v => v.Price);
        b.Ignore(v => v.SalePrice);
        b.Ignore(v => v.CompareAtPrice);
        b.Ignore(v => v.CostPrice);

        b.HasIndex(v => v.Sku).IsUnique().HasDatabaseName("ux_product_variants_sku");
        b.HasIndex(v => v.ProductId).HasDatabaseName("ix_product_variants_product_id");
    }
}

internal sealed class ProductMediaConfiguration : IEntityTypeConfiguration<ProductMedia>
{
    public void Configure(EntityTypeBuilder<ProductMedia> b)
    {
        b.ToTable("product_media");
        b.MapAudit(updated: false);
        b.Property(m => m.ProductId).HasColumnName("product_id");
        b.Property(m => m.VariantId).HasColumnName("variant_id");
        b.Property(m => m.Url).HasColumnName("url").IsRequired();
        b.Property(m => m.AltText).HasColumnName("alt_text").HasMaxLength(255);
        b.Property(m => m.Type).HasColumnName("media_type").HasMaxLength(20).HasConversion(new SnakeCaseEnumConverter<MediaType>());
        b.Property(m => m.Position).HasColumnName("position");
        b.Property(m => m.Width).HasColumnName("width");
        b.Property(m => m.Height).HasColumnName("height");
        b.Property(m => m.BlurDataUrl).HasColumnName("blur_data_url");
        b.HasIndex(m => new { m.ProductId, m.Position }).HasDatabaseName("ix_product_media_product_position");
        b.HasOne<ProductVariant>().WithMany().HasForeignKey(m => m.VariantId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AttributeDefinitionConfiguration : IEntityTypeConfiguration<AttributeDefinition>
{
    public void Configure(EntityTypeBuilder<AttributeDefinition> b)
    {
        b.ToTable("attributes");
        b.MapAudit(updated: false);
        b.Property(a => a.NameEn).HasColumnName("name_en").HasMaxLength(100).IsRequired();
        b.Property(a => a.NameBn).HasColumnName("name_bn").HasMaxLength(100).IsRequired();
        b.Property(a => a.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
        b.Property(a => a.DataType).HasColumnName("data_type").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<AttributeDataType>());
        b.Property(a => a.Unit).HasColumnName("unit").HasMaxLength(30);
        b.Property(a => a.IsFilterable).HasColumnName("is_filterable");
        b.HasIndex(a => a.Code).IsUnique().HasDatabaseName("ux_attributes_code");
        b.HasMany(a => a.Values).WithOne().HasForeignKey(v => v.AttributeId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(a => a.Values).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class AttributeValueConfiguration : IEntityTypeConfiguration<AttributeValue>
{
    public void Configure(EntityTypeBuilder<AttributeValue> b)
    {
        b.ToTable("attribute_values");
        b.MapAudit(updated: false);
        b.Property(v => v.AttributeId).HasColumnName("attribute_id");
        b.Property(v => v.ValueEn).HasColumnName("value_en").HasMaxLength(150).IsRequired();
        b.Property(v => v.ValueBn).HasColumnName("value_bn").HasMaxLength(150).IsRequired();
        b.Property(v => v.SortOrder).HasColumnName("sort_order");
    }
}

internal sealed class ProductAttributeValueConfiguration : IEntityTypeConfiguration<ProductAttributeValue>
{
    public void Configure(EntityTypeBuilder<ProductAttributeValue> b)
    {
        b.ToTable("product_attribute_values");
        b.HasKey(p => new { p.ProductId, p.AttributeValueId });
        b.Property(p => p.ProductId).HasColumnName("product_id");
        b.Property(p => p.AttributeValueId).HasColumnName("attribute_value_id");
        b.HasIndex(p => new { p.AttributeValueId, p.ProductId }).HasDatabaseName("ix_product_attr_val_lookup");
        b.HasOne<AttributeValue>().WithMany().HasForeignKey(p => p.AttributeValueId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SlugHistoryConfiguration : IEntityTypeConfiguration<SlugHistory>
{
    public void Configure(EntityTypeBuilder<SlugHistory> b)
    {
        b.ToTable("slug_histories");
        b.MapAudit(updated: false);
        b.Property(s => s.EntityType).HasColumnName("entity_type").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<SlugEntityType>());
        b.Property(s => s.EntityId).HasColumnName("entity_id");
        b.Property(s => s.OldSlug).HasColumnName("old_slug").HasMaxLength(255).IsRequired();
        // Deviation from DATA_MODEL (old_slug alone): product and category slug spaces are independent.
        b.HasIndex(s => new { s.EntityType, s.OldSlug }).IsUnique().HasDatabaseName("ux_slug_histories_type_old_slug");
        b.HasIndex(s => new { s.EntityType, s.EntityId }).HasDatabaseName("ix_slug_histories_entity");
    }
}

internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> b)
    {
        b.ToTable("warehouses");
        b.MapAudit(updated: false);
        b.Property(w => w.SellerId).HasColumnName("seller_id");
        b.Property(w => w.Name).HasColumnName("name").HasMaxLength(150).IsRequired();
        b.Property(w => w.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
        b.Property(w => w.Division).HasColumnName("division").HasMaxLength(100);
        b.Property(w => w.District).HasColumnName("district").HasMaxLength(100);
        b.Property(w => w.Address).HasColumnName("address");
        b.Property(w => w.IsActive).HasColumnName("is_active");
        b.HasIndex(w => w.Code).IsUnique().HasDatabaseName("ux_warehouses_code");
        b.HasOne<Seller>().WithMany().HasForeignKey(w => w.SellerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> b)
    {
        b.ToTable("inventory_items", t =>
        {
            t.HasCheckConstraint("ck_inventory_available", "on_hand >= reserved AND reserved >= 0");
            t.HasCheckConstraint("ck_inventory_on_hand", "on_hand >= 0");
        });
        b.MapAudit();
        b.IgnoreEvents();
        b.Property(i => i.VariantId).HasColumnName("variant_id");
        b.Property(i => i.WarehouseId).HasColumnName("warehouse_id");
        b.Property(i => i.OnHand).HasColumnName("on_hand");
        b.Property(i => i.Reserved).HasColumnName("reserved");
        b.Property(i => i.LowStockThreshold).HasColumnName("low_stock_threshold");
        b.Ignore(i => i.Available);
        b.HasIndex(i => new { i.VariantId, i.WarehouseId }).IsUnique().HasDatabaseName("ux_inventory_variant_warehouse");
        b.HasOne<ProductVariant>().WithMany().HasForeignKey(i => i.VariantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(i => i.WarehouseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> b)
    {
        b.ToTable("stock_reservations", t =>
        {
            t.HasCheckConstraint("ck_reservations_quantity", "quantity > 0");
            t.HasCheckConstraint("ck_reservations_price", "locked_unit_price_minor > 0");
        });
        b.MapAudit();
        b.Property(r => r.VariantId).HasColumnName("variant_id");
        b.Property(r => r.WarehouseId).HasColumnName("warehouse_id");
        b.Property(r => r.ReferenceId).HasColumnName("reference_id");
        b.Property(r => r.Quantity).HasColumnName("quantity");
        b.Property(r => r.LockedUnitPriceMinor).HasColumnName("locked_unit_price_minor");
        b.Property(r => r.Status).HasColumnName("status").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<ReservationStatus>());
        b.Property(r => r.ExpiresAtUtc).HasColumnName("expires_at");
        b.HasIndex(r => r.ExpiresAtUtc).HasDatabaseName("ix_stock_reservations_cleanup").HasFilter("status = 'active'");
        b.HasIndex(r => r.ReferenceId).HasDatabaseName("ix_stock_reservations_reference");
        b.HasOne<ProductVariant>().WithMany().HasForeignKey(r => r.VariantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(r => r.WarehouseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> b)
    {
        b.ToTable("reviews", t => t.HasCheckConstraint("ck_reviews_rating", "rating BETWEEN 1 AND 5"));
        b.MapAudit(updated: false);
        b.Property(r => r.ProductId).HasColumnName("product_id");
        b.Property(r => r.UserId).HasColumnName("user_id");
        b.Property(r => r.OrderItemId).HasColumnName("order_item_id");
        b.Property(r => r.Rating).HasColumnName("rating");
        b.Property(r => r.Title).HasColumnName("title").HasMaxLength(200);
        b.Property(r => r.Body).HasColumnName("body").IsRequired();
        b.Property(r => r.Status).HasColumnName("status").HasMaxLength(30).HasConversion(new SnakeCaseEnumConverter<ReviewStatus>());
        b.Property(r => r.HelpfulCount).HasColumnName("helpful_count");
        b.Property(r => r.SellerReply).HasColumnName("seller_reply");
        b.Property(r => r.SellerRepliedAtUtc).HasColumnName("seller_replied_at");
        b.HasIndex(r => new { r.ProductId, r.Status, r.CreatedAtUtc }).HasDatabaseName("ix_reviews_product_status");
        b.HasIndex(r => r.OrderItemId).IsUnique().HasDatabaseName("ux_reviews_order_item").HasFilter("order_item_id IS NOT NULL");
        b.HasOne<Product>().WithMany().HasForeignKey(r => r.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ShopVerse.Domain.Identity.User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.Media).WithOne().HasForeignKey(m => m.ReviewId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(r => r.Media).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ReviewMediaConfiguration : IEntityTypeConfiguration<ReviewMedia>
{
    public void Configure(EntityTypeBuilder<ReviewMedia> b)
    {
        b.ToTable("review_media");
        b.MapAudit(updated: false);
        b.Property(m => m.ReviewId).HasColumnName("review_id");
        b.Property(m => m.MediaUrl).HasColumnName("media_url").IsRequired();
    }
}

internal sealed class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    public void Configure(EntityTypeBuilder<Question> b)
    {
        b.ToTable("questions");
        b.MapAudit(updated: false);
        b.Property(q => q.ProductId).HasColumnName("product_id");
        b.Property(q => q.UserId).HasColumnName("user_id");
        b.Property(q => q.QuestionText).HasColumnName("question_text").IsRequired();
        b.Property(q => q.IsApproved).HasColumnName("is_approved");
        b.HasIndex(q => q.ProductId).HasDatabaseName("ix_questions_product");
        b.HasOne<Product>().WithMany().HasForeignKey(q => q.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ShopVerse.Domain.Identity.User>().WithMany().HasForeignKey(q => q.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(q => q.Answers).WithOne().HasForeignKey(a => a.QuestionId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(q => q.Answers).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class AnswerConfiguration : IEntityTypeConfiguration<Answer>
{
    public void Configure(EntityTypeBuilder<Answer> b)
    {
        b.ToTable("answers");
        b.MapAudit(updated: false);
        b.Property(a => a.QuestionId).HasColumnName("question_id");
        b.Property(a => a.UserId).HasColumnName("user_id");
        b.Property(a => a.IsSellerOrStaff).HasColumnName("is_seller_or_staff");
        b.Property(a => a.AnswerText).HasColumnName("answer_text").IsRequired();
        b.Property(a => a.IsApproved).HasColumnName("is_approved");
        b.HasOne<ShopVerse.Domain.Identity.User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages");
        b.HasKey(o => o.Id);
        b.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(o => o.EventType).HasColumnName("event_type").HasMaxLength(150).IsRequired();
        b.Property(o => o.AggregateType).HasColumnName("aggregate_type").HasMaxLength(100).IsRequired();
        b.Property(o => o.AggregateId).HasColumnName("aggregate_id");
        b.Property(o => o.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        b.Property(o => o.OccurredAtUtc).HasColumnName("occurred_at");
        b.Property(o => o.ProcessedAtUtc).HasColumnName("processed_at");
        b.Property(o => o.RetryCount).HasColumnName("retry_count");
        b.Property(o => o.Error).HasColumnName("error");
        b.HasIndex(o => new { o.ProcessedAtUtc, o.OccurredAtUtc }).HasDatabaseName("ix_outbox_unprocessed").HasFilter("processed_at IS NULL");
    }
}
