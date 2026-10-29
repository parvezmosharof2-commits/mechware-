using FluentAssertions;
using ShopVerse.Domain.Catalog;
using ShopVerse.Infrastructure.Seed;

namespace ShopVerse.UnitTests.Catalog;

/// <summary>Guards the seed dataset against typos and drift without needing a database.</summary>
public class CatalogSeedPlanTests
{
    private static readonly SeedOptions Options = new();
    private static readonly Lazy<List<ProductPlan>> Plans = new(() => CatalogPlanner.Build(Options));

    [Fact]
    public void Taxonomy_Has12Roots_ThreeLevels_And72Leaves()
    {
        SeedTaxonomy.Roots.Should().HaveCount(12);
        SeedTaxonomy.Roots.Should().OnlyContain(r => r.Subs.Length == 3);
        SeedTaxonomy.Roots.SelectMany(r => r.Subs).Should().OnlyContain(s => s.Leaves.Length == 2);
        SeedTaxonomy.Leaves().Should().HaveCount(72);

        var slugs = SeedTaxonomy.Roots.SelectMany(r => new[] { r.Slug }
            .Concat(r.Subs.SelectMany(s => new[] { s.Slug }.Concat(s.Leaves.Select(l => l.Slug))))).ToList();
        slugs.Should().OnlyHaveUniqueItems().And.OnlyContain(s => Slug.IsValid(s));
    }

    [Fact]
    public void World_Has40Brands_And25Sellers()
    {
        SeedWorld.Brands.Should().HaveCount(40);
        SeedWorld.Brands.Select(b => b.Name).Should().OnlyHaveUniqueItems();
        SeedWorld.Sellers.Should().HaveCount(25);
        SeedWorld.Sellers.Select(s => s.StoreName).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryFamilyRow_MatchesHeader_AndAllTemplateTokensResolve()
    {
        foreach (var family in CatalogPlanner.AllFamilies())
        {
            foreach (var row in family.ParsedRows())
            {
                var act = () =>
                {
                    family.Expand(family.Title, row);
                    family.Expand(family.Intro, row);
                    foreach (var h in family.Highlights) family.Expand(h, row);
                };
                act.Should().NotThrow($"{family.Brand} {family.Series}");
            }
        }
    }

    [Fact]
    public void Catalogue_Has400PlusProducts_EachLeafHasProducts_AndBrandsAreAllUsedOrListed()
    {
        var plans = Plans.Value;
        plans.Count.Should().BeGreaterThanOrEqualTo(400);
        plans.Count(p => p.TargetStatus == "active").Should().BeGreaterThanOrEqualTo(400);

        var leavesWithProducts = plans.Select(p => p.LeafSlug).Distinct().ToHashSet();
        SeedTaxonomy.Leaves().Select(l => l.Leaf.Slug).Should().OnlyContain(slug => leavesWithProducts.Contains(slug));
    }

    [Fact]
    public void Slugs_AndSkus_AreUnique_AndValid()
    {
        var plans = Plans.Value;
        plans.Select(p => p.Slug).Should().OnlyHaveUniqueItems().And.OnlyContain(s => Slug.IsValid(s));
        plans.Select(p => p.SkuBase).Should().OnlyHaveUniqueItems();
        plans.Should().OnlyContain(p => p.TitleEn.Length <= 255 && p.TitleBn.Length <= 255);
    }

    [Fact]
    public void Prices_AreRealisticTaka_AndDiscountsAreConsistent()
    {
        var plans = Plans.Value;
        plans.Should().OnlyContain(p => p.BasePrice >= 30m && p.BasePrice <= 700_000m);
        plans.Where(p => p.CompareAt.HasValue).Should().OnlyContain(p => p.CompareAt > p.BasePrice);
        plans.Where(p => p.Sale != null).Should().OnlyContain(p => p.Sale!.Price < p.BasePrice && p.Sale.EndsAtUtc > p.Sale.StartsAtUtc);
        plans.Count(p => p.CompareAt.HasValue).Should().BeGreaterThan(100);
    }

    [Fact]
    public void Reviews_Exceed3000_WithVariedRatingsAndTone_AndSomePhotos()
    {
        var reviews = Plans.Value.SelectMany(p => p.Reviews).ToList();
        reviews.Count.Should().BeGreaterThanOrEqualTo(3000);
        reviews.Select(r => r.Rating).Distinct().Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
        reviews.Count(r => r.Rating >= 4).Should().BeGreaterThan(reviews.Count / 2);
        reviews.Count(r => r.Rating <= 2).Should().BeGreaterThan(30);
        reviews.Count(r => r.PhotoCount > 0).Should().BeGreaterThan(100);
        reviews.Select(r => r.Body.Length).Distinct().Count().Should().BeGreaterThan(100, "review lengths must vary");
        reviews.Select(r => r.Body).Distinct().Count().Should().BeGreaterThan(reviews.Count * 8 / 10, "reviews should rarely be exact duplicates");
        reviews.Any(r => r.Body.Any(c => c is >= 'ঀ' and <= '৿')).Should().BeTrue("some reviews are written in Bangla");
    }

    [Fact]
    public void Qa_Exists_AndStockMixIncludesLowAndOutOfStock()
    {
        var plans = Plans.Value;
        plans.SelectMany(p => p.Questions).Count().Should().BeGreaterThan(100);
        plans.Count(p => p.Stock == StockProfile.Low).Should().BeGreaterThan(20);
        plans.Count(p => p.Stock == StockProfile.OutOfStock).Should().BeGreaterThan(10);
        plans.Select(p => p.TargetStatus).Distinct().Should().Contain(["active", "pending_review", "rejected", "archived", "draft"]);
    }

    [Fact]
    public void Plan_IsDeterministic_ForTheSameSeed()
    {
        var a = CatalogPlanner.Build(new SeedOptions());
        var b = CatalogPlanner.Build(new SeedOptions());

        a.Select(p => p.Slug).Should().Equal(b.Select(p => p.Slug));
        a.Select(p => p.BasePrice).Should().Equal(b.Select(p => p.BasePrice));
        a.SelectMany(p => p.Reviews).Select(r => r.Body).Should().Equal(b.SelectMany(p => p.Reviews).Select(r => r.Body));

        var other = CatalogPlanner.Build(new SeedOptions { RandomSeed = 7 });
        other.Select(p => p.BasePrice).Should().NotEqual(a.Select(p => p.BasePrice));
    }

    [Fact]
    public void ProductText_IsSpecificNotLoremIpsum()
    {
        foreach (var plan in Plans.Value.Take(80))
        {
            plan.DescriptionHtml.Should().Contain(plan.Brand);
            plan.Highlights.Should().NotBeEmpty();
            plan.Specs.SelectMany(g => g.Items).Should().NotBeEmpty();
            (plan.TitleEn + plan.DescriptionHtml).ToLowerInvariant().Should().NotContain("lorem").And.NotContain("placeholder");
        }
    }
}
