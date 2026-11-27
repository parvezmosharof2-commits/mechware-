using Mechware.Domain.Common.Exceptions;
using SkiaSharp;

namespace Mechware.Infrastructure.Orders;

/// <summary>A re-encoded photo: a baseline JPEG and its pixel size.</summary>
public sealed record ReencodedPhoto(byte[] Jpeg, int Width, int Height);

/// <summary>
/// F2.2: the upload is decoded (SkiaSharp, already used for the PDFs) and only its pixels are drawn into a new picture, which is encoded
/// as a fresh JPEG. Nothing of the original file travels: no EXIF (camera, time, GPS), no XMP/IPTC, no ICC profile, no comments, no
/// trailing bytes. The EXIF orientation is applied to the pixels first, so a portrait phone photo stays upright without its tag;
/// transparency is flattened onto white; the longest edge is cut to <see cref="MaxEdge"/>. A picture larger than
/// <see cref="MaxPixels"/> is refused before its pixels are decoded (a small file can claim a huge canvas).
/// </summary>
public static class ReturnPhotoImage
{
    public const int MaxEdge = 2560;
    public const long MaxPixels = 50_000_000;
    public const int MaxSide = 16_384;
    public const int Quality = 85;

    public static ReencodedPhoto Reencode(byte[] upload)
    {
        using var encoded = SKData.CreateCopy(upload);
        using var codec = SKCodec.Create(encoded) ?? throw Unreadable();
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) throw Unreadable();
        if (info.Width > MaxSide || info.Height > MaxSide || (long)info.Width * info.Height > MaxPixels)
            throw new ValidationException("file", $"The photo is too large; at most {MaxPixels / 1_000_000} megapixels.");

        // Decode at the smallest size the codec offers that is still about the target (JPEG scales by eighths while decoding).
        float wanted = Math.Min(1f, MaxEdge / (float)Math.Max(info.Width, info.Height));
        var size = codec.GetScaledDimensions(wanted);
        if (size.Width <= 0 || size.Height <= 0) size = info.Size;
        var decodeInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        if (codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success) throw Unreadable();

        var origin = codec.EncodedOrigin;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = decoded.Width, h = decoded.Height;
        int orientedW = swap ? h : w, orientedH = swap ? w : h;
        double scale = Math.Min(1d, MaxEdge / (double)Math.Max(orientedW, orientedH));
        int outW = Math.Max(1, (int)Math.Round(orientedW * scale)), outH = Math.Max(1, (int)Math.Round(orientedH * scale));

        using var output = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(outW / (float)orientedW, outH / (float)orientedH);
            Orient(canvas, origin, w, h);
            using var image = SKImage.FromBitmap(decoded);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        using var data = output.Encode(SKEncodedImageFormat.Jpeg, Quality) ?? throw Unreadable();
        return new ReencodedPhoto(data.ToArray(), outW, outH);
    }

    /// <summary>Maps the stored pixels (w × h) to the upright picture the EXIF orientation describes.</summary>
    private static void Orient(SKCanvas canvas, SKEncodedOrigin origin, int w, int h)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.Scale(-1, 1); canvas.RotateDegrees(90); break;   // (x, y) → (y, x)
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.Scale(1, -1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(270); break;
        }
    }

    private static ValidationException Unreadable() => new("file", "The photo could not be read; it may be damaged or cut short.");
}
