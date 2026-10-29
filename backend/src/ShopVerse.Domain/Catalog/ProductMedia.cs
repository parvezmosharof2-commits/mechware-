using ShopVerse.Domain.Common;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Catalog;

public enum MediaType { Image = 1, Video = 2 }

public sealed class ProductMedia : Entity<Guid>
{
    public Guid ProductId { get; private set; }
    public Guid? VariantId { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public string AltText { get; private set; } = string.Empty;
    public MediaType Type { get; private set; }
    public int Position { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? BlurDataUrl { get; private set; }

    private ProductMedia() { }

    internal ProductMedia(Guid id, Guid productId, Guid? variantId, string url, string altText, MediaType type,
        int position, int width, int height, string? blurDataUrl) : base(id)
    {
        SafeUrl.Require(url, "url");
        if (string.IsNullOrWhiteSpace(altText)) throw new ValidationException("altText", "Meaningful alt text is required.");
        if (width <= 0 || height <= 0) throw new ValidationException("dimensions", "Media width/height are required (CLS).");

        ProductId = productId;
        VariantId = variantId;
        Url = url;
        AltText = altText;
        Type = type;
        Position = position;
        Width = width;
        Height = height;
        BlurDataUrl = SafeUrl.RequireBlurPlaceholder(blurDataUrl, "blurDataUrl");
    }
}
