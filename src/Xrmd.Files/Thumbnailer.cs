using SkiaSharp;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Files;

/// <summary>
/// An image value that cannot be made a thumbnail: not an image, a type an image column does not store, or a corrupt one.
/// The Core turns it into a 400 <see cref="ErrorCodes.ProcessImageFailure"/> naming the record and the column.
/// </summary>
public sealed class ImageProcessingException(string reason) : Exception(reason)
{
    /// <summary>Always <see cref="ErrorCodes.ProcessImageFailure"/> (0x80072553).</summary>
    public int ErrorCode => ErrorCodes.ProcessImageFailure;
}

/// <summary>
/// The thumbnail of an image column (ruling F3, Learn "Resize rules for thumbnail-sized images"), made with SkiaSharp:
/// <list type="bullet">
/// <item>when either side is larger than 144 pixels, the centre square on the shorter side, resized to 144x144;</item>
/// <item>when both sides are at most 144, the centre square on the shorter side, not resized.</item>
/// </list>
/// The thumbnail is JPEG when the image is JPEG, PNG otherwise. Image columns store GIF, JPEG, BMP and PNG (Learn "Image file
/// types"): anything else, and anything that does not decode completely, is refused, never stored as is.
/// </summary>
public static class Thumbnailer
{
    /// <summary>
    /// The largest image decoded, in pixels once decoded (after the JPEG decoder's down-sampling): a server-side bound (not a
    /// Dataverse rule) that keeps a small compressed file from expanding to gigabytes of pixels. 64 megapixels is 256 MB of RGBA.
    /// </summary>
    public const long MaxPixels = 64L * 1024 * 1024;

    private const int JpegQuality = 90;

    /// <summary>At most one decode per processor at a time: with <see cref="MaxPixels"/>, it bounds the decoding memory.</summary>
    private static readonly SemaphoreSlim Decodes = new(Environment.ProcessorCount, Environment.ProcessorCount);

    /// <summary>The thumbnail of the image in <paramref name="input"/>, and its MIME type (<c>image/jpeg</c> or <c>image/png</c>).</summary>
    /// <exception cref="ImageProcessingException">The data is not a complete GIF, JPEG, BMP or PNG image, or is over <see cref="MaxPixels"/>.</exception>
    public static (byte[] Bytes, string MimeType) Create(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input is MemoryStream { Position: 0 } memory && memory.TryGetBuffer(out var buffer) && buffer.Offset == 0 && buffer.Count == buffer.Array!.Length)
            return Create(buffer.Array);
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        return Create(copy.ToArray());
    }

    /// <summary>
    /// <see cref="Create(byte[])"/> for an asynchronous caller (the Core's Create and Update): the wait for a decode slot does not
    /// block a thread; the decode itself runs on the calling thread, as it is CPU work.
    /// </summary>
    /// <inheritdoc cref="Create(Stream)"/>
    public static async Task<(byte[] Bytes, string MimeType)> CreateAsync(byte[] data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        await Decodes.WaitAsync(ct);
        try
        {
            return CreateThumbnail(data);
        }
        finally
        {
            Decodes.Release();
        }
    }

    /// <inheritdoc cref="Create(Stream)"/>
    public static (byte[] Bytes, string MimeType) Create(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Decodes.Wait();
        try
        {
            return CreateThumbnail(data);
        }
        finally
        {
            Decodes.Release();
        }
    }

    private static (byte[] Bytes, string MimeType) CreateThumbnail(byte[] data)
    {
        using var codec = Codec(data);
        var format = codec.EncodedFormat;
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0) throw new ImageProcessingException("The image has no pixels.");
        // The thumbnail's geometry is set by the image's own size (F3); the decode may be smaller than it.
        var target = width > FileColumns.ThumbnailSize || height > FileColumns.ThumbnailSize ? FileColumns.ThumbnailSize : Math.Min(width, height);

        using var bitmap = Decode(codec, target);
        var side = Math.Min(bitmap.Width, bitmap.Height);
        var source = SKRectI.Create((bitmap.Width - side) / 2, (bitmap.Height - side) / 2, side, side);

        // EXIF orientation is not applied: Learn's resize rules say nothing about it, so the pixels are taken as stored.
        var jpeg = format == SKEncodedImageFormat.Jpeg;
        var info = new SKImageInfo(target, target, SKColorType.Rgba8888, jpeg ? SKAlphaType.Opaque : SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
                            ?? throw new InvalidOperationException($"SkiaSharp could not create a {target}x{target} raster surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        // An image over the bitmap's own pixels (no copy of the decoded image): the bitmap outlives it.
        using (var pixels = bitmap.PeekPixels())
        using (var image = SKImage.FromPixels(pixels))
            surface.Canvas.DrawImage(image, source, SKRect.Create(target, target), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, JpegQuality)
                            ?? throw new InvalidOperationException("SkiaSharp could not encode the thumbnail.");
        return (encoded.ToArray(), jpeg ? "image/jpeg" : "image/png");
    }

    /// <summary>The MIME type of a GIF, JPEG, BMP or PNG image (from its content, not decoded).</summary>
    /// <exception cref="ImageProcessingException">The data is not one of those types.</exception>
    public static string MimeTypeOf(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var codec = Codec(data);
        return MimeType(codec.EncodedFormat);
    }

    private static SKCodec Codec(byte[] data)
    {
        if (data.Length == 0) throw new ImageProcessingException("The image is empty.");
        var codec = SKCodec.Create(new SKMemoryStream(data))
                    ?? throw new ImageProcessingException("The data is not an image of a type that image columns store (GIF, JPEG, BMP or PNG).");
        try
        {
            MimeType(codec.EncodedFormat);
            return codec;
        }
        catch
        {
            codec.Dispose();
            throw;
        }
    }

    private static string MimeType(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Gif => "image/gif",
        SKEncodedImageFormat.Jpeg => "image/jpeg",
        SKEncodedImageFormat.Bmp => "image/bmp",
        SKEncodedImageFormat.Png => "image/png",
        _ => throw new ImageProcessingException(
            $"The image is of type {format}; image columns store GIF, JPEG, BMP and PNG images only."),
    };

    /// <summary>
    /// The first frame of the image, decoded completely (an incomplete or corrupt image is refused). A codec that can scale while
    /// decoding (JPEG: 1/2, 1/4, 1/8) decodes at the smallest size whose shorter side still covers <paramref name="target"/>; the
    /// others decode at full size. The decoded size must be within <see cref="MaxPixels"/>.
    /// </summary>
    private static SKBitmap Decode(SKCodec codec, int target)
    {
        var size = codec.Info.Size;
        foreach (var scale in (float[])[0.125f, 0.25f, 0.5f])
        {
            var scaled = codec.GetScaledDimensions(scale);
            if (Math.Min(scaled.Width, scaled.Height) >= target && scaled.Width < size.Width)
            {
                size = scaled;
                break;
            }
        }
        if ((long)size.Width * size.Height > MaxPixels)
            throw new ImageProcessingException(
                $"The image is {codec.Info.Width}x{codec.Info.Height} pixels; this server decodes images of at most {MaxPixels} pixels.");

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new ImageProcessingException($"The image could not be decoded ({result}).");
        }
        return bitmap;
    }
}
