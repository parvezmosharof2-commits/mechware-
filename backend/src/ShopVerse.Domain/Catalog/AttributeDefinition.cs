using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Catalog;

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
        NameEn = nameEn.Trim();
        NameBn = nameBn.Trim();
        Code = code.Trim().ToLowerInvariant();
        DataType = dataType;
        Unit = unit;
        IsFilterable = isFilterable;
    }

    public AttributeValue AddValue(Guid id, string valueEn, string valueBn, int sortOrder)
    {
        var value = new AttributeValue(id, Id, valueEn, valueBn, sortOrder);
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

    private AttributeValue() { }

    internal AttributeValue(Guid id, Guid attributeId, string valueEn, string valueBn, int sortOrder) : base(id)
    {
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
