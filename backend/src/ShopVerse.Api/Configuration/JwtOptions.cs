using System.ComponentModel.DataAnnotations;

namespace ShopVerse.Api.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required(ErrorMessage = "JWT Secret is required.")]
    [MinLength(32, ErrorMessage = "JWT Secret must be at least 32 characters (256 bits).")]
    public string Secret { get; set; } = string.Empty;

    [Required(ErrorMessage = "JWT Issuer is required.")]
    public string Issuer { get; set; } = "https://api.mechware.com.bd";

    [Required(ErrorMessage = "JWT Audience is required.")]
    public string Audience { get; set; } = "https://mechware.com.bd";

    public int AccessTokenExpiryMinutes { get; set; } = 15;
    public int RefreshTokenExpiryDays { get; set; } = 7;
}
