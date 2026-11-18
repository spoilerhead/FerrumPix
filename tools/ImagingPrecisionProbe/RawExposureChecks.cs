using System.Diagnostics;
using System.Net;
using System.Text;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

internal static class RawExposureChecks
{
    internal static void Run(ProbeReport report)
    {
        try
        {
            Reference();
            Guards();
            report.Checks.Add(new ProbeCheck("RAW exposure policy / reference and guards", "PASS", "Independent legacy-coordinate tone reference across -2/-0.5/0/+0.5/+2 EV, camera base exposures, normalization below/above 1, partial alpha, hard/soft shoulders; neutral ownership and invalid settings checked."));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("RAW exposure policy / reference and guards", ex.ToString())); }
    }

    private static void Reference()
    {
        const int width = 256;
        var pixels = new float[width * 4];
        for (var x = 0; x < width; x++)
        {
            var value = (float)Math.Pow(2, -17 + x * 20.0 / (width - 1));
            var alpha = x % 2 == 0 ? 1f : .5f;
            pixels[x * 4] = pixels[x * 4 + 1] = pixels[x * 4 + 2] = value * alpha;
            pixels[x * 4 + 3] = alpha;
        }
        foreach (var norm in new[] { .5, 1, 2.13379 })
        foreach (var baseEv in new[] { -.316, .28 })
        {
            using var source = new RawDecodeService.SceneSource(LinearWorkingFrame.FromLinearPixels(width, 1, pixels, WorkingImageDomain.SceneLinear), norm, baseEv);
            var before = new float[pixels.Length]; source.Frame.ReadLinearRow(0, before);
            foreach (var ev in new[] { -2.0, -.5, 0, .5, 2 })
            foreach (var knee in new[] { .05, .45, 1 })
            {
                using var result = RawSceneDevelopment.Render(source, ev, knee < 1, knee, false);
                var after = new float[pixels.Length]; result.ReadLinearRow(0, after);
                if (result.Domain != WorkingImageDomain.RenderedLinear) throw new InvalidOperationException("Wrong rendering domain.");
                for (var x = 0; x < width; x++)
                {
                    var alpha = before[x * 4 + 3];
                    // Reference in the OLD cache coordinate system, independently of ParametersFor.
                    var cacheValue = before[x * 4] / (alpha * norm);
                    var oldEv = baseEv + ev + (norm > 1 ? Math.Log2(norm) : 0);
                    var oldBlack = .003 / Math.Pow(2, Math.Max(0, ev)) / Math.Max(1, norm);
                    var expected = Tone(cacheValue, oldEv, oldBlack, knee) * alpha;
                    for (var c = 0; c < 3; c++)
                        if (Math.Abs(after[x * 4 + c] - expected) > Math.Max(.00015, Math.Abs(expected) * .004))
                            throw new InvalidOperationException($"Legacy tone mismatch: norm={norm}, base={baseEv}, EV={ev}, knee={knee}, x={x}: {after[x * 4 + c]} != {expected}.");
                    if (alpha != after[x * 4 + 3]) throw new InvalidOperationException("Exposure policy changed alpha.");
                }
            }
            var unchanged = new float[pixels.Length]; source.Frame.ReadLinearRow(0, unchanged);
            if (!before.SequenceEqual(unchanged)) throw new InvalidOperationException("Development modified its source.");
        }

        // The principal regression: -2 EV must reduce the rendering black point by four.
        var minus = RawSceneDevelopment.ParametersFor(.28, 2, -2, true, .45);
        var plus = RawSceneDevelopment.ParametersFor(.28, 2, 2, false, .05);
        if (Math.Abs(minus.BlackLevel - .00075) > 1e-12 || Math.Abs(plus.BlackLevel - .003) > 1e-12 || plus.Knee != 1)
            throw new InvalidOperationException("Black policy or disabled recovery is incorrect.");
        var adjustments = new ImageAdjustments { Exposure = (float)(-2 * ImageAdjustments.ExposurePointsPerStop), RawHighlightRecovery = true, Highlights = -100 };
        if (adjustments.RawExposureEv() != -2 || Math.Abs(adjustments.RawHighlightKnee() - RawDecodeService.HighlightKneeAtFullLowering) > 1e-12)
            throw new InvalidOperationException("App RAW control mapping changed.");
    }

    private static double Tone(double value, double ev, double black, double knee)
    {
        var slope = 1 / (Math.Pow(2, -ev) - black);
        var radius = Math.Min(.5 * black, 1 / (16 * slope));
        double v;
        if (value <= black - radius) v = 0;
        else if (value < black + radius) v = slope / (4 * radius) * Math.Pow(value - black + radius, 2);
        else
        {
            v = (value - black) * slope;
            if (knee < 1 && v > knee) v = 1 - (1 - knee) * Math.Exp(-(v - knee) / (1 - knee));
        }
        return RawDecodeService.Acr3CurveAt(Math.Clamp(v, 0, 1));
    }

    private static void Guards()
    {
        foreach (var call in new Action[] {
            () => RawSceneDevelopment.ParametersFor(0, double.NaN, 0, true, .45),
            () => RawSceneDevelopment.ParametersFor(0, 0, 0, true, .45),
            () => RawSceneDevelopment.ParametersFor(0, 1, double.NaN, true, .45),
            () => RawSceneDevelopment.ParametersFor(0, 1, 11, true, .45),
            () => RawSceneDevelopment.ParametersFor(0, 1, 0, false, double.NaN) })
        {
            try { call(); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Invalid RAW exposure parameters accepted.");
        }
    }

    internal static void Capture(ProbeReport report, string output, RawDecodeService.SceneSource source, string name)
    {
        // Keep the accepted repair-on/fixed-black rendering as the visual reference.
        using var repaired = RawDecodeService.RepairSceneHighlights(source);
        foreach (var ev in new[] { -2.0, 0, 2.0 })
        {
            var setting = ev < 0 ? "minus2ev" : ev > 0 ? "plus2ev" : "neutral";
            using var exposed = SceneFrameOperations.ApplyExposure(repaired, ev);
            SceneRenderingChecks.Capture(report, output, exposed, source.BaseExposureEv, name, setting + "-previous", true);
            Save(report, output, source, name, setting, ev, true, .45);
        }
        Save(report, output, source, name, "recovery-off", -2, false, .45);
        Save(report, output, source, name, "strong-shoulder", -2, true, .05);
    }

    private static void Save(ProbeReport report, string output, RawDecodeService.SceneSource source, string name, string setting, double ev, bool recovery, double knee)
    {
        var clock = Stopwatch.StartNew();
        using var rendered = RawSceneDevelopment.Render(source, ev, recovery, knee);
        var dimensions = SceneRenderingChecks.SavePreview(rendered, output, $"{name}-raw-policy-{setting}.png");
        report.Checks.Add(new ProbeCheck($"RAW policy preview / {name} / {setting}", "PASS", $"{dimensions}; user EV {ev}, recovery {recovery}, requested knee {knee}; original repair before exposure; shared app tone-ramp policy, continuous F32/F16 rendering. Other RAW corrections not migrated.", clock.Elapsed.TotalMilliseconds));
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>RAW exposure policy</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}h2{margin-top:40px}.pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:520px;object-fit:contain;background:#111}a{color:#bce0ff}p{max-width:1100px;line-height:1.5}</style>
            <h1>RAW exposure / black-point migration</h1><p>Left: accepted F16 rendering with fixed black point. Right: app-equivalent RAW exposure/black-point policy. Both use the accepted highlight color repair before exposure. Neutral and +2 EV should match; -2 EV should retain slightly more shadow detail. Click an image for the full preview.</p><p>The final pair uses -2 EV and compares highlight recovery off (hard clipping) against a strong shoulder (knee 0.05). Highlight color repair remains on in both. Lens/geometry/shadow-saturation corrections are still omitted. <a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            if (!RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(images[i]), StringComparer.OrdinalIgnoreCase)) continue;
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2>");
            foreach (var setting in new[] { "minus2ev", "neutral", "plus2ev" })
            {
                html.Append($"<h3>{setting}</h3><div class='pair'>");
                Figure(html, $"image-{i + 1}-scene-{setting}-previous.png", "Previous fixed black point");
                Figure(html, $"image-{i + 1}-raw-policy-{setting}.png", "App RAW black-point policy");
                html.Append("</div>");
            }
            html.Append("<h3>−2 EV: highlight shoulder</h3><div class='pair'>");
            Figure(html, $"image-{i + 1}-raw-policy-recovery-off.png", "Highlight recovery off");
            Figure(html, $"image-{i + 1}-raw-policy-strong-shoulder.png", "Strong recovery shoulder");
            html.Append("</div>");
        }
        File.WriteAllText(Path.Combine(output, "raw-exposure-review.html"), html.Append("</html>").ToString());
    }

    private static void Figure(StringBuilder html, string file, string label) => html.Append($"<figure><figcaption>{label}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{label}'></a></figure>");
}
