using System.Globalization;
using System.Text;
using ShopVerse.Domain.Catalog;

namespace ShopVerse.Infrastructure.Seed;

internal enum StockProfile { Normal, Low, OutOfStock }

internal sealed record SalePlan(decimal Price, DateTime StartsAtUtc, DateTime EndsAtUtc);

internal sealed record ReviewPlan(
    int ReviewerIndex, int Rating, string? Title, string Body, int Helpful, DateTime CreatedAtUtc, int PhotoCount,
    string? SellerReply, bool Published);

internal sealed record QuestionPlan(int AskerIndex, string Question, DateTime CreatedAtUtc, string? SellerAnswer, string? CustomerAnswer, int AnswererIndex);

/// <summary>Everything needed to materialise one product, decided up front and deterministically.</summary>
internal sealed class ProductPlan
{
    public required Family Family { get; init; }
    public required Dictionary<string, string> Row { get; init; }
    public required string Brand { get; init; }
    public required string LeafSlug { get; init; }
    public required string RootSlug { get; init; }
    public required string Domain { get; init; }
    public required string Model { get; init; }
    public required string TitleEn { get; init; }
    public required string TitleBn { get; init; }
    public required string Slug { get; init; }
    public required string ShortEn { get; init; }
    public required string ShortBn { get; init; }
    public required string DescriptionHtml { get; init; }
    public required List<string> Highlights { get; init; }
    public required List<(string Group, List<(string Key, string Value)> Items)> Specs { get; init; }
    public required string SkuBase { get; init; }
    public required decimal BasePrice { get; init; }
    public decimal? CompareAt { get; init; }
    public SalePlan? Sale { get; init; }
    public required IReadOnlyList<AxisSeed> Axes { get; init; }
    public required int SellerIndex { get; init; }
    public required int ImageCount { get; init; }
    public required StockProfile Stock { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required string TargetStatus { get; init; }
    public List<ReviewPlan> Reviews { get; } = [];
    public List<QuestionPlan> Questions { get; } = [];
    public int SoldCount { get; set; }
    public string BrandColor { get; set; } = "#0EA5E9";
    public decimal RatingAvg => Reviews.Count(r => r.Published) == 0 ? 0m : Math.Round((decimal)Reviews.Where(r => r.Published).Average(r => r.Rating), 2);
    public int RatingCount => Reviews.Count(r => r.Published);
}

/// <summary>Deterministically expands the family tables into a complete catalogue plan (no database access).</summary>
internal static class CatalogPlanner
{
    public static IEnumerable<Family> AllFamilies() => FamiliesAutomation.All()
        .Concat(FamiliesSensorsPower.All())
        .Concat(FamiliesFluidNetworkPanel.All())
        .Concat(FamiliesProcessSafety.All());

