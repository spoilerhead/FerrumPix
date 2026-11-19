using System.Diagnostics;
using System.Runtime.InteropServices;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class WorkingFrameChecks
{
    public static void Run(ProbeReport report)
    {
        Check(report, "storage, metadata and F32 staging", Storage);
        Check(report, "sRGB alpha boundaries", AlphaBoundaries);
        Check(report, "bounded SDR normalization of extended premultiplication", ExtendedPremultiplication);
        Check(report, "16-bit source precision", DeepInput);
        Check(report, "domain and invalid input guards", Guards);
        Check(report, "lifetime", Lifetime);
    }

    private static void Check(ProbeReport report, string name, Func<string> test)
    {
        var clock = Stopwatch.StartNew();
        try { report.Checks.Add(new ProbeCheck("Frame / " + name, "PASS", test(), clock.Elapsed.TotalMilliseconds)); }
        catch (Exception ex) { report.Checks.Add(new ProbeCheck("Frame / " + name, "FAIL", ex.ToString(), clock.Elapsed.TotalMilliseconds)); }
    }

    private static string Storage()
    {
        float[] values = [-0.25f, 0.000001f, 4, 1, 16, 0.18f, 0, 1, 0, 0, 0, 0, 0.000001f, 0, 0, 0.000001f];
        var expected = HalfPixelStorage.Unpack(HalfPixelStorage.Pack(values));
        using var frame = LinearWorkingFrame.FromLinearPixels(2, 2, values, WorkingImageDomain.SceneLinear);
        if (frame.ColorType != SKColorType.RgbaF16 || frame.AlphaType != SKAlphaType.Premul ||
            frame.Domain != WorkingImageDomain.SceneLinear || frame.Primaries != "Rec.2020" ||
            frame.WhitePoint != "D65" || !frame.IsLinear || frame.StorageBytes != 32)
            throw new InvalidOperationException("Frame metadata/storage contract is incorrect.");
        values[0] = 10; // Caller mutation must not alter the frame.
        using var staging = frame.ToLinearBitmap();
        using var copy = LinearWorkingFrame.FromLinearBitmap(staging, frame.Domain);
        var row = new float[8];
        for (var y = 0; y < 2; y++)
        {
            copy.ReadLinearRow(y, row);
            if (!row.SequenceEqual(expected.Skip(y * 8).Take(8)))
                throw new InvalidOperationException("Frame/F32 round trip changed half values or row order.");
        }
        using var snapshot = frame.CreateNativeSnapshot();
        if (snapshot.ColorType != SKColorType.RgbaF16) throw new InvalidOperationException("Snapshot lost native F16 format.");
        return "Linear Rec.2020 D65 / F16 / premultiplied contract; negative/HDR values, tiny alpha and caller ownership preserved across multiple rows and F32 staging.";
    }

    private static SKBitmap Pattern(SKColorType type, SKAlphaType alphaType)
    {
        const int width = 257, height = 4;
        var bitmap = new SKBitmap(new SKImageInfo(width, height, type, alphaType));
        var row = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            var a = alphaType == SKAlphaType.Opaque ? (byte)255 : new byte[] { 0, 1, 127, 255 }[y];
            for (var x = 0; x < width; x++)
            {
                var r = Math.Min(x, 255); var g = (x * 7) % 256; var b = 255 - r;
                if (alphaType == SKAlphaType.Premul) { r = r * a / 255; g = g * a / 255; b = b * a / 255; }
                var at = x * 4;
                row[at] = (byte)(type == SKColorType.Bgra8888 ? b : r);
                row[at + 1] = (byte)g;
                row[at + 2] = (byte)(type == SKColorType.Bgra8888 ? r : b);
                row[at + 3] = a;
            }
            Marshal.Copy(row, 0, bitmap.GetPixels() + y * bitmap.RowBytes, row.Length);
        }
        return bitmap;
    }

    private static string AlphaBoundaries()
    {
        var maximum = 0;
        foreach (var type in new[] { SKColorType.Bgra8888, SKColorType.Rgba8888 })
        foreach (var alphaType in new[] { SKAlphaType.Premul, SKAlphaType.Unpremul, SKAlphaType.Opaque })
        {
            using var input = Pattern(type, alphaType);
            using var frame = WorkingImageBoundary.FromEncodedSrgb(input);
            if (frame.Domain != WorkingImageDomain.RenderedLinear) throw new InvalidOperationException("Rendered input was relabeled as scene-linear.");
            using var output = WorkingImageBoundary.ToDiagnosticSdrSrgb8(frame);
            maximum = Math.Max(maximum, CompareSdr(input, output));
        }
        return $"Both 8-bit channel orders and all supported alpha modes tested; maximum premultiplied sRGB error {maximum} code levels (limit 2).";
    }

    internal static int CompareSdr(SKBitmap input, SKBitmap output)
    {
        if (input.ColorType == SKColorType.Bgra8888 && input.AlphaType == SKAlphaType.Premul &&
            output.ColorType == SKColorType.Bgra8888 && output.AlphaType == SKAlphaType.Premul)
            return ComparePremultipliedBytes(input, output);
        var maximum = 0;
        for (var y = 0; y < input.Height; y++)
        for (var x = 0; x < input.Width; x++)
        {
            var before = input.GetPixel(x, y);
            var after = output.GetPixel(x, y);
            // Compare premultiplied code values: straight RGB is undefined at zero alpha.
            if (before.Alpha != after.Alpha) throw new InvalidOperationException("Boundary changed alpha.");
            foreach (var error in new[] { Math.Abs(before.Red * before.Alpha / 255.0 - after.Red * after.Alpha / 255.0),
                         Math.Abs(before.Green * before.Alpha / 255.0 - after.Green * after.Alpha / 255.0),
                         Math.Abs(before.Blue * before.Alpha / 255.0 - after.Blue * after.Alpha / 255.0) })
            {
                maximum = Math.Max(maximum, (int)Math.Ceiling(error));
                if (error > 2) throw new InvalidOperationException($"SDR round-trip error exceeds 2 code values at {x},{y}: {error}.");
            }
        }
        return maximum;
    }

    private static int ComparePremultipliedBytes(SKBitmap input, SKBitmap output)
    {
        var before = new byte[input.Width * 4];
        var after = new byte[before.Length];
        var maximum = 0;
        for (var y = 0; y < input.Height; y++)
        {
            Marshal.Copy(input.GetPixels() + y * input.RowBytes, before, 0, before.Length);
            Marshal.Copy(output.GetPixels() + y * output.RowBytes, after, 0, after.Length);
            for (var i = 0; i < before.Length; i++)
            {
                var error = Math.Abs(before[i] - after[i]);
                if ((i % 4 == 3 && error != 0) || error > 2)
                    throw new InvalidOperationException($"SDR boundary changed byte {i} on row {y} by {error} (color limit 2, alpha limit 0). Input {before[i]}, output {after[i]}, source alpha {before[(i / 4) * 4 + 3]}.");
                if (i % 4 != 3) maximum = Math.Max(maximum, error);
            }
        }
        return maximum;
    }

    internal static void CaptureBoundary(ProbeReport report, string output, SKBitmap source, string name)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var canonical = CanonicalPremultipliedSdr(source, out var extended, out var excess);
            if (canonical is not null)
                report.Checks.Add(new ProbeCheck($"Legacy input / {name} original SDR roundtrip", "SKIP",
                    $"Legacy preview supplied {extended} RGB samples above alpha (maximum excess {excess} codes): extended colors outside bounded premultiplied SDR. A lossless SDR roundtrip of these values is inapplicable because this boundary explicitly clips to SDR. The required comparison below uses a canonical SDR reference at the unchanged two-code limit. Original legacy bytes are unchanged; independent profiled fixture input is checked separately.", Required: false));
            using var frame = WorkingImageBoundary.FromEncodedSrgb(source);
            using var roundTrip = WorkingImageBoundary.ToDiagnosticSdrSrgb8(frame);
            var elapsed = clock.Elapsed.TotalMilliseconds;
            var maximum = CompareSdr(canonical ?? source, roundTrip);
            using var image = SKImage.FromBitmap(roundTrip);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(output, $"{name}-f16-roundtrip.png"));
            png.SaveTo(file);
            report.Checks.Add(new ProbeCheck($"Boundary / {name}", "PASS",
                $"sRGB -> native linear Rec.2020 F16 -> diagnostic SDR sRGB; max error {maximum} code levels, alpha unchanged; frame {frame.StorageBytes / 1_000_000.0:F2} MB. {(canonical is null ? "Original source is valid SDR." : "Compared against explicitly canonicalized SDR; original extended-range samples are reported separately.")} Not scene-linear RAW development.", elapsed));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Boundary / {name}", ex.ToString())); }
    }

    // A bounded SDR boundary intentionally clips RGB>alpha, so that clipping is
    // not a quantization error. Preserve the original legacy bytes and report the
    // extended input separately rather than relaxing the two-code fidelity limit.
    private static SKBitmap? CanonicalPremultipliedSdr(SKBitmap source, out long extended, out int maximumExcess)
    {
        extended = 0; maximumExcess = 0;
        if (source.AlphaType != SKAlphaType.Premul || source.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888)) return null;
        var row = new byte[source.Width * 4];
        for (var y = 0; y < source.Height; y++)
        {
            Marshal.Copy(source.GetPixels() + y * source.RowBytes, row, 0, row.Length);
            for (var x = 0; x < source.Width; x++) for (var c = 0; c < 3; c++)
            {
                var excess = row[x * 4 + c] - row[x * 4 + 3];
                if (excess <= 0) continue;
                extended++; maximumExcess = Math.Max(maximumExcess, excess);
            }
        }
        if (extended == 0) return null;
        var result = new SKBitmap(source.Info);
        try
        {
            if (result.GetPixels() == IntPtr.Zero) throw new OutOfMemoryException("Cannot allocate canonical SDR reference.");
            for (var y = 0; y < source.Height; y++)
            {
                Marshal.Copy(source.GetPixels() + y * source.RowBytes, row, 0, row.Length);
                for (var x = 0; x < source.Width; x++) for (var c = 0; c < 3; c++) row[x * 4 + c] = Math.Min(row[x * 4 + c], row[x * 4 + 3]);
                Marshal.Copy(row, 0, result.GetPixels() + y * result.RowBytes, row.Length);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static string ExtendedPremultiplication()
    {
        using var source = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        byte[] original = [67, 20, 0, 63];
        Marshal.Copy(original, 0, source.GetPixels(), 4);
        using var canonical = CanonicalPremultipliedSdr(source, out var count, out var excess)!;
        using var frame = WorkingImageBoundary.FromEncodedSrgb(source);
        using var output = WorkingImageBoundary.ToDiagnosticSdrSrgb8(frame);
        if (count != 1 || excess != 4) throw new InvalidOperationException("Extended input classification changed.");
        CompareSdr(canonical, output);
        var after = new byte[4]; Marshal.Copy(source.GetPixels(), after, 0, 4);
        if (!original.SequenceEqual(after)) throw new InvalidOperationException("SDR normalization mutated legacy source.");
        return "Explicit SDR projection maps RGB 67/alpha 63 to bounded RGB<=63 with unchanged alpha/source; strict two-code fidelity is tested against the canonical SDR reference, not against intentionally clipped extended values.";
    }

    private static string DeepInput()
    {
        using var input = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba16161616, SKAlphaType.Premul));
        Marshal.Copy(new short[] { 1, 1, 1, -1 }, 0, input.GetPixels(), 4);
        using var frame = WorkingImageBoundary.FromEncodedSrgb(input);
        var row = new float[4];
        frame.ReadLinearRow(0, row);
        var expected = 1.0 / 65535 / 12.92;
        if (row.Take(3).Any(v => v <= 0 || Math.Abs(v - expected) > 0.00000006))
            throw new InvalidOperationException("16-bit input was quantized to bytes or lost its subnormal values.");
        return "A one-code-value RGB16 neutral remains nonzero linear Rec.2020; no intermediate RGB8 conversion.";
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static string Guards()
    {
        Reject<ArgumentException>(() => LinearWorkingFrame.FromLinearPixels(1, 1, [0, 0], WorkingImageDomain.RenderedLinear));
        Reject<ArgumentOutOfRangeException>(() => LinearWorkingFrame.FromLinearPixels(0, 1, [], WorkingImageDomain.RenderedLinear));
        Reject<ArgumentOutOfRangeException>(() => LinearWorkingFrame.FromLinearPixels(1, 1, [0, 0, 0, 1], (WorkingImageDomain)99));
        Reject<ArgumentException>(() => LinearWorkingFrame.FromLinearPixels(1, 1, [0, 0, 0, 2], WorkingImageDomain.RenderedLinear));
        Reject<ArgumentOutOfRangeException>(() => LinearWorkingFrame.FromLinearPixels(1, 1, [65536, 0, 0, 1], WorkingImageDomain.RenderedLinear));
        Reject<ArgumentOutOfRangeException>(() => LinearWorkingFrame.FromLinearPixels(1, 1, [float.NaN, 0, 0, 1], WorkingImageDomain.RenderedLinear));
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        Reject<ArgumentException>(() => WorkingImageBoundary.ToDiagnosticSdrSrgb8(scene));
        using var wrongFormat = new SKBitmap(1, 1);
        Reject<ArgumentException>(() => LinearWorkingFrame.FromLinearBitmap(wrongFormat, WorkingImageDomain.RenderedLinear));
        using var linearProfile = SKColorSpace.CreateSrgbLinear();
        using var wrongProfile = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul, linearProfile));
        Reject<ArgumentException>(() => WorkingImageBoundary.FromEncodedSrgb(wrongProfile));
        return "Rejects invalid dimensions/domain/alpha, non-finite and overflow values, undeclared formats/profiles, and scene-linear input at the SDR boundary.";
    }

    private static string Lifetime()
    {
        var frame = LinearWorkingFrame.FromLinearPixels(1, 1, [0, 0, 0, 1], WorkingImageDomain.RenderedLinear);
        frame.Dispose(); frame.Dispose();
        Reject<ObjectDisposedException>(() => frame.ReadLinearRow(0, new float[4]));
        Reject<ObjectDisposedException>(() => frame.ToLinearBitmap());
        Reject<ObjectDisposedException>(() => frame.CreateNativeSnapshot());
        return "Disposal is idempotent; pixel access after disposal is rejected.";
    }
}
