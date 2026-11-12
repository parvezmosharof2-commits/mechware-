using System.Text;
using FluentAssertions;
using Mechware.Application.Catalog;
using Mechware.Domain.Catalog;
using Mechware.Infrastructure.Catalog;

namespace Mechware.UnitTests.Catalog;

public sealed class CatalogImportValidatorTests
{
    private const string Header =
        "product_slug,product_title_en,product_title_bn,brand,category,sku,manufacturer_part_number,price_bdt,min_order_qty,pack_size,supply_status,datasheet_url,attr:rated_current,attr:voltage,attr:color,attr:ip_rated";

    private sealed class FakeLookup : ICatalogImportLookup
    {
        public List<ImportVariantRef> Variants { get; } = [];
        public HashSet<string> Slugs { get; } = [];
        public Task<IReadOnlyList<ImportBrandRef>> BrandsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ImportBrandRef>>([new("Siemens", "siemens")]);
        public Task<IReadOnlyList<ImportCategoryRef>> CategoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ImportCategoryRef>>([new("plcs", "root.automation.plcs")]);
        public Task<IReadOnlyList<ImportAttributeRef>> AttributesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ImportAttributeRef>>([
            new("rated_current", AttributeDataType.Number, "A"), new("voltage", AttributeDataType.Range, "V"),
            new("color", AttributeDataType.String, null), new("ip_rated", AttributeDataType.Boolean, null)]);
        public Task<IReadOnlySet<string>> ExistingProductSlugsAsync(IReadOnlyCollection<string> slugs, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(slugs.Where(Slugs.Contains).ToHashSet());
        public Task<IReadOnlyList<ImportVariantRef>> ExistingVariantsAsync(IReadOnlyCollection<string> partKeys, IReadOnlyCollection<string> skus, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImportVariantRef>>(Variants.Where(v => skus.Contains(v.Sku) || (v.PartNumberKey != null && partKeys.Contains(v.PartNumberKey))).ToList());
    }

    private static string Row(string slug = "plc-1", string sku = "SKU-1", string pn = "6ES7 214-1AG40", string price = "1250.50", string brand = "siemens",
        string category = "plcs", string moq = "", string pack = "", string supply = "", string url = "", string current = "10 A", string volt = "", string color = "", string ip = "",
        string titleEn = "PLC", string titleBn = "পিএলসি") =>
        $"{slug},{titleEn},{titleBn},{brand},{category},{sku},{pn},{price},{moq},{pack},{supply},{url},{current},{volt},{color},{ip}";

    private static async Task<CatalogImportDryRun> RunAsync(string csv, FakeLookup? lookup = null, CatalogImportOptions? options = null, bool bom = false)
    {
        byte[] body = new UTF8Encoding(false).GetBytes(csv);
        await using var stream = new MemoryStream(bom ? [.. Encoding.UTF8.GetPreamble(), .. body] : body);
        return await new CatalogImportValidator(lookup ?? new FakeLookup(), options ?? new CatalogImportOptions()).DryRunAsync(stream);
    }

    private static Task<CatalogImportDryRun> OneRow(string row, FakeLookup? lookup = null) => RunAsync(Header + "\n" + row + "\n", lookup);

    private static async Task Expect(string row, string code, string column)
    {
        var r = await OneRow(row);
        r.Errors.Should().ContainSingle(e => e.Code == code && e.Column == column && e.Row == 2, $"row: {row}");
        r.InvalidRows.Should().Be(1);
    }

    [Fact]
    public async Task A_good_file_with_bom_and_bangla_is_valid()
    {
        var r = await RunAsync(Header + "\r\n" + Row() + "\r\n" + Row(slug: "plc-2", sku: "SKU-2", pn: "০১২৩৪৫", titleBn: "\"পিএলসি \"\"স্পেশাল\"\", ২\"", volt: "85-264 V", color: "Red", ip: "yes") + "\r\n", bom: true);
        r.Errors.Should().BeEmpty();
        (r.Rows, r.ValidRows, r.NewProducts, r.NewVariants).Should().Be((2, 2, 2, 2));
    }

    [Fact]
    public async Task Quoted_fields_with_commas_and_line_breaks_are_one_field()
    {
        var r = await OneRow(Row(titleEn: "\"PLC, \"\"big\"\"\nline\""));
        r.Errors.Should().BeEmpty();
    }

    [Fact] public Task Missing_required_value() => Expect(Row(sku: ""), "REQUIRED", "sku");
    [Fact] public Task Bad_slug() => Expect(Row(slug: "Bad_Slug"), "INVALID_SLUG", "product_slug");
    [Fact] public Task Title_too_long() => Expect(Row(titleEn: new string('a', 256)), "TOO_LONG", "product_title_en");
    [Fact] public Task Unknown_brand() => Expect(Row(brand: "nobody"), "UNKNOWN_BRAND", "brand");
    [Fact] public Task Unknown_category() => Expect(Row(category: "nothing"), "UNKNOWN_CATEGORY", "category");
    [Fact] public Task Sku_with_space() => Expect(Row(sku: "\"A B\""), "INVALID_SKU", "sku");
    [Fact] public Task Part_number_too_long() => Expect(Row(pn: new string('A', 65)), "TOO_LONG", "manufacturer_part_number");
    [Fact] public Task Part_number_without_letters_or_digits() => Expect(Row(pn: "--"), "INVALID_PART_NUMBER", "manufacturer_part_number");
    [Fact] public Task Price_not_a_number() => Expect(Row(price: "12;50"), "INVALID_PRICE", "price_bdt");
    [Fact] public Task Price_zero() => Expect(Row(price: "0"), "PRICE_NOT_POSITIVE", "price_bdt");
    [Fact] public Task Price_three_decimals() => Expect(Row(price: "10.255"), "PRICE_PRECISION", "price_bdt");
    [Fact] public Task Price_negative_is_refused() => Expect(Row(price: "-5"), "INVALID_PRICE", "price_bdt");
    [Fact] public Task Moq_zero() => Expect(Row(moq: "0"), "INVALID_MOQ", "min_order_qty");
    [Fact] public Task Pack_zero() => Expect(Row(pack: "0"), "INVALID_PACK_SIZE", "pack_size");
    [Fact] public Task Moq_not_whole_packs() => Expect(Row(moq: "5", pack: "4"), "MOQ_NOT_PACK_MULTIPLE", "min_order_qty");
    [Fact] public Task Unknown_supply_status() => Expect(Row(supply: "dead"), "INVALID_SUPPLY_STATUS", "supply_status");
    [Fact] public Task Numeric_supply_status_is_refused() => Expect(Row(supply: "2"), "INVALID_SUPPLY_STATUS", "supply_status");
    [Fact] public Task Http_url() => Expect(Row(url: "http://x.com/a.pdf"), "INVALID_URL", "datasheet_url");
    [Fact] public Task Javascript_url() => Expect(Row(url: "javascript:alert(1)"), "INVALID_URL", "datasheet_url");
    [Fact] public Task Wrong_quantity_unit() => Expect(Row(current: "230 V"), "INVALID_UNIT_VALUE", "attr:rated_current");
    [Fact] public Task Number_without_unit() => Expect(Row(current: "10"), "INVALID_UNIT_VALUE", "attr:rated_current");
    [Fact] public Task Range_on_single_value_attribute() => Expect(Row(current: "5-10 A"), "INVALID_UNIT_VALUE", "attr:rated_current");
    [Fact] public Task Absurd_value() => Expect(Row(current: "999999999999 A"), "INVALID_UNIT_VALUE", "attr:rated_current");
    [Fact] public Task Unit_the_catalogue_does_not_know() => Expect(Row(current: "6 kVA"), "INVALID_UNIT_VALUE", "attr:rated_current");
    [Fact]
    public async Task Breaking_capacity_in_kA_is_read_as_current() =>
        (await OneRow(Row(current: "6 kA", category: "root.automation.plcs"))).Errors.Should().BeEmpty();
    [Fact] public Task Bad_boolean() => Expect(Row(ip: "maybe"), "INVALID_BOOLEAN", "attr:ip_rated");
    [Fact] public Task Text_attribute_too_long() => Expect(Row(color: new string('x', 151)), "TOO_LONG", "attr:color");

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd")]
    [InlineData("-cmd")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tx")]
    public Task Formula_injection_is_flagged(string value) => Expect(Row(titleEn: "\"" + value + "\""), "FORMULA_INJECTION", "product_title_en");

    [Fact]
    public async Task A_milliampere_value_and_a_category_path_are_accepted()
    {
        (await OneRow(Row(current: "500 mA", category: "root.automation.plcs"))).Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Duplicates_and_normalised_collisions_inside_the_file()
    {
        var collision = await RunAsync(Header + "\n" + Row() + "\n" + Row(slug: "plc-2", sku: "SKU-2", pn: "6es7214.1ag40") + "\n");
        collision.Errors.Should().ContainSingle(e => e.Code == "NORMALISED_COLLISION" && e.Row == 3);
        var exact = await RunAsync(Header + "\n" + Row() + "\n" + Row(slug: "plc-2", sku: "SKU-2") + "\n");
        exact.Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_PART_NUMBER" && e.Row == 3);
        var sku = await RunAsync(Header + "\n" + Row() + "\n" + Row(slug: "plc-2", pn: "OTHER-1") + "\n");
        sku.Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_SKU" && e.Row == 3);
    }

    [Fact]
    public async Task Rows_of_one_product_must_agree()
    {
        var r = await RunAsync(Header + "\n" + Row() + "\n" + Row(sku: "SKU-2", pn: "OTHER-1", titleEn: "Different") + "\n");
        r.Errors.Should().ContainSingle(e => e.Code == "PRODUCT_MISMATCH" && e.Row == 3);
        r.NewProducts.Should().Be(1);
        r.NewVariants.Should().Be(1);
    }

    [Fact]
    public async Task Existing_products_and_variants_are_counted_and_clashes_reported()
    {
        var lookup = new FakeLookup();
        lookup.Slugs.Add("plc-1");
        lookup.Variants.Add(new("SKU-1", "6ES72141AG40", "plc-1"));
        lookup.Variants.Add(new("OLD-9", "TAKEN99", "other-product"));
        var r = await RunAsync(Header + "\n" + Row() + "\n" + Row(sku: "SKU-2", pn: "NEW-PART-1") + "\n" + Row(slug: "plc-9", sku: "SKU-9", pn: "taken-99") + "\n" + Row(slug: "plc-8", sku: "OLD-9", pn: "NEW-8") + "\n", lookup);
        r.Errors.Select(e => (e.Row, e.Code)).Should().Equal((4, "PART_NUMBER_IN_OTHER_PRODUCT"), (5, "SKU_IN_USE"));
        (r.ExistingProducts, r.NewProducts, r.ExistingVariants, r.NewVariants).Should().Be((1, 0, 1, 1));
    }

    [Fact]
    public async Task Header_problems_are_reported_and_stop_the_read()
    {
        var r = await RunAsync("product_slug,sku,sku,surprise,attr:nothing\nx,y,z,w,v\n");
        r.Errors.Select(e => e.Code).Should().Contain(["DUPLICATE_COLUMN", "UNKNOWN_COLUMN", "UNKNOWN_ATTRIBUTE", "MISSING_COLUMN"]);
        r.Errors.Should().OnlyContain(e => e.Row == 0);
        r.Rows.Should().Be(0);
    }

    [Fact]
    public async Task Wrong_field_count_and_blank_lines()
    {
        var r = await RunAsync(Header + "\n\n" + Row() + ",extra\n" + Row(slug: "plc-2", sku: "S2", pn: "ABC-123") + "\n");
        r.Errors.Should().ContainSingle(e => e.Code == "COLUMN_COUNT" && e.Row == 2);
        r.ValidRows.Should().Be(1);
    }

    [Fact]
    public async Task Empty_file_unclosed_quote_and_bad_encoding()
    {
        (await RunAsync("")).Errors.Should().ContainSingle(e => e.Code == "EMPTY_FILE");
        (await RunAsync(Header + "\n\"open,field\n")).Errors.Should().ContainSingle(e => e.Code == "MALFORMED_CSV");
        await using var bad = new MemoryStream([.. Encoding.ASCII.GetBytes("a,b\n"), 0xFF, 0xFE, 0xFA]);
        (await new CatalogImportValidator(new FakeLookup(), new()).DryRunAsync(bad)).Errors.Should().ContainSingle(e => e.Code == "INVALID_ENCODING");
    }

    [Fact]
    public async Task Size_row_and_field_limits_stop_the_read()
    {
        var many = Header + "\n" + string.Join("\n", Enumerable.Range(0, 5).Select(i => Row(slug: $"p-{i}", sku: $"S{i}", pn: $"PN-{i}0")));
        (await RunAsync(many, options: new(MaxRows: 3))).Errors.Should().ContainSingle(e => e.Code == "TOO_MANY_ROWS");
        (await RunAsync(many, options: new(MaxBytes: 100))).Errors.Should().ContainSingle(e => e.Code == "FILE_TOO_LARGE");
        (await RunAsync(Header + "\n" + Row(titleEn: new string('a', 500)), options: new(MaxFieldChars: 100))).Errors.Should().ContainSingle(e => e.Code == "FIELD_TOO_LONG");
    }

    [Fact]
    public async Task Errors_are_capped_but_counted()
    {
        var csv = Header + "\n" + string.Join("\n", Enumerable.Range(0, 10).Select(i => Row(slug: $"p-{i}", sku: $"S{i}", pn: $"PN-{i}0", price: "0")));
        var r = await RunAsync(csv, options: new(MaxErrors: 4));
        (r.ErrorCount, r.Errors.Count, r.ErrorsTruncated, r.InvalidRows).Should().Be((10, 4, true, 10));
    }
}
