using Mechware.Domain.Identity;

namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Pre-seeded Bangladesh administrative hierarchy (8 Divisions, 64 Districts and the Upazilas, defined in BangladeshLocationData)
/// with bilingual English & Bangla names and shipping zone classification.
/// </summary>
public static class BangladeshLocationSeeder
{
    public static readonly Guid DivDhakaId = Guid.Parse("11111111-0001-0000-0000-000000000001");
    public static readonly Guid DivChattogramId = Guid.Parse("11111111-0001-0000-0000-000000000002");
    public static readonly Guid DivRajshahiId = Guid.Parse("11111111-0001-0000-0000-000000000003");
    public static readonly Guid DivKhulnaId = Guid.Parse("11111111-0001-0000-0000-000000000004");
    public static readonly Guid DivSylhetId = Guid.Parse("11111111-0001-0000-0000-000000000005");
    public static readonly Guid DivRangpurId = Guid.Parse("11111111-0001-0000-0000-000000000006");
    public static readonly Guid DivBarishalId = Guid.Parse("11111111-0001-0000-0000-000000000007");
    public static readonly Guid DivMymensinghId = Guid.Parse("11111111-0001-0000-0000-000000000008");

    public static readonly Guid DistDhakaId = Guid.Parse("22222222-0001-0000-0000-000000000001");
    public static readonly Guid DistGazipurId = Guid.Parse("22222222-0001-0000-0000-000000000002");
    public static readonly Guid DistNarayanganjId = Guid.Parse("22222222-0001-0000-0000-000000000003");
    public static readonly Guid DistChattogramId = Guid.Parse("22222222-0002-0000-0000-000000000001");
    public static readonly Guid DistPabnaId = Guid.Parse("22222222-0003-0000-0000-000000000001");
    public static readonly Guid DistRajshahiId = Guid.Parse("22222222-0003-0000-0000-000000000002");
    public static readonly Guid DistKhulnaId = Guid.Parse("22222222-0004-0000-0000-000000000001");
    public static readonly Guid DistSylhetId = Guid.Parse("22222222-0005-0000-0000-000000000001");

    private static readonly IReadOnlyList<BdLocation> Seed = Build();

    /// <summary>
    /// The single source of the Bangladesh hierarchy (A9): IdentityService serves and validates from this list, and the
    /// <c>SeedBangladeshLocations</c> migration writes the same rows to <c>bd_locations</c>.
    /// Ids are counters, never hashes. Division n: <c>11111111-0001-0000-0000-{n}</c>. District k of division n:
    /// <c>22222222-{n:D4}-0000-0000-{k}</c>. Upazila u of district k of division n: <c>33333333-{n:D4}-{k:D4}-0000-{u}</c>.
    /// n, k, u are the 1-based positions in <see cref="BangladeshLocationData"/>, which is append-only.
    /// </summary>
    public static IReadOnlyList<BdLocation> GetSeedLocations() => Seed;

    private static List<BdLocation> Build()
    {
        var rows = new List<BdLocation>();
        int div = 0, dist = 0, upa = 0;
        Guid divId = Guid.Empty, distId = Guid.Empty;
        foreach (var raw in BangladeshLocationData.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var body = line[2..];
            switch (line[0])
            {
                case 'D':
                    div++; dist = 0;
                    divId = Guid.Parse($"11111111-0001-0000-0000-{div:D12}");
                    rows.Add(Make(divId, null, "division", body));
                    break;
                case 'C':
                    dist++; upa = 0;
                    distId = Guid.Parse($"22222222-{div:D4}-0000-0000-{dist:D12}");
                    rows.Add(Make(distId, divId, "district", body));
                    break;
                case 'U':
                    foreach (var item in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        upa++;
                        rows.Add(Make(Guid.Parse($"33333333-{div:D4}-{dist:D4}-0000-{upa:D12}"), distId, "upazila", item));
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Bad location data line: {line}");
            }
        }
        return rows;
    }

    private static BdLocation Make(Guid id, Guid? parent, string level, string fields)
    {
        var f = fields.Split('|');
        return new BdLocation(id, parent, level, f[0], f[1], f.Length > 2 ? f[2] : "outside_dhaka", f.Length > 3 ? f[3] : null);
    }
}
