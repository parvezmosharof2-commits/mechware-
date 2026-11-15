using Microsoft.EntityFrameworkCore.Migrations;

namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// Writes the Bangladesh location hierarchy (<see cref="BangladeshLocationSeeder"/>, the single source) into
/// <c>bd_locations</c>. Idempotent: <c>ON CONFLICT (id) DO NOTHING</c>, so a re-run, or a database that already has some of the
/// rows, is safe. Generated from the seeder rather than written out in the migration. Tests that build their schema with
/// EnsureCreated run <see cref="Statements"/> too.
/// </summary>
public static class BangladeshLocationSql
{
    private const int BatchSize = 100;

    public static IReadOnlyList<string> Statements()
    {
        var rows = BangladeshLocationSeeder.GetSeedLocations();
        var list = new List<string>();
        for (var i = 0; i < rows.Count; i += BatchSize)
        {
            var values = rows.Skip(i).Take(BatchSize).Select(l =>
                $"('{l.Id}', {(l.ParentId is null ? "NULL" : $"'{l.ParentId}'")}, {Q(l.Level)}, {Q(l.NameEn)}, {Q(l.NameBn)}, {Q(l.ShippingZone)}, {(l.PostalCodePrefix is null ? "NULL" : Q(l.PostalCodePrefix))}, now())");
            list.Add("INSERT INTO bd_locations (id, parent_id, level, name_en, name_bn, shipping_zone, postal_code_prefix, \"CreatedAtUtc\") VALUES\n"
                + string.Join(",\n", values) + "\nON CONFLICT (id) DO NOTHING;");
        }
        return list;
    }

    public static void Install(MigrationBuilder migrationBuilder)
    {
        foreach (var sql in Statements()) migrationBuilder.Sql(sql);
    }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";
}
