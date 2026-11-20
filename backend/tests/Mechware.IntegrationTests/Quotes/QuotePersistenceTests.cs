using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Domain.Companies;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.4: RFQs and quotes through EF, and the database backstop of ADR-0011 §6, §8.6, §11 (threat model §১৩ QT3, QT4, QT7).
/// The aggregates already refuse all of this; the raw-SQL tests go around them, as a code bug or a hand-written query would.
/// </summary>
public sealed class QuotePersistenceTests(PostgresDatabaseFixture db) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Trigger = "23000";
    private const string Check = "23514";
    private const string Unique = "23505";
    private const string Exclusion = "23P01";

    /// <summary>10:00 in Dhaka on 17 November 2026, whole seconds (timestamptz keeps microseconds).</summary>
    private static readonly DateTime Now = new(2026, 11, 17, 4, 0, 0, DateTimeKind.Utc);
    private static readonly QuoteOptions Options = new();
    private static readonly MarginFloorRow[] ZeroFloor = [new(MarginFloorScope.Global, null, null, 0m)];
    private static long _binSeq;

    private static readonly Guid Author = Guid.NewGuid(), Editor = Guid.NewGuid(), Sender = Guid.NewGuid(), Approver = Guid.NewGuid();

    private sealed record World(Guid Company, Guid Site, Guid Contact, Guid V1, Guid Seller1, Guid V2, Guid Seller2);

    private static QuoteActor Staff(Guid id) => new(id, []);

    // ---- Round trips --------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task RoundTrip_RfqAndQuoteThroughApprovalAndAcceptance_ReloadEqual_AndTheContentHashRecomputes()
    {
        var w = await WorldAsync();
        var rfq = Rfq.Submit(Guid.NewGuid(), QuoteNumber.NewRfq(Now), w.Company, w.Contact, RfqSource.Text, "Need 10 PLCs, দ্রুত দরকার",
            QuoteValidity.DhakaDate(Now).AddDays(10), w.Site,
            [new("6ES7214-1AG40 10", "6ES72141AG40", 10, RfqMatchStatus.Matched, w.V1, null, "Original only"),
             new("unknown relay 4", null, 4, RfqMatchStatus.NotFound, null, null, null)],
            "idem-key-0001", new string('b', 64), Now);
        await SaveNewAsync(rfq); // born Submitted
        var assignee = await TestUsers.CreateAsync(db);
        await using (var ctx = db.CreateContext())
        {
            var stored = await ctx.Rfqs.Include(r => r.Lines).SingleAsync(r => r.Id == rfq.Id);
            stored.Assign(assignee, Now);
            stored.StartQuoting(Now);
            await ctx.SaveChangesAsync();
            rfq = stored;
        }

        var quote = await PendingAsync(w, fixedDelivery: true, rfqId: rfq.Id);
        await using (var ctx = db.CreateContext())
        {
            var q = await LoadAsync(ctx, quote.Id);
            Approve(q, 1, Approver);
            await ctx.SaveChangesAsync();
        }

        var order = await OrderAsync(w);
        Quote sent;
        await using (var ctx = db.CreateContext())
        {
            sent = await LoadAsync(ctx, quote.Id);
            sent.Accept(1, sent.Version(1).ContentHash!, order, w.Contact, false, Now.AddHours(2)); // recomputes the hash from the rows
            (await ctx.Rfqs.Include(r => r.Lines).SingleAsync(r => r.Id == rfq.Id)).RecordQuoteSent(Now.AddHours(1));
            await ctx.SaveChangesAsync();
        }

        await using var read = db.CreateContext();
        var back = await LoadAsync(read, quote.Id);
        back.Should().BeEquivalentTo(sent, o => o.ComparingByMembers<Quote>().ComparingByMembers<QuoteVersion>().ComparingByMembers<QuoteLine>()
            .Excluding(q => q.DomainEvents).Using<DateTime>(c => c.Subject.Should().BeCloseTo(c.Expectation, TimeSpan.FromMilliseconds(1)))
            .WhenTypeIs<DateTime>());
        var v = back.Version(1);
        v.Status.Should().Be(QuoteVersionStatus.Accepted);
        v.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$").And.Be(QuoteContentHash.Compute(back, v), "the hash survives the database");
        v.Lines.Single(l => l.LineNo == 2).VatRate.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be("0.0750");
        v.Lines.Should().OnlyContain(l => l.TitleBn == "সিমেন্স এস৭-১২০০ সিপিইউ" && l.ReferenceSource == PriceSource.Contract
            && l.CostMinorAtCheck == 2, "the approval rewrote the check figures in the same save that sent the version");
        v.CompanySnapshot.Should().Be(new QuoteCompanySnapshot("Acme ইঞ্জিনিয়ারিং Ltd", "000123456-0101", "12 Tejgaon I/A, Dhaka 1208"));
        v.ShipTo.Should().Be(SiteCopy, "the ship-to snapshot round-trips (D4.7) and is part of the recomputed hash");
        (await ScalarAsync<string>($"SELECT ship_to->>'siteName' FROM quote_versions WHERE id = '{v.Id}'")).Should().Be("তেজগাঁও কারখানা");
        v.Flags!.OverAuthorityLineIds.Should().ContainSingle();
        v.Contributors.Select(c => c.UserId).Should().BeEquivalentTo([Author, Editor]);
        (await ScalarAsync<string>($"SELECT company_snapshot->>'bin' FROM quote_versions WHERE id = '{v.Id}'"))
            .Should().NotContain("000123456", "the BIN is encrypted inside the snapshot as in companies");

        var backRfq = await read.Rfqs.Include(r => r.Lines).SingleAsync(r => r.Id == rfq.Id);
        backRfq.Should().BeEquivalentTo(rfq, o => o.ComparingByMembers<Rfq>().ComparingByMembers<RfqLine>().Excluding(r => r.DomainEvents)
            .Excluding(r => r.Status).Excluding(r => r.FirstQuoteSentAtUtc).Excluding(r => r.UpdatedAtUtc)
            .Using<DateTime>(c => c.Subject.Should().BeCloseTo(c.Expectation, TimeSpan.FromMilliseconds(1))).WhenTypeIs<DateTime>());
        backRfq.Status.Should().Be(RfqStatus.Answered);
    }

    [PostgresFact]
    public async Task SendingANewerVersion_SupersedesTheLiveOne_InOneSave_AndTwoLiveVersionsFailAtDbLevel()
    {
        var w = await WorldAsync();
        var quote = await SentAsync(w);
        await using (var ctx = db.CreateContext())
        {
            (await LoadAsync(ctx, quote.Id)).Revise(Guid.NewGuid(), Staff(Editor), false, Now.AddHours(1));
            await ctx.SaveChangesAsync();
        }

        var v2 = await ScalarAsync<Guid>($"SELECT id FROM quote_versions WHERE quote_id = '{quote.Id}' AND version_no = 2");
        await FreezeRawAsync(v2, "PendingApproval");
        // v2 approved while v1 is still Sent: refused when the transaction commits (deferred exclusion constraint).
        await ShouldFailAsync(() => SqlAsync($"BEGIN; {ApproveSql(v2, $"'{Approver}'")}; COMMIT;"), Exclusion, "ux_quote_versions_one_live");
        await SqlAsync($"BEGIN; {ApproveSql(v2, $"'{Approver}'")}; UPDATE quote_versions SET status = 'Superseded', superseded_at = now() " +
            $"WHERE quote_id = '{quote.Id}' AND version_no = 1; COMMIT;");
        (await ScalarAsync<long>($"SELECT count(*) FROM quote_versions WHERE quote_id = '{quote.Id}' AND status = 'Sent'")).Should().Be(1);

        // Through the aggregate: v3 sent supersedes v2 in one SaveChanges, in whatever order EF writes the rows.
        var other = await SentAsync(w);
        for (var n = 2; n <= 4; n++)
        {
            await using var ctx = db.CreateContext();
            var q = await LoadAsync(ctx, other.Id);
            q.Revise(Guid.NewGuid(), Staff(Editor), false, Now.AddHours(n));
            await ctx.SaveChangesAsync();
            q.Send(n, Issue(q, n, w), Staff(Sender), false, Options, Now.AddHours(n));
            await ctx.SaveChangesAsync();
        }

        await using var read = db.CreateContext();
        var back = await LoadAsync(read, other.Id);
        back.Versions.Select(v => v.Status).Should().Equal(QuoteVersionStatus.Superseded, QuoteVersionStatus.Superseded,
            QuoteVersionStatus.Superseded, QuoteVersionStatus.Sent);
        back.Versions.Should().OnlyContain(v => v.ContentHash == QuoteContentHash.Compute(back, v));
    }

    // ---- QT3: frozen versions -------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task UpdatingALineOrTheHeaderOfASentVersion_FailsAtDbLevel()
    {
        var w = await WorldAsync();
        var quote = await SentAsync(w);
        var v = quote.Version(1);
        var line = v.Lines.First().Id;

        foreach (var set in new[] { "unit_price_minor = 1, line_net_minor = quantity", "quantity = 1, line_net_minor = unit_price_minor",
                     "title_en = 'Cheaper title'", "vat_rate = 0", "line_note = 'changed'", "public_unit_price_minor = 1",
                     "below_floor = true, below_floor_reason = 'tampered'", "reference_unit_price_minor = 1", "variant_id = variant_id" })
            await ShouldFailAsync(() => SqlAsync($"UPDATE quote_lines SET {set} WHERE id = '{line}'"), Trigger, "cannot change", set);
        foreach (var set in new[] { "validity_days = 90", "terms_text = 'New terms'", "internal_note = 'x'", "content_hash = repeat('0', 64)",
                     "flags = '{}'::jsonb", "valid_until_utc = valid_until_utc + interval '30 days'", "goods_net_minor = 0, tax_minor = 0, goods_total_minor = 0",
                     "company_snapshot = '{\"name\":\"Other\"}'::jsonb", "sent_by = gen_random_uuid()", "version_no = 9", "quote_id = gen_random_uuid()" })
            await ShouldFailAsync(() => SqlAsync($"UPDATE quote_versions SET {set} WHERE id = '{v.Id}'"), Trigger, "cannot change", set);
        await ShouldFailAsync(() => SqlAsync($"""
            INSERT INTO quote_lines (id, version_id, line_no, variant_id, quantity, unit_price_minor, seller_id, sku, title_en, title_bn, vat_rate,
                public_unit_price_minor, line_net_minor, line_tax_minor, line_total_minor, below_floor, over_authority, created_at)
            SELECT gen_random_uuid(), version_id, 3, variant_id, 1, 1, seller_id, 'X', 'X', 'X', 0, 1, 1, 0, 1, false, false, now()
            FROM quote_lines WHERE id = '{line}'
            """), Trigger, "cannot change");

        await using var read = db.CreateContext();
        var back = await LoadAsync(read, quote.Id);
        back.Version(1).ContentHash.Should().Be(QuoteContentHash.Compute(back, back.Version(1)), "nothing changed");
    }

    [PostgresFact]
    public async Task DeletingAVersionOrLine_FailsAtDbLevel()
    {
        var w = await WorldAsync();
        var sent = await SentAsync(w);
        var draft = await DraftAsync(w);
        foreach (var (version, status) in new[] { (sent.Version(1), "Sent"), (draft.Version(1), "Draft") })
        {
            await ShouldFailAsync(() => SqlAsync($"DELETE FROM quote_versions WHERE id = '{version.Id}'"), Trigger, "never deleted", status);
            await ShouldFailAsync(() => SqlAsync($"DELETE FROM quote_version_contributors WHERE version_id = '{version.Id}'"), Trigger, "never", status);
        }

        await ShouldFailAsync(() => SqlAsync($"DELETE FROM quote_lines WHERE id = '{sent.Version(1).Lines.First().Id}'"), Trigger, "cannot change");
        await ShouldFailAsync(() => SqlAsync($"DELETE FROM quotes WHERE id = '{draft.Id}'"), Trigger, "never deleted");
        foreach (var table in new[] { "rfqs", "rfq_lines", "quotes", "quote_versions", "quote_lines", "quote_version_contributors" })
            await ShouldFailAsync(() => SqlAsync($"TRUNCATE {table} CASCADE"), Trigger, "never truncated", table);

        // A Draft's lines may still be replaced (the editor's PUT replaces all lines).
        await using var ctx = db.CreateContext();
        var q = await LoadAsync(ctx, draft.Id);
        q.EditDraft(1, Content(w, Line(w.V2, w.Seller2, qty: 3)), Staff(Editor), Options, Now.AddMinutes(5));
        await ctx.SaveChangesAsync();
        (await ScalarAsync<long>($"SELECT count(*) FROM quote_lines WHERE version_id = '{draft.Version(1).Id}'")).Should().Be(1);
    }

    [PostgresFact]
    public async Task TransitionsOutsideTheTable_FailAtDbLevel()
    {
        var w = await WorldAsync();
        var sent = (await SentAsync(w)).Version(1).Id;
        var draft = (await DraftAsync(w)).Version(1).Id;

        var order = await OrderAsync(w);
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_versions SET status = 'Accepted', accepted_order_id = '{order}', " +
            $"accepted_by = gen_random_uuid(), accepted_at = now() WHERE id = '{draft}'"), Trigger, "cannot become");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_versions SET status = 'PendingApproval' WHERE id = '{sent}'"), Trigger, "cannot become");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_versions SET status = 'Draft', content_hash = NULL, submitted_by = NULL, submitted_at = NULL, " +
            $"flags = NULL, sent_by = NULL, sent_at = NULL, valid_until_utc = NULL WHERE id = '{sent}'"), Trigger, "cannot become");
        await SqlAsync($"UPDATE quote_versions SET status = 'Rejected', rejected_by = gen_random_uuid(), rejected_at = now() WHERE id = '{sent}'");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_versions SET status = 'Sent', rejected_by = NULL, rejected_at = NULL WHERE id = '{sent}'"),
            Trigger, "cannot become");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_versions SET reject_reason = 'changed later' WHERE id = '{sent}'"), Trigger, "cannot change");

        // A version is born a Draft; a direct send cannot carry an approval.
        await ShouldFailAsync(() => SqlAsync($"""
            CREATE TEMP TABLE qv ON COMMIT DROP AS SELECT * FROM quote_versions WHERE id = '{sent}';
            UPDATE qv SET id = gen_random_uuid(), version_no = 7;
            INSERT INTO quote_versions SELECT * FROM qv;
            """), Trigger, "created as a Draft");
        await ShouldFailAsync(() => FreezeRawAsync(draft, "Sent", $", approved_by = '{Approver}', approved_at = now()"), Trigger, "cannot change");

        // Steps the trigger allows must still carry their columns (status shapes), and a Draft's money must add up.
        await ShouldFailAsync(() => FreezeRawAsync(draft, "PendingApproval", hash: "NULL"), Check, "ck_quote_versions_frozen_shape");
        await ShouldFailAsync(() => FreezeRawAsync(draft, "Sent", validUntil: "NULL"), Check, "ck_quote_versions_sent_shape");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_lines SET line_net_minor = 1, line_total_minor = 1 + line_tax_minor WHERE version_id = '{draft}'"),
            Check, "ck_quote_lines_money");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_lines SET below_floor = true WHERE version_id = '{draft}'"), Check, "ck_quote_lines_below_floor_reason");
    }

    // ---- QT7: four-eyes ----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ApprovedByEqualsCreatedBy_FailsAtDbLevel()
    {
        var w = await WorldAsync();
        // A version written around the aggregate with no contributor rows: only the CHECK stands between its author and an approval.
        var quote = await SentAsync(w);
        var bare = Guid.NewGuid();
        var creator = Guid.NewGuid();
        await SqlAsync($"""
            UPDATE quote_versions SET status = 'Superseded', superseded_at = now() WHERE quote_id = '{quote.Id}';
            INSERT INTO quote_versions (id, quote_id, version_no, status, validity_days, payment_terms, shipping_mode, goods_net_minor, tax_minor,
                goods_total_minor, created_by, created_at)
            VALUES ('{bare}', '{quote.Id}', 2, 'Draft', 15, 'AsAccount', 'Calculated', 0, 0, 0, '{creator}', now());
            """);
        await FreezeRawAsync(bare, "PendingApproval", submitter: Sender);
        await ShouldFailAsync(() => SqlAsync(ApproveSql(bare, "created_by")), Check, "ck_quote_versions_four_eyes");

        var pending = (await PendingAsync(w)).Version(1).Id;
        await ShouldFailAsync(() => SqlAsync(ApproveSql(pending, "submitted_by")), Check, "ck_quote_versions_four_eyes");
        await ShouldFailAsync(() => SqlAsync(ApproveSql(pending, $"'{Editor}'")), Trigger, "cannot approve");
        await SqlAsync(ApproveSql(pending, $"'{Approver}'")); // the same statement with a second person passes
        (await ScalarAsync<string>($"SELECT status FROM quote_versions WHERE id = '{pending}'")).Should().Be("Sent");
    }

    [PostgresFact]
    public async Task CheckFigures_ChangeOnlyDuringTheApproval_AndContributorsOnlyWhileDraft()
    {
        var w = await WorldAsync();
        var pending = await PendingAsync(w);
        var v = pending.Version(1);
        var line = v.Lines.First().Id;

        await SqlAsync($"UPDATE quote_lines SET reference_unit_price_minor = reference_unit_price_minor + 1, over_authority = NOT over_authority WHERE id = '{line}'");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_lines SET unit_price_minor = unit_price_minor - 1, line_net_minor = line_net_minor - quantity, " +
            $"line_total_minor = line_total_minor - quantity WHERE id = '{line}'"), Trigger, "cannot change");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_versions SET flags = '{{}}'::jsonb WHERE id = '{v.Id}'"), Trigger, "cannot change");
        await ShouldFailAsync(() => SqlAsync($"INSERT INTO quote_version_contributors (version_id, user_id, first_contributed_at) VALUES ('{v.Id}', gen_random_uuid(), now())"),
            Trigger, "only while a version is a Draft");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_version_contributors SET user_id = gen_random_uuid() WHERE version_id = '{v.Id}'"), Trigger, "never change");

        await SqlAsync(ApproveSql(v.Id, $"'{Approver}'"));
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_lines SET over_authority = NOT over_authority WHERE id = '{line}'"), Trigger, "cannot change");
    }

    // ---- QT4: the accepted order ---------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task TheOrderOfAnAcceptedVersion_MovesOnlyAwayFromAnOrderThatLapsedBeforeConfirmation()
    {
        var w = await WorldAsync();
        var quote = await SentAsync(w);
        var v = quote.Version(1).Id;
        var first = await OrderAsync(w, "PendingPayment");
        var second = await OrderAsync(w, "PendingPayment");
        await SqlAsync($"UPDATE quote_versions SET status = 'Accepted', accepted_order_id = '{first}', accepted_by = '{w.Contact}', accepted_at = now() WHERE id = '{v}'");

        var move = () => SqlAsync($"UPDATE quote_versions SET accepted_order_id = '{second}', accepted_at = now() WHERE id = '{v}'");
        var supersede = () => SqlAsync($"UPDATE quote_versions SET status = 'Superseded', superseded_at = now() WHERE id = '{v}'");
        await ShouldFailAsync(move, Trigger, "lapsed before confirmation");
        await ShouldFailAsync(supersede, Trigger, "lapsed before confirmation");

        await SqlAsync($"UPDATE orders SET status = 'Confirmed', confirmed_at = now() WHERE id = '{first}'");
        await SqlAsync($"UPDATE orders SET status = 'Cancelled' WHERE id = '{first}'");
        await ShouldFailAsync(move, Trigger, "lapsed before confirmation"); // confirmed once: the quote is done (§8.6)

        await SqlAsync($"UPDATE orders SET confirmed_at = NULL WHERE id = '{first}'"); // as if it lapsed unconfirmed
        await move();
        (await ScalarAsync<Guid>($"SELECT accepted_order_id FROM quote_versions WHERE id = '{v}'")).Should().Be(second);
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_versions SET accepted_by = gen_random_uuid() WHERE id = '{v}'"), Trigger, "lapsed before confirmation");
    }

    // ---- One open version, closed quotes, PDFs -------------------------------------------------------------------------

    [PostgresFact]
    public async Task TwoOpenVersions_FailAtDbLevel_AndAClosedQuoteNeverChanges()
    {
        var w = await WorldAsync();
        var quote = await DraftAsync(w);
        var insert = () => SqlAsync($"""
            INSERT INTO quote_versions (id, quote_id, version_no, status, validity_days, payment_terms, shipping_mode, goods_net_minor, tax_minor,
                goods_total_minor, created_by, created_at)
            VALUES (gen_random_uuid(), '{quote.Id}', 2, 'Draft', 15, 'AsAccount', 'Calculated', 0, 0, 0, '{Author}', now())
            """);
        await ShouldFailAsync(insert, Unique, "ux_quote_versions_one_open");

        await ShouldFailAsync(() => SqlAsync($"UPDATE quotes SET company_id = (SELECT id FROM companies WHERE id <> company_id LIMIT 1) WHERE id = '{quote.Id}'"),
            Trigger, "never change");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quotes SET number = 'MWQ-261117-AAAAAA' WHERE id = '{quote.Id}'"), Trigger, "never change");
        await using (var ctx = db.CreateContext())
        {
            (await LoadAsync(ctx, quote.Id)).Close(Author, "No longer needed", Now.AddHours(1));
            await ctx.SaveChangesAsync();
        }

        var newContact = await TestUsers.CreateAsync(db);
        await ShouldFailAsync(() => SqlAsync($"UPDATE quotes SET contact_user_id = '{newContact}' WHERE id = '{quote.Id}'"), Trigger, "closed quote");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quotes SET closed_at = NULL, closed_by = NULL, close_reason = NULL WHERE id = '{quote.Id}'"), Trigger, "closed quote");
        // Its Draft is not editable any more, nor are its lines; a closed quote takes no new version.
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_versions SET validity_days = 20 WHERE quote_id = '{quote.Id}'"), Trigger, "is closed");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_lines SET quantity = 2, line_net_minor = 2 * unit_price_minor WHERE version_id = '{quote.Version(1).Id}'"),
            Trigger, "is closed");
        await ShouldFailAsync(() => SqlAsync($"INSERT INTO quote_version_contributors (version_id, user_id, first_contributed_at) VALUES ('{quote.Version(1).Id}', gen_random_uuid(), now())"),
            Trigger, "is closed");
        await ShouldFailAsync(() => SqlAsync($"""
            INSERT INTO quote_versions (id, quote_id, version_no, status, validity_days, payment_terms, shipping_mode, goods_net_minor, tax_minor,
                goods_total_minor, created_by, created_at)
            VALUES (gen_random_uuid(), '{quote.Id}', 3, 'Draft', 15, 'AsAccount', 'Calculated', 0, 0, 0, '{Author}', now())
            """), Trigger, "is closed");
    }

    [PostgresFact]
    public async Task AStoredPdf_IsRecordedOnceAfterSending_AndNeverForADraft()
    {
        var w = await WorldAsync();
        var sent = (await SentAsync(w)).Version(1).Id;
        var draft = (await DraftAsync(w)).Version(1).Id;
        string Pdf(Guid id, char c) => $"UPDATE quote_versions SET pdf_en_key = 'quotes/{id:N}/v1-en.pdf', pdf_en_sha256 = repeat('{c}', 64) WHERE id = '{id}'";

        await ShouldFailAsync(() => SqlAsync(Pdf(draft, 'a')), Trigger, "cannot change"); // and ck_quote_versions_pdf behind it
        await SqlAsync(Pdf(sent, 'a'));
        await ShouldFailAsync(() => SqlAsync(Pdf(sent, 'b')), Trigger, "recorded once");
        await ShouldFailAsync(() => SqlAsync($"UPDATE quote_versions SET pdf_en_key = NULL, pdf_en_sha256 = NULL WHERE id = '{sent}'"), Trigger, "recorded once");
    }

    // ---- RFQs ------------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task RfqLines_AreAppendOnly_AndAnRfqKeepsItsIdentity_AndMovesOnlyAlongItsTable()
    {
        var w = await WorldAsync();
        var rfq = Rfq.Submit(Guid.NewGuid(), QuoteNumber.NewRfq(Now), w.Company, w.Contact, RfqSource.Product, null, null, null,
            [new("one PLC", null, 1, RfqMatchStatus.Matched, w.V1, null, null)], "idem-key-0002", new string('c', 64), Now);
        await SaveNewAsync(rfq);
        var line = rfq.Lines.Single().Id;

        await ShouldFailAsync(() => SqlAsync($"UPDATE rfq_lines SET quantity = 2 WHERE id = '{line}'"), Trigger, "append-only");
        await ShouldFailAsync(() => SqlAsync($"DELETE FROM rfq_lines WHERE id = '{line}'"), Trigger, "append-only");
        foreach (var set in new[] { "source = 'Cart'", "company_id = gen_random_uuid()", "number = 'MWR-261117-AAAAAA'", "requested_by = gen_random_uuid()",
                     "message = 'changed'", "request_hash = repeat('d', 64)" })
            await ShouldFailAsync(() => SqlAsync($"UPDATE rfqs SET {set}, updated_at = now() WHERE id = '{rfq.Id}'"), Trigger, null, set);
        await ShouldFailAsync(() => SqlAsync($"UPDATE rfqs SET status = 'Answered', first_quote_sent_at = now() WHERE id = '{rfq.Id}'"), Trigger, "cannot become");
        await ShouldFailAsync(() => SqlAsync($"DELETE FROM rfqs WHERE id = '{rfq.Id}'"), Trigger, "never deleted");

        await using (var ctx = db.CreateContext())
        {
            (await ctx.Rfqs.SingleAsync(r => r.Id == rfq.Id)).Close(Author, "No bid on this one", Now.AddHours(1));
            await ctx.SaveChangesAsync();
        }

        await ShouldFailAsync(() => SqlAsync($"UPDATE rfqs SET status = 'Submitted', closed_by = NULL, closed_at = NULL, close_reason = NULL WHERE id = '{rfq.Id}'"),
            Trigger, "cannot become");
        await ShouldFailAsync(() => SqlAsync($"UPDATE rfqs SET assigned_to = '{w.Contact}' WHERE id = '{rfq.Id}'"), Trigger, null);
        await ShouldFailAsync(() => SqlAsync($"""
            INSERT INTO rfq_lines (id, rfq_id, line_no, raw_text, quantity, match_status, created_at)
            VALUES (gen_random_uuid(), '{rfq.Id}', 2, 'late line', 1, 'NotFound', now())
            """), Trigger, "append-only");
        await ShouldFailAsync(() => SqlAsync($"""
            INSERT INTO rfqs (id, number, company_id, requested_by, source, status, line_count, idempotency_key, request_hash, created_at)
            VALUES (gen_random_uuid(), 'MWR-261117-BBBBBB', '{w.Company}', '{w.Contact}', 'Text', 'Answered', 1, 'idem-key-0003', repeat('e', 64), now())
            """), Trigger, "created as Submitted");
    }

    // ---- Migration ---------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Migrate_CreatesTheQuoteTables_InstallsTheGuards_AndIsIdempotent()
    {
        string name = "mw_d44_" + Guid.NewGuid().ToString("N");
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
            await ctx.Database.ExecuteSqlRawAsync(QuoteSql.Install); // a re-run changes nothing

            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var q = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'trg_rfq%' OR tgname LIKE 'trg_quote%')
                     + (SELECT count(*) FROM pg_constraint WHERE conname IN ('ck_quote_versions_four_eyes', 'ux_quote_versions_one_live', 'ck_rfq_lines_variant'))
                     + (SELECT count(*) FROM pg_indexes WHERE indexname IN ('ux_quote_versions_one_open', 'ux_rfqs_idempotency', 'ux_quote_lines_variant'))
                """, conn);
            ((long)(await q.ExecuteScalarAsync())!).Should().Be(12 + 3 + 3, "six guards, six truncate guards, three constraints, three indexes");
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

    // ---- Fixtures --------------------------------------------------------------------------------------------------------

    private async Task<World> WorldAsync()
    {
        await using var ctx = db.CreateContext();
        var bin = (Interlocked.Increment(ref _binSeq) + 7_440_000_000_000L).ToString("D13");
        var company = Company.Register(Guid.NewGuid(), "Quote Buyer Ltd", "TL-D44-1", bin, null, null);
        ctx.Companies.Add(company);
        var site = new CompanySite(Guid.NewGuid(), company.Id, "Factory", "Engr. Karim", "+8801711000000", "Plot 7", null, "Tongi", "Gazipur", "Dhaka", "1710", true);
        ctx.CompanySites.Add(site);
        await ctx.SaveChangesAsync();
        var v1 = await CatalogTestData.SeedVariantAsync(ctx, onHand: 1);
        var v2 = await CatalogTestData.SeedVariantAsync(ctx, onHand: 1);
        Guid SellerOf(Guid product) => ctx.Products.Where(p => p.Id == product).Select(p => p.SellerId).Single();
        return new World(company.Id, site.Id, await TestUsers.CreateAsync(db), v1.VariantId, SellerOf(v1.ProductId), v2.VariantId, SellerOf(v2.ProductId));
    }

    private static QuoteLineFacts Facts(Guid variant, Guid seller, decimal vat) =>
        new(variant, true, seller, $"SKU-{variant.ToString("N")[..6]}", "6ES7214-1AG40", "Siemens S7-1200 CPU", "সিমেন্স এস৭-১২০০ সিপিইউ", vat, 120_000, null);

    private static QuoteLineDraft Line(Guid variant, Guid seller, int qty = 10, long price = 100_000, decimal vat = 0.15m) =>
        new(variant, qty, price, Facts(variant, seller, vat), "Genuine part");

    private static QuoteDraft Content(World w, params QuoteLineDraft[] lines) => Content(w, false, lines);

    private static QuoteDraft Content(World w, bool fixedDelivery, params QuoteLineDraft[] lines) =>
        new(15, QuotePaymentTerms.AsAccount, fixedDelivery ? QuoteShippingMode.Fixed : QuoteShippingMode.Calculated, fixedDelivery ? w.Site : null,
            fixedDelivery ? [new(w.Seller2, 0), new(w.Seller1, 50_000)] : null, "Prices in BDT.", "আলোচনা অনুযায়ী।", "Margin is thin.", lines,
            fixedDelivery ? SiteCopy : null);

    private static readonly QuoteShipTo SiteCopy = new("তেজগাঁও কারখানা", "Karim Mia", "01711000000", "12 Tejgaon I/A", null, "Tejgaon", "Dhaka", "Dhaka", "1208");

    /// <summary>A quote of the world's company whose version 1 is a Draft with two lines, by <see cref="Author"/> and changed by <see cref="Editor"/>.</summary>
    private static Quote Drafted(World w, bool fixedDelivery = false, Guid? rfqId = null)
    {
        var quote = Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(Now), w.Company, rfqId, w.Contact, Staff(Author), Options, Now);
        quote.EditDraft(1, Content(w, fixedDelivery, Line(w.V1, w.Seller1), Line(w.V2, w.Seller2, qty: 4, price: 250_000, vat: 0.075m)),
            Staff(Editor), Options, Now);
        return quote;
    }

    /// <summary>The policy run: cost 1, a 0 % floor, the buyer's contract price equal to the quote price (doubled on line 1 when over authority).</summary>
    private static QuoteApprovalCheck CheckOf(QuoteVersion v, bool overAuthority, long cost = 1) => QuoteApprovalPolicy.Evaluate(v,
        v.Lines.Select(l => new VariantCostFacts(l.VariantId, cost, null, [], 0.15m)).ToList(), ZeroFloor,
        v.Lines.Select(l => new PriceResolution(l.VariantId, overAuthority && l.LineNo == 1 ? l.UnitPriceMinor * 2 : l.UnitPriceMinor,
            PriceSource.Contract, PriceBasis.NetOfVat, null, null, null, null, l.UnitPriceMinor)).ToList(), Options, false);

    private static QuoteIssue Issue(Quote quote, int versionNo, World w, bool overAuthority = false)
    {
        var v = quote.Version(versionNo);
        return new(new QuoteCompanySnapshot("Acme ইঞ্জিনিয়ারিং Ltd", "000123456-0101", "12 Tejgaon I/A, Dhaka 1208"), "Rahim Uddin",
            v.Lines.Select(l => Facts(l.VariantId, l.SellerId, l.VatRate)).ToList(), CheckOf(v, overAuthority));
    }

    /// <summary>Approves with the policy run of the approval: the cost moved to 2 since the send, so every line's check figures are rewritten.</summary>
    private static void Approve(Quote quote, int versionNo, Guid approver)
    {
        var v = quote.Version(versionNo);
        var check = CheckOf(v, overAuthority: true, cost: 2);
        quote.Approve(versionNo, Staff(approver), new QuoteApproval(false, v.ContentHash!, check.Flags.BelowFloorLineIds, check.Flags.OverAuthorityLineIds,
            "Volume deal"), check, false, Now.AddHours(1));
    }

    private async Task<Quote> DraftAsync(World w, bool fixedDelivery = false, Guid? rfqId = null)
    {
        var quote = Drafted(w, fixedDelivery, rfqId);
        await SaveNewAsync(quote);
        return quote;
    }

    /// <summary>Saved as a Draft first (a version is born a Draft), then sent in a second save, as the send endpoint will.</summary>
    private async Task<Quote> SendAsync(World w, bool overAuthority, bool fixedDelivery, Guid? rfqId)
    {
        var draft = await DraftAsync(w, fixedDelivery, rfqId);
        await using var ctx = db.CreateContext();
        var quote = await LoadAsync(ctx, draft.Id);
        quote.Send(1, Issue(quote, 1, w, overAuthority), Staff(Sender), false, Options, Now);
        await ctx.SaveChangesAsync();
        return quote;
    }

    private Task<Quote> SentAsync(World w) => SendAsync(w, overAuthority: false, fixedDelivery: false, rfqId: null);

    private Task<Quote> PendingAsync(World w, bool fixedDelivery = false, Guid? rfqId = null) => SendAsync(w, overAuthority: true, fixedDelivery, rfqId);

    private async Task SaveNewAsync(object aggregate)
    {
        await using var ctx = db.CreateContext();
        ctx.Add(aggregate);
        await ctx.SaveChangesAsync();
    }

    /// <summary>Versions in version order: <see cref="Quote.Revise"/> copies the last one, so a loader must order them.</summary>
    private static Task<Quote> LoadAsync(MechwareDbContext ctx, Guid id) => ctx.Quotes
        .Include(q => q.Versions.OrderBy(v => v.VersionNo)).ThenInclude(v => v.Lines)
        .Include(q => q.Versions).ThenInclude(v => v.Contributors)
        .AsSplitQuery().SingleAsync(q => q.Id == id);

    private async Task<Guid> OrderAsync(World w, string status = "PendingPayment")
    {
        await using var ctx = db.CreateContext();
        return await CreditTestData.InsertOrderAsync(ctx, w.Contact, w.Company, 1_000, onCredit: false, status: status);
    }

    /// <summary>Freezes a Draft with raw SQL, as a send would (hash, submitter, flags, snapshot, preparer; and the send columns for Sent).</summary>
    private Task FreezeRawAsync(Guid id, string status, string extra = "", Guid? submitter = null, string hash = "repeat('f', 64)",
        string validUntil = "now() + interval '15 days'") => SqlAsync($"""
        UPDATE quote_versions SET status = '{status}', content_hash = {hash}, submitted_by = '{submitter ?? Sender}', submitted_at = now(),
            flags = '{"{"}"belowFloorLineIds":[],"overAuthorityLineIds":[],"longValidity":true{"}"}'::jsonb,
            company_snapshot = '{"{"}"name":"Acme"{"}"}'::jsonb, preparer_name = 'Rahim Uddin'
            {(status == "Sent" ? $", sent_by = '{submitter ?? Sender}', sent_at = now(), valid_until_utc = {validUntil}" : "")} {extra}
        WHERE id = '{id}'
        """);

    /// <summary>PendingApproval → Sent with raw SQL as <paramref name="approver"/> (a column or a quoted uuid).</summary>
    private static string ApproveSql(Guid id, string approver) => $"""
        UPDATE quote_versions SET status = 'Sent', approved_by = {approver}, approved_at = now(), sent_by = {approver}, sent_at = now(),
            valid_until_utc = now() + interval '15 days'
        WHERE id = '{id}'
        """;

    private async Task SqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Raw SQL must fail with <paramref name="sqlState"/>; <paramref name="text"/> is looked for in the message or the constraint name.</summary>
    private static async Task ShouldFailAsync(Func<Task> act, string sqlState, string? text, string? because = null)
    {
        var ex = (await act.Should().ThrowAsync<PostgresException>(because)).Which;
        ex.SqlState.Should().Be(sqlState, $"{because}: {ex.MessageText}");
        if (text is not null) $"{ex.MessageText} {ex.ConstraintName}".Should().Contain(text, because);
    }
}