    public static List<ProductPlan> Build(SeedOptions options)
    {
        var rng = new SeedRandom(options.RandomSeed);
        var families = AllFamilies().ToList();
        var leafToRoot = SeedTaxonomy.Leaves().ToDictionary(l => l.Leaf.Slug, l => l.Root.Slug);

        // Which sellers carry which brand (3–5 independent sellers; the official store carries about half of them).
        var brandSellers = new Dictionary<string, List<int>>();
        foreach (var brand in SeedWorld.Brands)
        {
            var carriers = rng.Sample(Enumerable.Range(1, SeedWorld.Sellers.Length - 1).ToList(), rng.Next(3, 6)).ToList();
            if (rng.Chance(0.55)) carriers.Insert(0, 0);
            brandSellers[brand.Name] = carriers;
        }

        var plans = new List<ProductPlan>();
        var usedSlugs = new HashSet<string>(StringComparer.Ordinal);
        var usedSkus = new HashSet<string>(StringComparer.Ordinal);
        var anchor = options.AnchorDateUtc;

        foreach (var family in families)
        {
            if (!leafToRoot.TryGetValue(family.Leaf, out var rootSlug))
                throw new InvalidOperationException($"Family {family.Brand} {family.Series} references unknown leaf '{family.Leaf}'.");
            if (SeedWorld.Brands.All(b => b.Name != family.Brand))
                throw new InvalidOperationException($"Family {family.Series} uses brand '{family.Brand}' that is not in the 40-brand list.");

            var brandColor = SeedWorld.Brands.First(b => b.Name == family.Brand).Color;
            foreach (var row in family.ParsedRows())
            {
                var model = row.ContainsKey("Model") ? row["Model"] : row[family.Header.Split('|')[0]];
                var mpn = row.TryGetValue("MPN", out var m) ? m : model;
                var titleEn = CleanTitle(family.Expand(family.Title, row));
                var basePrice = RoundPrice(decimal.Parse(row["Price"], CultureInfo.InvariantCulture) * (decimal)(0.97 + rng.NextDouble() * 0.08));

                var slugCore = Slug.From(titleEn);
                if (slugCore.Length > 80) slugCore = slugCore[..80].TrimEnd('-');
                var slug = $"{slugCore}-{DeterministicGuid.From("slug:" + titleEn):N}"[..(slugCore.Length + 7)];
                while (!usedSlugs.Add(slug)) slug += "x";

                var skuBase = $"MW-{BrandCode(family.Brand)}-{Sanitize(mpn)}";
                if (skuBase.Length > 44) skuBase = skuBase[..44];
                while (!usedSkus.Add(skuBase)) skuBase += "A";

                var highlights = family.Highlights.Select(h => family.Expand(h, row)).ToList();
                var specs = BuildSpecs(family, row);
                var axes = family.ParsedAxes();

                decimal? compareAt = rng.Chance(0.38) ? RoundPrice(basePrice * (decimal)(1.08 + rng.NextDouble() * 0.22)) : null;
                SalePlan? sale = null;
                if (rng.Chance(0.07))
                {
                    var salePrice = RoundPrice(basePrice * (decimal)(0.88 + rng.NextDouble() * 0.07));
                    var offsetDays = rng.PickWeighted([(-1, 4.0), (-12, 1.0), (7, 1.0)]);   // mostly running now, some ended, some upcoming
                    var start = anchor.AddDays(offsetDays - (offsetDays < 0 ? 0 : 0)).AddHours(-rng.Next(0, 48));
                    sale = new SalePlan(salePrice, start, start.AddDays(rng.Next(3, 10)));
                    if (sale.Price >= basePrice) sale = null;
                }

                var stock = rng.PickWeighted([(StockProfile.Normal, 82.0), (StockProfile.Low, 12.0), (StockProfile.OutOfStock, 6.0)]);

                var candidates = brandSellers[family.Brand];
                var sellerIndex = rng.Pick(candidates);

                var status = "active";
                var idx = plans.Count;
                if (idx % 61 == 7) status = "pending_review";
                else if (idx % 97 == 11) status = "rejected";
                else if (idx % 89 == 23) status = "archived";
                else if (idx % 113 == 31) status = "draft";

                plans.Add(new ProductPlan
                {
                    Family = family,
                    Row = row,
                    Brand = family.Brand,
                    LeafSlug = family.Leaf,
                    RootSlug = rootSlug,
                    Domain = ReviewCorpus.RootDomain[rootSlug],
                    Model = model,
                    TitleEn = titleEn.Length > 250 ? titleEn[..250] : titleEn,
                    TitleBn = CleanTitle($"{family.Brand} {model} {family.NounBn}"),
                    Slug = slug,
                    ShortEn = Truncate(string.Join(" · ", highlights.Take(2)), 280),
                    ShortBn = $"{family.Brand} {model} {family.NounBn} — অনুমোদিত সরবরাহকারীর কাছ থেকে ওয়ারেন্টি ও টেকনিক্যাল সাপোর্টসহ।",
                    DescriptionHtml = BuildDescription(family, row, highlights),
                    Highlights = highlights,
                    Specs = specs,
                    SkuBase = skuBase,
                    BasePrice = basePrice,
                    CompareAt = compareAt,
                    Sale = sale,
                    Axes = axes,
                    SellerIndex = sellerIndex,
                    ImageCount = rng.PickWeighted([(2, 14.0), (3, 34.0), (4, 28.0), (5, 16.0), (6, 8.0)]),
                    Stock = stock,
                    CreatedAtUtc = anchor.AddDays(-rng.Next(40, 900)),
                    TargetStatus = status,
                    BrandColor = brandColor
                });
            }
        }

        AllocateEngagement(plans, rng, options);
        return plans;
    }

