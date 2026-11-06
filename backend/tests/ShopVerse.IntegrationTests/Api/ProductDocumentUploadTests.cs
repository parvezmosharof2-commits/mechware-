using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Domain.Catalog;
using ShopVerse.Infrastructure.Identity;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Api;

/// <summary>
/// Sprint 3–4 upload abuse gate, through the real API: wrong type, wrong name, too big, a virus, a scanner that is down,
/// hostile file names, the same file twice, and who may upload and who may download. After every refusal nothing is stored.
/// </summary>
public sealed class ProductDocumentUploadTests : IDisposable
{
    private const string AdminEmail = "admin@mechware.com.bd";
    private const string AdminPassword = "MechAdmin#2026!";
    private const string AdminSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
    private static readonly TotpService Totp = new();

    private readonly string _storage = Path.Combine(Path.GetTempPath(), "mw-doc-it-" + Guid.NewGuid().ToString("N"));
    private readonly List<DbBackedFactory> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }

    private DbBackedFactory Factory(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string> { ["Documents:LocalPath"] = _storage };
        foreach (var (key, value) in extra) settings[key] = value;
        var factory = new DbBackedFactory(settings: settings);
        _factories.Add(factory);
        return factory;
    }

    private static ShopVerseDbContext Db(DbBackedFactory factory) =>
        new(new DbContextOptionsBuilder<ShopVerseDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private static async Task<HttpClient> AdminAsync(DbBackedFactory factory)
    {
        HttpClient client = factory.CreateClient();
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = AdminEmail, password = AdminPassword, totpCode = Totp.ComputeTotpCode(AdminSecret, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
        return client;
    }

    private static async Task<(Guid Id, string Slug)> PublishedProductAsync(DbBackedFactory factory)
    {
        await using var db = Db(factory);
        var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 5);
        var slug = await db.Products.AsNoTracking().Where(p => p.Id == seeded.ProductId).Select(p => p.Slug).SingleAsync();
        return (seeded.ProductId, slug);
    }

    private static byte[] Pdf(string body = "hello") => Encoding.ASCII.GetBytes("%PDF-1.4\n" + body + "\n%%EOF");

    private static MultipartFormDataContent Form(string? kind, string? fileName, byte[]? bytes, string claimedContentType = "application/pdf")
    {
        var form = new MultipartFormDataContent();
        if (kind is not null) form.Add(new StringContent(kind), "kind");
        if (bytes is not null)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(claimedContentType);
            form.Add(file, "file", fileName ?? "x.pdf");
        }

        return form;
    }

    private static string Url(Guid product, string tail = "") => $"/api/v1/admin/products/{product}/documents{tail}";

    private string[] StoredFiles() =>
        Directory.Exists(_storage) ? Directory.GetFiles(_storage, "*", SearchOption.AllDirectories) : [];

    private static async Task<int> RowsAsync(DbBackedFactory factory, Guid product)
    {
        await using var db = Db(factory);
        return await db.ProductDocuments.CountAsync(d => d.ProductId == product);
    }

    private static async Task<string> ErrorCodeOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    // ----- who may upload -----

    [PostgresFact]
    public async Task Anonymous_Gets401_AndABuyer_Gets403_OnEveryDocumentEndpoint()
    {
        var factory = Factory();
        var id = Guid.NewGuid();
        (HttpMethod Method, string Path, bool Body)[] endpoints =
        [
            (HttpMethod.Get, Url(id), false),
            (HttpMethod.Post, Url(id), true),
            (HttpMethod.Delete, Url(id, $"/{Guid.NewGuid()}"), false)
        ];
        HttpRequestMessage Build((HttpMethod Method, string Path, bool Body) e) =>
            new(e.Method, e.Path) { Content = e.Body ? Form("datasheet", "a.pdf", Pdf()) : null };

        using var anonymous = factory.CreateClient();
        foreach (var e in endpoints) (await anonymous.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, e.Path);

        using var buyer = factory.CreateClient();
        var login = await buyer.PostAsJsonAsync("/api/v1/auth/login", new { identifier = "buyer@mechware.com.bd", password = "BuyerPass#2026!" });
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        buyer.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        foreach (var e in endpoints) (await buyer.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Forbidden, e.Path);
        StoredFiles().Should().BeEmpty();
    }

    // ----- the good path -----

    [PostgresAndRedisFact]
    public async Task AValidDatasheet_IsStored_Listed_OnTheProductPage_AndDownloadsAsAnAttachment()
    {
        var factory = Factory();
        var (product, slug) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);
        byte[] bytes = Pdf("a real datasheet");

        var upload = await admin.PostAsync(Url(product), Form("Datasheet", "VFD007 datasheet.pdf", bytes));

        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        using var created = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var data = created.RootElement.GetProperty("data");
        var id = data.GetProperty("id").GetGuid();
        (data.GetProperty("kind").GetString(), data.GetProperty("fileName").GetString(), data.GetProperty("contentType").GetString(), data.GetProperty("sizeBytes").GetInt64())
            .Should().Be(("datasheet", "VFD007 datasheet.pdf", "application/pdf", (long)bytes.Length));
        data.GetProperty("sha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        upload.Content.Headers.ContentLength.Should().BeGreaterThan(0);
        (await upload.Content.ReadAsStringAsync()).Should().NotContain("storageKey").And.NotContain(_storage, "where it is kept is not told");

        StoredFiles().Should().ContainSingle().Which.Should().MatchRegex(@"products[\\/][0-9a-f]{32}[\\/][0-9a-f]{32}\.pdf$");

        using var list = JsonDocument.Parse(await admin.GetStringAsync(Url(product)));
        list.RootElement.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).Should().Equal(id);

        using var anonymous = factory.CreateClient();
        using var page = JsonDocument.Parse(await anonymous.GetStringAsync($"/api/v1/storefront/products/{slug}"));
        var listed = page.RootElement.GetProperty("data").GetProperty("documents").EnumerateArray().Single();
        listed.GetProperty("url").GetString().Should().Be($"/api/v1/storefront/products/{slug}/documents/{id}");
        listed.GetProperty("kind").GetString().Should().Be("datasheet");

        var download = await anonymous.GetAsync(listed.GetProperty("url").GetString());
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
        download.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        download.Content.Headers.ContentDisposition.FileName.Should().Contain("VFD007 datasheet.pdf");
        download.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
    }

    [PostgresFact]
    public async Task TheKind_IsOneOfTheThreeWords_InAnyCase_WithOrWithoutSpacesAround()
    {
        var factory = Factory();
        var (product, _) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);

        (await admin.PostAsync(Url(product), Form("DATASHEET", "a.pdf", Pdf("one")))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.PostAsync(Url(product), Form("  Certificate ", "b.pdf", Pdf("two")))).StatusCode.Should().Be(HttpStatusCode.Created);

        await using var db = Db(factory);
        (await db.ProductDocuments.AsNoTracking().Where(d => d.ProductId == product).OrderBy(d => d.FileName).Select(d => d.Kind).ToListAsync())
            .Should().Equal(DocumentKind.Datasheet, DocumentKind.Certificate);
    }

    [PostgresFact]
    public async Task ACadModelAndACertificate_AreAccepted_WithTheirOwnTypes()
    {
        var factory = Factory();
        var (product, _) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);
        byte[] step = Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nEND-ISO-10303-21;");
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

        (await admin.PostAsync(Url(product), Form("cad", "drive.STP", step, "application/octet-stream"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.PostAsync(Url(product), Form("certificate", "ce.png", png, "image/png"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.PostAsync(Url(product), Form("certificate", "ul.JPEG", jpg, "image/jpeg"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.PostAsync(Url(product), Form("certificate", "iso.pdf", Pdf("iso")))).StatusCode.Should().Be(HttpStatusCode.Created);

        await using var db = Db(factory);
        var stored = await db.ProductDocuments.AsNoTracking().Where(d => d.ProductId == product).ToListAsync();
        stored.Select(d => d.ContentType).Should().BeEquivalentTo("model/step", "image/png", "image/jpeg", "application/pdf");
        StoredFiles().Should().HaveCount(4);
    }

    // ----- refusals: nothing is stored -----

    private static object?[] Row(string kind, string fileName, byte[] bytes, string claimedType, string reason) => [kind, fileName, bytes, claimedType, reason];

    public static IEnumerable<object?[]> Refused() =>
    [
        // an executable with a document's name, and with a lying content type
        Row("datasheet", "manual.pdf", Encoding.ASCII.GetBytes("MZ\u0090\0\u0003\0\0\0\u0004\0\0\0"), "application/pdf", "content is not a type"),
        Row("datasheet", "setup.exe", Encoding.ASCII.GetBytes("MZ\u0090\0\u0003\0\0\0\u0004\0\0\0"), "application/pdf", "must be a file ending in"),
        // a real PDF with a dangerous name
        Row("datasheet", "manual.pdf.exe", Pdf(), "application/pdf", "must be a file ending in"),
        Row("datasheet", "manual.html", Pdf(), "text/html", "must be a file ending in"),
        Row("datasheet", "manual", Pdf(), "application/pdf", "must be a file ending in"),
        Row("datasheet", "..", Pdf(), "application/pdf", "must be a file ending in"),
        // scripts, pages and archives passing as documents
        Row("datasheet", "run.pdf", Encoding.ASCII.GetBytes("#!/bin/sh\nrm -rf / --no-preserve-root\n"), "application/pdf", "content is not a type"),
        Row("datasheet", "page.pdf", Encoding.ASCII.GetBytes("<html><script>alert(document.cookie)</script></html>"), "application/pdf", "content is not a type"),
        Row("datasheet", "bundle.pdf", Encoding.ASCII.GetBytes("PK\u0003\u0004\u0014\0\0\0\b\0 padding padding"), "application/zip", "content is not a type"),
        Row("datasheet", "svg.pdf", Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>"), "image/svg+xml", "content is not a type"),
        // the signature must be the very first bytes (no leading junk, no polyglot header offset)
        Row("datasheet", "junk.pdf", Encoding.ASCII.GetBytes("<script>x</script>%PDF-1.4\n"), "application/pdf", "content is not a type"),
        Row("datasheet", "space.pdf", Encoding.ASCII.GetBytes("   %PDF-1.4\nbody padding"), "application/pdf", "content is not a type"),
        // a file that is a kind of document, but not this kind
        Row("datasheet", "drive.step", Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;"), "model/step", "must be a file ending in"),
        Row("cad", "drive.pdf", Pdf(), "application/pdf", "must be a file ending in"),
        Row("cad", "drive.step", Pdf(), "application/octet-stream", "content is not a type"),
        Row("datasheet", "ds.pdf", Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;"), "application/pdf", "content is not a type"),
        Row("datasheet", "ds.pdf", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8], "image/png", "content is not a type"),
        // a PDF whose extension says it is a picture
        Row("certificate", "scan.png", Pdf(), "image/png", "does not match"),
        Row("certificate", "scan.jpg", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8], "image/jpeg", "does not match"),
        // empty and tiny
        Row("datasheet", "empty.pdf", Array.Empty<byte>(), "application/pdf", "empty"),
        Row("datasheet", "tiny.pdf", Encoding.ASCII.GetBytes("%PDF"), "application/pdf", "content is not a type"),
    ];

    [PostgresTheory]
    [MemberData(nameof(Refused))]
    public async Task AFileThatIsNotWhatItClaims_IsRefusedWith400_AndNothingIsStored(
        string kind, string fileName, byte[] bytes, string claimedType, string reason)
    {
        var factory = Factory();
        var (product, _) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);

        var response = await admin.PostAsync(Url(product), Form(kind, fileName, bytes, claimedType));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain(reason);
        StoredFiles().Should().BeEmpty();
        (await RowsAsync(factory, product)).Should().Be(0);
    }

    [PostgresFact]
    public async Task BadForms_AreRefusedWith400_AndNothingIsStored()
    {
        var factory = Factory();
        var (product, _) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);

        foreach (var form in new[]
        {
            Form("firmware", "a.pdf", Pdf()),       // unknown kind
            Form("1", "a.pdf", Pdf()),              // a number is not a kind
            Form("0", "a.pdf", Pdf()),
            Form("99", "a.pdf", Pdf()),
            Form("datasheet,cad", "a.pdf", Pdf()),  // Enum.TryParse would or these into "certificate"
            Form("datasheet, certificate", "a.pdf", Pdf()),
            Form("Datasheet\0", "a.pdf", Pdf()),
            Form("data sheet", "a.pdf", Pdf()),
            Form("-1", "a.pdf", Pdf()),
            Form("", "a.pdf", Pdf()),
            Form(null, "a.pdf", Pdf()),             // no kind at all
            Form("datasheet", null, null)           // no file
        })
        {
            var response = await admin.PostAsync(Url(product), form);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        }

        var notMultipart = await admin.PostAsync(Url(product), new StringContent("{\"kind\":\"datasheet\"}", Encoding.UTF8, "application/json"));
        notMultipart.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType);

        StoredFiles().Should().BeEmpty();
        (await RowsAsync(factory, product)).Should().Be(0);
    }

    [PostgresFact]
    public async Task AFileOverItsLimit_IsRefusedWith413_AndNothingIsStored()
    {
        var factory = Factory(("Documents:MaxDatasheetBytes", "2048"), ("Documents:MaxCadBytes", "4096"));
        var (product, _) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);

        var tooBig = await admin.PostAsync(Url(product), Form("datasheet", "big.pdf", Pdf(new string('x', 3000))));
        tooBig.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, await tooBig.Content.ReadAsStringAsync());
        (await ErrorCodeOf(tooBig)).Should().Be("FILE_TOO_LARGE");
        StoredFiles().Should().BeEmpty();
        (await RowsAsync(factory, product)).Should().Be(0);

        // Exactly at the limit is fine; one byte over is not; and each kind has its own limit.
        byte[] atLimit = Pdf(new string('y', 2048 - Pdf("").Length));
        atLimit.Length.Should().Be(2048);
        (await admin.PostAsync(Url(product), Form("datasheet", "at-limit.pdf", atLimit))).StatusCode.Should().Be(HttpStatusCode.Created);
        byte[] overByOne = Pdf(new string('z', 2049 - Pdf("").Length));
        overByOne.Length.Should().Be(2049);
        (await admin.PostAsync(Url(product), Form("datasheet", "over.pdf", overByOne))).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        byte[] cad = Encoding.ASCII.GetBytes("ISO-10303-21;\n" + new string('c', 3500));
        (await admin.PostAsync(Url(product), Form("cad", "big-ok.step", cad))).StatusCode.Should().Be(HttpStatusCode.Created, "a CAD file may be larger than a datasheet");
        StoredFiles().Should().HaveCount(2);
    }

    [PostgresFact]
    public async Task AVirus_IsRefused_WhereverItHides_AndNothingIsStored()
    {
        var factory = Factory();
        var (product, _) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);

        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("plain.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n" + Eicar)),                                        // behind a valid header
            ("end.pdf", [.. Pdf(new string('p', 200_000)), .. Encoding.ASCII.GetBytes(Eicar)]),               // after a lot of ordinary data
            ("middle.pdf", [.. Encoding.ASCII.GetBytes("%PDF-1.4\n"), .. new byte[70_000], .. Encoding.ASCII.GetBytes(Eicar), .. new byte[70_000]])
        })
        {
            var response = await admin.PostAsync(Url(product), Form("datasheet", name, bytes));
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, name);
            (await response.Content.ReadAsStringAsync()).Should().Contain("malware");
        }

        StoredFiles().Should().BeEmpty();
        (await RowsAsync(factory, product)).Should().Be(0);
    }

    [PostgresFact]
    public async Task WhenTheScannerIsDown_NothingIsAccepted_And503IsAnswered()
    {
        int closedPort;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); closedPort = ((IPEndPoint)probe.LocalEndpoint).Port; }
        var factory = Factory(("Antivirus:Host", "127.0.0.1"), ("Antivirus:Port", closedPort.ToString()), ("Antivirus:TimeoutSeconds", "2"));
        var (product, _) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);

        var response = await admin.PostAsync(Url(product), Form("datasheet", "ok.pdf", Pdf()));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());
        (await ErrorCodeOf(response)).Should().Be("SCAN_UNAVAILABLE");
        StoredFiles().Should().BeEmpty("an unscanned file is never kept");
        (await RowsAsync(factory, product)).Should().Be(0);
    }

    // ----- names, duplicates, races -----

    [PostgresFact]
    public async Task AHostileFileName_IsOnlyALabel_TheStoredNameIsOurs_AndNothingLandsOutsideTheStore()
    {
        var factory = Factory();
        var (product, _) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);
        var before = Directory.GetParent(_storage)!.GetFiles("*.pdf", SearchOption.AllDirectories).Select(f => f.FullName).ToHashSet();

        var upload = await admin.PostAsync(Url(product), Form("datasheet", "../../../../tmp/evil.pdf", Pdf("x")));

        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("fileName").GetString().Should().Be("evil.pdf");
        var stored = StoredFiles().Should().ContainSingle().Subject;
        Path.GetFullPath(stored).Should().StartWith(Path.GetFullPath(_storage));
        Path.GetFileName(stored).Should().NotContain("evil");
        Regex.IsMatch(Path.GetFileName(stored), "^[0-9a-f]{32}\\.pdf$").Should().BeTrue();
        Directory.GetParent(_storage)!.GetFiles("evil*", SearchOption.TopDirectoryOnly).Should().BeEmpty();
        Directory.GetParent(_storage)!.GetFiles("*.pdf", SearchOption.AllDirectories).Select(f => f.FullName).ToHashSet()
            .Except(before).Should().OnlyContain(f => f.StartsWith(Path.GetFullPath(_storage)), "no new file anywhere else");
    }

    [PostgresFact]
    public async Task TheSameFileTwice_IsA409_AndTheSecondCopyIsNotStored()
    {
        var factory = Factory();
        var (product, _) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);
        byte[] bytes = Pdf("same");

        (await admin.PostAsync(Url(product), Form("datasheet", "a.pdf", bytes))).StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await admin.PostAsync(Url(product), Form("certificate", "renamed.pdf", bytes));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeOf(second)).Should().Be("DOCUMENT_EXISTS");
        StoredFiles().Should().ContainSingle();
        (await RowsAsync(factory, product)).Should().Be(1);
    }

    [PostgresFact]
    public async Task EightIdenticalUploadsAtOnce_LeaveExactlyOneFileAndOneRow_AndNeverA500()
    {
        var factory = Factory();
        var (product, _) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);
        byte[] bytes = Pdf("race");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => admin.PostAsync(Url(product), Form("datasheet", $"race-{i}.pdf", bytes))));

        results.Select(r => r.StatusCode).Should().OnlyContain(c => c == HttpStatusCode.Created || c == HttpStatusCode.Conflict);
        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        StoredFiles().Should().ContainSingle();
        (await RowsAsync(factory, product)).Should().Be(1);
    }

    [PostgresFact]
    public async Task AProductUnderReview_CannotGetADocument_AndAnUnknownProductIs404()
    {
        var factory = Factory();
        Guid underReview;
        await using (var db = Db(factory))
        {
            var template = await CatalogTestData.SeedVariantAsync(db, onHand: 1);
            var p = await db.Products.Include(x => x.Variants).SingleAsync(x => x.Id == template.ProductId);
            p.Archive();
            p.ReturnToDraft();
            p.SubmitForReview();
            await db.SaveChangesAsync();
            underReview = p.Id;
        }

        using var admin = await AdminAsync(factory);
        (await admin.PostAsync(Url(underReview), Form("datasheet", "a.pdf", Pdf()))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsync(Url(Guid.NewGuid()), Form("datasheet", "a.pdf", Pdf()))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        StoredFiles().Should().BeEmpty();
    }

    // ----- delete, and who may download -----

    [PostgresAndRedisFact]
    public async Task Deleting_RemovesTheRowAndTheFile_AndTheDownloadIs404()
    {
        var factory = Factory();
        var (product, slug) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);
        var upload = await admin.PostAsync(Url(product), Form("datasheet", "a.pdf", Pdf()));
        using var doc = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync($"/api/v1/storefront/products/{slug}/documents/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.DeleteAsync(Url(product, $"/{id}"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        StoredFiles().Should().BeEmpty();
        (await RowsAsync(factory, product)).Should().Be(0);
        (await anonymous.GetAsync($"/api/v1/storefront/products/{slug}/documents/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.DeleteAsync(Url(product, $"/{id}"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.DeleteAsync(Url(product, $"/{Guid.NewGuid()}"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresAndRedisFact]
    public async Task OnlyADocumentOfAPublishedProduct_UnderItsOwnSlug_CanBeDownloaded()
    {
        var factory = Factory();
        var (published, publishedSlug) = await PublishedProductAsync(factory);
        var (other, otherSlug) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);
        Guid IdOf(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.GetProperty("data").GetProperty("id").GetGuid(); }
        var mine = IdOf(await (await admin.PostAsync(Url(published), Form("datasheet", "a.pdf", Pdf("mine")))).Content.ReadAsStringAsync());
        var theirs = IdOf(await (await admin.PostAsync(Url(other), Form("datasheet", "b.pdf", Pdf("theirs")))).Content.ReadAsStringAsync());
        using var anonymous = factory.CreateClient();
        string Get(string slug, Guid id) => $"/api/v1/storefront/products/{slug}/documents/{id}";

        (await anonymous.GetAsync(Get(publishedSlug, mine))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync(Get(publishedSlug, theirs))).StatusCode.Should().Be(HttpStatusCode.NotFound, "another product's document under this slug");
        (await anonymous.GetAsync(Get(otherSlug, mine))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(Get("no-such-product", mine))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(Get("../../etc", mine))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(Get(publishedSlug, Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Once the product is no longer published, its files are no longer public.
        await using (var db = Db(factory))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET status = 'archived' WHERE id = {published}");
        (await anonymous.GetAsync(Get(publishedSlug, mine))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using (var db = Db(factory))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET status = 'active', supply_status = 'obsolete' WHERE id = {published}");
        (await anonymous.GetAsync(Get(publishedSlug, mine))).StatusCode.Should().Be(HttpStatusCode.NotFound, "an obsolete product is not linked to, so neither are its files");
    }

    [PostgresAndRedisFact]
    public async Task AStoredFileThatWentMissing_IsA404_NotA500()
    {
        var factory = Factory();
        var (product, slug) = await PublishedProductAsync(factory);
        using var admin = await AdminAsync(factory);
        var upload = await admin.PostAsync(Url(product), Form("datasheet", "a.pdf", Pdf()));
        using var doc = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        foreach (var file in StoredFiles()) File.Delete(file);

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync($"/api/v1/storefront/products/{slug}/documents/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
