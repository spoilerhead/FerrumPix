using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class CompositeChecks
{
    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try { report.Checks.Add(new ProbeCheck("Working composite / " + name, "PASS", run())); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Working composite / " + name, ex.ToString())); }
    }

    internal static void Run(ProbeReport report)
    {
        Check(report, "independent linear premultiplied over/mask references", Reference);
        Check(report, "coverage ownership, identities and contracts", Contracts);
        Check(report, "encoded UI background to working light", Background);
    }

    internal static ProbeCheck MeasureGpu(GRContext context)
    {
        try
        {
            double maximum = 0; var compared = 0;
            foreach (var domain in Enum.GetValues<WorkingImageDomain>())
            {
                using var lower = LinearWorkingFrame.Create(97, 3, domain, (y, row) =>
                {
                    for (var x = 0; x < 97; x++)
                    {
                        row[x * 4] = (x % 19 - 7) * .75f;
                        row[x * 4 + 1] = MathF.Pow(2, -24 + x % 26);
                        row[x * 4 + 2] = -.25f + y * 2;
                        row[x * 4 + 3] = new float[] { 0, .125f, .5f, 1 }[x % 4];
                    }
                });
                using var upper = LinearWorkingFrame.Create(97, 3, domain, (y, row) =>
                {
                    for (var x = 0; x < 97; x++)
                    {
                        row[x * 4] = (x % 17 - 5) * .5f;
                        row[x * 4 + 1] = MathF.Pow(2, -24 + x % 23);
                        row[x * 4 + 2] = 8 - y * 3;
                        row[x * 4 + 3] = new float[] { .5f, 0, 1, .25f }[x % 4];
                    }
                });
                foreach (var opacity in new[] { 0f, .17f, .5f, 1f })
                foreach (var over in new[] { false, true })
                {
                    using var cpu = over ? WorkingComposite.SourceOver(upper, lower, opacity) : WorkingComposite.Mix(lower, upper, opacity);
                    using var gpu = WorkingCompositeGpu.Render(context, upper, lower, opacity, over);
                    var expected = new float[388]; var actual = new float[388]; var top = new float[388]; var bottom = new float[388];
                    for (var y = 0; y < 3; y++)
                    {
                        cpu.ReadLinearRow(y, expected); gpu.ReadLinearRow(y, actual); upper.ReadLinearRow(y, top); lower.ReadLinearRow(y, bottom);
                        for (var i = 0; i < actual.Length; i++)
                        {
                            var error = Math.Abs(actual[i] - expected[i]); maximum = Math.Max(maximum, error);
                            // Half storage error plus a small F32 cancellation budget
                            // tied to operand magnitude, not an arbitrary shadow floor.
                            var budget = Math.Max(1.2e-7, Math.Max(Math.Abs(expected[i]) * .002, Math.Max(Math.Abs(top[i]), Math.Abs(bottom[i])) * 5e-7));
                            if (!float.IsFinite(actual[i]) || error > budget)
                                throw new InvalidOperationException($"GPU composite differs at {domain}/{opacity}/{over}/{y}/{i}: {actual[i]} vs {expected[i]} (budget {budget}).");
                            compared++;
                        }
                    }
                    if (gpu.Domain != domain) throw new InvalidOperationException("GPU composite changed domain.");
                }
            }
            return new ProbeCheck("GPU / working composite kernels", "PASS",
                $"Actual native F16 source-over and unmasked result-mix kernels match explicit-half CPU across both domains, opacity, transparency, signed/HDR values and 2^-24 shadows. {compared} channel comparisons; max absolute error {maximum:G6}. Relative budget 0.2% plus operand-scaled F32 cancellation allowance. F32 coverage masks stay on CPU.");
        }
        catch (Exception ex) { return ProbeCheck.Failure("GPU / working composite kernels", ex.ToString()); }
    }
    private static string Reference()
    {
        float[] lower = [.1f, -.1f, 3, .5f, 0, 0, 0, 0, -.5f, 2, .125f, 1, 0, 0, 0, 0];
        float[] upper = [1, .2f, -.2f, .5f, 8, -1, .25f, 1, .5f, .75f, .1f, .25f, 0, 0, 0, 0];
        foreach (var domain in Enum.GetValues<WorkingImageDomain>())
        {
            using var bottom = LinearWorkingFrame.FromLinearPixels(4, 1, lower, domain);
            using var top = LinearWorkingFrame.FromLinearPixels(4, 1, upper, domain);
            float[] b = new float[16], t = new float[16]; bottom.ReadLinearRow(0, b); top.ReadLinearRow(0, t);
            foreach (var opacity in new[] { 0f, .17f, .5f, 1f })
            foreach (var maskValues in new[] { new float[] { 0, 0, 0, 0 }, new float[] { 1, 1, 1, 1 }, new float[] { .001f, .1234567f, .50123f, 1 } })
            foreach (var over in new[] { false, true })
            {
                var mask = new WorkingCoverage(4, 1, maskValues);
                using var result = over ? WorkingComposite.SourceOver(top, bottom, opacity, mask) : WorkingComposite.Mix(bottom, top, opacity, mask);
                var actual = new float[16]; result.ReadLinearRow(0, actual);
                for (var x = 0; x < 4; x++) for (var c = 0; c < 4; c++)
                {
                    var i = x * 4 + c; var w = opacity * (double)maskValues[x];
                    var expected = t[i] * w + b[i] * (over ? 1 - t[x * 4 + 3] * w : 1 - w);
                    var half = (float)(Half)(float)expected;
                    if (actual[i] != half) throw new InvalidOperationException("Independent coverage/over result differs.");
                }
                if (result.Domain != domain) throw new InvalidOperationException("Composite changed domain.");
            }
            var unchanged = new float[16]; top.ReadLinearRow(0, unchanged);
            if (!unchanged.SequenceEqual(t)) throw new InvalidOperationException("Composite mutated top.");
        }
        // Two adjustment results with identical coverage must retain that coverage,
        // rather than increasing .5 to .75 as source-over would.
        using var partial = LinearWorkingFrame.FromLinearPixels(1, 1, [2, -.2f, .3f, .5f], WorkingImageDomain.RenderedLinear);
        using var mixed = WorkingComposite.Mix(partial, partial, .5f);
        var values = new float[4]; mixed.ReadLinearRow(0, values);
        if (values[3] != .5f) throw new InvalidOperationException("Adjustment mixing increased alpha.");
        return "Independent Double Porter-Duff source-over and full-result interpolation match after one half rounding, across both domains, signed/HDR samples, transparency, opacity and fractional F32 masks. Adjustment alpha remains .5, not .75.";
    }
    private static string Contracts()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(1, 1, [1, -.1f, .000000059604644775390625f, .5f], WorkingImageDomain.RenderedLinear);
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        var values = new[] { .1234567f }; var mask = new WorkingCoverage(1, 1, values); values[0] = 1;
        if (mask.At(0, 0) != .1234567f) throw new InvalidOperationException("Coverage snapshot aliases caller array.");
        foreach (var reject in new Action[] {
            () => _ = new WorkingCoverage(1, 1, [float.NaN]), () => _ = new WorkingCoverage(1, 1, [-.01f]),
            () => _ = new WorkingCoverage(1, 1, [1.01f]), () => _ = new WorkingCoverage(2, 1, [1]),
            () => { using var r = WorkingComposite.SourceOver(source, scene); },
            () => { using var r = WorkingComposite.Mix(source, source, float.PositiveInfinity); },
            () => { using var r = WorkingComposite.Mix(source, source, coverage: new WorkingCoverage(2, 1, [1, 1])); } })
        {
            try { reject(); throw new Exception("Invalid composite accepted."); } catch (ArgumentException) { }
        }
        var before = new float[4]; source.ReadLinearRow(0, before);
        using var zero = WorkingComposite.Mix(source, source, 0);
        using var full = WorkingComposite.Mix(source, source, 1);
        var actual = new float[4];
        foreach (var result in new[] { zero, full })
        {
            result.ReadLinearRow(0, actual);
            if (ReferenceEquals(result, source) || !before.SequenceEqual(actual)) throw new InvalidOperationException("Mix identity changed ownership or subnormals.");
        }
        return "F32 masks are cloned and validated; opacity/domain/dimension errors fail explicitly; mask endpoints and smallest F16 shadow samples are exact, with independent result ownership.";
    }
    private static string Background()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(2, 1, [2, -.1f, .25f, .5f, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        var input = new float[8]; source.ReadLinearRow(0, input);
        foreach (var color in new[] { "#00000000", "#FFFFFFFF", "#801234AB" })
        {
            using var result = WorkingComposite.FillBackground(source, color); var actual = new float[8]; result.ReadLinearRow(0, actual);
            var codes = Convert.FromHexString(color[1..]); var alpha = codes[0] / 255.0;
            var rgb = codes.Skip(1).Select(v => RenderedContrastChecks.Decode(v / 255.0)).ToArray();
            double[] matrix = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
            var rec = RenderedContrastChecks.Matrix(matrix, rgb).Append(1.0).Select(v => v * alpha).ToArray();
            for (var x = 0; x < 2; x++) for (var c = 0; c < 4; c++)
            {
                var i = x * 4 + c;
                var expected = (float)(Half)(float)(input[i] + (1 - input[x * 4 + 3]) * rec[c]);
                if (actual[i] != expected) throw new InvalidOperationException("Background profile/alpha reference differs.");
            }
        }
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [0, 0, 0, 0], WorkingImageDomain.SceneLinear);
        try { using var rejected = WorkingComposite.FillBackground(scene, "#FFFFFFFF"); throw new Exception("Scene background accepted."); } catch (ArgumentException) { }
        return "Independent sRGB decoding, Rec.2020 matrix and premultiplied Double background reference pass for transparent, white and partial-alpha chromatic UI colors. Scene-radiance background use rejected.";
    }
    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var cube = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? ProfiledPngFixture.Read(path) : null;
        using var source = cube?.Frame ?? RenderedContrastChecks.Input(path);
        using var adjusted = RenderedBasicTone.Apply(source, 30, 20);
        var coverage = new float[source.Width * source.Height];
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++) coverage[y * source.Width + x] = x / (float)Math.Max(1, source.Width - 1);
        using var mix = WorkingComposite.Mix(source, adjusted, coverage: new WorkingCoverage(source.Width, source.Height, coverage));
        SceneRenderingChecks.SavePreview(source, output, $"{name}-composite-zero.png");
        SceneRenderingChecks.SavePreview(adjusted, output, $"{name}-composite-adjusted.png");
        SceneRenderingChecks.SavePreview(mix, output, $"{name}-composite-mask-ramp.png");
        using var transformed = WorkingGeometry.Apply(source, new ImageAdjustments { GeometryOperations = [new() { Kind = "transform", Adjustments = new() { StraightenDegrees = 12, StraightenExpandCanvas = true } }] });
        SceneRenderingChecks.SavePreview(transformed, output, $"{name}-composite-transparent.png");
        using var white = WorkingComposite.FillBackground(transformed, "#FFFFFFFF");
        using var color = WorkingComposite.FillBackground(transformed, "#FF3468B2");
        SceneRenderingChecks.SavePreview(white, output, $"{name}-composite-white.png");
        SceneRenderingChecks.SavePreview(color, output, $"{name}-composite-color.png");
        report.Checks.Add(new ProbeCheck($"Working composite / {name}", "PASS", "F16 adjustment-mask ramp and linear background previews generated; full source profiles retained until diagnostic SDR output."));
    }
    internal static void WriteReview(string output, IReadOnlyList<string> images) =>
        StageReview.Write(output, images, "composite", "composite-review.html", "Linear masks and document backgrounds",
            "Compare zero, adjusted (brightness 30/contrast 20) and mask-ramp: original at left, adjusted at right, smoothly mixed in linear light. Check transparency and white/blue document backgrounds after expanded straighten. Source-over and adjustment mixing have different alpha equations. This implements CPU normal compositing only; masks from the editor, artistic blend modes, objects and production routing are not connected.",
            new[] { ("zero", false), ("adjusted", false), ("mask-ramp", false), ("transparent", false), ("white", false), ("color", false) });
}
