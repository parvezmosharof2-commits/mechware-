using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Catalog;

public enum AttributeDataType { String = 1, Number = 2, Boolean = 3, Range = 4 }

/// <summary>Normalized technical attribute (e.g. Input Voltage) powering relational faceted filtering.</summary>
public sealed class AttributeDefinition : Entity<Guid>
{
    private readonly List<AttributeValue> _values = [];

    public string NameEn { get; private set; } = string.Empty;
    public string NameBn { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public AttributeDataType DataType { get; private set; } = AttributeDataType.String;
    public string? Unit { get; private set; }
    public bool IsFilterable { get; private set; } = true;
    public IReadOnlyCollection<AttributeValue> Values => _values.AsReadOnly();

    private AttributeDefinition() { }

    public AttributeDefinition(Guid id, string nameEn, string nameBn, string code, AttributeDataType dataType,
        string? unit, bool isFilterable) : base(id)
    {
        if (string.IsNullOrWhiteSpace(nameEn)) throw new ValidationException("nameEn", "Attribute name is required.");
        if (string.IsNullOrWhiteSpace(code)) throw new ValidationException("code", "Attribute code is required.");
        if (!Enum.IsDefined(dataType)) throw new ValidationException("dataType", "Unknown attribute type.");
        bool numeric = dataType is AttributeDataType.Number or AttributeDataType.Range;
        if (numeric && !UnitCatalog.IsKnown(unit))
            throw new ValidationException("unit", "A numeric attribute needs a known unit (e.g. kW, A, mm, bar).");
        if (!numeric && unit is not null)
            throw new ValidationException("unit", "Only a numeric attribute has a unit.");
        NameEn = nameEn.Trim();
        NameBn = nameBn.Trim();
        Code = code.Trim().ToLowerInvariant();
        DataType = dataType;
        Unit = unit;
        IsFilterable = isFilterable;
    }

    /// <summary>
    /// Adds a value. For a numeric attribute the number is read from the text in the attribute's unit (a Range keeps both
    /// ends), so "750 W" and "0.75 kW" are the same value; text that holds no such number is refused.
    /// </summary>
    public AttributeValue AddValue(Guid id, string valueEn, string valueBn, int sortOrder)
    {
        if (valueEn?.Trim().Length > UnitCatalog.MaxTextLength || valueBn?.Trim().Length > UnitCatalog.MaxTextLength)
            throw new ValidationException("valueEn", $"A value is at most {UnitCatalog.MaxTextLength} characters.");
        if (_values.Any(v => string.Equals(v.ValueEn, valueEn?.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException("valueEn", $"'{valueEn}' is already a value of this attribute.");

        (decimal Min, decimal Max)? number = DataType is AttributeDataType.Number or AttributeDataType.Range
            ? UnitCatalog.Parse(valueEn ?? string.Empty, Unit!, allowRange: DataType == AttributeDataType.Range)
            : null;
        var value = new AttributeValue(id, Id, valueEn!, valueBn, sortOrder, number?.Min, number?.Max);
        _values.Add(value);
        return value;
    }
}

public sealed class AttributeValue : Entity<Guid>
{
    public Guid AttributeId { get; private set; }
    public string ValueEn { get; private set; } = string.Empty;
    public string ValueBn { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }
    /// <summary>For a numeric attribute: the value (or a range's low end) in the attribute's unit.</summary>
    public decimal? NumericValue { get; private set; }
    /// <summary>For a numeric attribute: the range's high end, equal to <see cref="NumericValue"/> for a single value.</summary>
    public decimal? NumericMax { get; private set; }

    private AttributeValue() { }

    internal AttributeValue(Guid id, Guid attributeId, string valueEn, string valueBn, int sortOrder,
        decimal? numericValue = null, decimal? numericMax = null) : base(id)
    {
        NumericValue = numericValue;
        NumericMax = numericMax;
        if (string.IsNullOrWhiteSpace(valueEn)) throw new ValidationException("valueEn", "Attribute value is required.");
        AttributeId = attributeId;
        ValueEn = valueEn.Trim();
        ValueBn = string.IsNullOrWhiteSpace(valueBn) ? valueEn.Trim() : valueBn.Trim();
        SortOrder = sortOrder;
    }
}

/// <summary>Join row (composite key product_id + attribute_value_id).</summary>
public sealed class ProductAttributeValue
{
    public Guid ProductId { get; private set; }
    public Guid AttributeValueId { get; private set; }

    private ProductAttributeValue() { }

    public ProductAttributeValue(Guid productId, Guid attributeValueId)
    {
        ProductId = productId;
        AttributeValueId = attributeValueId;
    }
}
