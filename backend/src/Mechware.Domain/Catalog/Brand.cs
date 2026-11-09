using Mechware.Domain.Common;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Catalog;

public sealed class Brand : Entity<Guid>
{
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string? LogoUrl { get; private set; }
    public bool IsVerified { get; private set; }

    private Brand() { }

    public Brand(Guid id, string name, string slug, string? logoUrl, bool isVerified) : base(id)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("name", "Brand name is required.");
        Name = name.Trim();
        Slug = Catalog.Slug.Require(slug);
        LogoUrl = SafeUrl.RequireOptional(logoUrl, "logoUrl");
        IsVerified = isVerified;
    }
}
