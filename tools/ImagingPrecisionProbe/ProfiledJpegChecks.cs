using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class ProfiledJpegChecks
{
    private static readonly double[] RecToSrgb = [1.660491002, -.587641139, -.072849863, -.124550475, 1.132899897, -.008349422, -.018150763, -.100578898, 1.118729661];
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at, 4));
    private static double Fixed(byte[] b, int at) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at, 4)) / 65536.0;
    private static void Put(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at, 4), value);
    private static void Sig(byte[] b, int at, string value) => Encoding.ASCII.GetBytes(value).CopyTo(b, at);

    internal static void Run(ProbeReport report)
    {
        try
        {
            var profile = TestProfile();
            using var space = SKColorSpace.CreateIcc(profile) ?? throw new InvalidOperationException("Synthetic ICC rejected.");
            using var source = new SKBitmap(new SKImageInfo(7, 1, SKColorType.Bgra8888, SKAlphaType.Opaque));
            byte[] values = [0, 0, 0, 255, 1, 1, 1, 255, 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255, 40, 90, 180, 255];
            Marshal.Copy(values, 0, source.GetPixels(), values.Length);
            using var frame = ProfiledJpegInput.FromProfiledPixels(source, profile);
            var stats = Validate(frame, source, new IccReference(profile), 1);
            var row = new float[28];
            frame.ReadLinearRow(0, row);
            if (row[4] <= 0 || row[4] >= Math.Pow(2, -14) || stats.Negative == 0 || stats.AboveOne == 0)
                throw new InvalidOperationException("Tiny shadows or out-of-Rec.2020 colors were lost.");
            // Regression for the real Adobe RGB fixture: its ICC gamma is 563/256,
            // not the nearby 2.2 curve a native color-space implementation may use.
            var adobeGamma = profile.ToArray();
            BinaryPrimitives.WriteUInt16BigEndian(adobeGamma.AsSpan(276, 2), 563);
            using var ramp = new SKBitmap(new SKImageInfo(256, 1, SKColorType.Bgra8888, SKAlphaType.Opaque));
            byte[] rampBytes = Enumerable.Range(0, 256).SelectMany(i => new byte[] { (byte)i, (byte)i, (byte)i, 255 }).ToArray();
            Marshal.Copy(rampBytes, 0, ramp.GetPixels(), rampBytes.Length);
            using var exactGamma = ProfiledJpegInput.FromProfiledPixels(ramp, adobeGamma);
            Validate(exactGamma, ramp, new IccReference(adobeGamma), 1);
            var truncatedCurve = profile.ToArray();
            Put(truncatedCurve, 176, 12);
            Reject(() => { using var rejected = ProfiledJpegInput.FromProfiledPixels(source, truncatedCurve); });

            Orientation();
            var jpeg = Packet(profile);
            if (!ProfiledJpegInput.ReadEmbeddedProfile(jpeg).SequenceEqual(profile)) throw new InvalidOperationException("ICC assembly changed profile bytes.");
            if (!ProfiledJpegInput.ReadEmbeddedProfile(Packet(profile, true)).SequenceEqual(profile)) throw new InvalidOperationException("Out-of-order ICC chunks failed.");
            Reject(() => ProfiledJpegInput.ReadEmbeddedProfile(Packet(profile, missing: true)));
            Reject(() => ProfiledJpegInput.ReadEmbeddedProfile(Packet(profile, duplicate: true)));
            Reject(() => ProfiledJpegInput.ReadEmbeddedProfile(jpeg[..20]));
            var wrong = profile.ToArray(); Sig(wrong, 16, "CMYK"); Reject(() => ProfiledJpegInput.ReadEmbeddedProfile(Packet(wrong)));
            var lut = profile.ToArray(); Sig(lut, 132, "A2B0"); Reject(() => ProfiledJpegInput.ReadEmbeddedProfile(Packet(lut)));
            report.Checks.Add(new ProbeCheck("Profiled JPEG / ICC guards, float transform and orientation", "PASS",
                "Independent ICC matrix/TRC reference; ProPhoto primaries retain signed/above-one Rec.2020, one-code shadows retain F16 subnormals, opaque alpha/rendered domain; eight exact orientations; ordered/reordered/missing/duplicate/truncated ICC packets and unsupported color model checked."));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Profiled JPEG / ICC guards, float transform and orientation", ex.ToString())); }
    }

    // Independent ICC reference: colorants are PCS D50 columns; curves are read from
    // the embedded bytes, not from the CMM's transform or numerical-transfer result.
    internal sealed class IccReference
    {
        private readonly byte[] bytes;
        private readonly int[] curves = new int[3];
        private readonly double[] source = new double[9];
        private readonly double[] targetInverse = Invert(SKColorSpaceXyz.Rec2020.Values.Select(v => (double)v).ToArray());
        public IccReference(byte[] data)
        {
            bytes = data;
            var tags = new Dictionary<string, int>();
            for (var i = 0; i < U32(data, 128); i++) { var at = 132 + i * 12; tags.Add(Encoding.ASCII.GetString(data, at, 4), (int)U32(data, at + 4)); }
            for (var c = 0; c < 3; c++)
            {
                var xyz = tags[new[] { "rXYZ", "gXYZ", "bXYZ" }[c]];
                for (var r = 0; r < 3; r++) source[r * 3 + c] = Fixed(data, xyz + 8 + r * 4);
                curves[c] = tags[new[] { "rTRC", "gTRC", "bTRC" }[c]];
            }
        }
        private double Curve(int channel, double x)
        {
            var at = curves[channel];
            var kind = Encoding.ASCII.GetString(bytes, at, 4);
            if (kind == "curv")
            {
                var count = U32(bytes, at + 8);
                if (count == 0) return x;
                if (count == 1) return Math.Pow(x, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 12, 2)) / 256.0);
                var position = x * (count - 1); var index = Math.Min((int)count - 2, (int)position); var fraction = position - index;
                var a = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 12 + index * 2, 2)) / 65535.0;
                var b = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 14 + index * 2, 2)) / 65535.0;
                return a + (b - a) * fraction;
            }
            if (kind != "para") throw new NotSupportedException("Reference needs curve/parametric TRCs.");
            var type = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 8, 2));
            var counts = new[] { 1, 3, 4, 5, 7 };
            var p = Enumerable.Range(0, counts[type]).Select(i => Fixed(bytes, at + 12 + i * 4)).ToArray();
            return type switch
            {
                0 => Math.Pow(x, p[0]),
                1 => x >= -p[2] / p[1] ? Math.Pow(p[1] * x + p[2], p[0]) : 0,
                2 => x >= -p[2] / p[1] ? Math.Pow(p[1] * x + p[2], p[0]) + p[3] : p[3],
                3 => x >= p[4] ? Math.Pow(p[1] * x + p[2], p[0]) : p[3] * x,
                4 => x >= p[4] ? Math.Pow(p[1] * x + p[2], p[0]) + p[5] : p[3] * x + p[6],
                _ => throw new NotSupportedException("Unknown parametric TRC")
            };
        }
        public double[] Convert(byte r, byte g, byte b) => ConvertEncoded(r / 255.0, g / 255.0, b / 255.0);
        public double[] Convert16(ushort r, ushort g, ushort b) => ConvertEncoded(r / 65535.0, g / 65535.0, b / 65535.0);
        private double[] ConvertEncoded(double r, double g, double b) => Matrix(targetInverse, Matrix(source, [Curve(0, r), Curve(1, g), Curve(2, b)]));
    }

    private static double[] Matrix(double[] m, double[] v) => [m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2]];
    private static double[] Invert(double[] m)
    {
        var d = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
        return new[] { m[4] * m[8] - m[5] * m[7], m[2] * m[7] - m[1] * m[8], m[1] * m[5] - m[2] * m[4], m[5] * m[6] - m[3] * m[8], m[0] * m[8] - m[2] * m[6], m[2] * m[3] - m[0] * m[5], m[3] * m[7] - m[4] * m[6], m[1] * m[6] - m[0] * m[7], m[0] * m[4] - m[1] * m[3] }.Select(v => v / d).ToArray();
    }

    private sealed record Stats(long Negative, long AboveOne, long OutsideSrgb, double Min, double Max);
    private static Stats Validate(LinearWorkingFrame frame, SKBitmap raw, IccReference reference, int origin)
    {
        if (frame.Domain != WorkingImageDomain.RenderedLinear) throw new InvalidOperationException("A JPEG is not a sensor scene.");
        var bytes = new byte[raw.ByteCount];
        Marshal.Copy(raw.GetPixels(), bytes, 0, bytes.Length);
        var row = new float[frame.Width * 4];
        long negative = 0, above = 0, outside = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (var y = 0; y < frame.Height; y++)
        {
            frame.ReadLinearRow(y, row);
            for (var x = 0; x < frame.Width; x++)
            {
                var (sx, sy) = origin switch { 1 => (x, y), 2 => (raw.Width - 1 - x, y), 3 => (raw.Width - 1 - x, raw.Height - 1 - y), 4 => (x, raw.Height - 1 - y), 5 => (y, x), 6 => (y, raw.Height - 1 - x), 7 => (raw.Width - 1 - y, raw.Height - 1 - x), 8 => (raw.Width - 1 - y, x), _ => throw new InvalidOperationException("Invalid origin") };
                var at = sy * raw.RowBytes + sx * 4; var expected = reference.Convert(bytes[at + 2], bytes[at + 1], bytes[at]);
                double[] rgb = [row[x * 4], row[x * 4 + 1], row[x * 4 + 2]];
                for (var c = 0; c < 3; c++)
                {
                    // Half rounding plus a small allowance for target-matrix inversion.
                    // Keep an absolute floor for cancellation and subnormal values.
                    if (!double.IsFinite(rgb[c]) || Math.Abs(rgb[c] - expected[c]) > Math.Max(1.2e-7, Math.Abs(expected[c]) * .0006)) throw new InvalidOperationException($"ICC reference mismatch at {x},{y}/{c}: {rgb[c]} != {expected[c]}.");
                    if (rgb[c] < 0) negative++;
                    if (rgb[c] > 1) above++;
                    min = Math.Min(min, rgb[c]);
                    max = Math.Max(max, rgb[c]);
                }
                if (row[x * 4 + 3] != 1) throw new InvalidOperationException("JPEG alpha changed.");
                if (Matrix(RecToSrgb, rgb).Any(v => v < -.0001 || v > 1.0001)) outside++;
            }
        }
        return new Stats(negative, above, outside, min, max);
    }

    internal static byte[] TestProfile()
    {
        var b = new byte[280]; Put(b, 0, (uint)b.Length); Put(b, 8, 0x02100000); Sig(b, 12, "mntr"); Sig(b, 16, "RGB "); Sig(b, 20, "XYZ "); Sig(b, 36, "acsp");
        double[] xyz = [.7976749, .1351917, .0313534, .2880402, .7118741, .0000857, 0, 0, .82521];
        Put(b, 68, (uint)Math.Round(.9642 * 65536)); Put(b, 72, 65536); Put(b, 76, (uint)Math.Round(.8249 * 65536)); Put(b, 128, 6);
        for (var c = 0; c < 3; c++)
        {
            var at = 132 + c * 12; Sig(b, at, new[] { "rXYZ", "gXYZ", "bXYZ" }[c]); Put(b, at + 4, (uint)(204 + c * 20)); Put(b, at + 8, 20); Sig(b, 204 + c * 20, "XYZ ");
            for (var r = 0; r < 3; r++) Put(b, 212 + c * 20 + r * 4, (uint)Math.Round(xyz[r * 3 + c] * 65536));
            at = 168 + c * 12; Sig(b, at, new[] { "rTRC", "gTRC", "bTRC" }[c]); Put(b, at + 4, 264); Put(b, at + 8, 14);
        }
        Sig(b, 264, "curv"); Put(b, 272, 1); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(276, 2), 461);
        return b;
    }

    private static byte[] Packet(byte[] profile, bool reverse = false, bool missing = false, bool duplicate = false)
    {
        var bytes = new List<byte> { 255, 216 };
        void Segment(int marker, byte[] data) { bytes.Add(255); bytes.Add((byte)marker); bytes.Add((byte)((data.Length + 2) >> 8)); bytes.Add((byte)(data.Length + 2)); bytes.AddRange(data); }
        Segment(0xc0, [8, 0, 2, 0, 3, 3, 1, 17, 0, 2, 17, 0, 3, 17, 0]);
        foreach (var index in reverse ? new[] { 2, 1 } : new[] { 1, 2 })
        {
            if (missing && index == 2) continue;
            var data = Encoding.ASCII.GetBytes("ICC_PROFILE\0").Concat(new byte[] { (byte)(duplicate ? 1 : index), 2 }).Concat(index == 1 ? profile[..140] : profile[140..]).ToArray(); Segment(0xe2, data);
        }
        bytes.AddRange([255, 217]); return bytes.ToArray();
    }

    private static void Orientation()
    {
        using var source = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Bgra8888, SKAlphaType.Opaque));
        byte[] pixels = Enumerable.Range(1, 6).SelectMany(i => new byte[] { (byte)i, 30, 50, 255 }).ToArray(); Marshal.Copy(pixels, 0, source.GetPixels(), pixels.Length);
        int[][] expected = [[1, 2, 3, 4, 5, 6], [3, 2, 1, 6, 5, 4], [6, 5, 4, 3, 2, 1], [4, 5, 6, 1, 2, 3], [1, 4, 2, 5, 3, 6], [4, 1, 5, 2, 6, 3], [6, 3, 5, 2, 4, 1], [3, 6, 2, 5, 1, 4]];
        for (var code = 1; code <= 8; code++)
        {
            using var oriented = ProfiledJpegInput.OrientEncodedPixels(source, (SKEncodedOrigin)code);
            var bytes = new byte[oriented.ByteCount]; Marshal.Copy(oriented.GetPixels(), bytes, 0, bytes.Length);
            for (var i = 0; i < 6; i++) if (bytes[i * 4] != expected[code - 1][i] || bytes[i * 4 + 1] != 30 || bytes[i * 4 + 2] != 50 || bytes[i * 4 + 3] != 255) throw new InvalidOperationException("Orientation is not a lossless permutation.");
        }
    }

    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { return; } throw new InvalidOperationException("Malformed profile accepted."); }

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        // Compare the color transform, not two different JPEG decoders. The independent
        // reference consumes the same intrinsic RGB8 samples before any profile conversion.
        using var decoded = ProfiledJpegInput.Decode(path);
        var icc = ProfiledJpegInput.ReadEmbeddedProfile(File.ReadAllBytes(path));
        using var data = SKData.CreateCopy(File.ReadAllBytes(path));
        using var codec = SKCodec.Create(data);
        using var raw = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        if (codec.GetPixels(raw.Info, raw.GetPixels()) != SKCodecResult.Success) throw new InvalidOperationException("Reference JPEG decode failed.");
        var stats = Validate(decoded.Frame, raw, new IccReference(icc), (int)codec.EncodedOrigin);
        SceneRenderingChecks.SavePreview(decoded.Frame, output, $"{name}-profiled-f16.png");
        using var sdr = WorkingImageBoundary.ToDiagnosticSdrSrgb8(decoded.Frame);
        using var legacyDecoded = ImageProcessor.DecodeWorkingImage(path) ?? throw new InvalidOperationException("Legacy decode failed.");
        using var legacy = legacyDecoded.Copy(SKColorType.Bgra8888);
        var max = 0;
        var a = new byte[sdr.Width * 4];
        var b = new byte[a.Length];
        if (sdr.Width != legacy.Width || sdr.Height != legacy.Height) throw new InvalidOperationException("Profiled image orientation/size differs from legacy.");
        for (var y = 0; y < sdr.Height; y++)
        {
            Marshal.Copy(sdr.GetPixels() + y * sdr.RowBytes, a, 0, a.Length);
            Marshal.Copy(legacy.GetPixels() + y * legacy.RowBytes, b, 0, b.Length);
            for (var i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }
        if (max > 3) throw new InvalidOperationException($"SDR preview differs from legacy by {max} codes (limit 3).");
        report.Checks.Add(new ProbeCheck($"Profiled JPEG / {name}", "PASS", $"{decoded.ProfileDescription}; embedded ICC SHA256 {decoded.ProfileSha256}; {decoded.Frame.Width}x{decoded.Frame.Height}; RenderedLinear Rec.2020 F16; independent matrix/TRC checked every pixel. Range [{stats.Min:G6},{stats.Max:G6}], negative channels {stats.Negative}, above-one channels {stats.AboveOne}, out-of-sRGB pixels {stats.OutsideSrgb}. SDR comparison max {max} codes; source orientation {decoded.OriginalOrientation}. No RAW tone or capture-WB applied."));
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Profiled JPEG input</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}.pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:520px;object-fit:contain;background:#111}a{color:#bce0ff}</style><h1>Wide-gamut JPEG: profiled F16 input</h1><p>Left: existing color-managed sRGB8 pipeline. Right: embedded ICC -> linear Rec.2020 F16 -> diagnostic SDR sRGB output. Appearance should be close: both previews are SDR sRGB. The report records retained out-of-sRGB/signed/above-one values before output; an sRGB PNG alone cannot display the full retained gamut. These are rendered files, with no RAW tone curve or capture WB applied. <a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2><div class='pair'>");
            foreach (var setting in new[] { "neutral", "profiled-f16" }) { var file = $"image-{i + 1}-{setting}.png"; html.Append($"<figure><figcaption>{setting}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{setting}'></a></figure>"); }
            html.Append("</div>");
        }
        File.WriteAllText(Path.Combine(output, "profiled-jpeg-review.html"), html.Append("</html>").ToString());
    }
}
