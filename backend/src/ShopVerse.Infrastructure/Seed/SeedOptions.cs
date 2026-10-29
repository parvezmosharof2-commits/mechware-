namespace ShopVerse.Infrastructure.Seed;

/// <summary>Seeding knobs. Defaults reproduce the reference dataset exactly.</summary>
public sealed class SeedOptions
{
    /// <summary>Fixed RNG seed: same value ⇒ identical products, prices, stock, reviews and Q&amp;A.</summary>
    public int RandomSeed { get; set; } = 20261029;

    /// <summary>"Now" for the dataset: review dates and sale windows are relative to this fixed anchor.</summary>
    public DateTime AnchorDateUtc { get; set; } = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Directory where generated artwork (SVG) is written. Serve it at <see cref="MediaBaseUrl"/>.</summary>
    public string MediaRoot { get; set; } = Path.Combine("seed-media");

    /// <summary>Public URL prefix stored in the database for generated artwork.</summary>
    public string MediaBaseUrl { get; set; } = "/seed-media";

    /// <summary>Minimum review count to generate (the reference run produces ~3 200).</summary>
    public int TargetReviews { get; set; } = 3200;

    public int CustomerCount { get; set; } = 320;

    public bool WriteMediaFiles { get; set; } = true;
}

/// <summary>Everything the seeder created, for the acceptance report.</summary>
public sealed class SeedReport
{
    public bool AlreadySeeded { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int Categories { get; set; }
    public int Brands { get; set; }
    public int Sellers { get; set; }
    public int Warehouses { get; set; }
    public int Products { get; set; }
    public int Variants { get; set; }
    public int MediaRows { get; set; }
    public int InventoryRows { get; set; }
    public int OutOfStockVariants { get; set; }
    public int LowStockVariants { get; set; }
    public int Customers { get; set; }
    public int Reviews { get; set; }
    public int ReviewPhotos { get; set; }
    public int Questions { get; set; }
    public int Answers { get; set; }
    public int Attributes { get; set; }
    public int MediaFilesWritten { get; set; }
    public List<SpotCheck> SpotChecks { get; } = [];
}

public sealed record SpotCheck(
    string Slug, string Title, string Brand, string Category, string Seller, string Price, string? CompareAt,
    int Variants, int Images, int Reviews, decimal Rating, string StockStatus, string FirstHighlight);
