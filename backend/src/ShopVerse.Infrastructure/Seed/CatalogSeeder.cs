using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Domain.Identity;
using ShopVerse.Domain.Inventory;
using ShopVerse.Domain.Reviews;
using ShopVerse.Domain.Sellers;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Seed;

/// <summary>
/// Deterministic demo catalogue (brief Phase 4): 12 categories × 3 levels, 40 brands, 25 sellers, 400+ industrial
/// products with variants, stock, 3 000+ reviews and Q&amp;A. Same <see cref="SeedOptions.RandomSeed"/> ⇒ same data.
/// </summary>
public static class CatalogSeeder
{
    private static readonly string[] ViewCaptions = ["Front view", "Angled view", "Detail", "Packaging", "Rating label", "Side view"];
    private static readonly string[] RejectionReasons =
    [
        "Manufacturer datasheet link missing — please attach the datasheet and resubmit.",
        "Images do not show the actual product label. Please upload photos of the rating plate.",
        "Specification table is incomplete (rated current / voltage missing)."
    ];

    public static async Task<SeedReport> SeedAsync(ShopVerseDbContext db, SeedOptions options, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var report = new SeedReport();

        var marker = DeterministicGuid.From("brand:" + SeedWorld.Brands[0].Name);
        if (await db.Brands.AnyAsync(b => b.Id == marker, ct))
        {
            report.AlreadySeeded = true;
            report.Elapsed = sw.Elapsed;
            return report;
        }

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        var rng = new SeedRandom(options.RandomSeed + 17);
        var plans = CatalogPlanner.Build(options);
        var media = new MediaSink(options, report);
        var anchor = options.AnchorDateUtc;

        // ── Users (25 seller owners + customers) ──────────────────────────────────────────
        var sellerUsers = new List<User>();
        for (var i = 0; i < SeedWorld.Sellers.Length; i++)
        {
            var s = SeedWorld.Sellers[i];
            var user = User.Create($"seller{i + 1:D2}@suppliers.example", Phone(rng, i), null, s.StoreName.Length > 2 ? s.StoreName : "Seller");
            sellerUsers.Add(user);
            db.Users.Add(user);
            db.Entry(user).Property(nameof(User.Id)).CurrentValue = DeterministicGuid.From("seller-user:" + i);
        }

        var customers = new List<User>();
        for (var i = 0; i < options.CustomerCount; i++)
        {
            var female = rng.Chance(0.22);
            var first = rng.Pick(female ? SeedWorld.FemaleFirst : SeedWorld.MaleFirst);
            var last = rng.Pick(SeedWorld.LastNames);
            var user = User.Create($"{first}.{last}.{i + 1}@mail.example".ToLowerInvariant(), Phone(rng, 1000 + i), null, $"{first} {last}");
            customers.Add(user);
            db.Users.Add(user);
            db.Entry(user).Property(nameof(User.Id)).CurrentValue = DeterministicGuid.From("customer:" + i);
        }

        await db.SaveChangesAsync(ct);
        report.Customers = customers.Count;

        // ── Sellers & warehouses ──────────────────────────────────────────────────────────
        var sellers = new List<Seller>();
        var warehouses = new List<Warehouse>
        {
            new(DeterministicGuid.From("wh:dhaka-hub"), null, "MECHWARE Central Hub — Tejgaon", "MW-DHK", "Dhaka", "Dhaka", "Tejgaon Industrial Area, Dhaka 1208"),
            new(DeterministicGuid.From("wh:ctg-hub"), null, "MECHWARE Chattogram Regional Hub", "MW-CTG", "Chattogram", "Chattogram", "Agrabad Commercial Area, Chattogram 4100")
        };

        for (var i = 0; i < SeedWorld.Sellers.Length; i++)
        {
            var s = SeedWorld.Sellers[i];
            var slug = Slug.From(s.StoreName);
            var official = i == 0;
            var seller = new Seller(
                DeterministicGuid.From("seller:" + slug), sellerUsers[i].Id, s.StoreName, slug,
                media.Write($"sellers/{slug}-logo.svg", SeedMedia.SellerLogo(s.StoreName, s.Initials, s.Color)),
                media.Write($"sellers/{slug}-banner.svg", SeedMedia.SellerBanner(s.StoreName, s.City, s.Color)),
                s.Blurb,
                official ? 4.8m : Math.Round(3.9m + (decimal)rng.NextDouble() * 1.0m, 2),
                official ? 2840 : rng.Next(120, 3800),
                official ? 99m : Math.Round(82m + (decimal)rng.NextDouble() * 17m, 2),
                anchor.AddDays(-rng.Next(300, 2200)));
            seller.Approve(anchor.AddDays(-rng.Next(30, 280)));
            sellers.Add(seller);
            db.Sellers.Add(seller);

            if (!official)
            {
                warehouses.Add(new Warehouse(DeterministicGuid.From("wh:" + slug), seller.Id,
                    $"{s.StoreName} warehouse", $"S{i + 1:D2}-{new string(s.City.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant()}",
                    s.Division, s.City, $"{s.City}, Bangladesh"));
            }
            else
            {
                warehouses.Add(warehouses[0]);   // official store stocks the hubs; placeholder keeps index alignment
            }
        }

        db.Warehouses.AddRange(warehouses.Distinct());
        report.Sellers = sellers.Count;
        report.Warehouses = warehouses.Distinct().Count();
        await db.SaveChangesAsync(ct);

        // ── Categories ────────────────────────────────────────────────────────────────────
        var categoryIds = new Dictionary<string, Guid>();
        var filterSchemaByLeaf = plans.GroupBy(p => p.LeafSlug).ToDictionary(g => g.Key, g => FilterSchemaJson(g.SelectMany(p => p.Family.FilterSpecs).Distinct()));
        var rootOrder = 0;
        var categoryCount = 0;
        foreach (var rootSeed in SeedTaxonomy.Roots)
        {
            var color = SeedWorld.Brands[rootOrder % SeedWorld.Brands.Length].Color;
            var root = Category.CreateRoot(DeterministicGuid.From("cat:" + rootSeed.Slug), rootSeed.NameEn, rootSeed.NameBn, rootSeed.Slug,
                rootOrder++, media.Write($"categories/{rootSeed.Slug}.svg", SeedMedia.CategoryTile(rootSeed.NameEn, rootSeed.Tagline, color, 0)));
            db.Categories.Add(root);
            categoryIds[root.Slug] = root.Id;
            categoryCount++;
            var subOrder = 0;
            foreach (var subSeed in rootSeed.Subs)
            {
                var sub = Category.CreateChild(root, DeterministicGuid.From("cat:" + subSeed.Slug), subSeed.NameEn, subSeed.NameBn, subSeed.Slug,
                    subOrder++, media.Write($"categories/{subSeed.Slug}.svg", SeedMedia.CategoryTile(subSeed.NameEn, rootSeed.NameEn, color, 1)));
                db.Categories.Add(sub);
                categoryIds[sub.Slug] = sub.Id;
                categoryCount++;
                var leafOrder = 0;
                foreach (var leafSeed in subSeed.Leaves)
                {
                    var leaf = Category.CreateChild(sub, DeterministicGuid.From("cat:" + leafSeed.Slug), leafSeed.NameEn, leafSeed.NameBn, leafSeed.Slug,
                        leafOrder++, media.Write($"categories/{leafSeed.Slug}.svg", SeedMedia.CategoryTile(leafSeed.NameEn, subSeed.NameEn, color, 2)),
                        attributeSchemaJson: filterSchemaByLeaf.GetValueOrDefault(leafSeed.Slug, "[]"));
                    db.Categories.Add(leaf);
                    categoryIds[leaf.Slug] = leaf.Id;
                    categoryCount++;
                }
            }
        }

        report.Categories = categoryCount;
        await db.SaveChangesAsync(ct);

        // ── Brands ────────────────────────────────────────────────────────────────────────
        var brandIds = new Dictionary<string, Guid>();
        foreach (var b in SeedWorld.Brands)
        {
            var slug = Slug.From(b.Name);
            var brand = new Brand(DeterministicGuid.From("brand:" + b.Name), b.Name, slug,
                media.Write($"brands/{slug}.svg", SeedMedia.BrandLogo(b.Name, b.Initials, b.Color)), isVerified: true);
            db.Brands.Add(brand);
            brandIds[b.Name] = brand.Id;
        }

        report.Brands = brandIds.Count;
        await db.SaveChangesAsync(ct);

        // ── Attributes (filterable specs) ─────────────────────────────────────────────────
        var attributes = new Dictionary<string, AttributeDefinition>();
        var attributeValueIds = new Dictionary<(string Code, string Value), Guid>();
        foreach (var plan in plans)
        {
            foreach (var label in plan.Family.FilterSpecs)
            {
                var value = FindSpec(plan, label);
                if (value is null) continue;
                var code = AttributeCode(label);
                if (!attributes.TryGetValue(code, out var def))
                {
                    def = new AttributeDefinition(DeterministicGuid.From("attr:" + code), label, label, code, AttributeDataType.String, null, true);
                    attributes[code] = def;
                    db.Attributes.Add(def);
                }

                if (!attributeValueIds.ContainsKey((code, value)))
                {
                    var id = DeterministicGuid.From($"attrval:{code}:{value}");
                    def.AddValue(id, value, value, attributeValueIds.Count);
                    attributeValueIds[(code, value)] = id;
                }
            }
        }

        report.Attributes = attributes.Count;
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        // ── Products, variants, media, stock, reviews, Q&A ────────────────────────────────
        var availability = new Dictionary<string, (int Available, int Variants)>();
        var chunk = 0;
        foreach (var plan in plans)
        {
            var seller = sellers[plan.SellerIndex];
            var product = BuildProduct(plan, seller.Id, categoryIds[plan.LeafSlug], brandIds[plan.Brand], attributeValueIds, media);
            product.ClearDomainEvents();
            product.RequestReindex();
            db.Products.Add(product);
            db.Entry(product).Property(nameof(Product.CreatedAtUtc)).CurrentValue = plan.CreatedAtUtc;
            report.Products++;
            report.Variants += product.Variants.Count;
            report.MediaRows += product.Media.Count;

            var totalAvailable = 0;
            foreach (var variant in product.Variants)
            {
                foreach (var item in StockFor(plan, variant, sellers[plan.SellerIndex], plan.SellerIndex, warehouses, rng))
                {
                    db.InventoryItems.Add(item);
                    report.InventoryRows++;
                    totalAvailable += item.Available;
                }
            }

            availability[plan.Slug] = (totalAvailable, product.Variants.Count);
            report.OutOfStockVariants += product.Variants.Count(v => db.InventoryItems.Local.Where(i => i.VariantId == v.Id).Sum(i => i.Available) <= 0);
            report.LowStockVariants += product.Variants.Count(v =>
            {
                var a = db.InventoryItems.Local.Where(i => i.VariantId == v.Id).Sum(i => i.Available);
                return a is > 0 and <= 5;
            });

            AddReviews(db, plan, product, customers, sellers[plan.SellerIndex], media, report);
            AddQuestions(db, plan, product, customers, sellerUsers[plan.SellerIndex], report);

            if (++chunk % 40 == 0)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        BuildSpotChecks(report, plans, availability, sellers, rng: new SeedRandom(options.RandomSeed + 1));
        report.Elapsed = sw.Elapsed;
        return report;
    }

    // ───────────────────────── builders ─────────────────────────

    private static Product BuildProduct(
        ProductPlan plan, Guid sellerId, Guid categoryId, Guid brandId, Dictionary<(string Code, string Value), Guid> attributeValueIds, MediaSink media)
    {
        var product = new Product(DeterministicGuid.From("product:" + plan.Slug), sellerId, categoryId, brandId,
            plan.TitleEn, plan.TitleBn, plan.Slug, plan.ShortEn, plan.ShortBn, plan.DescriptionHtml);

        var specs = plan.Specs.ToDictionary(g => g.Group, g => g.Items.ToDictionary(i => i.Key, i => i.Value));
        product.SetContent(JsonSerializer.Serialize(plan.Highlights), JsonSerializer.Serialize(specs), null, null);
        var box = plan.Family.BoxCm;
        product.SetLogistics(ProductCondition.BrandNew, plan.Family.WarrantyMonths, 7,
            plan.Family.WeightKg, box.L, box.W, box.H);

        var basePrice = new Money(plan.BasePrice, Currency.BDT);
        if (plan.Axes.Count == 0)
        {
            product.AddVariant(Guid.NewGuid(), plan.SkuBase, null, new Dictionary<string, string>(), basePrice,
                plan.CompareAt is { } c ? new Money(c, Currency.BDT) : null, null, plan.Family.WeightKg, true);
        }
        else
        {
            var axes = plan.Axes.ToDictionary(a => a.Name, a => (IReadOnlyList<string>)a.Options.Select(o => o.Value).ToList());
            product.GenerateVariants(plan.SkuBase, axes, basePrice, combo =>
            {
                var factor = 1m;
                foreach (var axis in plan.Axes) factor *= axis.Options.First(o => o.Value == combo[axis.Name]).Factor;
                return new Money(SeedRandom.ShelfPrice(plan.BasePrice * factor), Currency.BDT);
            }, plan.Family.WeightKg);
        }

        var ratio = plan.CompareAt is { } cmp ? cmp / plan.BasePrice : 0m;
        foreach (var variant in product.Variants)
        {
            if (plan.Axes.Count > 0 && ratio > 0m)
            {
                variant.ChangePrice(variant.Price, new Money(SeedRandom.ShelfPrice(variant.Price.Amount * ratio), Currency.BDT));
            }

            if (plan.Sale is { } sale)
            {
                var salePrice = new Money(SeedRandom.ShelfPrice(variant.Price.Amount * (sale.Price / plan.BasePrice)), Currency.BDT);
                if (salePrice < variant.Price) variant.ScheduleSale(salePrice, sale.StartsAtUtc, sale.EndsAtUtc);
            }
        }

        var blur = SeedMedia.BlurDataUrl(plan.BrandColor);
        for (var n = 0; n < plan.ImageCount; n++)
        {
            var url = media.Write($"products/{plan.Slug}/{n + 1}.svg",
                SeedMedia.ProductImage(plan.Family.Shape, plan.Brand, plan.Model, plan.BrandColor, n, DeterministicHash(plan.Slug)));
            product.AddMedia(Guid.NewGuid(), null, url!, $"{plan.Brand} {plan.Model} — {ViewCaptions[n]}", 1200, 1200, blur);
        }

        foreach (var label in plan.Family.FilterSpecs)
        {
            var value = FindSpec(plan, label);
            if (value is not null) product.AssignAttributeValue(attributeValueIds[(AttributeCode(label), value)]);
        }

        switch (plan.TargetStatus)
        {
            case "pending_review":
                product.SubmitForReview();
                break;
            case "rejected":
                product.SubmitForReview();
                product.Reject(RejectionReasons[DeterministicHash(plan.Slug) % RejectionReasons.Length]);
                break;
            case "archived":
                product.SubmitForReview();
                product.Approve();
                product.Archive();
                break;
            case "active":
                product.SubmitForReview();
                product.Approve();
                break;
        }

        product.ApplyRatingSummary(plan.RatingAvg, plan.RatingCount);
        product.ApplySoldCount(plan.SoldCount);
        return product;
    }

    private static IEnumerable<InventoryItem> StockFor(
        ProductPlan plan, ProductVariant variant, Seller seller, int sellerIndex, List<Warehouse> warehouses, SeedRandom rng)
    {
        var profile = plan.Stock;
        if (profile == StockProfile.Normal)
        {
            var roll = rng.NextDouble();
            if (roll < 0.04) profile = StockProfile.OutOfStock;
            else if (roll < 0.14) profile = StockProfile.Low;
        }

        var threshold = plan.BasePrice > 30_000m ? 2 : 5;
        int onHand, reserved = 0;
        switch (profile)
        {
            case StockProfile.OutOfStock:
                onHand = 0;
                break;
            case StockProfile.Low:
                onHand = rng.Next(1, 7);
                reserved = onHand > 1 && rng.Chance(0.3) ? rng.Next(0, Math.Min(2, onHand - 1) + 1) : 0;
                break;
            default:
                onHand = plan.BasePrice switch
                {
                    < 2_000m => rng.Next(120, 900),
                    < 20_000m => rng.Next(20, 200),
                    _ => rng.Next(4, 40)
                };
                reserved = rng.Chance(0.08) ? rng.Next(1, Math.Min(3, onHand) + 1) : 0;
                break;
        }

        if (sellerIndex == 0)
        {
            // Official store: split across the Dhaka and Chattogram hubs.
            var dhaka = (int)Math.Ceiling(onHand * 0.65);
            var ctg = onHand - dhaka;
            yield return Item(variant, warehouses[0], dhaka, Math.Min(reserved, dhaka), threshold);
            if (ctg > 0) yield return Item(variant, warehouses[1], ctg, 0, threshold);
            yield break;
        }

        var own = warehouses[sellerIndex + 2];
        yield return Item(variant, own, onHand, reserved, threshold);
        if (profile == StockProfile.Normal && onHand >= 20 && rng.Chance(0.2))
            yield return Item(variant, warehouses[0], onHand / 3, 0, threshold);
    }

    private static InventoryItem Item(ProductVariant variant, Warehouse warehouse, int onHand, int reserved, int threshold)
    {
        var item = new InventoryItem(Guid.NewGuid(), variant.Id, warehouse.Id, onHand, threshold);
        if (reserved > 0) item.TryReserve(reserved);
        item.ClearDomainEvents();
        return item;
    }

    private static void AddReviews(ShopVerseDbContext db, ProductPlan plan, Product product, List<User> customers, Seller seller, MediaSink media, SeedReport report)
    {
        var k = 0;
        foreach (var r in plan.Reviews)
        {
            var id = DeterministicGuid.From($"review:{plan.Slug}:{k++}");
            var review = new Review(id, product.Id, customers[r.ReviewerIndex].Id, null, r.Rating, r.Title, r.Body,
                r.Published ? ReviewStatus.Published : ReviewStatus.Pending, r.Helpful, r.CreatedAtUtc);
            for (var p = 0; p < r.PhotoCount; p++)
            {
                var url = media.Write($"reviews/{id:N}-{p + 1}.svg", SeedMedia.ReviewPhoto($"{plan.Brand} {plan.Model}", p + r.Rating));
                review.AddPhoto(DeterministicGuid.From($"reviewmedia:{id:N}:{p}"), url!);
                report.ReviewPhotos++;
            }

            if (r.SellerReply is not null) review.ReplyAsSeller(r.SellerReply, r.CreatedAtUtc.AddDays(2));
            db.Reviews.Add(review);
            report.Reviews++;
        }
    }

    private static void AddQuestions(ShopVerseDbContext db, ProductPlan plan, Product product, List<User> customers, User sellerUser, SeedReport report)
    {
        var k = 0;
        foreach (var q in plan.Questions)
        {
            var question = new Question(DeterministicGuid.From($"question:{plan.Slug}:{k++}"), product.Id, customers[q.AskerIndex].Id, q.Question, true, q.CreatedAtUtc);
            if (q.SellerAnswer is not null)
            {
                question.AddAnswer(DeterministicGuid.From($"answer:{plan.Slug}:{k}:s"), sellerUser.Id, q.SellerAnswer, true, true, q.CreatedAtUtc.AddHours(20));
                report.Answers++;
            }

            if (q.CustomerAnswer is not null)
            {
                question.AddAnswer(DeterministicGuid.From($"answer:{plan.Slug}:{k}:c"), customers[q.AnswererIndex].Id, q.CustomerAnswer, false, true, q.CreatedAtUtc.AddDays(2));
                report.Answers++;
            }

            db.Questions.Add(question);
            report.Questions++;
        }
    }

    private static void BuildSpotChecks(
        SeedReport report, List<ProductPlan> plans, Dictionary<string, (int Available, int Variants)> availability, List<Seller> sellers, SeedRandom rng)
    {
        var active = plans.Where(p => p.TargetStatus == "active").ToList();
        foreach (var plan in rng.Sample(active, 20))
        {
            var a = availability[plan.Slug].Available;
            var root = SeedTaxonomy.Leaves().First(l => l.Leaf.Slug == plan.LeafSlug);
            report.SpotChecks.Add(new SpotCheck(
                plan.Slug, plan.TitleEn, plan.Brand, $"{root.Root.NameEn} › {root.Sub.NameEn} › {root.Leaf.NameEn}",
                sellers[plan.SellerIndex].StoreName, Taka(plan.BasePrice), plan.CompareAt is { } c ? Taka(c) : null,
                availability[plan.Slug].Variants, plan.ImageCount, plan.RatingCount, plan.RatingAvg,
                a <= 0 ? "out of stock" : a <= 5 ? "low stock" : "in stock", plan.Highlights.FirstOrDefault() ?? string.Empty));
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    private static string Taka(decimal v) => "৳" + v.ToString("N2", CultureInfo.InvariantCulture);

    private static string? FindSpec(ProductPlan plan, string label) =>
        plan.Specs.SelectMany(g => g.Items).Where(i => i.Key == label).Select(i => i.Value).FirstOrDefault();

    private static string AttributeCode(string label) =>
        new string(label.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');

    private static string FilterSchemaJson(IEnumerable<string> labels) =>
        JsonSerializer.Serialize(labels.Select(l => new { code = AttributeCode(l), label = l, type = "string" }));

    private static int DeterministicHash(string text)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in text) h = h * 31 + c;
            return Math.Abs(h == int.MinValue ? 0 : h);
        }
    }

    /// <summary>Unique, valid-looking Bangladeshi mobile number (01[3-9]XXXXXXXX) derived from <paramref name="salt"/>.</summary>
    private static string Phone(SeedRandom rng, int salt) =>
        $"01{3 + salt % 7}{(long)salt * 7919 % 100_000_000:D8}";

    /// <summary>Writes artwork to disk (when enabled) and returns the public URL stored in the database.</summary>
    private sealed class MediaSink(SeedOptions options, SeedReport report)
    {
        public string? Write(string relativePath, string svg)
        {
            if (options.WriteMediaFiles)
            {
                var full = Path.Combine(options.MediaRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, svg);
                report.MediaFilesWritten++;
            }

            return $"{options.MediaBaseUrl.TrimEnd('/')}/{relativePath}";
        }
    }
}
