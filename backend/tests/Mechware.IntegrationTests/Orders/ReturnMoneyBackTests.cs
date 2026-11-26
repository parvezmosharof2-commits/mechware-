using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Domain.Payments;
using Mechware.Domain.Returns;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Payments;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Invoicing;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// F2.3b, the money of an inspected return. Order (one seller): PLC-1200 2 × and CBL-10 25 ×; the buyer returns 1 PLC + 5 CBL, inspection
/// accepts 1 + 4 (nothing restocked). With a Live 6.3: a credit-note request whose approval moves the return to CreditNoted in its transaction.
/// Without one: an E4 refund of the accepted share (paid gateway order) or a credit reduction approved by a second person (credit order).
/// </summary>
public sealed class ReturnMoneyBackTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "ReturnsF23b#2026!";
    private readonly MockPaymentProvider _mock = new();

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    private sealed class Actor(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.24";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-f23b";
        public bool HasPermission(string permission) => true;
    }

    private sealed record World(Guid Seller, Guid OrderId, Guid Buyer, Guid ReturnId, Guid? InvoiceId, Guid? PaymentId, long ShareMinor);

    private sealed record Person(HttpClient Client, Guid UserId);

    private CreditNoteService CreditNotes(MechwareDbContext db, Guid actor, Guid seller)
    {
        var who = new Actor(actor);
        return new CreditNoteService(db, new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance),
            new CreditLedgerService(db, who, TimeProvider.System), InvoiceTestIssues.Options(seller), who, TimeProvider.System);
    }

    private ReturnRequestService Returns(MechwareDbContext db, Guid actor, Guid seller)
    {
        var who = new Actor(actor);
        return new ReturnRequestService(db, who, TimeProvider.System, null, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), null,
            CreditNotes(db, actor, seller), new CreditLedgerService(db, who, TimeProvider.System));
    }

    private async Task<T> As<T>(Guid actor, World w, Func<ReturnRequestService, Task<T>> act)
    {
        await using var db = Db();
        return await act(Returns(db, actor, w.Seller));
    }

    private async Task<Guid> StaffAsync()
    {
        await using var db = Db();
        return await InvoiceTestIssues.UserAsync(db);
    }

    /// <summary>A delivered order (Live 6.3 when <paramref name="invoice"/>; on company credit, charged, when <paramref name="credit"/>; paid
    /// through the mock gateway when <paramref name="paid"/>) and its return, inspected by staff: 1 PLC + 4 CBL accepted.</summary>
    private async Task<World> WorldAsync(bool invoice, bool credit = false, bool paid = false)
    {
        Guid seller, orderId, buyer;
        Guid? invoiceId = null, paymentId = null, companyId = null;
        var now = DateTime.UtcNow;
        await using (var db = Db())
        {
            seller = await InvoiceTestIssues.SellerAsync(db);
            Company? company = null;
            if (credit)
            {
                company = await InvoiceTestIssues.ActiveCompanyAsync(db);
                company.SetCredit(CreditTerms.Net30, Money.FromMinorUnits(1_000_000_000));
                await db.SaveChangesAsync();
                companyId = company.Id;
            }
            await using var tx = await db.Database.BeginTransactionAsync();
            var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller, company);
            if (invoice)
            {
                var built = InvoiceTestIssues.Build(order, shipment, seller,
                    await new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance).TakeAsync(seller, InvoiceSeries.TaxInvoice), company);
                db.Invoices.Add(built);
                invoiceId = built.Id;
            }
            order.MarkDelivered(shipment, order.UserId, DateTime.UtcNow);
            if (company is not null)
            {
                db.CompanyMembers.Add(new CompanyMember(Guid.NewGuid(), company.Id, order.UserId, CompanyRoles.Buyer, null, null, accepted: true));
                db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order.Id, order.GrandTotalMinor, now, now).Entry);
            }
            if (paid)
            {
                var session = await _mock.CreateSessionAsync(order.Id, order.OrderNumber, order.GrandTotalMinor, "BDT");
                _mock.SimulatePayment(session.ProviderRef);
                var payment = new Payment(Guid.NewGuid(), order.Id, "mock", session.ProviderRef, order.GrandTotalMinor, "BDT");
                payment.MarkCaptured(now);
                db.Payments.Add(payment);
                paymentId = payment.Id;
            }
            await db.SaveChangesAsync();
            if (credit) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET pays_on_credit = true WHERE id = {order.Id}");
            await tx.CommitAsync();
            (orderId, buyer) = (order.Id, order.UserId);
        }

        long share;
        Guid returnId;
        await using (var db = Db())
        {
            var items = await db.OrderItems.AsNoTracking().Where(i => db.SubOrders.Any(s => s.Id == i.SubOrderId && s.OrderId == orderId)).ToListAsync();
            var (plc, cbl) = (items.Single(i => i.Sku == "PLC-1200"), items.Single(i => i.Sku == "CBL-10"));
            share = plc.TotalMinor * 1 / plc.Quantity + cbl.TotalMinor * 4 / cbl.Quantity;
            var number = await db.Orders.Where(o => o.Id == orderId).Select(o => o.OrderNumber).SingleAsync();
            returnId = (await Returns(db, buyer, seller).OpenAsync(new OpenReturnRequest(buyer, number, "f23b-" + Guid.NewGuid().ToString("N"), companyId,
                "damaged", null, [new ReturnLineInput(plc.Id, 1), new ReturnLineInput(cbl.Id, 5)]))).Return.Id;
        }
        var staff = await StaffAsync();
        var w = new World(seller, orderId, buyer, returnId, invoiceId, paymentId, share);
        var r = await As(staff, w, async s => await s.ApproveAsync(returnId, new ReturnStepRequest((await s.GetStaffAsync(returnId)).RowVersion)));
        r = await As(staff, w, s => s.MarkReceivedAsync(returnId, new ReturnStepRequest(r.RowVersion)));
        await As(staff, w, s => s.InspectAsync(returnId, new InspectReturnRequest(r.RowVersion,
            [.. r.Lines.Select(l => new ReturnInspectionInput(l.Id, l.Sku == "PLC-1200" ? 1 : 4, 0, null))])));
        return w;
    }

    private Task<StaffReturnDto> AskAsync(Guid asker, World w, uint? version = null) =>
        As(asker, w, async s => await s.RequestMoneyBackAsync(w.ReturnId, new ReturnStepRequest(version ?? (await s.GetStaffAsync(w.ReturnId)).RowVersion)));

    private async Task<List<CreditNoteRequest>> RequestsOfAsync(Guid returnId)
    {
        await using var db = Db();
        return await db.CreditNoteRequests.AsNoTracking().Where(r => r.SourceKey == $"return:{returnId:N}").ToListAsync();
    }

    private async Task<string> XminAsync(string table, Guid id)
    {
        await using var db = Db();
        var rows = table switch
        {
            "return_requests" => db.Database.SqlQuery<string>($"SELECT xmin::text AS \"Value\" FROM return_requests WHERE id = {id}"),
            "credit_note_requests" => db.Database.SqlQuery<string>($"SELECT xmin::text AS \"Value\" FROM credit_note_requests WHERE id = {id}"),
            _ => db.Database.SqlQuery<string>($"SELECT xmin::text AS \"Value\" FROM refunds WHERE id = {id}")
        };
        return (await rows.ToListAsync()).Single();
    }

    private async Task<int> AuditCountAsync(string action, Guid returnId)
    {
        await using var db = Db();
        return await db.AuditLogs.CountAsync(a => a.Action == action && a.EntityId == returnId.ToString());
    }

    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<ConflictException>(act)).Code;

    [PostgresFact]
    public async Task WithALive63_TheMoneyIsACreditNoteRequestOfTheAcceptedUnits_AndItsApprovalMovesTheReturn_InTheSameTransaction()
    {
        var w = await WorldAsync(invoice: true);
        var (asker, approver) = (await StaffAsync(), await StaffAsync());

        var asked = await AskAsync(asker, w);
        asked.Status.Should().Be("inspected", "nothing moves until another person approves");
        asked.Settlement!.Kind.Should().Be("credit_note");
        asked.Settlement.AmountMinor.Should().BeNull();
        asked.Settlement.RequestedBy.Should().Be(asker);
        var request = (await RequestsOfAsync(w.ReturnId)).Single();
        asked.Settlement.CreditNoteRequestId.Should().Be(request.Id);
        (request.Status, request.Reason, request.RequestedBy, request.OriginalInvoiceId).Should().Be(
            (CreditNoteRequestStatus.Requested, CreditNoteReason.GoodsReturned, asker, w.InvoiceId!.Value));
        request.Lines.Select(l => l.Quantity).Should().BeEquivalentTo([1, 4], "the accepted units, not the asked ones; no delivery line");
        (await AuditCountAsync("return.money_back_requested", w.ReturnId)).Should().Be(1);

        await using (var db = Db())
        {
            (await CodeOf(() => CreditNotes(db, asker, w.Seller).ApproveAsync(request.Id))).Should().Be(CreditNoteRequest.FourEyes);
        }
        ApprovedCreditNote approved;
        await using (var db = Db()) approved = await CreditNotes(db, approver, w.Seller).ApproveAsync(request.Id);
        approved.TotalMinor.Should().Be(request.TotalMinor);

        var settled = await As(approver, w, s => s.GetStaffAsync(w.ReturnId));
        settled.Status.Should().Be("credit_noted");
        (settled.Settlement!.SettledBy, settled.Settlement.CreditNoteRequestId).Should().Be((approver, request.Id));
        (await XminAsync("return_requests", w.ReturnId)).Should().Be(await XminAsync("credit_note_requests", request.Id),
            "the return moved in the very transaction that approved its credit note");
        (await AuditCountAsync("return.credit_noted", w.ReturnId)).Should().Be(1);
        await using (var db = Db())
            (await db.OutboxMessages.CountAsync(m => m.EventType == nameof(ReturnSettledEvent) && m.AggregateId == w.ReturnId)).Should().Be(1);
    }

    [PostgresFact]
    public async Task WithoutALive63_APaidOrder_GetsAnE4RefundOfTheAcceptedShare_AndItsCompletionRefundsTheReturn()
    {
        var w = await WorldAsync(invoice: false, paid: true);
        var (asker, approver) = (await StaffAsync(), await StaffAsync());

        var asked = await AskAsync(asker, w);
        (asked.Status, asked.Settlement!.Kind, asked.Settlement.AmountMinor).Should().Be(("inspected", "refund", w.ShareMinor));
        (await RequestsOfAsync(w.ReturnId)).Should().BeEmpty("no Live 6.3, no credit note");
        Refund refund;
        await using (var db = Db()) refund = await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == asked.Settlement.RefundId);
        (refund.PaymentId, refund.AmountMinor, refund.Status, refund.RequestedBy).Should().Be((w.PaymentId!.Value, w.ShareMinor, RefundStatus.Requested, asker));

        await using (var db = Db())
            (await new RefundService(db, [_mock], TimeProvider.System).ApproveAsync(approver, refund.Id)).Status.Should().Be(nameof(RefundStatus.Completed));
        var settled = await As(approver, w, s => s.GetStaffAsync(w.ReturnId));
        (settled.Status, settled.Settlement!.SettledBy).Should().Be(("refunded", approver));
        (await XminAsync("return_requests", w.ReturnId)).Should().Be(await XminAsync("refunds", refund.Id), "refunded with the refund's completion");
        _mock.RefundedOn((await PaymentRefAsync(w.PaymentId.Value))).Should().Be(w.ShareMinor, "the share of the accepted units, never the whole payment");
    }

    [PostgresFact]
    public async Task WithoutALive63_ACreditOrder_WaitsForASecondPerson_ThenItsChargeIsReducedAndTheReturnRefunded_Once()
    {
        var w = await WorldAsync(invoice: false, credit: true);
        var (asker, approver) = (await StaffAsync(), await StaffAsync());

        var asked = await AskAsync(asker, w);
        (asked.Settlement!.Kind, asked.Settlement.AmountMinor, asked.Settlement.RefundId).Should().Be(("credit_reduction", w.ShareMinor, (Guid?)null));
        (await CodeOf(() => As(asker, w, s => s.ApproveMoneyBackAsync(w.ReturnId)))).Should().Be(ReturnRequest.FourEyes);

        var done = await As(approver, w, s => s.ApproveMoneyBackAsync(w.ReturnId));
        (done.Status, done.Settlement!.SettledBy).Should().Be(("refunded", approver));
        (await As(approver, w, s => s.ApproveMoneyBackAsync(w.ReturnId))).Status.Should().Be("refunded", "again = the same answer");
        await using var db = Db();
        var reduced = await db.CreditLedgerEntries.AsNoTracking().Where(l => l.OrderId == w.OrderId && l.Kind == CreditLedgerKind.ChargeReduced).ToListAsync();
        reduced.Sum(l => l.ReceivableDeltaMinor).Should().Be(-w.ShareMinor, "reduced once, by the accepted share");
    }

    [PostgresFact]
    public async Task AskingAgain_IsTheSameAnswer_EvenWithTheOldRowVersion_AndNothingNewIsWritten()
    {
        var w = await WorldAsync(invoice: true);
        var asker = await StaffAsync();
        var version = (await As(asker, w, s => s.GetStaffAsync(w.ReturnId))).RowVersion;

        var first = await AskAsync(asker, w, version);
        var again = await AskAsync(asker, w, version);
        again.Settlement.Should().BeEquivalentTo(first.Settlement);
        again.RowVersion.Should().Be(first.RowVersion, "the replay writes nothing");
        (await RequestsOfAsync(w.ReturnId)).Should().ContainSingle();
        (await AuditCountAsync("return.money_back_requested", w.ReturnId)).Should().Be(1);
        var other = w with { ReturnId = (await WorldAsync(invoice: true)).ReturnId };
        (await CodeOf(() => AskAsync(asker, other, version))).Should().Be("RETURN_CHANGED",
            "a stale rowVersion of a return not asked yet is still refused");
    }

    [PostgresFact]
    public async Task ParallelAsks_GiveOneRequest_ForACreditNoteAndForARefund_FourRounds()
    {
        foreach (var invoiced in new[] { true, false, true, false })
        {
            var w = await WorldAsync(invoice: invoiced, paid: !invoiced);
            var version = (await As(await StaffAsync(), w, s => s.GetStaffAsync(w.ReturnId))).RowVersion;
            var askers = new[] { await StaffAsync(), await StaffAsync(), await StaffAsync(), await StaffAsync() };
            using var gate = new ManualResetEventSlim(false);
            var tasks = askers.Select(a => Task.Run(async () =>
            {
                gate.Wait();
                return await AskAsync(a, w, version);
            })).ToList();
            gate.Set();
            var answers = await Task.WhenAll(tasks);
            answers.Select(a => (a.Settlement!.Kind, a.Settlement.RequestedBy, a.Settlement.CreditNoteRequestId, a.Settlement.RefundId)).Distinct()
                .Should().ContainSingle("every caller gets the one winner's request");
            (await RequestsOfAsync(w.ReturnId)).Should().HaveCount(invoiced ? 1 : 0);
            await using var db = Db();
            (await db.Refunds.CountAsync(r => r.PaymentId == w.PaymentId)).Should().Be(invoiced ? 0 : 1);
            (await AuditCountAsync("return.money_back_requested", w.ReturnId)).Should().Be(1);
        }
    }

    [PostgresFact]
    public async Task MoneyBack_NeedsReturnsManage_ItsApproval_PaymentsRefundApproveAndAStepUpCode()
    {
        var w = await WorldAsync(invoice: true);
        var (ask, approve) = ($"/api/v1/admin/returns/{w.ReturnId}/money-back", $"/api/v1/admin/returns/{w.ReturnId}/money-back/approve");
        var stale = new { rowVersion = 1 };

        using (var anonymous = factory.CreateClient())
        {
            (await anonymous.PostAsJsonAsync(ask, stale)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anonymous.PostAsJsonAsync(approve, new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        foreach (var role in new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Moderator, Permissions.Roles.Finance })
            (await (await LoginAsync(role)).Client.PostAsJsonAsync(ask, stale)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        foreach (var role in new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Moderator, Permissions.Roles.Support })
            (await (await LoginAsync(role)).Client.PostAsJsonAsync(approve, new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        foreach (var role in new[] { Permissions.Roles.Support, Permissions.Roles.Admin })
            (await ErrorAsync(await (await LoginAsync(role)).Client.PostAsJsonAsync(ask, stale), HttpStatusCode.Conflict)).Should().Be("RETURN_CHANGED", role);
        foreach (var role in new[] { Permissions.Roles.Finance, Permissions.Roles.Admin })
        {
            var who = await LoginAsync(role);
            (await ErrorAsync(await PostAsync(who, approve, null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED", role);
            (await ErrorAsync(await PostAsync(who, approve, await CodeAsync(who)), HttpStatusCode.Conflict)).Should().Be("RETURN_NOT_AWAITING_APPROVAL", role);
        }

        var support = await LoginAsync(Permissions.Roles.Support);
        var version = (await As(support.UserId, w, s => s.GetStaffAsync(w.ReturnId))).RowVersion;
        var answer = await support.Client.PostAsJsonAsync(ask, new { rowVersion = version });
        (answer.Headers.CacheControl!.Private, answer.Headers.CacheControl.NoStore).Should().Be((true, true));
        var data = await DataAsync(answer);
        data.GetProperty("settlement").GetProperty("kind").GetString().Should().Be("credit_note");
        data.GetProperty("status").GetString().Should().Be("inspected");
    }

    private async Task<string> PaymentRefAsync(Guid paymentId)
    {
        await using var db = Db();
        return await db.Payments.Where(p => p.Id == paymentId).Select(p => p.ProviderRef).SingleAsync();
    }

    private async Task<Person> LoginAsync(string role)
    {
        factory.CreateClient().Dispose();
        var email = $"f23b.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "F23b " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Person(client, id);
    }

    /// <summary>The current authenticator code, usable once more: the replay guard of this user is cleared first.</summary>
    private async Task<string> CodeAsync(Person who)
    {
        await using var db = Db();
        await db.Users.Where(u => u.Id == who.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    private static Task<HttpResponseMessage> PostAsync(Person who, string url, string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) };
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return who.Client.SendAsync(request);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }
}
