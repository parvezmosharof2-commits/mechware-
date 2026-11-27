using System.Text;
using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Returns;
using Mechware.Infrastructure.Orders;
using SkiaSharp;

namespace Mechware.UnitTests.Orders;

/// <summary>F2.2: the type from the first bytes, the server's key, the status rules, and the re-encoding that drops every metadata block.</summary>
public sealed class ReturnPhotoTests
{
    private static byte[] Encode(SKEncodedImageFormat format, int w, int h)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, 8, 8, red);   // top-left corner marks the orientation
        }
        using var data = bitmap.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>A JPEG with an EXIF APP1 (orientation <paramref name="orientation"/>, an ImageDescription carrying a GPS-like text).</summary>
    private static byte[] WithExif(byte[] jpeg, ushort orientation)
    {
        var t = new List<byte>();
        void U16(int v) { t.Add((byte)(v >> 8)); t.Add((byte)v); }
        void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
        byte[] text = Encoding.ASCII.GetBytes("GPS 23.8103N 90.4125E\0");
        t.AddRange("MM"u8.ToArray()); U16(42); U32(8);
        U16(2);
        U16(0x010E); U16(2); U32(text.Length); U32(8 + 2 + 24 + 4);       // ImageDescription → data after the IFD
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);
        U32(0);
        t.AddRange(text);
        byte[] payload = [.. "Exif\0\0"u8.ToArray(), .. t];
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload, .. jpeg[2..]];
    }

    [Fact]
    public void Detect_ReadsOnlyTheFirstBytes()
    {
        ReturnPhoto.Detect([0xFF, 0xD8, 0xFF, 0xE0]).Should().Be(UploadedImageType.Jpeg);
        ReturnPhoto.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]).Should().Be(UploadedImageType.Png);
        ReturnPhoto.Detect("RIFF\x10\0\0\0WEBP"u8).Should().Be(UploadedImageType.WebP);
        ReturnPhoto.Detect("RIFF\x10\0\0\0WAVE"u8).Should().Be(UploadedImageType.Unknown);
        ReturnPhoto.Detect("%PDF-1.7 ...."u8).Should().Be(UploadedImageType.Unknown);
        ReturnPhoto.Detect("GIF89a......"u8).Should().Be(UploadedImageType.Unknown);
        ReturnPhoto.Detect([0xFF, 0xD8]).Should().Be(UploadedImageType.Unknown);
    }

    [Fact]
    public void TheKey_IsBuiltFromTheReturnAndServerRandomness_AndNothingElse()
    {
        var id = Guid.NewGuid();
        var random = new string('a', 32);
        ReturnPhoto.KeyFor(id, random).Should().Be($"uploads/rma/{id:N}/{random}.jpg").And.MatchRegex(ReturnPhoto.KeyPattern().ToString());
        foreach (var bad in new[] { "../../etc/passwd", "ABCDEF0123456789ABCDEF0123456789", new string('a', 31), "evil.php" + new string('0', 24) })
            FluentActions.Invoking(() => ReturnPhoto.KeyFor(id, bad)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new ReturnPhoto(Guid.NewGuid(), id, 1, ReturnPhoto.KeyFor(Guid.NewGuid(), random), 10, 1, 1, new string('0', 64), Guid.NewGuid(), DateTime.UtcNow))
            .Should().Throw<ValidationException>("a key of another return");
        FluentActions.Invoking(() => new ReturnPhoto(Guid.NewGuid(), id, 6, ReturnPhoto.KeyFor(id, random), 10, 1, 1, new string('0', 64), Guid.NewGuid(), DateTime.UtcNow))
            .Should().Throw<ValidationException>("slot 6");
    }

    [Theory]
    [InlineData(ReturnStatus.Requested, true, true)]
    [InlineData(ReturnStatus.Approved, true, false)]
    [InlineData(ReturnStatus.Received, false, false)]
    [InlineData(ReturnStatus.Inspected, false, false)]
    [InlineData(ReturnStatus.Refunded, false, false)]
    [InlineData(ReturnStatus.CreditNoted, false, false)]
    [InlineData(ReturnStatus.Rejected, false, false)]
    [InlineData(ReturnStatus.Withdrawn, false, false)]
    public void PhotosAreAddedUntilReceived_AndRemovedOnlyBeforeTheDecision(ReturnStatus status, bool add, bool remove) =>
        (ReturnPhoto.CanAdd(status), ReturnPhoto.CanRemove(status)).Should().Be((add, remove));

    [Fact]
    public void Reencode_DropsTheExif_AndAppliesTheOrientationToThePixels()
    {
        var original = WithExif(Encode(SKEncodedImageFormat.Jpeg, 64, 48), orientation: 6);   // stored landscape, shown rotated 90° clockwise
        Encoding.ASCII.GetString(original).Should().Contain("Exif").And.Contain("23.8103N");

        var photo = ReturnPhotoImage.Reencode(original);

        (photo.Width, photo.Height).Should().Be((48, 64));
        var text = Encoding.ASCII.GetString(photo.Jpeg);
        text.Should().NotContain("Exif").And.NotContain("23.8103N");
        ReturnPhoto.Detect(photo.Jpeg).Should().Be(UploadedImageType.Jpeg);
        using var decoded = SKBitmap.Decode(photo.Jpeg);
        // The red corner was top-left in the stored pixels; after a 90° clockwise turn it is top-right.
        decoded.GetPixel(44, 3).Red.Should().BeGreaterThan(200);
        decoded.GetPixel(3, 3).Red.Should().BeLessThan(60);
    }

    [Fact]
    public void Reencode_FlattensPngAndWebp_ShrinksTheLongestEdge_AndRefusesAHugeCanvasBeforeDecoding()
    {
        ReturnPhoto.Detect(ReturnPhotoImage.Reencode(Encode(SKEncodedImageFormat.Png, 30, 20)).Jpeg).Should().Be(UploadedImageType.Jpeg);
        ReturnPhoto.Detect(ReturnPhotoImage.Reencode(Encode(SKEncodedImageFormat.Webp, 30, 20)).Jpeg).Should().Be(UploadedImageType.Jpeg);

        var big = ReturnPhotoImage.Reencode(Encode(SKEncodedImageFormat.Png, 3000, 1500));
        (big.Width, big.Height).Should().Be((ReturnPhotoImage.MaxEdge, ReturnPhotoImage.MaxEdge / 2));

        // A PNG header claiming 20000 × 20000 (400 MP): refused from the header alone.
        var png = Encode(SKEncodedImageFormat.Png, 4, 4);
        png[16] = 0; png[17] = 0; png[18] = 0x4E; png[19] = 0x20; png[20] = 0; png[21] = 0; png[22] = 0x4E; png[23] = 0x20;
        uint crc = Crc32(png.AsSpan(12, 17));   // "IHDR" + its 13 bytes, so the header is valid and only its size is absurd
        (png[29], png[30], png[31], png[32]) = ((byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc);
        FluentActions.Invoking(() => ReturnPhotoImage.Reencode(png)).Should().Throw<ValidationException>().WithMessage("*megapixels*");
        FluentActions.Invoking(() => ReturnPhotoImage.Reencode([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3])).Should().Throw<ValidationException>();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }
}
