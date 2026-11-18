using System.Diagnostics;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class CpuFallbackChecks
{
    private const string Identity = "uniform shader src; half4 main(float2 p) { return src.eval(p); }";
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest);

    public static void Run(ProbeReport report)
    {
        Check(report, "all finite half storage values", FiniteStorage);
        Check(report, "shadow/HDR identity and exposure", Exposure);
        Check(report, "linear sampling", Sampling);
        Check(report, "premultiplied blending", Blending);
        Check(report, "Rec.2020 conversion", ColorConversion);
        Check(report, "repeated shadow storage", Repeated);
        Check(report, "invalid input rejection", InvalidInput);
        Check(report, "storage staging smoke benchmark", Benchmark);
    }

    private static void Check(ProbeReport report, string name, Func<string> test)
    {
        var clock = Stopwatch.StartNew();
        try { report.Checks.Add(new ProbeCheck("CPU fallback / " + name, "PASS", test(), clock.Elapsed.TotalMilliseconds)); }
        catch (Exception ex) { report.Checks.Add(new ProbeCheck("CPU fallback / " + name, "FAIL", ex.ToString(), clock.Elapsed.TotalMilliseconds)); }
    }

    private static SKRuntimeEffect Effect(string source)
    {
        var effect = SKRuntimeEffect.CreateShader(source, out var errors);
        return effect ?? throw new InvalidOperationException(errors);
    }

    private static string FiniteStorage()
    {
        // Exercise every finite bit pattern, including both zeros, both signs, and subnormals.
        var bits = Enumerable.Range(0, 65536).Where(i => (i & 0x7C00) != 0x7C00).Select(i => unchecked((short)i)).ToArray();
        var roundTrip = CpuHalfStorage.Pack(CpuHalfStorage.Unpack(bits));
        if (!bits.SequenceEqual(roundTrip)) throw new InvalidOperationException("Finite half bit patterns changed in storage round trip.");
        return $"{bits.Length} finite half patterns round-trip bit-exactly through F32; includes signed zero and subnormals.";
    }

    private static void Close(short[] actual, float[] expected, double relative = 0.002, double absolute = 0.00000006)
    {
        var values = CpuHalfStorage.Unpack(actual);
        for (var i = 0; i < expected.Length; i++)
            if (!float.IsFinite(values[i]) || Math.Abs(values[i] - expected[i]) > Math.Max(absolute, Math.Abs(expected[i]) * relative))
                throw new InvalidOperationException($"Sample {i}: expected {expected[i]:G9}, got {values[i]:G9}.");
    }

    private static string Exposure()
    {
        var input = CpuHalfStorage.Pack([-0.25f, 0.000001f, 0.18f, 1, 4, 16, 0.0001f, 1]);
        using var identity = Effect(Identity);
        var copied = CpuHalfStorage.Render(input, 2, 1, identity, Nearest);
        if (!input.SequenceEqual(copied)) throw new InvalidOperationException("F32 shader identity changed stored half values.");
        using var exposure = Effect("uniform shader src; half4 main(float2 p) { float4 c = float4(src.eval(p)); return half4(c.rgb * exp2(-2.0), c.a); }");
        var result = CpuHalfStorage.Render(input, 2, 1, exposure, Nearest);
        var expected = CpuHalfStorage.Unpack(input).Select((v, i) => i % 4 == 3 ? v : v * 0.25f).ToArray();
        if (!result.SequenceEqual(CpuHalfStorage.Pack(expected))) throw new InvalidOperationException("Exposure differs from explicitly rounded F32 reference.");
        if (CpuHalfStorage.Unpack(result)[1] == 0) throw new InvalidOperationException("Shadow value was flushed to zero.");
        return "Identity bit-exact; -2 EV matches explicit F32 reference rounding; 1e-6 shadow remains nonzero after darkening.";
    }

    private static string Sampling()
    {
        var input = CpuHalfStorage.Pack([0, 0, 0, 1, 4, 2, 1, 1]);
        using var effect = Effect("uniform shader src; half4 main(float2 p) { return src.eval(float2(1.0,0.5)); }");
        var result = CpuHalfStorage.Render(input, 2, 1, effect, new SKSamplingOptions(SKFilterMode.Linear));
        Close(result, [2, 1, 0.5f, 1, 2, 1, 0.5f, 1]);
        return "Linear sampling retains HDR values and matches the midpoint reference.";
    }

    private static string Blending()
    {
        var foreground = CpuHalfStorage.Pack([0.5f, 0.125f, 0, 0.5f]);
        var background = CpuHalfStorage.Pack([0.2f, 0.4f, 2, 1]);
        using var effect = Effect(Identity);
        Close(CpuHalfStorage.Render(foreground, 1, 1, effect, Nearest, SKBlendMode.SrcOver, background), [0.6f, 0.325f, 1, 1]);
        return "F32 SrcOver with F16 storage matches premultiplied reference without clipping HDR input.";
    }

    private static string ColorConversion()
    {
        double[] m = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
        var input = CpuHalfStorage.Pack([1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1, 0.000001f, 0.000001f, 0.000001f, 1, -0.1f, 2, 4, 1]);
        var values = CpuHalfStorage.Unpack(input);
        var expected = new float[values.Length];
        for (var p = 0; p < values.Length / 4; p++)
        {
            for (var row = 0; row < 3; row++)
                expected[p * 4 + row] = (float)Enumerable.Range(0, 3).Sum(c => m[row * 3 + c] * values[p * 4 + c]);
            expected[p * 4 + 3] = 1;
        }
        using var effect = Effect("uniform shader src; half4 main(float2 p) { float4 c=float4(src.eval(p)); float3 v=c.rgb; return half4(float3(dot(float3(.627403896,.329283038,.043313066),v),dot(float3(.069097289,.919540395,.011362316),v),dot(float3(.016391439,.088013308,.895595253),v)),c.a); }");
        var result = CpuHalfStorage.Render(input, 5, 1, effect, Nearest);
        Close(result, expected);
        if (CpuHalfStorage.Unpack(result)[12] == 0) throw new InvalidOperationException("Rec.2020 conversion lost the shadow neutral.");
        return "Primaries, subnormal neutral and extended-range color agree with a double matrix reference within F16 rounding tolerance.";
    }

    private static string Repeated()
    {
        var values = new List<float>();
        for (var ev = -24; ev <= 4; ev++) values.AddRange([MathF.Pow(2, ev), -MathF.Pow(2, ev), 0.18f, 1]);
        var first = CpuHalfStorage.Pack(values.ToArray());
        var current = first;
        using var effect = Effect(Identity);
        for (var pass = 0; pass < 32; pass++) current = CpuHalfStorage.Render(current, current.Length / 4, 1, effect, Nearest);
        if (!first.SequenceEqual(current)) throw new InvalidOperationException("Repeated F16 storage introduced drift or lost a shadow value.");
        return "32 shader/storage cycles remain bit-exact for both signs from 2^-24 through 16x white.";
    }

    private static string InvalidInput()
    {
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 65536f, -65536f })
        {
            try { CpuHalfStorage.Pack([value]); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidOperationException("Non-finite/overflow input was not rejected.");
        }
        try { CpuHalfStorage.Unpack([unchecked((short)0x7C00)]); }
        catch (ArgumentOutOfRangeException) { return "Rejects non-finite/overflow samples instead of clipping them or persisting infinity."; }
        throw new InvalidOperationException("Non-finite stored half input was accepted.");
    }

    private static string Benchmark()
    {
        const int edge = 1024;
        var values = new float[edge * edge * 4];
        for (var i = 0; i < values.Length; i += 4)
        {
            values[i] = values[i + 1] = values[i + 2] = 0.18f;
            values[i + 3] = 1;
        }
        var input = CpuHalfStorage.Pack(values);
        using var effect = Effect(Identity);
        var times = new List<double>();
        long allocation = 0;
        for (var run = 0; run < 4; run++)
        {
            var before = GC.GetTotalAllocatedBytes(true);
            var clock = Stopwatch.StartNew();
            var result = CpuHalfStorage.Render(input, edge, edge, effect, Nearest);
            if (result[0] != input[0] || result[^1] != input[^1]) throw new InvalidOperationException("Benchmark result mismatch.");
            if (run > 0)
            {
                times.Add(clock.Elapsed.TotalMilliseconds);
                allocation += GC.GetTotalAllocatedBytes(true) - before;
            }
        }
        times.Sort();
        return $"1024x1024 median {times[1]:F2} ms; mean managed allocation {allocation / 3 / 1_000_000.0:F2} MB; F16 persistent buffer {input.Length * 2 / 1_000_000.0:F2} MB. Whole-image F32 staging, not a production memory/performance target.";
    }
}
