using System.Net;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class RenderedContrastChecks
{
    private static readonly double[] ToSrgb = [1.660491002, -.587641139, -.072849863, -.124550475, 1.132899897, -.008349422, -.018150763, -.100578898, 1.118729661];
    private static readonly double[] ToRec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
    private static readonly int[] Sliders = [-40, 0, 40];

    internal static void Run(ProbeReport report)
    {
        Check(report, "measured curves and RGB tone reference", Reference);
        Check(report, "identity, ownership and invalid contracts", Contracts);
        Check(report, "neutral ramp monotonicity", Ramp);
    }

    private static void Check(ProbeReport report, string name, Func<string> action)
    {
        try { report.Checks.Add(new ProbeCheck("Rendered contrast / " + name, "PASS", action())); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Rendered contrast / " + name, ex.ToString())); }
    }

    // Independent double evaluation of the measured contrast curve and its slope
    // budget. Do not call the application's table builder or RGB tone kernel here.
    internal static double[] Curve(float slider)
    {
        const int intervals = 4096;
        const double minimumSlope = .2;
        var result = Enumerable.Range(0, intervals + 1).Select(i => i / (double)intervals + ToneSliderCurves.Lift("Contrast", slider, i / (double)intervals)).ToArray();
        var slopes = Enumerable.Range(0, intervals).Select(i => (result[i + 1] - result[i]) * intervals).ToArray();
        var deficit = slopes.Sum(s => Math.Max(0, minimumSlope - s)) / intervals;
        if (deficit > 0)
        {
            var surplus = slopes.Sum(s => Math.Max(0, s - minimumSlope)) / intervals;
            var keep = Math.Max(0, 1 - deficit / surplus);
            for (var i = 0; i < intervals; i++) result[i + 1] = result[i] + (minimumSlope + Math.Max(0, slopes[i] - minimumSlope) * keep) / intervals;
        }
        return result.Select(v => Math.Clamp(v, 0, 1)).ToArray();
    }

    internal static double Sample(double[] curve, double value)
    {
        var position = Math.Clamp(value, 0, 1) * (curve.Length - 1);
        var i = Math.Min(curve.Length - 2, (int)position);
        return curve[i] + (curve[i + 1] - curve[i]) * (position - i);
    }

    internal static double[] Matrix(double[] m, double[] v) => [m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2]];
    internal static double Encode(double x) => x <= .0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - .055;
    internal static double Decode(double x) => x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4);

    private static string Reference()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(4, 2,
            [.000001f, .000001f, .000001f, 1, .02f, .04f, .01f, 1, .3f, .2f, .1f, 1, .7f, .8f, .6f, 1,
             3, -.25f, .8f, 1, .08f, .03f, .01f, .5f, 4, 4, 4, 1, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        foreach (var slider in new[] { -100f, -50, -40, 40, 50, 100 })
        {
            var curve = Curve(slider);
            using var result = RenderedContrast.Apply(source, slider);
            var input = new float[16]; var output = new float[16];
            for (var y = 0; y < source.Height; y++)
            {
                source.ReadLinearRow(y, input); result.ReadLinearRow(y, output);
                for (var x = 0; x < source.Width; x++)
                {
                    var at = x * 4;
                    var alpha = (double)input[at + 3];
                    if (output[at + 3] != alpha) throw new InvalidOperationException("Contrast changed alpha.");
                    if (alpha == 0) continue;
                    double[] rgb = [input[at] / alpha, input[at + 1] / alpha, input[at + 2] / alpha];
                    var encoded = Matrix(ToSrgb, rgb).Select(v => Encode(Math.Clamp(v, 0, 1))).ToArray();
                    var low = encoded.Min(); var high = encoded.Max();
                    var bottom = Sample(curve, low); var top = Sample(curve, high);
                    var mapped = encoded.Select(v => high - low < 1e-6 ? top : bottom + (v - low) * (top - bottom) / (high - low)).ToArray();
                    var delta = Matrix(ToRec, Enumerable.Range(0, 3).Select(c => Decode(mapped[c]) - Decode(encoded[c])).ToArray());
                    for (var c = 0; c < 3; c++)
                    {
                        var expected = input[at + c] + delta[c] * alpha;
                        if (!float.IsFinite(output[at + c]) || Math.Abs(output[at + c] - expected) > Math.Max(2e-7, Math.Abs(expected) * .001))
                            throw new InvalidOperationException($"Contrast {slider}, pixel {x},{y}/{c}: {output[at + c]} != {expected}.");
                    }
                }
            }
            result.ReadLinearRow(1, output);
            if (output[0] <= 1 || output[1] >= 0 || output[8] != 4) throw new InvalidOperationException("Contrast clipped original signed/HDR reserves.");
        }
        return "Independent double measured-curve/slope-budget/min-max RGB-tone reference at +/-40/50/100, with linear-sRGB delta returned to Rec.2020. Includes saturated/neutral colors, subnormal shadow, signed/HDR channels and partial/zero alpha.";
    }

    private static string Contracts()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(2, 1, [-.25f, .000001f, 4, 1, .08f, .03f, .01f, .5f], WorkingImageDomain.RenderedLinear);
        var before = new float[8]; source.ReadLinearRow(0, before);
        using (var zero = RenderedContrast.Apply(source, 0))
        {
            var after = new float[8]; zero.ReadLinearRow(0, after);
            if (!before.SequenceEqual(after)) throw new InvalidOperationException("Neutral contrast is not exact.");
        }
        using (var changed = RenderedContrast.Apply(source, 40)) { }
        var unchanged = new float[8]; source.ReadLinearRow(0, unchanged);
        if (!before.SequenceEqual(unchanged)) throw new InvalidOperationException("Contrast modified its source.");
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, -101, 101 }) Reject(() => { using var result = RenderedContrast.Apply(source, invalid); });
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        Reject(() => { using var result = RenderedContrast.Apply(scene, 0); });
        return "Exact zero on signed/HDR/subnormal/alpha data; output lifetime independent of immutable source. Scene-linear and non-finite/out-of-range slider inputs rejected.";
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid contrast contract accepted.");
    }

    private static string Ramp()
    {
        using var ramp = LinearWorkingFrame.Create(4097, 1, WorkingImageDomain.RenderedLinear, (_, row) =>
        {
            for (var x = 0; x < 4097; x++)
            {
                var v = (float)Decode(x / 4096.0);
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = v;
                row[x * 4 + 3] = 1;
            }
        });
        foreach (var slider in new[] { -100f, 100 })
        {
            using var result = RenderedContrast.Apply(ramp, slider);
            var row = new float[4097 * 4]; result.ReadLinearRow(0, row);
            for (var x = 1; x < 4097; x++)
                if (row[x * 4] < row[(x - 1) * 4] || Math.Abs(row[x * 4] - row[x * 4 + 1]) > 1e-6 || Math.Abs(row[x * 4] - row[x * 4 + 2]) > 1e-6)
                    throw new InvalidOperationException("Contrast reverses a neutral ramp or introduces a cast.");
        }
        return "4097-step neutral ramp remains monotone and neutral at both slider extremes after F16 storage; no artificial source RGB8 ramp.";
    }

    internal static LinearWorkingFrame Input(string path)
    {
        if (RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            using var scene = RawDecodeService.ReadSceneSource(path, true, true);
            return RawSceneDevelopment.Render(scene, 0, true);
        }
        if (TiffPreviewService.IsSupportedTiff(path))
        {
            using var decoded = ProfiledTiffInput.Decode(path);
            return RenderedContrast.Apply(decoded.Frame, 0);
        }
        using var jpeg = ProfiledJpegInput.Decode(path);
        return RenderedContrast.Apply(jpeg.Frame, 0);
    }

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        using var source = Input(path);
        using var legacySource = WorkingImageBoundary.ToDiagnosticSdrSrgb8(source);
        foreach (var slider in Sliders)
        {
            using var result = RenderedContrast.Apply(source, slider);
            var setting = slider == 0 ? "zero" : slider < 0 ? "minus" : "plus";
            SceneRenderingChecks.SavePreview(result, output, $"{name}-contrast-{setting}.png");
            if (slider == 0) continue;
            // A visual compatibility reference from the SAME developed source, quantized
            // only for the legacy renderer. Different RAW input development would confound it.
            var legacyResult = ImageProcessor.RenderPreviewSkBitmap(legacySource, new ImageAdjustments { Contrast = slider })
                ?? throw new InvalidOperationException("Legacy contrast reference failed.");
            using var owned = ReferenceEquals(legacyResult, legacySource) ? legacyResult.Copy() : legacyResult;
            using var linear = WorkingImageBoundary.FromEncodedSrgb(owned);
            SceneRenderingChecks.SavePreview(linear, output, $"{name}-contrast-{setting}-legacy.png");
        }
        report.Checks.Add(new ProbeCheck($"Rendered contrast / {name}", "PASS", $"{Path.GetFileName(path)}: full-resolution neutral/-40/+40 with two legacy references from the same developed input. RAW lens/TCA/repair/rendering or embedded-ICC rendered input precedes contrast; other tools identity. These are visual artifacts, not automatic photographic look acceptance."));
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new System.Text.StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>F16 rendered contrast</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:440px;object-fit:contain;background:#111}a{color:#bce0ff}</style><h1>Rendered contrast</h1><p>Top row: contrast -40 / neutral / +40. Second row: old sRGB8 contrast -40 / neutral / +40 using the SAME developed source. Check skin, gradients, dark texture and saturated colors. The new path retains floating working values; the old reference is quantized/clipped at its input. Wide-gamut differences can therefore be intentional. All review PNGs are diagnostic SDR sRGB. No brightness, local highlight/shadow, white/black, curves or masks are active. <a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2><div class='grid'>");
            foreach (var setting in new[] { "minus", "zero", "plus", "minus-legacy", "zero", "plus-legacy" })
            {
                var file = $"image-{i + 1}-contrast-{setting}.png";
                html.Append($"<figure><figcaption>{setting}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{setting}'></a></figure>");
            }
            html.Append("</div>");
        }
        File.WriteAllText(Path.Combine(output, "rendered-contrast-review.html"), html.Append("</html>").ToString());
    }
}
