using System.Globalization;
using System.Text;
using FluentAssertions;
using SkiaSharp;
using Xrmd.Core.Errors;
using Xrmd.Files;

namespace Xrmd.Files.Tests;

/// <summary>
/// The thumbnail rules of an image column (ruling F3, Learn "Resize rules for thumbnail-sized images"). The fixtures are made
/// here with SkiaSharp: no binary file is committed.
/// </summary>
public sealed class ThumbnailerTests
{
    /// <summary>An image of <paramref name="width"/> x <paramref name="height"/>: four coloured quadrants, a white centre dot.</summary>
    internal static byte[] Image(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            using var paint = new SKPaint();
            paint.Color = SKColors.Blue;
            canvas.DrawRect(width / 2f, 0, width / 2f, height / 2f, paint);
            paint.Color = SKColors.Green;
            canvas.DrawRect(0, height / 2f, width / 2f, height / 2f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        if (data is not null) return data.ToArray();
        // Skia has no GIF nor BMP encoder: minimal one-colour files of the size asked for.
        return format switch
        {
            SKEncodedImageFormat.Gif => Gif(width, height),
            SKEncodedImageFormat.Bmp => Bmp(width, height),
            _ => throw new InvalidOperationException($"No encoder for {format}."),
        };
    }

    /// <summary>A 24-bit uncompressed BMP, every pixel red.</summary>
    private static byte[] Bmp(int width, int height)
    {
        var stride = (width * 3 + 3) / 4 * 4;
        var size = 54 + stride * height;
        var bytes = new byte[size];
        "BM"u8.CopyTo(bytes);
        BitConverter.GetBytes(size).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bytes[54 + y * stride + x * 3 + 2] = 0xFF; // B, G, R
        return bytes;
    }

    /// <summary>A valid GIF of one red colour (LZW minimum code size 2, every pixel colour 0).</summary>
    private static byte[] Gif(int width, int height)
    {
        var bytes = new List<byte>();
        bytes.AddRange("GIF87a"u8.ToArray());
        bytes.AddRange([(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x80, 0, 0]);
        bytes.AddRange([0xFF, 0, 0, 0, 0, 0]); // global colour table: red, black
        bytes.AddRange([0x2C, 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0]);
        bytes.Add(2); // LZW minimum code size
        // Codes of 3 bits: a clear code (4) before every 2 pixels keeps the code size at 3, then the end code (5).
        var codes = new List<int>();
        for (var i = 0; i < width * height; i++)
        {
            if (i % 2 == 0) codes.Add(4);
            codes.Add(0);
        }
        codes.Add(5);
        var data = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var code in codes)
        {
            buffer |= code << bits;
            bits += 3;
            while (bits >= 8)
            {
                data.Add((byte)buffer);
                buffer >>= 8;
                bits -= 8;
            }
        }
        if (bits > 0) data.Add((byte)buffer);
        for (var i = 0; i < data.Count; i += 255)
        {
            var count = Math.Min(255, data.Count - i);
            bytes.Add((byte)count);
            bytes.AddRange(data.GetRange(i, count));
        }
        bytes.Add(0);
        bytes.Add(0x3B);
        return [.. bytes];
    }

    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("not decodable");

    [Fact]
    public void _300x428_becomes_144x144_center_crop()
    {
        var (bytes, mimeType) = Thumbnailer.Create(new MemoryStream(Image(300, 428, SKEncodedImageFormat.Png)));

        mimeType.Should().Be("image/png");
        using var thumbnail = Decode(bytes);
        thumbnail.Width.Should().Be(144);
        thumbnail.Height.Should().Be(144);
        // The centre square (300x300 from y = 64) scaled down: the quadrant boundaries stay at the centre of the thumbnail.
        thumbnail.GetPixel(20, 20).Should().Be(SKColors.Red);
        thumbnail.GetPixel(124, 20).Should().Be(SKColors.Blue);
        thumbnail.GetPixel(20, 124).Should().Be(SKColors.Green);
        thumbnail.GetPixel(124, 124).Should().Be(SKColors.Red);
    }

    [Fact]
    public async Task CreateAsync_makes_the_same_thumbnail_and_honours_cancellation()
    {
        var image = Image(300, 428, SKEncodedImageFormat.Png);
        var (bytes, mimeType) = await Thumbnailer.CreateAsync(image, default);
        (bytes, mimeType).Should().BeEquivalentTo(Thumbnailer.Create(image));
        await ((Func<Task>)(() => Thumbnailer.CreateAsync(image, new CancellationToken(canceled: true)))).Should()
            .ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => Thumbnailer.CreateAsync("not an image"u8.ToArray(), default))).Should().ThrowAsync<ImageProcessingException>();
    }

    [Fact]
    public void _91x130_becomes_91x91()
    {
        var (bytes, _) = Thumbnailer.Create(new MemoryStream(Image(91, 130, SKEncodedImageFormat.Png)));

        using var thumbnail = Decode(bytes);
        thumbnail.Width.Should().Be(91);
        thumbnail.Height.Should().Be(91);
        // Cropped on the centre (rows 19..109), not scaled: the green half starts at row 65 - 19 = 46.
        thumbnail.GetPixel(5, 44).Should().Be(SKColors.Red);
        thumbnail.GetPixel(5, 47).Should().Be(SKColors.Green);
    }

    [Fact]
    public void Wide_image_over_144_on_one_side_only_is_cropped_square_and_scaled_to_144()
    {
        var (bytes, _) = Thumbnailer.Create(new MemoryStream(Image(400, 100, SKEncodedImageFormat.Png)));

        using var thumbnail = Decode(bytes);
        (thumbnail.Width, thumbnail.Height).Should().Be((144, 144));
    }

    [Fact]
    public void jpeg_stays_jpeg_png_stays_png_gif_becomes_png()
    {
        Thumbnailer.Create(new MemoryStream(Image(200, 200, SKEncodedImageFormat.Jpeg))).MimeType.Should().Be("image/jpeg");
        Thumbnailer.Create(new MemoryStream(Image(200, 200, SKEncodedImageFormat.Png))).MimeType.Should().Be("image/png");
        var (gif, gifType) = Thumbnailer.Create(new MemoryStream(Image(50, 30, SKEncodedImageFormat.Gif)));
        gifType.Should().Be("image/png");
        using var codec = SKCodec.Create(new MemoryStream(gif));
        codec.EncodedFormat.Should().Be(SKEncodedImageFormat.Png);
        (codec.Info.Width, codec.Info.Height).Should().Be((30, 30));

        var (jpeg, _) = Thumbnailer.Create(new MemoryStream(Image(200, 200, SKEncodedImageFormat.Jpeg)));
        jpeg[..3].Should().Equal([0xFF, 0xD8, 0xFF], "the JPEG magic number");
    }

    [Fact]
    public void Bmp_is_an_image_type_and_its_thumbnail_is_png()
    {
        Thumbnailer.Create(new MemoryStream(Image(20, 10, SKEncodedImageFormat.Bmp))).MimeType.Should().Be("image/png");
    }

    [Fact]
    public void Non_image_bytes_fail_with_ProcessImageFailure()
    {
        var act = () => Thumbnailer.Create(new MemoryStream("this is not an image"u8.ToArray()));

        act.Should().Throw<ImageProcessingException>().Which.ErrorCode.Should().Be(ErrorCodes.ProcessImageFailure);
        ((Action)(() => Thumbnailer.Create(new MemoryStream([])))).Should().Throw<ImageProcessingException>();
    }

    [Fact]
    public void Decodable_types_outside_gif_jpeg_bmp_png_fail_with_ProcessImageFailure()
    {
        var webp = Image(40, 40, SKEncodedImageFormat.Webp);

        ((Action)(() => Thumbnailer.Create(new MemoryStream(webp)))).Should().Throw<ImageProcessingException>()
            .Which.Message.Should().Contain("Webp");
    }

    [Fact]
    public void Truncated_image_fails_with_ProcessImageFailure()
    {
        var png = Image(200, 200, SKEncodedImageFormat.Png);

        ((Action)(() => Thumbnailer.Create(new MemoryStream(png[..40])))).Should().Throw<ImageProcessingException>();
    }

    /// <summary>A PNG whose header announces <paramref name="width"/> x <paramref name="height"/>, with one empty IDAT chunk.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new List<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            bytes.AddRange(BigEndian(data.Length));
            bytes.AddRange(body);
            bytes.AddRange(BigEndian((int)Crc32(body)));
        }
        Chunk("IHDR", [.. BigEndian(width), .. BigEndian(height), 8, 6, 0, 0, 0]); // 8-bit RGBA
        Chunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        Chunk("IEND", []);
        return [.. bytes];
    }

    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void Image_over_the_pixel_bound_is_refused_before_decoding()
    {
        var bomb = PngHeader(50000, 50000);
        bomb.Length.Should().BeLessThan(100);

        var refusal = ((Action)(() => Thumbnailer.Create(new MemoryStream(bomb)))).Should().Throw<ImageProcessingException>().Which;

        refusal.ErrorCode.Should().Be(ErrorCodes.ProcessImageFailure);
        refusal.Message.Should().Contain("50000x50000").And.Contain(Thumbnailer.MaxPixels.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Large_jpeg_is_decoded_down_sampled_to_the_thumbnail_geometry()
    {
        // 8200x8200 is over the pixel bound at full size; the JPEG decoder scales down while decoding, so it is accepted.
        ((long)8200 * 8200).Should().BeGreaterThan(Thumbnailer.MaxPixels);
        var jpeg = Image(8200, 8200, SKEncodedImageFormat.Jpeg);

        var (bytes, mimeType) = Thumbnailer.Create(new MemoryStream(jpeg));

        mimeType.Should().Be("image/jpeg");
        using var thumbnail = Decode(bytes);
        (thumbnail.Width, thumbnail.Height).Should().Be((144, 144));
    }

    [Fact]
    public void MimeTypeOf_names_the_source_format()
    {
        Thumbnailer.MimeTypeOf(Image(10, 10, SKEncodedImageFormat.Jpeg)).Should().Be("image/jpeg");
        Thumbnailer.MimeTypeOf(Image(10, 10, SKEncodedImageFormat.Gif)).Should().Be("image/gif");
        Thumbnailer.MimeTypeOf(Image(10, 10, SKEncodedImageFormat.Bmp)).Should().Be("image/bmp");
        Thumbnailer.MimeTypeOf(Image(10, 10, SKEncodedImageFormat.Png)).Should().Be("image/png");
    }
}
