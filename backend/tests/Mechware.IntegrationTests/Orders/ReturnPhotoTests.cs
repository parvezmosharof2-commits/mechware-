using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Application.Inventory;
using Mechware.Application.Orders;
using Mechware.Domain.Identity;
using Mechware.Domain.Returns;
using Mechware.Infrastructure.Documents;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;
using SkiaSharp;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// F2.2 return photos through the real API (own database, own storage directory, the EICAR development scanner): the type from the first
/// bytes and not the name, the 5 MB limit, five per return, EXIF/GPS gone from what is stored, an infected file refused, the server's key,
/// one identical 404 for everything the caller may not see, and no upload once the goods are received.
/// </summary>
public sealed class ReturnPhotoTests : IDisposable
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "ReturnsF22#2026!";
    private const string Eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
    private const string GpsText = "MECHWARE-GPS-23.8103N-90.4125E";

    private readonly string _storage = Path.Combine(Path.GetTempPath(), "mw-rma-it-" + Guid.NewGuid().ToString("N"));
    private readonly DbBackedFactory _factory;

    public ReturnPhotoTests() => _factory = new DbBackedFactory(settings: new Dictionary<string, string> { ["Documents:LocalPath"] = _storage });

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    private sealed record Person(HttpClient Client, Guid UserId);

    private sealed class Actor(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.22";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-f22";
        public bool HasPermission(string permission) => true;
    }

    private async Task<Person> LoginAsync(string role)
    {
        _factory.CreateClient().Dispose();
        var email = $"f22.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "F22 " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Person(client, id);
    }

    private ReturnRequestService Service(MechwareDbContext db, Guid user) =>
        new(db, new Actor(user), TimeProvider.System, null, new InventoryService(db, new NoopLockProvider(), TimeProvider.System, user: new Actor(user)));

    /// <summary>A delivered order of <paramref name="buyer"/> (one line of 2, deducted from stock) and a return of it, opened at the service.</summary>
    private async Task<Guid> ReturnAsync(Guid buyer)
    {
        await using var db = Db();
        var now = DateTime.UtcNow;
        var v = await CatalogTestData.SeedVariantAsync(db, onHand: 10);
        var id = await CreditTestData.InsertOrderAsync(db, buyer, null, 25_000, onCredit: false, status: "Delivered", confirmedAtUtc: now.AddDays(-3), placedAtUtc: now.AddDays(-4));
        var sub = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO sub_orders (id, order_id, seller_id, sub_order_number, status, goods_net, discount_total, tax_total, shipping_total, total,
                commission_rate, commission, seller_earning, created_at)
            VALUES ({sub}, {id}, {Guid.NewGuid()}, {"S-" + Guid.NewGuid().ToString("N")[..12]}, 'Delivered', 0, 0, 0, 0, 0, 0, 0, 0, {now})");
        var line = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO order_items (id, sub_order_id, variant_id, sku, title, quantity, unit_price, vat_rate, gross, discount, net, tax, total, created_at)
            VALUES ({line}, {sub}, {v.VariantId}, 'SKU-P', 'Breaker', 2, 1000, 0.05, 2000, 0, 2000, 0, 2000, {now})");
        await CreditTestData.AddEventAsync(db, id, 2, "Shipped", "Delivered", now.AddDays(-1));
        var inventory = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
        (await inventory.ReserveAsync(new ReserveStockRequest(id, [new ReservationLine(v.VariantId, 2)]))).Succeeded.Should().BeTrue();
        await inventory.CommitAsync(id);
        var number = await db.Orders.Where(o => o.Id == id).Select(o => o.OrderNumber).SingleAsync();
        return (await Service(db, buyer).OpenAsync(new OpenReturnRequest(buyer, number, "f22-" + Guid.NewGuid().ToString("N"), null, "damaged", null,
            [new ReturnLineInput(line, 2)]))).Return.Id;
    }

    private async Task<StaffReturnDto> MoveAsync(Guid staff, Guid returnId, Func<ReturnRequestService, StaffReturnDto, Task<StaffReturnDto>> move)
    {
        await using var db = Db();
        var service = Service(db, staff);
        return await move(service, await service.GetStaffAsync(returnId));
    }

    // ---- files ----

    private static byte[] Encode(SKEncodedImageFormat format, int w = 64, int h = 48)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(new SKColor(200, 40, 40));
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>A real JPEG with an EXIF APP1 right after SOI: orientation 1, a GPS IFD with latitude ref "N" and a processing-method text.</summary>
    internal static byte[] JpegWithGps(ushort orientation = 1, int w = 64, int h = 48)
    {
        var jpeg = Encode(SKEncodedImageFormat.Jpeg, w, h);
        var tiff = new List<byte>();
        void U16(int v) { tiff.Add((byte)(v >> 8)); tiff.Add((byte)v); }
        void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
        void Entry(int tag, int type, int count, int value, bool shortValue = false) { U16(tag); U16(type); U32(count); if (shortValue) { U16(value); U16(0); } else U32(value); }
        byte[] text = [.. "ASCII\0\0\0"u8.ToArray(), .. Encoding.ASCII.GetBytes(GpsText)];
        tiff.AddRange("MM"u8.ToArray()); U16(42); U32(8);
        U16(2); Entry(0x0112, 3, 1, orientation, shortValue: true); Entry(0x8825, 4, 1, 38); U32(0);            // IFD0 at 8 (30 bytes)
        U16(2); tiff.AddRange([0x00, 0x01, 0x00, 0x02, 0, 0, 0, 2, (byte)'N', 0, 0, 0]); Entry(0x001B, 7, text.Length, 68); U32(0);   // GPS IFD at 38
        tiff.AddRange(text);                                                                                     // data at 68
        byte[] payload = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        int length = payload.Length + 2;
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }

    /// <summary>The APPn markers of a JPEG before its first scan (APP0 = E0 is JFIF; APP1 = EXIF/XMP, APP2 = ICC).</summary>
    internal static List<byte> AppMarkers(byte[] jpeg)
    {
        var markers = new List<byte>();
        int i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF && jpeg[i + 1] != 0xDA)
        {
            if (jpeg[i + 1] is >= 0xE0 and <= 0xEF) markers.Add(jpeg[i + 1]);
            i += 2 + (jpeg[i + 2] << 8 | jpeg[i + 3]);
        }
        return markers;
    }

    private static MultipartFormDataContent Form(byte[] bytes, string fileName, string claimedType = "image/jpeg")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(claimedType);
        form.Add(file, "file", fileName);
        return form;
    }

    private static Task<HttpResponseMessage> UploadAsync(Person p, Guid returnId, byte[] bytes, string fileName = "photo.jpg", string claimedType = "image/jpeg") =>
        p.Client.PostAsync($"/api/v1/returns/{returnId}/photos", Form(bytes, fileName, claimedType));

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<(string Code, string Message)> ErrorAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        var e = doc.RootElement.GetProperty("error");
        return (e.GetProperty("code").GetString()!, e.GetProperty("message").GetString()!);
    }

    private int FilesOf(Guid returnId)
    {
        var dir = Path.Combine(_storage, "uploads", "rma", returnId.ToString("N"));
        return Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;
    }

    [PostgresFact]
    public async Task AJpegWithGps_IsStoredReencoded_UnderTheServersKey_AndServedInline_NoStore_Nosniff_ToTheBuyerAndStaff()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var support = await LoginAsync(Permissions.Roles.Support);
        var id = await ReturnAsync(buyer.UserId);
        var original = JpegWithGps();
        Encoding.ASCII.GetString(original).Should().Contain(GpsText).And.Contain("Exif");

        var upload = await UploadAsync(buyer, id, original, "../../../../etc/evil.php", "text/html");
        upload.Headers.CacheControl!.NoStore.Should().BeTrue();
        var dto = await DataAsync(upload, HttpStatusCode.Created);
        dto.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "slot", "contentType", "sizeBytes", "width", "height", "createdAtUtc"]);
        (dto.GetProperty("slot").GetInt32(), dto.GetProperty("contentType").GetString(), dto.GetProperty("width").GetInt32(), dto.GetProperty("height").GetInt32())
            .Should().Be((1, "image/jpeg", 64, 48));
        var photoId = dto.GetProperty("id").GetGuid();

        ReturnPhoto row;
        await using (var db = Db())
        {
            row = await db.ReturnPhotos.SingleAsync(p => p.ReturnRequestId == id);
            row.StorageKey.Should().MatchRegex($"^uploads/rma/{id:N}/[0-9a-f]{{32}}\\.jpg$").And.NotContain("evil");
            (await db.AuditLogs.Where(a => a.EntityId == id.ToString() && a.Action == "return.photo_added").Select(a => a.NewValuesJson).SingleAsync())
                .Should().Contain(photoId.ToString());
        }
        var stored = await File.ReadAllBytesAsync(Path.Combine(_storage, row.StorageKey.Replace('/', Path.DirectorySeparatorChar)));
        stored.Length.Should().Be((int)row.SizeBytes);
        AppMarkers(stored).Should().NotContain([(byte)0xE1, (byte)0xE2], "no EXIF, XMP or ICC survives");
        var storedText = Encoding.ASCII.GetString(stored);
        storedText.Should().NotContain("Exif").And.NotContain(GpsText).And.NotContain("ASCII");
        Directory.GetFiles(_storage, "*", SearchOption.AllDirectories).Should().ContainSingle("the client's name is never used for a file");

        foreach (var (who, url) in new[] { (buyer, $"/api/v1/returns/{id}/photos/{photoId}"), (support, $"/api/v1/admin/returns/{id}/photos/{photoId}") })
        {
            var view = await who.Client.GetAsync(url);
            view.StatusCode.Should().Be(HttpStatusCode.OK);
            view.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
            view.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
            view.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
            (view.Headers.CacheControl!.Private, view.Headers.CacheControl.NoStore).Should().Be((true, true));
            (await view.Content.ReadAsByteArrayAsync()).Should().Equal(stored);
        }
        (await DataAsync(await buyer.Client.GetAsync($"/api/v1/returns/{id}/photos"), HttpStatusCode.OK))[0].GetProperty("id").GetGuid().Should().Be(photoId);
        (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/returns/{id}/photos"), HttpStatusCode.OK)).GetArrayLength().Should().Be(1);
        (await buyer.Client.GetAsync($"/api/v1/admin/returns/{id}/photos/{photoId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [PostgresFact]
    public async Task TheType_ComesFromTheFirstBytes_NeverTheNameOrClaimedType()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var id = await ReturnAsync(buyer.UserId);

        foreach (var bad in new[] { Encoding.ASCII.GetBytes("%PDF-1.4\nnot a photo\n%%EOF"), Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>"),
                     Encoding.ASCII.GetBytes("GIF89a......................") })
            (await ErrorAsync(await UploadAsync(buyer, id, bad, "photo.jpg"), HttpStatusCode.BadRequest))
                .Message.Should().Be("Only JPEG, PNG or WebP photos are accepted.");
        FilesOf(id).Should().Be(0);

        // A real PNG and a real WebP with misleading names and types are accepted, and stored as the server's JPEG.
        var png = await DataAsync(await UploadAsync(buyer, id, Encode(SKEncodedImageFormat.Png), "scan.exe", "application/octet-stream"), HttpStatusCode.Created);
        var webp = await DataAsync(await UploadAsync(buyer, id, Encode(SKEncodedImageFormat.Webp), "photo.gif", "image/gif"), HttpStatusCode.Created);
        (png.GetProperty("contentType").GetString(), webp.GetProperty("contentType").GetString()).Should().Be(("image/jpeg", "image/jpeg"));
        FilesOf(id).Should().Be(2);
        // JPEG magic followed by garbage passes the type check but is not a picture.
        (await ErrorAsync(await UploadAsync(buyer, id, [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[200]]), HttpStatusCode.BadRequest))
            .Message.Should().StartWith("The photo could not be read");
    }

    [PostgresFact]
    public async Task AnOversizeFile_AnInfectedFile_AndASixthPhoto_AreRefused_AndNothingIsStored()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var id = await ReturnAsync(buyer.UserId);

        byte[] oversize = new byte[ReturnPhoto.MaxUploadBytes + 1];
        JpegWithGps().CopyTo(oversize, 0);
        (await ErrorAsync(await UploadAsync(buyer, id, oversize), HttpStatusCode.RequestEntityTooLarge)).Code.Should().Be("FILE_TOO_LARGE");

        byte[] infected = [.. JpegWithGps(), .. Encoding.ASCII.GetBytes(Eicar)];
        (await ErrorAsync(await UploadAsync(buyer, id, infected), HttpStatusCode.BadRequest)).Message.Should().Contain("malware");
        FilesOf(id).Should().Be(0);

        for (int i = 0; i < ReturnPhoto.MaxPerReturn; i++)
            (await UploadAsync(buyer, id, JpegWithGps())).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ErrorAsync(await UploadAsync(buyer, id, JpegWithGps()), HttpStatusCode.Conflict)).Code.Should().Be("RETURN_PHOTOS_FULL");
        FilesOf(id).Should().Be(5);

        // Removing one frees its slot (and its file).
        var photos = await DataAsync(await buyer.Client.GetAsync($"/api/v1/returns/{id}/photos"), HttpStatusCode.OK);
        var third = photos.EnumerateArray().Single(p => p.GetProperty("slot").GetInt32() == 3).GetProperty("id").GetGuid();
        (await buyer.Client.DeleteAsync($"/api/v1/returns/{id}/photos/{third}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        FilesOf(id).Should().Be(4);
        (await DataAsync(await UploadAsync(buyer, id, JpegWithGps()), HttpStatusCode.Created)).GetProperty("slot").GetInt32().Should().Be(3);
        await using var db = Db();
        (await db.AuditLogs.CountAsync(a => a.EntityId == id.ToString() && a.Action == "return.photo_removed")).Should().Be(1);
    }

    [PostgresFact]
    public async Task EverythingTheCallerMayNotSee_IsOneIdentical404()
    {
        var owner = await LoginAsync(Permissions.Roles.Buyer);
        var other = await LoginAsync(Permissions.Roles.Buyer);
        var support = await LoginAsync(Permissions.Roles.Support);
        var mine = await ReturnAsync(owner.UserId);
        var theirs = await ReturnAsync(other.UserId);
        var photo = (await DataAsync(await UploadAsync(owner, mine, JpegWithGps()), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var otherPhoto = (await DataAsync(await UploadAsync(other, theirs, JpegWithGps()), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var misses = new List<HttpResponseMessage>
        {
            await UploadAsync(other, mine, JpegWithGps()),
            await other.Client.GetAsync($"/api/v1/returns/{mine}/photos"),
            await other.Client.GetAsync($"/api/v1/returns/{mine}/photos/{photo}"),
            await other.Client.GetAsync($"/api/v1/returns/{theirs}/photos/{photo}"),       // own return, someone else's photo
            await other.Client.DeleteAsync($"/api/v1/returns/{mine}/photos/{photo}"),
            await owner.Client.GetAsync($"/api/v1/returns/{mine}/photos/{Guid.NewGuid()}"),
            await owner.Client.GetAsync($"/api/v1/returns/{mine}/photos/{otherPhoto}"),
            await owner.Client.GetAsync($"/api/v1/returns/{Guid.NewGuid()}/photos"),
            await support.Client.GetAsync($"/api/v1/returns/{mine}/photos/{photo}"),        // staff use their own route
            await support.Client.DeleteAsync($"/api/v1/returns/{mine}/photos/{photo}"),     // and cannot delete through the buyer's
            await support.Client.GetAsync($"/api/v1/admin/returns/{mine}/photos/{otherPhoto}"),
            await support.Client.GetAsync($"/api/v1/admin/returns/{Guid.NewGuid()}/photos"),
        };
        var answers = new HashSet<(string, string)>();
        foreach (var r in misses)
        {
            answers.Add(await ErrorAsync(r, HttpStatusCode.NotFound));
            r.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        answers.Should().Equal(("NOT_FOUND", "Not found."));

        (await support.Client.DeleteAsync($"/api/v1/admin/returns/{mine}/photos/{photo}")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        await using var db = Db();
        (await db.ReturnPhotos.CountAsync(p => p.ReturnRequestId == mine)).Should().Be(1);
    }

    [PostgresFact]
    public async Task Photos_AreAddedUntilReceived_RemovedOnlyBeforeTheDecision_AndNoUploadAfterInspected()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var support = await LoginAsync(Permissions.Roles.Support);
        var id = await ReturnAsync(buyer.UserId);
        var first = (await DataAsync(await UploadAsync(buyer, id, JpegWithGps()), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        await MoveAsync(support.UserId, id, (s, r) => s.ApproveAsync(id, new ReturnStepRequest(r.RowVersion)));
        (await UploadAsync(buyer, id, JpegWithGps())).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ErrorAsync(await buyer.Client.DeleteAsync($"/api/v1/returns/{id}/photos/{first}"), HttpStatusCode.Conflict)).Code.Should().Be("RETURN_PHOTOS_CLOSED");

        await MoveAsync(support.UserId, id, (s, r) => s.MarkReceivedAsync(id, new ReturnStepRequest(r.RowVersion)));
        (await ErrorAsync(await UploadAsync(buyer, id, JpegWithGps()), HttpStatusCode.Conflict)).Code.Should().Be("RETURN_PHOTOS_CLOSED");

        var inspected = await MoveAsync(support.UserId, id, (s, r) =>
            s.InspectAsync(id, new InspectReturnRequest(r.RowVersion, [.. r.Lines.Select(l => new ReturnInspectionInput(l.Id, l.Quantity, 0, null))])));
        inspected.Status.Should().Be("inspected");
        (await ErrorAsync(await UploadAsync(buyer, id, JpegWithGps()), HttpStatusCode.Conflict)).Code.Should().Be("RETURN_PHOTOS_CLOSED");
        (await ErrorAsync(await buyer.Client.DeleteAsync($"/api/v1/returns/{id}/photos/{first}"), HttpStatusCode.Conflict)).Code.Should().Be("RETURN_PHOTOS_CLOSED");
        FilesOf(id).Should().Be(2);
        (await buyer.Client.GetAsync($"/api/v1/returns/{id}/photos/{first}")).StatusCode.Should().Be(HttpStatusCode.OK, "evidence stays viewable");
    }
}
