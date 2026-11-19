using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using BitMiracle.LibTiff.Classic;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class ProfiledTiffChecks
{
    private static readonly ushort[] Samples = [1, 1, 1, 129, 129, 129, 130, 130, 130, 65535, 0, 0, 0, 65535, 0, 0, 0, 65535];
    private static readonly int[][] Orientations = [[0, 1, 2, 3, 4, 5], [2, 1, 0, 5, 4, 3], [5, 4, 3, 2, 1, 0], [3, 4, 5, 0, 1, 2], [0, 3, 1, 4, 2, 5], [3, 0, 4, 1, 5, 2], [5, 2, 4, 1, 3, 0], [2, 5, 1, 4, 0, 3]];

    internal static void Run(ProbeReport report, string output)
    {
        var folder = Path.Combine(output, "tiff16-fixtures");
        Directory.CreateDirectory(folder);
        Check(report, "precision, ICC and byte order", () => Precision(folder));
        Check(report, "eight orientations and strict layouts", () => Layouts(folder));
        Check(report, "tabulated ICC curves and guards", () => Tables(folder));
    }

    private static void Check(ProbeReport report, string name, Func<string> action)
    {
        try { report.Checks.Add(new ProbeCheck("Profiled TIFF / " + name, "PASS", action())); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Profiled TIFF / " + name, ex.ToString())); }
    }

    // The written RGB16 words are the oracle, independent of the file reader. Include
    // two low codes that would collapse to the SAME value through an RGB8 staging image.
    private static string Precision(string folder)
    {
        var profile = ProfiledJpegChecks.TestProfile();
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(276, 2), 256); // Linear ProPhoto-like primaries.
        foreach (var (mode, compression) in new[] { ("wl", Compression.NONE), ("wb", Compression.LZW), ("wl", Compression.ADOBE_DEFLATE) })
        {
            var path = Path.Combine(folder, $"precision-{mode}-{compression}.tif");
            Write(path, profile, mode: mode, compression: compression);
            using var image = ProfiledTiffInput.Decode(path);
            Validate(image.Frame, Samples, 3, 2, profile, 1);
            var row = new float[12];
            image.Frame.ReadLinearRow(0, row);
            if (row[0] <= 0 || row[0] >= Math.Pow(2, -14) || row[4] == row[8])
                throw new InvalidOperationException("Subnormal shadow or distinct 16-bit source codes lost.");
        }
        return "Known RGB16 codes agree with independent ICC reference in little/big-endian files and uncompressed/LZW/Deflate strips. One-code shadow remains subnormal; codes 129/130 remain distinct; signed/above-one ProPhoto primaries preserved.";
    }

    private static string Layouts(string folder)
    {
        var profile = ProfiledJpegChecks.TestProfile();
        var path = Path.Combine(folder, "layout.tif");
        for (var orientation = 1; orientation <= 8; orientation++)
        {
            Write(path, profile, orientation: orientation);
            using var image = ProfiledTiffInput.Decode(path);
            Validate(image.Frame, Samples, 3, 2, profile, orientation);
        }
        Write(path, null); Reject(() => { using var image = ProfiledTiffInput.Decode(path); });
        Write(path, profile, bits: 8); Reject(() => { using var image = ProfiledTiffInput.Decode(path); });
        Write(path, profile, planar: PlanarConfig.SEPARATE); Reject(() => { using var image = ProfiledTiffInput.Decode(path); });
        Write(path, profile, pages: 2); Reject(() => { using var image = ProfiledTiffInput.Decode(path); });
        Write(path, profile, samples: 4); Reject(() => { using var image = ProfiledTiffInput.Decode(path); });
        Write(path, profile, sampleFormat: SampleFormat.INT); Reject(() => { using var image = ProfiledTiffInput.Decode(path); });
        Write(path, profile, photometric: Photometric.MINISBLACK); Reject(() => { using var image = ProfiledTiffInput.Decode(path); });
        Write(path, profile, tiled: true); Reject(() => { using var image = ProfiledTiffInput.Decode(path); });
        return "All eight orientations match independent permutations with swapped dimensions for 5..8. Missing ICC, RGB8, signed samples, extra channels, grayscale, tiles, separate planes and multiple pages are rejected; no legacy 8-bit fallback.";
    }

    private static string Tables(string folder)
    {
        var profile = TableProfile();
        var path = Path.Combine(folder, "table.tif");
        Write(path, profile);
        using (var image = ProfiledTiffInput.Decode(path)) Validate(image.Frame, Samples, 3, 2, profile, 1);
        var truncated = profile.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(truncated.AsSpan(272, 4), 65536);
        Reject(() => _ = new MatrixRgbProfile(truncated, 16, true));
        var reversed = profile.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(reversed.AsSpan(280, 2), 1);
        Reject(() => _ = new MatrixRgbProfile(reversed, 16, true));
        Reject(() => _ = new MatrixRgbProfile(profile, 8)); // JPEG's narrower contract is unchanged.
        return "Tabulated TRCs use exact ICC linear interpolation at each 16-bit source code; malformed/truncated and non-monotonic tables rejected. JPEG analytic-only policy unchanged.";
    }

    private static byte[] TableProfile()
    {
        var profile = new byte[284];
        ProfiledJpegChecks.TestProfile().CopyTo(profile, 0);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(0, 4), (uint)profile.Length);
        for (var c = 0; c < 3; c++) BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(176 + c * 12, 4), 20);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(272, 4), 4);
        ushort[] values = [0, 2048, 16384, 65535];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(276 + i * 2, 2), values[i]);
        return profile;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidOperationException("Unsupported TIFF/profile accepted.");
    }

    private static void Write(string path, byte[]? profile, string mode = "wl", Compression compression = Compression.NONE,
        int orientation = 1, int bits = 16, PlanarConfig planar = PlanarConfig.CONTIG, int pages = 1,
        int samples = 3, SampleFormat sampleFormat = SampleFormat.UINT, Photometric photometric = Photometric.RGB, bool tiled = false)
    {
        using var tif = Tiff.Open(path, mode) ?? throw new InvalidOperationException("Cannot write TIFF fixture.");
        for (var page = 0; page < pages; page++)
        {
            tif.SetField(TiffTag.IMAGEWIDTH, 3); tif.SetField(TiffTag.IMAGELENGTH, 2);
            tif.SetField(TiffTag.SAMPLESPERPIXEL, samples); tif.SetField(TiffTag.BITSPERSAMPLE, bits);
            tif.SetField(TiffTag.SAMPLEFORMAT, sampleFormat); tif.SetField(TiffTag.PHOTOMETRIC, photometric);
            tif.SetField(TiffTag.PLANARCONFIG, planar); tif.SetField(TiffTag.ORIENTATION, orientation);
            tif.SetField(TiffTag.COMPRESSION, compression); tif.SetField(TiffTag.ROWSPERSTRIP, 1);
            if (profile is not null) tif.SetField(TiffTag.ICCPROFILE, profile.Length, profile);
            if (tiled)
            {
                tif.SetField(TiffTag.TILEWIDTH, 16);
                tif.SetField(TiffTag.TILELENGTH, 16);
                var tile = new byte[tif.TileSize()];
                if (tif.WriteEncodedTile(0, tile, tile.Length) < 0) throw new InvalidOperationException("TIFF tile fixture failed.");
                if (!tif.WriteDirectory()) throw new InvalidOperationException("TIFF tile directory failed.");
                continue;
            }
            var row = new byte[tif.ScanlineSize()];
            for (var y = 0; y < 2; y++)
            {
                // Unsupported-layout fixtures only need readable headers; supported layouts
                // always receive the exact host-endian reference words for libtiff to encode.
                if (bits == 16 && planar == PlanarConfig.CONTIG && samples == 3) Buffer.BlockCopy(Samples, y * 18, row, 0, 18);
                if (!tif.WriteScanline(row, y)) throw new InvalidOperationException("TIFF fixture write failed.");
            }
            if (!tif.WriteDirectory()) throw new InvalidOperationException("TIFF fixture directory failed.");
        }
    }

    private static (double Min, double Max) Validate(LinearWorkingFrame frame, ushort[] samples, int width, int height, byte[] profile, int orientation)
    {
        var swapped = orientation >= 5;
        if (frame.Width != (swapped ? height : width) || frame.Height != (swapped ? width : height) || frame.Domain != WorkingImageDomain.RenderedLinear)
            throw new InvalidOperationException("TIFF dimensions/domain changed.");
        var reference = new ProfiledJpegChecks.IccReference(profile);
        var row = new float[frame.Width * 4];
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (var y = 0; y < frame.Height; y++)
        {
            frame.ReadLinearRow(y, row);
            for (var x = 0; x < frame.Width; x++)
            {
                int pixel;
                if (width == 3 && height == 2) pixel = Orientations[orientation - 1][y * frame.Width + x];
                else if (orientation == 1) pixel = y * width + x;
                else throw new NotSupportedException("Photographic reference currently requires orientation 1.");
                var at = pixel * 3;
                var expected = reference.Convert16(samples[at], samples[at + 1], samples[at + 2]);
                for (var c = 0; c < 3; c++)
                {
                    var actual = row[x * 4 + c];
                    if (!float.IsFinite(actual) || Math.Abs(actual - expected[c]) > Math.Max(1.2e-7, Math.Abs(expected[c]) * .0006))
                        throw new InvalidOperationException($"TIFF ICC mismatch at {x},{y}/{c}: {actual} != {expected[c]}.");
                    min = Math.Min(min, actual); max = Math.Max(max, actual);
                }
                if (row[x * 4 + 3] != 1) throw new InvalidOperationException("TIFF opaque alpha changed.");
            }
        }
        return (min, max);
    }

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        using var image = ProfiledTiffInput.Decode(path);
        using var tif = Tiff.Open(path, "r") ?? throw new InvalidDataException("Reference TIFF cannot open.");
        var width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        var height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        var samples = new ushort[width * height * 3];
        var row = new byte[tif.ScanlineSize()];
        for (var y = 0; y < height; y++)
        {
            if (!tif.ReadScanline(row, y)) throw new InvalidDataException("Reference scanline failed.");
            Buffer.BlockCopy(row, 0, samples, y * width * 6, width * 6);
        }
        var profile = tif.GetField(TiffTag.ICCPROFILE)[1].ToByteArray();
        var stats = Validate(image.Frame, samples, width, height, profile, image.OriginalOrientation);
        SceneRenderingChecks.SavePreview(image.Frame, output, $"{name}-tiff-f16.png", $"{name}-tiff-f16-detail.png");
        using var sdr = WorkingImageBoundary.ToDiagnosticSdrSrgb8(image.Frame);
        using var legacy = ImageProcessor.DecodeWorkingImage(path) ?? throw new InvalidDataException("Legacy TIFF decode failed.");
        var maxDifference = 0;
        var a = new byte[width * 4]; var b = new byte[a.Length];
        if (sdr.Width != legacy.Width || sdr.Height != legacy.Height) throw new InvalidOperationException("Legacy TIFF dimensions differ.");
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(sdr.GetPixels() + y * sdr.RowBytes, a, 0, a.Length);
            Marshal.Copy(legacy.GetPixels() + y * legacy.RowBytes, b, 0, b.Length);
            for (var i = 0; i < a.Length; i++) maxDifference = Math.Max(maxDifference, Math.Abs(a[i] - b[i]));
        }
        if (maxDifference > 3) throw new InvalidOperationException($"TIFF SDR deviation {maxDifference} exceeds three codes.");
        using var detail = new SKBitmap();
        var size = Math.Min(1024, Math.Min(width, height));
        if (!legacy.ExtractSubset(detail, new SKRectI(width - size, 0, width, size))) throw new InvalidOperationException("Legacy detail failed.");
        using var png = SKImage.FromBitmap(detail);
        using var data = png.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(output, $"{name}-tiff-legacy-detail.png"));
        data.SaveTo(file);
        report.Checks.Add(new ProbeCheck($"Profiled TIFF / {name}", "PASS", $"{image.ProfileDescription}; ICC SHA256 {image.ProfileSha256}; {width}x{height} RGB16 -> RenderedLinear Rec.2020 F16; independent original-ICC reference checked every pixel. Range [{stats.Min:G6},{stats.Max:G6}]; SDR maximum deviation {maxDifference} codes; orientation {image.OriginalOrientation}. No RAW tone or sRGB16 intermediate."));
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Profiled TIFF16 input</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}.pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:600px;object-fit:contain;background:#111}a{color:#bce0ff}</style><h1>TIFF16: direct profiled F16 input</h1><p>Left: existing color-managed TIFF rendering. Right: original RGB16 + embedded ICC directly to linear Rec.2020 F16, then diagnostic sRGB output. Compare casts, fine gradients and detail; SDR appearance should remain close. The report and generated fixtures validate source precision before SDR quantization. No RAW development curve is applied. <a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            var name = $"image-{i + 1}";
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2>");
            foreach (var files in new[] { new[] { $"{name}-neutral.png", $"{name}-tiff-f16.png" }, new[] { $"{name}-tiff-legacy-detail.png", $"{name}-tiff-f16-detail.png" } })
            {
                html.Append("<div class='pair'>");
                foreach (var file in files) html.Append($"<figure><figcaption>{file}</figcaption><a href='{file}'><img src='{file}'></a></figure>");
                html.Append("</div>");
            }
        }
        File.WriteAllText(Path.Combine(output, "profiled-tiff-review.html"), html.Append("</html>").ToString());
    }
}
