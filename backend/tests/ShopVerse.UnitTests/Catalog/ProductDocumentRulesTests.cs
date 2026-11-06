using System.Text;
using FluentAssertions;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;

namespace ShopVerse.UnitTests.Catalog;

/// <summary>Sprint 3–4 upload abuse gate, the pure rules: what a file is, which kind may be what, and what a file name may be.</summary>
public class ProductDocumentRulesTests
{
    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    [Theory]
    [InlineData("%PDF-1.7\n%âãÏÓ", DetectedFileType.Pdf)]
    [InlineData("ISO-10303-21;\nHEADER;", DetectedFileType.Step)]
    [InlineData("MZ\u0090\0\u0003\0\0\0", DetectedFileType.Unknown)]            // a Windows executable
    [InlineData("#!/bin/sh\nrm -rf /", DetectedFileType.Unknown)]
    [InlineData("<html><script>alert(1)</script>", DetectedFileType.Unknown)]
    [InlineData("PK\u0003\u0004", DetectedFileType.Unknown)]                      // a zip is not accepted at all
    [InlineData("  %PDF-1.7", DetectedFileType.Unknown)]                         // the signature must be the first bytes
    [InlineData("\n%PDF-1.7", DetectedFileType.Unknown)]
    [InlineData("%PDF", DetectedFileType.Unknown)]
    [InlineData("", DetectedFileType.Unknown)]
    public void TheTypeIsReadFromTheFirstBytes(string head, DetectedFileType expected) =>
        DocumentFileRules.Detect(Bytes(head)).Should().Be(expected);

