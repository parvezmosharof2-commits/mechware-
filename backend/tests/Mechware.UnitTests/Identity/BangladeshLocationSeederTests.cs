using FluentAssertions;
using Mechware.Infrastructure.Persistence;
using Xunit;

namespace Mechware.UnitTests.Identity;

/// <summary>A9: the location reference data is consistent (ids, parents, levels, names) and the SQL generated from it is safe.</summary>
public class BangladeshLocationSeederTests
{
    private static readonly IReadOnlyList<Mechware.Domain.Identity.BdLocation> All = BangladeshLocationSeeder.GetSeedLocations();

    [Fact]
    public void ThereAreEightDivisionsAndSixtyFourDistricts_AndUpazilas()
    {
        All.Count(l => l.Level == "division").Should().Be(8);
        All.Count(l => l.Level == "district").Should().Be(64);
        All.Count(l => l.Level == "upazila").Should().BeGreaterThan(480);
    }

    [Fact]
    public void EveryIdIsUnique_AndEveryParentExistsOnTheLevelAbove()
    {
        All.Select(l => l.Id).Distinct().Should().HaveCount(All.Count);
        var byId = All.ToDictionary(l => l.Id);
        foreach (var l in All)
        {
            if (l.Level == "division") { l.ParentId.Should().BeNull(); continue; }
            byId.Should().ContainKey(l.ParentId!.Value, l.NameEn);
            byId[l.ParentId.Value].Level.Should().Be(l.Level == "district" ? "division" : "district", l.NameEn);
        }
        All.Where(l => l.Level == "district").GroupBy(l => l.ParentId).Should().HaveCount(8, "every division has districts");
        All.Where(l => l.Level == "district").Select(d => d.Id).Except(All.Where(l => l.Level == "upazila").Select(u => u.ParentId!.Value))
            .Should().BeEmpty("every district has upazilas");
    }

    [Fact]
    public void NamesAreFilled_AndNoSiblingsShareAnEnglishOrBanglaName()
    {
        All.Should().OnlyContain(l => l.NameEn.Length > 0 && l.NameBn.Length > 0 && l.NameEn.Length <= 100 && l.NameBn.Length <= 100);
        All.Should().OnlyContain(l => l.NameBn.Any(c => c >= '\u0980' && c <= '\u09FF'), "Bangla names are in Bangla script");
        All.GroupBy(l => (l.ParentId, l.NameEn.ToLowerInvariant())).Where(g => g.Count() > 1).Should().BeEmpty();
        All.GroupBy(l => (l.ParentId, l.NameBn)).Where(g => g.Count() > 1).Should().BeEmpty();
        All.Where(l => l.Level == "district").GroupBy(l => l.NameEn.ToLowerInvariant()).Where(g => g.Count() > 1).Should().BeEmpty("district names are unique country-wide");
        All.Should().OnlyContain(l => l.ShippingZone == "inside_dhaka" || l.ShippingZone == "dhaka_suburb" || l.ShippingZone == "outside_dhaka");
    }

    [Fact]
    public void TheOlderRowsKeepTheirIds_AndTheDistrictsSitInTheirDivisions()
    {
        var byId = All.ToDictionary(l => l.Id);
        byId[BangladeshLocationSeeder.DistGazipurId].NameEn.Should().Be("Gazipur");
        byId[BangladeshLocationSeeder.DistPabnaId].ParentId.Should().Be(BangladeshLocationSeeder.DivRajshahiId);
        byId[BangladeshLocationSeeder.DistSylhetId].ParentId.Should().Be(BangladeshLocationSeeder.DivSylhetId);
        byId[Guid.Parse("33333333-0002-0001-0000-000000000002")].NameEn.Should().Be("Sitakunda Shipbreaking & Steel Zone");
        byId[Guid.Parse("33333333-0003-0001-0000-000000000002")].NameEn.Should().Be("Ishwardi EPZ");

        string DivisionOf(string district) => byId[All.Single(l => l.Level == "district" && l.NameEn == district).ParentId!.Value].NameEn;
        DivisionOf("Dhaka").Should().Be("Dhaka");
        DivisionOf("Cumilla").Should().Be("Chattogram");
        DivisionOf("Jamalpur").Should().Be("Mymensingh");
        DivisionOf("Sirajganj").Should().Be("Rajshahi");
        DivisionOf("Jhalokati").Should().Be("Barishal");
        DivisionOf("Sunamganj").Should().Be("Sylhet");
        DivisionOf("Panchagarh").Should().Be("Rangpur");
        DivisionOf("Satkhira").Should().Be("Khulna");
        DivisionOf("Tangail").Should().Be("Dhaka");
    }

    [Fact]
    public void TheDistrictCountPerDivisionIsRight()
    {
        var byId = All.ToDictionary(l => l.Id);
        All.Where(l => l.Level == "district").GroupBy(l => byId[l.ParentId!.Value].NameEn).ToDictionary(g => g.Key, g => g.Count())
            .Should().BeEquivalentTo(new Dictionary<string, int>
            {
                ["Dhaka"] = 13,
                ["Chattogram"] = 11,
                ["Rajshahi"] = 8,
                ["Khulna"] = 10,
                ["Sylhet"] = 4,
                ["Rangpur"] = 8,
                ["Barishal"] = 6,
                ["Mymensingh"] = 4,
            });
    }

    [Fact]
    public void TheGeneratedSql_IsIdempotent_QuotesNames_AndCoversEveryRow()
    {
        var sql = BangladeshLocationSql.Statements();
        sql.Should().OnlyContain(s => s.EndsWith("ON CONFLICT (id) DO NOTHING;"));
        string.Concat(sql).Should().Contain("'Cox''s Bazar'");
        All.Should().OnlyContain(l => string.Concat(sql).Contains(l.Id.ToString()));
    }
}
