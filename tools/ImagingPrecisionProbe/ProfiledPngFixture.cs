using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

// Deliberately a diagnostic fixture adapter, not a production PNG-input migration.
// Preserve original ICC bytes and straight RGBA8 codes, including partial alpha.
internal static class ProfiledPngFixture
{
    internal sealed record Decoded(LinearWorkingFrame Frame, string Description, string ProfileHash, string PixelHash);

    internal static Decoded Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length > 16 * 1024 * 1024 || bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature)) throw new InvalidDataException("Expected a bounded PNG fixture.");
        if (Encoding.ASCII.GetString(bytes, 12, 4) != "IHDR" || bytes[24] != 8 || bytes[25] != 6) throw new NotSupportedException("PNG fixture requires RGBA8.");
        byte[]? icc = null;
        for (var at = 8; at <= bytes.Length - 12;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
            if (length > bytes.Length - at - 12) throw new InvalidDataException("Truncated PNG chunk.");
            var count = (int)length;
            var kind = Encoding.ASCII.GetString(bytes, at + 4, 4);
            if (kind == "acTL") throw new NotSupportedException("Animated fixtures are outside this test adapter.");
            if (kind == "iCCP")
            {
                if (icc is not null || Crc(bytes.AsSpan(at + 4, count + 4)) != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8 + count, 4))) throw new InvalidDataException("Duplicate or invalid ICC PNG chunk.");
                var zero = Array.IndexOf(bytes, (byte)0, at + 8, count);
                if (zero <= at + 8 || zero > at + 8 + 79 || zero + 2 >= at + 8 + count || bytes[zero + 1] != 0) throw new InvalidDataException("Invalid PNG ICC header.");
                using var compressed = new MemoryStream(bytes, zero + 2, at + 8 + count - zero - 2);
                using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                using var expanded = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = zlib.Read(buffer)) != 0)
                {
                    if (expanded.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("ICC fixture too large.");
                    expanded.Write(buffer, 0, read);
                }
                icc = expanded.ToArray();
            }
            if (kind == "IEND") break;
            at += count + 12;
        }
        if (icc is null) throw new InvalidDataException("Fixture has no embedded ICC; no fallback.");
        var transform = new MatrixRgbProfile(icc, 8, true);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("PNG fixture decode failed.");
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || codec.Info.Width > 4096 || codec.Info.Height > 4096 || codec.EncodedOrigin != SKEncodedOrigin.TopLeft) throw new NotSupportedException("Unsupported fixture dimensions/orientation.");
        using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("Incomplete PNG fixture.");
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(bitmap.GetPixels() + y * bitmap.RowBytes, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
        var frame = LinearWorkingFrame.Create(bitmap.Width, bitmap.Height, WorkingImageDomain.RenderedLinear, (y, row) =>
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var at = (y * bitmap.Width + x) * 4;
                transform.WritePixel(pixels[at], pixels[at + 1], pixels[at + 2], row, x * 4);
                var alpha = pixels[at + 3] / 255f;
                for (var c = 0; c < 3; c++) row[x * 4 + c] *= alpha;
                row[x * 4 + 3] = alpha;
            }
        });
        try
        {
            var reference = new ProfiledJpegChecks.IccReference(icc);
            var row = new float[bitmap.Width * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                frame.ReadLinearRow(y, row);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var at = (y * bitmap.Width + x) * 4;
                    var alpha = pixels[at + 3] / 255f;
                    if (row[x * 4 + 3] != (float)(Half)alpha) throw new InvalidOperationException("Fixture alpha changed.");
                    var expected = alpha == 0 ? new double[3] : reference.Convert(pixels[at], pixels[at + 1], pixels[at + 2]).Select(v => v * alpha).ToArray();
                    for (var c = 0; c < 3; c++) if (Math.Abs(row[x * 4 + c] - expected[c]) > Math.Max(1.2e-7, Math.Abs(expected[c]) * .0006)) throw new InvalidOperationException("PNG fixture ICC/alpha reference mismatch.");
                }
            }
            return new Decoded(frame, MatrixRgbProfile.DescriptionOf(icc), Convert.ToHexString(SHA256.HashData(icc)), Convert.ToHexString(SHA256.HashData(pixels)));
        }
        catch { frame.Dispose(); throw; }
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
}
