using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ShopVerse.Infrastructure.Persistence.Configurations;

/// <summary>Stores enums as snake_case strings (<c>PendingReview</c> ⇄ <c>pending_review</c>) per DATA_MODEL.</summary>
public sealed class SnakeCaseEnumConverter<TEnum> : ValueConverter<TEnum, string> where TEnum : struct, Enum
{
    public SnakeCaseEnumConverter() : base(v => ToSnake(v.ToString()), v => Enum.Parse<TEnum>(FromSnake(v), true)) { }

    internal static string ToSnake(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }

        return sb.ToString();
    }

    internal static string FromSnake(string snake) => snake.Replace("_", string.Empty);
}