    // ───────────────────────── engagement: sold counts, reviews, Q&A ─────────────────────────

    private static void AllocateEngagement(List<ProductPlan> plans, SeedRandom rng, SeedOptions options)
    {
        var active = plans.Where(p => p.TargetStatus == "active").ToList();
        var weights = active.Select(p => Math.Pow(p.Family.Popularity, 1.2) * (0.3 + 1.7 * rng.NextDouble())).ToList();
        for (var i = 0; i < weights.Count; i++)
        {
            if (rng.Chance(0.06)) weights[i] = 0;   // brand-new listings with no reviews yet
        }

        var total = weights.Sum();
        var counts = weights.Select(w => (int)Math.Round(w / total * options.TargetReviews)).ToList();
        var diff = options.TargetReviews + 40 - counts.Sum();   // small margin so the "3 000+" bar is always cleared
        var order = Enumerable.Range(0, counts.Count).OrderByDescending(i => weights[i]).ToList();
        for (var k = 0; diff != 0 && k < 100_000; k++)
        {
            var i = order[k % order.Count];
            if (weights[i] == 0) continue;
            counts[i] += Math.Sign(diff);
            diff -= Math.Sign(diff);
        }

        var anchor = options.AnchorDateUtc;
        for (var i = 0; i < active.Count; i++)
        {
            var plan = active[i];
            var quality = Math.Clamp(4.05 + (rng.NextDouble() - 0.4) * 0.9, 3.4, 4.9);
            plan.SoldCount = counts[i] == 0 ? rng.Next(0, 6) : counts[i] * rng.Next(5, 13) + rng.Next(0, 30);

            var reviewerPool = Enumerable.Range(0, options.CustomerCount).ToList();
            var chosen = rng.Sample(reviewerPool, Math.Min(counts[i], reviewerPool.Count));
            foreach (var reviewer in chosen)
            {
                var rating = (int)Math.Clamp(Math.Round(quality + Gaussian(rng) * 0.85), 1, 5);
                var city = rng.Pick(SeedWorld.Cities).City;
                var industry = rng.Pick(SeedWorld.Industries);
                var role = rng.Pick(SeedWorld.Roles);
                var (title, body) = ReviewCorpus.Compose(rng, plan.Domain, rating, city, industry, role, plan.Model);

                // Dates skew recent: age = 730 days × u²; never before the listing existed.
                var maxAge = Math.Max(2, (int)(anchor - plan.CreatedAtUtc).TotalDays - 1);
                var age = (int)(Math.Min(730, maxAge) * Math.Pow(rng.NextDouble(), 1.7));
                var created = anchor.AddDays(-age - 1).AddMinutes(rng.Next(0, 1440));

                var photos = rng.Chance(0.075) ? rng.Next(1, 4) : 0;
                var helpful = rng.Chance(0.55) ? (int)Math.Round(Math.Pow(rng.NextDouble(), 2.2) * 38) : 0;
                string? reply = null;
                if (rating <= 2 && rng.Chance(0.65) || rating >= 4 && rng.Chance(0.07))
                {
                    reply = rating <= 2
                        ? "We are sorry about this. Our service team has contacted you to arrange a replacement or refund."
                        : "Thank you for your feedback — glad it is working well for you.";
                }

                plan.Reviews.Add(new ReviewPlan(reviewer, rating, title, body, helpful, created, photos, reply, Published: !rng.Chance(0.015)));
            }

            if (rng.Chance(0.16))
            {
                var qaPool = ReviewCorpus.QuestionsFor(plan.Domain).ToList();
                foreach (var qa in rng.Sample(qaPool, rng.Next(1, 5)))
                {
                    var city = rng.Pick(SeedWorld.Cities).City;
                    var asked = anchor.AddDays(-rng.Next(2, 400)).AddMinutes(rng.Next(0, 1440));
                    plan.Questions.Add(new QuestionPlan(
                        rng.Next(0, options.CustomerCount),
                        ReviewCorpus.FillCity(qa.Question, city), asked,
                        rng.Chance(0.9) ? ReviewCorpus.FillCity(qa.SellerAnswer, city) : null,
                        rng.Chance(0.25) ? ReviewCorpus.Customer(rng, city) : null,
                        rng.Next(0, options.CustomerCount)));
                }
            }
        }

        foreach (var plan in plans.Where(p => p.TargetStatus != "active")) plan.SoldCount = 0;
    }

