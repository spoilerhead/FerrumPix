using System.Diagnostics;
using System.Net;
using System.Text;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

internal static class HighlightRepairChecks
{
    private static readonly double[] ToSrgb = [1.660491002, -.587641139, -.072849863, -.124550475, 1.132899897, -.008349422, -.018150763, -.100578898, 1.118729661];
    private static readonly double[] ToRec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];

    internal static void Run(ProbeReport report)
    {
        try
        {
            float[] pixels = new float[24];
            double[][] colors = [[.2, .1, .2], [.8, .8, .8], [.1, .8, .1], [.95, .55, .9], [1.8, .6, 1.6], [.9, .45, .85]];
            for (var x = 0; x < colors.Length; x++)
            {
                var alpha = x == 5 ? .5f : 1f;
                for (var c = 0; c < 3; c++) pixels[x * 4 + c] = (float)(Dot(ToRec, c, colors[x]) * 2 * alpha);
                pixels[x * 4 + 3] = alpha;
            }
            using var source = new RawDecodeService.SceneSource(LinearWorkingFrame.FromLinearPixels(3, 2, pixels, WorkingImageDomain.SceneLinear), 2, .28);
            using var repaired = RawDecodeService.RepairSceneHighlights(source);
            var changed = Validate(source, repaired);
            if (changed.Changed < 3) throw new InvalidOperationException("Synthetic highlight repair did not affect the intended highlights.");
            var before = new float[12]; var after = new float[12];
            source.Frame.ReadLinearRow(0, before); repaired.ReadLinearRow(0, after);
            if (!before.SequenceEqual(after)) throw new InvalidOperationException("Unaffected shadows, neutrals or green colors changed.");
            using var exposed = SceneFrameOperations.ApplyExposure(repaired, -2);
            exposed.ReadLinearRow(1, after); repaired.ReadLinearRow(1, before);
            for (var i = 0; i < before.Length; i++)
                if (after[i] != (i % 4 == 3 ? before[i] : (float)(Half)(before[i] * .25f)))
                    throw new InvalidOperationException("Exposure must follow repair, not alter its threshold.");
            using var invalid = new RawDecodeService.SceneSource(LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.RenderedLinear), 1, 0);
            Reject(() => RawDecodeService.RepairSceneHighlights(invalid));
            using var badNorm = new RawDecodeService.SceneSource(LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear), double.NaN, 0);
            Reject(() => RawDecodeService.RepairSceneHighlights(badNorm));
            report.Checks.Add(new ProbeCheck("Highlight repair / reference, order and guards", "PASS", "Independent double reference; shadows/neutrals/green unchanged bit-for-bit; HDR and partial alpha handled; repair precedes exposure; domain and normalization guarded."));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Highlight repair / reference, order and guards", ex.ToString())); }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid repair input accepted.");
    }

    private static double Dot(double[] matrix, int row, double[] rgb) => matrix[row * 3] * rgb[0] + matrix[row * 3 + 1] * rgb[1] + matrix[row * 3 + 2] * rgb[2];

    private static (long Changed, long Bright, long Candidates) Validate(RawDecodeService.SceneSource source, LinearWorkingFrame repaired)
    {
        if (repaired.Domain != WorkingImageDomain.SceneLinear || repaired.Width != source.Frame.Width || repaired.Height != source.Frame.Height)
            throw new InvalidOperationException("Repair changed scene domain or geometry.");
        var before = new float[repaired.Width * 4]; var after = new float[before.Length];
        long changed = 0, bright = 0, candidates = 0;
        // Reuse these three-element arrays to keep the full-resolution reference bounded.
        var rec = new double[3]; var srgb = new double[3]; var delta = new double[3];
        for (var y = 0; y < repaired.Height; y++)
        {
            source.Frame.ReadLinearRow(y, before); repaired.ReadLinearRow(y, after);
            for (var x = 0; x < repaired.Width; x++)
            {
                var at = x * 4; var alpha = before[at + 3];
                if (alpha != after[at + 3]) throw new InvalidOperationException("Repair changed alpha.");
                if (alpha == 0) continue;
                for (var c = 0; c < 3; c++) rec[c] = before[at + c] / (alpha * source.Normalization);
                for (var c = 0; c < 3; c++) srgb[c] = Dot(ToSrgb, c, rec);
                var cr = Math.Clamp(srgb[0], 0, 1); var cg = Math.Clamp(srgb[1], 0, 1); var cb = Math.Clamp(srgb[2], 0, 1);
                var maximum = Math.Max(cr, Math.Max(cg, cb));
                if (maximum > .5) bright++;
                Array.Clear(delta);
                if (maximum > .5 && Math.Min(cr, cb) > cg)
                {
                    candidates++;
                    var t = Math.Min(1, (maximum - .5) * 2); var blend = t * t * (3 - 2 * t);
                    delta[0] = (cr - srgb[0]) * blend;
                    delta[1] = Math.Max(0, Math.Min(cr, cb) * .8 + (cr + cb) * .1 - cg) * blend;
                    delta[2] = (cb - srgb[2]) * blend;
                }
                var differs = false;
                for (var c = 0; c < 3; c++)
                {
                    var expected = before[at + c] + Dot(ToRec, c, delta) * source.Normalization * alpha;
                    if (!float.IsFinite(after[at + c]) || Math.Abs(after[at + c] - expected) > Math.Max(.00001, Math.Abs(expected) * .003))
                        throw new InvalidOperationException($"Highlight reference mismatch at {x},{y}, channel {c}: {after[at + c]} != {expected}.");
                    differs |= before[at + c] != after[at + c];
                }
                if (differs) changed++;
            }
        }
        return (changed, bright, candidates);
    }

    internal static void Capture(ProbeReport report, string output, RawDecodeService.SceneSource source, string name)
    {
        var clock = Stopwatch.StartNew();
        using var repaired = RawDecodeService.RepairSceneHighlights(source);
        var changed = Validate(source, repaired);
        foreach (var ev in new[] { -2.0, 0, 2.0 })
        {
            using var exposed = SceneFrameOperations.ApplyExposure(repaired, ev);
            var setting = ev < 0 ? "minus2ev" : ev > 0 ? "plus2ev" : "neutral";
            SceneRenderingChecks.Capture(report, output, exposed, source.BaseExposureEv, name, setting + "-repair-on", true);
        }
        report.Checks.Add(new ProbeCheck($"Highlight repair / {name}", "PASS", $"{changed.Changed:N0} changed pixels out of {repaired.Width * (long)repaired.Height:N0}; {changed.Bright:N0} above the cache-relative 0.5 brightness gate; {changed.Candidates:N0} also meet the green-deficit condition. Every pixel checked against independent double reference in cache-relative linear sRGB. Repair applied before exposure; original off previews retained.", clock.Elapsed.TotalMilliseconds));
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Highlight repair comparison</title>
            <style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}h2{margin-top:40px}.pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:560px;object-fit:contain;background:#111}figcaption{margin:8px 0}a{color:#bce0ff}p{max-width:1100px;line-height:1.5}</style>
            <h1>Highlight color repair: off / on</h1><p>Both columns use the new F16 scene pipeline. The right column applies the app's existing repair before exposure and tone mapping, with thresholds measured in cache-relative linear sRGB. Click a preview to inspect it at full preview size. No lens, geometry or camera-specific shadow-saturation corrections are included.</p><p><a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            if (!RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(images[i]), StringComparer.OrdinalIgnoreCase)) continue;
            html.Append($"<section><h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2>");
            foreach (var (setting, label) in new[] { ("minus2ev", "−2 EV"), ("neutral", "Neutral"), ("plus2ev", "+2 EV") })
            {
                html.Append($"<h3>{label}</h3><div class='pair'>");
                foreach (var on in new[] { false, true })
                {
                    var file = $"image-{i + 1}-scene-{setting}{(on ? "-repair-on" : "")}.png";
                    var caption = on ? "Repair on" : "Repair off";
                    html.Append($"<figure><figcaption>{caption}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{caption}'></a></figure>");
                }
                html.Append("</div>");
            }
            html.Append("</section>");
        }
        File.WriteAllText(Path.Combine(output, "highlight-repair-review.html"), html.Append("</html>").ToString());
    }
}
