using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class PrecisionChecks
{
    private sealed class UnsupportedSurfaceException(string message) : Exception(message);
    private const string Identity = "uniform shader src; half4 main(float2 p) { return src.eval(p); }";

    public static void Run(ProbeReport report, GRContext? context, string backend) => report.Checks.AddRange(Measure(context, backend));

    public static List<ProbeCheck> Measure(GRContext? context, string backend)
    {
        var checks = new List<ProbeCheck>();
        foreach (var format in new[] { SKColorType.RgbaF32, SKColorType.RgbaF16 })
        {
            Check(checks, $"{backend} / {format} range and exposure", () => RangeAndExposure(context, format));
            Check(checks, $"{backend} / {format} linear sampling", () => Sampling(context, format));
            Check(checks, $"{backend} / {format} alpha blending", () => Blending(context, format));
        }
        Check(checks, $"{backend} / F16 repeated storage", () => RepeatedStorage(context));
        Check(checks, $"{backend} / F16 shadow transport isolation", () => ShadowTransport(context));
        Check(checks, $"{backend} / Rec.2020 reference", () => ColorReference(context));
        foreach (var format in new[] { SKColorType.Rgba8888, SKColorType.RgbaF16, SKColorType.RgbaF32 })
            Check(checks, $"{backend} / {format} transfer smoke benchmark", () => TransferBenchmark(context, format));
        return checks;
    }

    private static void Check(List<ProbeCheck> checks, string name, Func<string> work)
    {
        var clock = Stopwatch.StartNew();
        try { checks.Add(new ProbeCheck(name, "PASS", work(), clock.Elapsed.TotalMilliseconds)); }
        catch (UnsupportedSurfaceException ex) when (name.StartsWith("GPU /") && name.Contains("RgbaF32"))
        {
            // GPU F32 is an optional comparison. The proposed renderer requires F16; the
            // authoritative F32 reference can run on the CPU on a backend without F32 targets.
            checks.Add(new ProbeCheck(name, "SKIP", ex.Message, clock.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex) { checks.Add(new ProbeCheck(name, "FAIL", ex.Message, clock.Elapsed.TotalMilliseconds)); }
    }

    private static SKSurface Surface(GRContext? context, SKColorType format, int width, int height)
    {
        // No transfer function is attached: these values already represent linear working RGB.
        // F16Norm would clip HDR values, so it must never substitute for unrestricted F16.
        var info = new SKImageInfo(width, height, format, SKAlphaType.Premul);
        return (context is null ? SKSurface.Create(info) : SKSurface.Create(context, false, info))
            ?? throw new UnsupportedSurfaceException($"Cannot create {format} render target.");
    }

    private static SKImage Image(float[] values, bool halfStorage = false)
    {
        // A backend without F32 textures may silently convert an F32 upload to 8-bit before
        // the shader runs. Use native F16 input on the GPU to test F16 rather than that fallback.
        using var bitmap = new SKBitmap(new SKImageInfo(values.Length / 4, 1,
            halfStorage ? SKColorType.RgbaF16 : SKColorType.RgbaF32, SKAlphaType.Premul));
        if (halfStorage)
            Marshal.Copy(values.Select(v => BitConverter.HalfToInt16Bits((Half)v)).ToArray(), 0, bitmap.GetPixels(), values.Length);
        else
            Marshal.Copy(values, 0, bitmap.GetPixels(), values.Length);
        return SKImage.FromBitmap(bitmap);
    }

    private static void Draw(SKSurface target, SKImage image, string source = Identity, bool linear = false)
    {
        using var effect = SKRuntimeEffect.CreateShader(source, out var errors);
        if (effect is null) throw new InvalidOperationException(errors);
        var children = new SKRuntimeEffectChildren(effect);
        using var input = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(linear ? SKFilterMode.Linear : SKFilterMode.Nearest));
        children["src"] = input;
        using var shader = effect.ToShader(new SKRuntimeEffectUniforms(effect), children);
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        target.Canvas.DrawPaint(paint);
    }

    private static float[] Read(SKSurface surface, int width, int height = 1)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.RgbaF32, SKAlphaType.Premul));
        if (!surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            throw new InvalidOperationException("Floating-point readback failed.");
        var values = new float[width * height * 4];
        Marshal.Copy(bitmap.GetPixels(), values, 0, values.Length);
        return values;
    }

    private static double Compare(float[] actual, float[] expected, double relative = 0.002, double absolute = 0.00000012)
    {
        var maximum = 0.0;
        for (var i = 0; i < expected.Length; i++)
        {
            var error = Math.Abs(actual[i] - expected[i]);
            if (!float.IsFinite(actual[i]) || error > Math.Max(absolute, Math.Abs(expected[i]) * relative))
                throw new InvalidOperationException($"Sample {i}: expected {expected[i]:G9}, got {actual[i]:G9}; error {error:G6}.");
            maximum = Math.Max(maximum, error);
        }
        return maximum;
    }

    private static string RangeAndExposure(GRContext? context, SKColorType format)
    {
        float[] values = [-0.25f, 0.000001f, 0.18f, 1, 1, 4, 16, 1, 0, 0.0001f, 0.5f, 1];
        using var image = Image(values, halfStorage: context is not null);
        using var surface = Surface(context, format, 3, 1);
        Draw(surface, image);
        var identityError = Compare(Read(surface, 3), values);
        Draw(surface, image, "uniform shader src; half4 main(float2 p) { float4 c = float4(src.eval(p)); return half4(c.rgb * exp2(-2.0), c.a); }");
        var expected = values.Select((v, i) => i % 4 == 3 ? v : v * 0.25f).ToArray();
        var exposureError = Compare(Read(surface, 3), expected);
        return $"Negative, black, small shadows and 16x white retained; -2 EV verified. Max absolute errors: identity {identityError:G5}, exposure {exposureError:G5}.";
    }

    private static string Sampling(GRContext? context, SKColorType format)
    {
        using var image = Image([0, 0, 0, 1, 4, 2, 1, 1], halfStorage: context is not null);
        using var surface = Surface(context, format, 1, 1);
        // Pixel centers are .5 and 1.5; sampling at 1.0 must average both texels.
        Draw(surface, image, "uniform shader src; half4 main(float2 p) { return src.eval(float2(1.0, 0.5)); }", linear: true);
        var error = Compare(Read(surface, 1), [2, 1, 0.5f, 1]);
        return $"Bilinear sampling of HDR input verified; max error {error:G5}.";
    }

    private static string Blending(GRContext? context, SKColorType format)
    {
        using var surface = Surface(context, format, 1, 1);
        using var background = Image([0.2f, 0.4f, 2, 1], halfStorage: context is not null);
        Draw(surface, background);
        using var effect = SKRuntimeEffect.CreateShader("half4 main(float2 p) { return half4(0.5, 0.125, 0.0, 0.5); }", out var errors);
        if (effect is null) throw new InvalidOperationException(errors);
        using var shader = effect.ToShader(new SKRuntimeEffectUniforms(effect));
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.SrcOver };
        surface.Canvas.DrawPaint(paint);
        var error = Compare(Read(surface, 1), [0.6f, 0.325f, 1, 1]);
        return $"Premultiplied SrcOver matches F32 reference; max error {error:G5}.";
    }

    private static string RepeatedStorage(GRContext? context)
    {
        const int width = 1024;
        var values = new float[width * 4];
        for (var x = 0; x < width; x++)
        {
            values[x * 4] = x / 1023f;
            values[x * 4 + 1] = MathF.Pow(2, -14 + x / 1023f * 18);
            values[x * 4 + 2] = -0.25f;
            values[x * 4 + 3] = 1;
        }
        using var a = Surface(context, SKColorType.RgbaF16, width, 1);
        using var b = Surface(context, SKColorType.RgbaF16, width, 1);
        using (var source = Image(values, halfStorage: context is not null)) Draw(a, source);
        var first = Read(a, width);
        for (var pass = 0; pass < 16; pass++)
        {
            using (var source = a.Snapshot()) Draw(b, source);
            using (var source = b.Snapshot()) Draw(a, source);
        }
        var final = Read(a, width);
        var initialError = Compare(first, values);
        var drift = Compare(final, first, relative: 0, absolute: 0);
        return $"32 additional identity writes; initial error {initialError:G5}, subsequent drift {drift:G5}. This does not test accumulated arithmetic rounding.";
    }

    private static string ColorReference(GRContext? context)
    {
        // D65 sRGB to D65 Rec.2020 and its inverse. CPU double arithmetic is the reference;
        // the tested shader works in float before the explicitly measured F16 storage boundary.
        double[] forward = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
        double[] inverse = [1.660491002, -.587641139, -.072849863, -.124550475, 1.132899897, -.008349422, -.018150763, -.100578898, 1.118729661];
        float[] values = [1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1, .18f, .18f, .18f, 1, -.1f, 2, 4, 1];
        var expected = new float[values.Length];
        for (var pixel = 0; pixel < values.Length / 4; pixel++)
        {
            for (var row = 0; row < 3; row++)
                expected[pixel * 4 + row] = (float)Enumerable.Range(0, 3).Sum(c => forward[row * 3 + c] * values[pixel * 4 + c]);
            expected[pixel * 4 + 3] = 1;
        }
        var matrix = "uniform shader src; half4 main(float2 p) { float4 c = float4(src.eval(p)); float3 v = c.rgb; return half4(float3(" +
                     string.Join(",", Enumerable.Range(0, 3).Select(row => "dot(float3(" + string.Join(",", forward.Skip(row * 3).Take(3).Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "),v)")) + "),c.a); }";
        using var image = Image(values, halfStorage: context is not null);
        using var target = Surface(context, SKColorType.RgbaF16, 5, 1);
        Draw(target, image, matrix);
        var actual = Read(target, 5);
        var error = Compare(actual, expected);
        var roundTrip = new float[values.Length];
        for (var pixel = 0; pixel < 5; pixel++)
        {
            for (var row = 0; row < 3; row++)
                roundTrip[pixel * 4 + row] = (float)Enumerable.Range(0, 3).Sum(c => inverse[row * 3 + c] * actual[pixel * 4 + c]);
            roundTrip[pixel * 4 + 3] = 1;
        }
        var roundTripError = Compare(roundTrip, values, relative: 0.005, absolute: 0.003);
        return $"F32 shader conversion versus double reference; F16 max error {error:G5}; inverse round-trip error {roundTripError:G5}.";
    }

    private static string TransferBenchmark(GRContext? context, SKColorType format)
    {
        const int edge = 1024;
        using var bitmap = new SKBitmap(new SKImageInfo(edge, edge, SKColorType.RgbaF32, SKAlphaType.Premul));
        var sourceValues = new float[edge * edge * 4];
        for (var i = 0; i < sourceValues.Length; i += 4)
        {
            sourceValues[i] = sourceValues[i + 1] = sourceValues[i + 2] = 0.18f;
            sourceValues[i + 3] = 1;
        }
        Marshal.Copy(sourceValues, 0, bitmap.GetPixels(), sourceValues.Length);
        using var image = SKImage.FromBitmap(bitmap);
        var clock = Stopwatch.StartNew();
        using var target = Surface(context, format, edge, edge);
        var allocationMs = clock.Elapsed.TotalMilliseconds;
        var times = new List<double>();
        for (var run = 0; run < 4; run++)
        {
            clock.Restart();
            Draw(target, image);
            var result = Read(target, edge, edge); // Forces completion; asynchronous submission is not elapsed GPU work.
            var elapsed = clock.Elapsed.TotalMilliseconds;
            if (!float.IsFinite(result[0]) || Math.Abs(result[0] - 0.18f) > 0.005)
                throw new InvalidOperationException("Transfer benchmark returned an invalid pixel.");
            if (run > 0) times.Add(elapsed);
        }
        times.Sort();
        var bytes = (long)edge * edge * (format == SKColorType.RgbaF32 ? 16 : format == SKColorType.RgbaF16 ? 8 : 4);
        return $"1024x1024; target {bytes / 1_000_000.0:F2} MB; allocation {allocationMs:F2} ms; median draw+F32 readback {times[1]:F2} ms (3 warm runs). Includes shader setup/readback allocation; not isolated GPU throughput.";
    }

    private static string ShadowTransport(GRContext? context)
    {
        // Read native half bits as well as Skia-converted F32. Otherwise a failing transport
        // conversion could be mistaken for loss in the render target or vice versa.
        var details = new List<string>();
        var lost = false;
        foreach (var nativeInput in new[] { false, true })
        {
            using var image = Image([0.000001f, 0.0001f, 0.18f, 1], halfStorage: nativeInput);
            using var target = Surface(context, SKColorType.RgbaF16, 1, 1);
            Draw(target, image);
            using var native = new SKBitmap(new SKImageInfo(1, 1, SKColorType.RgbaF16, SKAlphaType.Premul));
            if (!target.ReadPixels(native.Info, native.GetPixels(), native.RowBytes, 0, 0))
                throw new InvalidOperationException("Native F16 readback failed.");
            var bits = new short[4];
            Marshal.Copy(native.GetPixels(), bits, 0, 4);
            var stored = (float)BitConverter.Int16BitsToHalf(bits[0]);
            var converted = Read(target, 1)[0];
            details.Add($"{(nativeInput ? "F16" : "F32")} input: native stored red {stored:G9}, F32 readback red {converted:G9}");
            // F32 GPU upload is diagnostic only on a backend without F32 textures. The native
            // F16 path must work, and CPU conversion from F32 is also required for migration.
            if ((nativeInput || context is null) && (stored == 0 || converted == 0)) lost = true;
        }
        var message = string.Join("; ", details);
        if (lost) throw new InvalidOperationException(message);
        return message;
    }
}