    private static double Gaussian(SeedRandom rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // ───────────────────────── text helpers ─────────────────────────

    private static List<(string Group, List<(string Key, string Value)> Items)> BuildSpecs(Family family, IReadOnlyDictionary<string, string> row)
    {
        var groups = new List<(string Group, List<(string Key, string Value)> Items)>();
        List<(string Key, string Value)> Group(string name)
        {
            var existing = groups.FindIndex(g => g.Group == name);
            if (existing >= 0) return groups[existing].Items;
            var created = (name, new List<(string, string)>());
            groups.Add(created);
            return created.Item2;
        }

        Group("General").Add(("Brand", family.Brand));
        Group("General").Add(("Series", family.Series));

        foreach (var spec in family.Specs)
        {
            var colon = spec.IndexOf(':');
            var group = spec[..colon];
            foreach (var pair in spec[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq < 0 ? pair : pair[..eq];
                var label = eq < 0 ? pair : pair[(eq + 1)..];
                Group(group).Add((label, row[key]));
            }
        }

        foreach (var fixedSpec in family.Fixed)
        {
            var colon = fixedSpec.IndexOf(':');
            var group = fixedSpec[..colon];
            var rest = fixedSpec[(colon + 1)..];
            var eq = rest.IndexOf('=');
            Group(group).Add((rest[..eq], rest[(eq + 1)..]));
        }

        Group("Warranty & Support").Add(("Warranty", $"{family.WarrantyMonths} months"));
        Group("Warranty & Support").Add(("Condition", "Brand new"));
        return groups;
    }

    private static string BuildDescription(Family family, IReadOnlyDictionary<string, string> row, List<string> highlights)
    {
        var sb = new StringBuilder();
        sb.Append("<p>").Append(Html(family.Expand(family.Intro, row))).Append("</p>");
        sb.Append("<h3>Key features</h3><ul>");
        foreach (var h in highlights) sb.Append("<li>").Append(Html(h)).Append("</li>");
        sb.Append("</ul>");
        if (family.Applications.Length > 0)
        {
            sb.Append("<h3>Typical applications</h3><ul>");
            foreach (var a in family.Applications) sb.Append("<li>").Append(Html(a)).Append("</li>");
            sb.Append("</ul>");
        }

        sb.Append("<h3>What you get</h3><p>New ").Append(Html(family.Brand)).Append(' ').Append(Html(family.NounEn.ToLowerInvariant()))
          .Append(" in original manufacturer packaging with the documentation and accessories listed in the specifications.</p>");
        sb.Append("<h3>Warranty and support</h3><p>").Append(family.WarrantyMonths)
          .Append("-month warranty handled locally by the seller. Technical selection, wiring and programming support is available from our engineers; datasheets are shared on request. VAT invoice provided with every order.</p>");
        return sb.ToString();
    }

    /// <summary>Drops a word when the next word repeats or extends it ("Fluke Fluke 17B+", "HG-KR HG-KR73").</summary>
    internal static string CleanTitle(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var i = 0; i < words.Count - 1; i++)
        {
            if (words[i].Length >= 3 && words[i + 1].StartsWith(words[i], StringComparison.OrdinalIgnoreCase))
            {
                words.RemoveAt(i);
                i--;
            }
        }

        return string.Join(' ', words);
    }

    private static string Html(string text) => System.Net.WebUtility.HtmlEncode(text);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private static string BrandCode(string brand)
    {
        var letters = new string(brand.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        return letters.Length >= 3 ? letters[..3] : letters.PadRight(3, 'X');
    }

    private static string Sanitize(string text) =>
        new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static decimal RoundPrice(decimal raw) => SeedRandom.ShelfPrice(raw);
}
