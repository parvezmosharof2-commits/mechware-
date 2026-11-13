using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Companies;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Pricing;

/// <summary>
/// D2.4: price lists through EF and the database backstop of ADR-0008 §3 (threat model §৮ PL1, PL4, PL8). The aggregate
/// already refuses all of this; these tests go around it with raw SQL, as a code bug or a hand-written query would.
/// </summary>
public sealed class PriceListPersistenceTests(PostgresDatabaseFixture db) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Trigger = "23000";
    private const string Check = "23514";
    private const string Unique = "23505";

    private static readonly DateTime Now = WholeSecond(DateTime.UtcNow);
    private static long _binSeq;

    private sealed record Actors(Guid Author, Guid Editor, Guid Submitter, Guid Approver);

    private static Actors NewActors() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private static DateTime WholeSecond(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    [PostgresFact]
    public async Task RoundTrip_ACompanyListThroughSubmitApproveAndWithdraw_ReloadsEqual()
    {
        var a = NewActors();
        var variant = await VariantAsync();
        var company = await CompanyAsync();
        var list = await ApprovedAsync(a, variant, PriceListScope.Company, company);

        await using var read = db.CreateContext();
        var back = await LoadAsync(read, list.Id);
        back.Should().BeEquivalentTo(list, o => o.ComparingByMembers<PriceList>().ComparingByMembers<PriceListEntry>().Excluding(p => p.DomainEvents)
            .Using<DateTime>(c => c.Subject.Should().BeCloseTo(c.Expectation, TimeSpan.FromMilliseconds(1))).WhenTypeIs<DateTime>());
        back.Status.Should().Be(PriceListStatus.Approved);
        back.CompanyId.Should().Be(company);
        back.ContractRef.Should().Be("CN-2026-001");
        back.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$").And.Be(back.ComputeContentHash(), "the hash survives the database");
        back.EffectiveFromUtc.Should().Be(Now.AddSeconds(60), "start = max(validFrom, approval + 60 s), stored as computed");
        back.Contributors.Select(c => c.UserId).Should().BeEquivalentTo([a.Author, a.Editor]);
        back.Entries.Should().HaveCount(2).And.OnlyContain(e => e.CostMinorAtApproval == 50_000 && e.FloorMinorAtApproval == 55_000
            && e.BelowFloorAtApproval == false);

        back.Withdraw(a.Approver, "Contract ended early", Now.AddMinutes(5));
        await read.SaveChangesAsync();
        await using var again = db.CreateContext();
        var withdrawn = await LoadAsync(again, list.Id);
        withdrawn.Status.Should().Be(PriceListStatus.Withdrawn);
        withdrawn.WithdrawReason.Should().Be("Contract ended early");
        withdrawn.WithdrawnBy.Should().Be(a.Approver);
    }

    [PostgresFact]
    public async Task ApprovedByEqualsSubmittedBy_FailsAtDbLevel()
    {
        var a = NewActors();
        var list = await PendingAsync(a, await VariantAsync());

        var self = () => ApproveRawAsync(list.Id, "submitted_by");
        (await self.Should().ThrowAsync<PostgresException>()).Which.Should().Match<PostgresException>(e =>
            e.SqlState == Check && e.ConstraintName == "ck_price_lists_four_eyes");

        await ApproveRawAsync(list.Id, $"'{a.Approver}'"); // the same statement with a second person passes
        (await StatusAsync(list.Id)).Should().Be("Approved");
    }

    [PostgresFact]
    public async Task ApprovedByAContributor_FailsAtDbLevel()
    {
        var a = NewActors();
        var list = await PendingAsync(a, await VariantAsync());

        var editor = () => ApproveRawAsync(list.Id, $"'{a.Editor}'");
        await ShouldFailAsync(editor, Trigger, "cannot approve");
        (await StatusAsync(list.Id)).Should().Be("PendingApproval");
    }

    [PostgresFact]
    public async Task UpdatingAnEntryOfAnApprovedList_FailsAtDbLevel()
    {
        var list = await ApprovedAsync(NewActors(), await VariantAsync());
        var entry = list.Entries.First();

        await ShouldFailAsync(() => SqlAsync($"UPDATE price_list_entries SET unit_price_minor = 1 WHERE id = '{entry.Id}'"), Trigger, "cannot change");
        await using var read = db.CreateContext();
        (await LoadAsync(read, list.Id)).Entries.Single(e => e.Id == entry.Id).UnitPriceMinor.Should().Be(entry.UnitPriceMinor);
    }

    [PostgresFact]
    public async Task ApprovedList_RefusesEntryAndHeaderChanges_AndAllowsOnlyTheWithdrawal()
    {
        var a = NewActors();
        var variant = await VariantAsync();
        var list = await ApprovedAsync(a, variant);
        var id = list.Id;
        var entry = list.Entries.First().Id;

        await ShouldFailAsync(() => SqlAsync($"DELETE FROM price_list_entries WHERE id = '{entry}'"), Trigger, "cannot be removed");
        await ShouldFailAsync(() => SqlAsync($"""
            INSERT INTO price_list_entries (id, price_list_id, variant_id, min_qty, unit_price_minor, created_at)
            VALUES (gen_random_uuid(), '{id}', '{variant}', 500, 10, now())
            """), Trigger, "only to a Draft");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_list_entries SET cost_minor_at_approval = 1 WHERE id = '{entry}'"), Trigger, "cannot change");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_lists SET valid_to = valid_to + interval '1 day' WHERE id = '{id}'"), Trigger, "cannot change");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_lists SET effective_from_utc = effective_from_utc + interval '1 day' WHERE id = '{id}'"), Trigger, "cannot change");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_lists SET content_hash = repeat('0', 64) WHERE id = '{id}'"), Trigger, "cannot change");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_lists SET name = 'Renamed' WHERE id = '{id}'"), Trigger, "cannot change");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_lists SET scope = 'Group' WHERE id = '{id}'"), Trigger, "never change");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_lists SET status = 'Draft', approved_by = NULL, approved_at = NULL, effective_from_utc = NULL, submitted_by = NULL, submitted_at = NULL, content_hash = NULL WHERE id = '{id}'"), Trigger, "cannot become");
        await ShouldFailAsync(() => SqlAsync($"DELETE FROM price_lists WHERE id = '{id}'"), Trigger, "only a Draft");

        await using (var ctx = db.CreateContext())
        {
            (await LoadAsync(ctx, id)).Withdraw(a.Approver, "Wrong prices approved", Now.AddMinutes(1));
            await ctx.SaveChangesAsync();
        }

        (await StatusAsync(id)).Should().Be("Withdrawn");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_lists SET withdraw_reason = 'Something else' WHERE id = '{id}'"), Trigger, "cannot change");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_lists SET status = 'Approved', withdrawn_by = NULL, withdrawn_at = NULL, withdraw_reason = NULL WHERE id = '{id}'"), Trigger, "cannot become");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_list_entries SET unit_price_minor = 1 WHERE id = '{entry}'"), Trigger, "cannot change");
    }

    [PostgresFact]
    public async Task ADraftCannotSkipTheSubmission_NorBeInsertedAsApproved()
    {
        var a = NewActors();
        var list = Draft(a, await VariantAsync());
        await SaveNewAsync(list);

        await ShouldFailAsync(() => SqlAsync($"UPDATE price_lists SET status = 'Approved', submitted_by = '{a.Submitter}', submitted_at = now(), content_hash = repeat('a', 64), approved_by = '{a.Approver}', approved_at = now(), effective_from_utc = greatest(valid_from, now() + interval '60 seconds') WHERE id = '{list.Id}'"), Trigger, "submitted before");
        await ShouldFailAsync(() => CloneAsync(list.Id, version: 2, status: "Approved"), Trigger, "created as a Draft");
    }

    [PostgresFact]
    public async Task NonPositivePrice_FailsAtDbLevel()
    {
        var list = Draft(NewActors(), await VariantAsync());
        await SaveNewAsync(list);
        var entry = list.Entries.First().Id;

        foreach (var price in new[] { 0L, -1L, PriceListLimits.MaxUnitPriceMinor + 1 })
        {
            var act = () => SqlAsync($"UPDATE price_list_entries SET unit_price_minor = {price} WHERE id = '{entry}'");
            (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_price_list_entries_price");
        }

        foreach (var minQty in new[] { 0, -3, 10_001 })
        {
            var act = () => SqlAsync($"UPDATE price_list_entries SET min_qty = {minQty} WHERE id = '{entry}'");
            (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_price_list_entries_min_qty");
        }

        await SqlAsync($"UPDATE price_list_entries SET unit_price_minor = 1 WHERE id = '{entry}'"); // a Draft may still change
    }

    [PostgresFact]
    public async Task OneOpenVersionPerSeries_ANewVersionOnlyAfterTheOpenOneIsApprovedOrWithdrawn()
    {
        var a = NewActors();
        var variant = await VariantAsync();
        var v1 = await ApprovedAsync(a, variant);

        var reviser = Guid.NewGuid();
        var v2 = v1.Revise(Guid.NewGuid(), reviser, Now);
        await SaveNewAsync(v2); // v1 is Approved: a new Draft is allowed

        var second = () => CloneAsync(v2.Id, version: 3, status: "Draft");
        (await second.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ux_price_lists_one_open_version");

        await using (var ctx = db.CreateContext())
        {
            var open = await LoadAsync(ctx, v2.Id);
            open.Submit(a.Submitter, Floors(open, variant), false, Now);
            await ctx.SaveChangesAsync();
            open.Approve(a.Approver, false, open.ContentHash!, [], Floors(open, variant), false, Now);
            await ctx.SaveChangesAsync();
            open.Withdraw(a.Approver, "Superseded by v3", Now.AddMinutes(1));
            await ctx.SaveChangesAsync();
        }

        await using var read = db.CreateContext();
        var v3 = (await LoadAsync(read, v2.Id)).Revise(Guid.NewGuid(), reviser, Now);
        await SaveNewAsync(v3); // v2 is Withdrawn
        (await read.PriceLists.CountAsync(p => p.SeriesId == v1.SeriesId)).Should().Be(3);
        var dup = () => CloneAsync(v3.Id, version: 4, status: "Draft");
        (await dup.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ux_price_lists_one_open_version");
    }

    [PostgresFact]
    public async Task Contributors_AreRecordedOnlyInADraft_AndNeverRemovedWhileTheListExists()
    {
        var a = NewActors();
        var variant = await VariantAsync();
        var draft = Draft(a, variant);
        await SaveNewAsync(draft);
        await ShouldFailAsync(() => SqlAsync($"DELETE FROM price_list_contributors WHERE price_list_id = '{draft.Id}' AND user_id = '{a.Editor}'"), Trigger, "kept");
        await ShouldFailAsync(() => SqlAsync($"UPDATE price_list_contributors SET user_id = gen_random_uuid() WHERE price_list_id = '{draft.Id}'"), Trigger, "never change");

        var pending = await PendingAsync(NewActors(), variant);
        await ShouldFailAsync(() => SqlAsync($"INSERT INTO price_list_contributors (price_list_id, user_id, first_contributed_at) VALUES ('{pending.Id}', gen_random_uuid(), now())"), Trigger, "only while");

        // Deleting a Draft cascades to its entries and contributors.
        await using var ctx = db.CreateContext();
        (await ctx.PriceLists.Where(p => p.Id == draft.Id).ExecuteDeleteAsync()).Should().Be(1);
        (await CountAsync($"SELECT count(*) FROM price_list_entries WHERE price_list_id = '{draft.Id}'")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM price_list_contributors WHERE price_list_id = '{draft.Id}'")).Should().Be(0);
    }

    [PostgresFact]
    public async Task MarginFloors_ShapeMarkupAndOneRowPerScopeBrandCategory()
    {
        (await CountAsync("SELECT count(*) FROM margin_floors WHERE scope = 'Global'")).Should().Be(1, "the fixture ran the seed");
        await ShouldFailAsync(() => InsertFloorAsync("Global", null, null, 12), Unique, null);

        await using var ctx = db.CreateContext();
        var brand = new Brand(Guid.NewGuid(), "Floor brand", "floor-brand-" + Guid.NewGuid().ToString("N")[..8], null, false);
        ctx.Brands.Add(brand);
        await ctx.SaveChangesAsync();
        ctx.MarginFloors.Add(MarginFloor.Create(Guid.NewGuid(), MarginFloorScope.Brand, brand.Id, null, 12.5m, Guid.NewGuid(), Now));
        await ctx.SaveChangesAsync();
        (await ctx.MarginFloors.SingleAsync(f => f.BrandId == brand.Id)).ToRow()
            .Should().Be(new MarginFloorRow(MarginFloorScope.Brand, brand.Id, null, 12.5m));

        var dup = () => InsertFloorAsync("Brand", brand.Id, null, 20);
        (await dup.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ux_margin_floors_scope_brand_category", "NULLS NOT DISTINCT");

        foreach (var (scope, b, c) in new (string, Guid?, Guid?)[] { ("Global", brand.Id, null), ("Brand", null, null), ("Category", brand.Id, null), ("BrandAndCategory", brand.Id, null), ("Nope", null, null) })
        {
            var bad = () => InsertFloorAsync(scope, b, c, 10);
            (await bad.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_margin_floors_shape", scope);
        }

        foreach (var markup in new[] { -0.01m, 1000.01m, 10.005m })
        {
            var bad = () => InsertFloorAsync("Brand", brand.Id, null, markup);
            (await bad.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_margin_floors_markup", markup.ToString());
        }
    }

    [PostgresFact]
    public async Task Migrate_SeedsOneGlobalFloor_InstallsTheGuards_AndIsIdempotent()
    {
        string name = "mw_d24_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(TestInfra.AdminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        string cs = MechwareDatabase.WithDefaults(new NpgsqlConnectionStringBuilder(TestInfra.AdminConnection) { Database = name }.ConnectionString);
        try
        {
            (await MechwareDatabase.MigrateAsync(cs, TextWriter.Null, TextWriter.Null)).Should().Be(0);
            (await MechwareDatabase.MigrateAsync(cs, TextWriter.Null, TextWriter.Null)).Should().Be(0);
            await using var ctx = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(cs).Options);
            await ctx.Database.ExecuteSqlRawAsync(PriceListSql.Install); // a re-run seeds nothing new

            var floors = await ctx.MarginFloors.ToListAsync();
            floors.Should().ContainSingle();
            floors[0].Id.Should().Be(PriceListSql.GlobalMarginFloorId);
            floors[0].ToRow().Should().Be(new MarginFloorRow(MarginFloorScope.Global, null, null, MarginFloorPolicy.DefaultGlobalMinMarkupPercent));
            (await ctx.PriceLists.CountAsync()).Should().Be(0, "no price list is seeded");

            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var q = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM pg_trigger WHERE tgname IN ('trg_price_lists_guard', 'trg_price_list_entries_guard', 'trg_price_list_contributors_guard'))
                     + (SELECT count(*) FROM pg_constraint WHERE conname IN ('ck_price_lists_four_eyes', 'ck_price_list_entries_price', 'ck_price_list_entries_min_qty', 'ck_margin_floors_shape'))
                     + (SELECT count(*) FROM pg_indexes WHERE indexname IN ('ux_price_lists_one_open_version', 'ux_margin_floors_scope_brand_category'))
                """, conn);
            ((long)(await q.ExecuteScalarAsync())!).Should().Be(9);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(TestInfra.AdminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // ---- Fixtures ------------------------------------------------------------------------------------------------

    private static PriceList Draft(Actors a, Guid variant, PriceListScope scope = PriceListScope.General, Guid? company = null)
    {
        var list = PriceList.Create(Guid.NewGuid(), Guid.NewGuid(), "D2.4 list", scope, company, company is null ? null : "CN-2026-001",
            Now.AddDays(-1), Now.AddDays(300), a.Author, Now);
        list.AddEntry(Guid.NewGuid(), variant, 2, 100_000, null, a.Author, Now);
        list.AddEntry(Guid.NewGuid(), variant, 10, 90_000, null, a.Editor, Now);
        return list;
    }

    /// <summary>Cost 500.00 and the 10 % global floor: floor 550.00, every entry above it.</summary>
    private static MarginFloorEvaluation Floors(PriceList list, Guid variant) => MarginFloorPolicy.Evaluate(list,
        [new VariantCostFacts(variant, 50_000, null, [], 0m)], [new MarginFloorRow(MarginFloorScope.Global, null, null, 10m)]);

    private async Task SaveNewAsync(PriceList list)
    {
        await using var ctx = db.CreateContext();
        ctx.PriceLists.Add(list);
        await ctx.SaveChangesAsync();
    }

    private async Task<PriceList> PendingAsync(Actors a, Guid variant, PriceListScope scope = PriceListScope.General, Guid? company = null)
    {
        var draft = Draft(a, variant, scope, company);
        await SaveNewAsync(draft);
        await using var ctx = db.CreateContext();
        var list = await LoadAsync(ctx, draft.Id);
        list.Submit(a.Submitter, Floors(list, variant), false, Now);
        await ctx.SaveChangesAsync();
        return list;
    }

    private async Task<PriceList> ApprovedAsync(Actors a, Guid variant, PriceListScope scope = PriceListScope.General, Guid? company = null)
    {
        var pending = await PendingAsync(a, variant, scope, company);
        await using var ctx = db.CreateContext();
        var list = await LoadAsync(ctx, pending.Id);
        list.Approve(a.Approver, false, list.ContentHash!, [], Floors(list, variant), false, Now);
        await ctx.SaveChangesAsync();
        return list;
    }

    private static Task<PriceList> LoadAsync(MechwareDbContext ctx, Guid id) =>
        ctx.PriceLists.Include(p => p.Entries).Include(p => p.Contributors).SingleAsync(p => p.Id == id);

    private async Task<Guid> VariantAsync()
    {
        await using var ctx = db.CreateContext();
        return (await CatalogTestData.SeedVariantAsync(ctx, onHand: 1)).VariantId;
    }

    private async Task<Guid> CompanyAsync()
    {
        await using var ctx = db.CreateContext();
        var bin = (Interlocked.Increment(ref _binSeq) + 7_000_000_000_000L).ToString("D13");
        var company = Company.Register(Guid.NewGuid(), "Contract Buyer Ltd", "TL-D24-1", bin, null, null);
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();
        return company.Id;
    }

    /// <summary>Approves with raw SQL as <paramref name="approver"/> (a column or a quoted uuid), around the aggregate.</summary>
    private Task ApproveRawAsync(Guid id, string approver) => SqlAsync($"""
        UPDATE price_lists SET status = 'Approved', approved_by = {approver}, approved_at = now(),
               effective_from_utc = greatest(valid_from, now() + interval '60 seconds')
        WHERE id = '{id}'
        """);

    /// <summary>Copies a list row (not its entries) as another version, with raw SQL.</summary>
    private Task CloneAsync(Guid id, int version, string status) => SqlAsync($"""
        CREATE TEMP TABLE pl_clone ON COMMIT DROP AS SELECT * FROM price_lists WHERE id = '{id}';
        UPDATE pl_clone SET id = gen_random_uuid(), version = {version}, status = '{status}';
        INSERT INTO price_lists SELECT * FROM pl_clone;
        """);

    private Task InsertFloorAsync(string scope, Guid? brand, Guid? category, decimal markup) => SqlAsync($"""
        INSERT INTO margin_floors (id, scope, brand_id, category_id, min_markup_percent, created_at)
        VALUES (gen_random_uuid(), '{scope}', {Uuid(brand)}, {Uuid(category)}, {markup.ToString(System.Globalization.CultureInfo.InvariantCulture)}, now())
        """);

    private static string Uuid(Guid? id) => id is null ? "NULL" : $"'{id}'";

    private async Task SqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string> StatusAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT status FROM price_lists WHERE id = '{id}'", conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ShouldFailAsync(Func<Task> act, string sqlState, string? message)
    {
        var ex = (await act.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(sqlState, ex.MessageText);
        if (message is not null) ex.MessageText.Should().Contain(message);
    }
}