    [Fact]
    public void PngAndJpeg_AreRecognisedByTheirSignatures()
    {
        DocumentFileRules.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]).Should().Be(DetectedFileType.Png);
        DocumentFileRules.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10]).Should().Be(DetectedFileType.Jpeg);
        DocumentFileRules.Detect([0x89, 0x50, 0x4E, 0x47]).Should().Be(DetectedFileType.Unknown, "a half signature is not a signature");
    }

    [Theory]
    [InlineData(DocumentKind.Datasheet, DetectedFileType.Pdf, true)]
    [InlineData(DocumentKind.Datasheet, DetectedFileType.Step, false)]
    [InlineData(DocumentKind.Datasheet, DetectedFileType.Png, false)]
    [InlineData(DocumentKind.Cad, DetectedFileType.Step, true)]
    [InlineData(DocumentKind.Cad, DetectedFileType.Pdf, false)]
    [InlineData(DocumentKind.Certificate, DetectedFileType.Pdf, true)]
    [InlineData(DocumentKind.Certificate, DetectedFileType.Png, true)]
    [InlineData(DocumentKind.Certificate, DetectedFileType.Jpeg, true)]
    [InlineData(DocumentKind.Certificate, DetectedFileType.Step, false)]
    [InlineData(DocumentKind.Datasheet, DetectedFileType.Unknown, false)]
    [InlineData(DocumentKind.Cad, DetectedFileType.Unknown, false)]
    [InlineData(DocumentKind.Certificate, DetectedFileType.Unknown, false)]
    public void EachKindAllowsOnlyItsOwnTypes(DocumentKind kind, DetectedFileType type, bool allowed) =>
        DocumentFileRules.Allows(kind, type).Should().Be(allowed);

    [Fact]
    public void TheStoredContentType_IsOursFromTheDetectedType()
    {
        DocumentFileRules.ContentType(DetectedFileType.Pdf).Should().Be("application/pdf");
        DocumentFileRules.ContentType(DetectedFileType.Step).Should().Be("model/step");
        DocumentFileRules.ContentType(DetectedFileType.Unknown).Should().Be("application/octet-stream");
    }

    [Theory]
    [InlineData("datasheet.pdf", "datasheet.pdf")]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("..\\..\\windows\\system32\\evil.pdf", "evil.pdf")]
    [InlineData("C:\\Users\\x\\data sheet.pdf", "data sheet.pdf")]
    [InlineData("/abs/path/file.step", "file.step")]
    [InlineData("a\"b<c>d|e:f*g?h.pdf", "abcdefgh.pdf")]
    [InlineData("na\r\nme\0.pdf", "name.pdf")]
    [InlineData("  .hidden.pdf  ", "hidden.pdf")]
    [InlineData("report.pdf.", "report.pdf")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("....", "")]
    [InlineData("dir/", "")]
    [InlineData("invoice\u202Efdp.pdf", "invoicefdp.pdf")]        // right-to-left override
    [InlineData("a\u200Bb\u200Dc.pdf", "abc.pdf")]               // zero-width space and joiner
    [InlineData("a\u2028b\u2029c.pdf", "abc.pdf")]               // line and paragraph separators
    [InlineData("a\u00ADb.pdf", "ab.pdf")]                        // soft hyphen
    [InlineData("a\uFEFFb.pdf", "ab.pdf")]                        // byte order mark inside the name
    [InlineData("a\uE000b.pdf", "ab.pdf")]                        // private use
    [InlineData("datenblatt-ü-数据表.pdf", "datenblatt-ü-数据表.pdf")]   // ordinary letters of any script stay
    public void AFileNameIsOnlyALabel_StrippedOfDirectoriesAndControlCharacters(string? raw, string expected) =>
        DocumentFileRules.CleanFileName(raw).Should().Be(expected);

    [Fact]
    public void ALoneSurrogate_IsDropped()
    {
        // Built at run time: a theory row with a lone surrogate does not survive xunit's own serialisation.
        DocumentFileRules.CleanFileName("a" + (char)0xD800 + "b" + (char)0xDFFF + ".pdf").Should().Be("ab.pdf");
    }

    [Fact]
    public void ALongName_IsCut_KeepingItsExtension()
    {
        var clean = DocumentFileRules.CleanFileName(new string('a', 400) + ".pdf");
        clean.Length.Should().BeLessThanOrEqualTo(150);
        clean.Should().EndWith(".pdf");
    }

    [Theory]
    [InlineData("a.PDF", "pdf")]
    [InlineData("a.tar.gz", "gz")]
    [InlineData("noextension", "")]
    [InlineData("a.", "")]
    public void TheExtension_IsTheLastOne_InLowerCase(string name, string expected) =>
        DocumentFileRules.ExtensionOf(name).Should().Be(expected);

    // ----- on the product -----

    private static Product NewProduct()
    {
        var p = new Product(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Drive", "ড্রাইভ", "drive-" + Guid.NewGuid().ToString("N")[..6], "s", "s", "<p>d</p>");
        p.AddVariant(Guid.NewGuid(), "SKU-" + Guid.NewGuid().ToString("N")[..6], null, new Dictionary<string, string>(), new Money(1000, Currency.BDT), null, null, 0.4m, true);
        p.AddMedia(Guid.NewGuid(), null, "/img/a.jpg", "front view", 1200, 1200);
        return p;
    }

    private static string Hash(int n) => n.ToString("x").PadLeft(64, '0');

    [Fact]
    public void ADocument_IsAddedWithTheTypeWeDetected_AndRemoved()
    {
        var p = NewProduct();
        var id = Guid.NewGuid();

        var d = p.AddDocument(id, DocumentKind.Datasheet, "ds.pdf", DetectedFileType.Pdf, 1234, Hash(1), $"products/{p.Id:N}/{id:N}.pdf", Guid.NewGuid());

        (d.Kind, d.ContentType, d.SizeBytes, d.FileName).Should().Be((DocumentKind.Datasheet, "application/pdf", 1234L, "ds.pdf"));
        p.Documents.Should().ContainSingle();
        p.RemoveDocument(id).StorageKey.Should().Be(d.StorageKey);
        p.Documents.Should().BeEmpty();
    }

    [Fact]
    public void TheSameContent_IsNotAttachedTwice_ButAnotherFileIs()
    {
        var p = NewProduct();
        p.AddDocument(Guid.NewGuid(), DocumentKind.Datasheet, "a.pdf", DetectedFileType.Pdf, 10, Hash(1), "k/1.pdf", null);

        var again = () => p.AddDocument(Guid.NewGuid(), DocumentKind.Certificate, "b.pdf", DetectedFileType.Pdf, 10, Hash(1), "k/2.pdf", null);
        again.Should().Throw<ConflictException>().Which.Code.Should().Be("DOCUMENT_EXISTS");

        p.AddDocument(Guid.NewGuid(), DocumentKind.Datasheet, "c.pdf", DetectedFileType.Pdf, 10, Hash(2), "k/3.pdf", null);
        p.Documents.Should().HaveCount(2);
    }

    [Fact]
    public void AProduct_HoldsAtMostTwentyDocuments()
    {
        var p = NewProduct();
        for (int i = 0; i < Product.MaxDocuments; i++)
            p.AddDocument(Guid.NewGuid(), DocumentKind.Datasheet, $"d{i}.pdf", DetectedFileType.Pdf, 10, Hash(i), $"k/{i}.pdf", null);

        var act = () => p.AddDocument(Guid.NewGuid(), DocumentKind.Datasheet, "one-more.pdf", DetectedFileType.Pdf, 10, Hash(99), "k/x.pdf", null);

        act.Should().Throw<ValidationException>();
        p.Documents.Should().HaveCount(Product.MaxDocuments);
    }

    [Theory]
    [InlineData(DocumentKind.Datasheet, DetectedFileType.Step)]
    [InlineData(DocumentKind.Cad, DetectedFileType.Pdf)]
    [InlineData(DocumentKind.Datasheet, DetectedFileType.Unknown)]
    public void TheEntity_RefusesAKindThatDoesNotMatchTheType_EvenIfTheServiceMissedIt(DocumentKind kind, DetectedFileType type)
    {
        var p = NewProduct();
        var act = () => p.AddDocument(Guid.NewGuid(), kind, "x.pdf", type, 10, Hash(1), "k/1.pdf", null);
        act.Should().Throw<ValidationException>();
        p.Documents.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0L, 64, "k/1.pdf", "x.pdf")]
    [InlineData(-5L, 64, "k/1.pdf", "x.pdf")]
    [InlineData(10L, 10, "k/1.pdf", "x.pdf")]     // not a SHA-256
    [InlineData(10L, 64, "", "x.pdf")]
    [InlineData(10L, 64, "k/1.pdf", "  ")]
    public void TheEntity_RefusesIncompleteFacts(long size, int hashLength, string key, string name)
    {
        var p = NewProduct();
        var act = () => p.AddDocument(Guid.NewGuid(), DocumentKind.Datasheet, name, DetectedFileType.Pdf, size, new string('a', hashLength), key, null);
        act.Should().Throw<ValidationException>();
        p.Documents.Should().BeEmpty();
    }

    [Fact]
    public void ADocument_CannotBeAddedOrRemoved_WhileTheProductIsUnderReview()
    {
        var p = NewProduct();
        var id = Guid.NewGuid();
        p.AddDocument(id, DocumentKind.Datasheet, "a.pdf", DetectedFileType.Pdf, 10, Hash(1), "k/1.pdf", null);
        p.SubmitForReview();

        ((Action)(() => p.AddDocument(Guid.NewGuid(), DocumentKind.Datasheet, "b.pdf", DetectedFileType.Pdf, 10, Hash(2), "k/2.pdf", null))).Should().Throw<ValidationException>();
        ((Action)(() => p.RemoveDocument(id))).Should().Throw<ValidationException>();
    }

    [Fact]
    public void RemovingAnUnknownDocument_IsANotFound()
    {
        var act = () => NewProduct().RemoveDocument(Guid.NewGuid());
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void AddingOrRemovingADocument_RaisesTheUpdateEvent_SoTheCachedPageIsEvicted()
    {
        var p = NewProduct();
        p.ClearDomainEvents();
        var id = Guid.NewGuid();
        p.AddDocument(id, DocumentKind.Datasheet, "a.pdf", DetectedFileType.Pdf, 10, Hash(1), "k/1.pdf", null);
        p.DomainEvents.Should().ContainSingle(e => e is ProductUpdatedEvent);

        p.ClearDomainEvents();
        p.RemoveDocument(id);
        p.DomainEvents.Should().ContainSingle(e => e is ProductUpdatedEvent);
    }
}
