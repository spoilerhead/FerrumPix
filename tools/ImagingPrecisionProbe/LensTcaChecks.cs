using System.Net;
using System.Text;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

internal static class LensTcaChecks
{
    internal static void Run(ProbeReport report)
    {
        try
        {
            const int width = 7, height = 5;
            var samples = new ushort[width * height * 3];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) for (var c = 0; c < 3; c++)
                samples[(y * width + x) * 3 + c] = (ushort)(1000 + x * x * (400 + c * 100) + y * 1800 + c * 700);
            var bytes = new byte[samples.Length * 2]; Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            float[] matrix = [1.4f, -.2f, -.2f, -.1f, 1.2f, -.1f, -.1f, -.2f, 1.3f];
            var lens = new LensDataService.Korrektur { HasChromaticAberration = true, NormScale = .25, CornerScale = .6, TcaVr = 1.08, TcaVb = .94, TcaCr = .03, TcaCb = -.02 };
            foreach (var vignette in new[] { false, true })
            {
                lens.HasVignetting = vignette; lens.Vk1 = -.2;
                using var result = RawDecodeService.BuildSceneFrame(width, height, bytes, matrix, 2, lens);
                Validate(result, samples, matrix, vignette, null);
            }
            lens.GainMaps = new DngGainMapService.PlaneGainMaps { ActiveWidth = width, ActiveHeight = height };
            lens.GainMaps.Maps.Add(new DngGainMapService.GainMap { Top = 0, Left = 0, Bottom = height, Right = width, Plane = 0, Planes = 3, RowPitch = 1, ColPitch = 1,
                PointsV = 1, PointsH = 1, SpacingV = 1, SpacingH = 1, MapPlanes = 3, Gains = [2, 3, 4] });
            lens.GainMaps.AfterWarp.Add(false);
            using (var mapped = RawDecodeService.BuildSceneFrame(width, height, bytes, matrix, 2, lens)) Validate(mapped, samples, matrix, true, [2, 3, 4]);
            lens.HasVignetting = false; lens.TcaVr = lens.TcaVb = 1;
            lens.TcaCr = lens.TcaCb = 0;
            using var unity = RawDecodeService.BuildSceneFrame(width, height, bytes, matrix, 2, lens);
            using var off = RawDecodeService.BuildSceneFrame(width, height, bytes, matrix, 2);
            var a = new float[width * 4]; var b = new float[a.Length];
            for (var y = 0; y < height; y++) { unity.ReadLinearRow(y, a); off.ReadLinearRow(y, b); if (!a.SequenceEqual(b)) throw new InvalidOperationException("Unity TCA is not bit-exact."); }
            if (Math.Abs(LensDataService.FineChromaticAberrationFactor(100) - 1.002) > 1e-12) throw new InvalidOperationException("Manual TCA scale changed.");
            report.Checks.Add(new ProbeCheck("Lens TCA / sampling, channel basis, gains and unity", "PASS",
                "Independent double bilinear reference on nonuniform camera RGB; multiplication direction, edge clamping, non-diagonal camera matrix, green basis, radial/plane gains and HDR; exact unity bypass; manual scale preserved."));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Lens TCA / sampling, channel basis, gains and unity", ex.ToString())); }
    }

    private static double Sample(ushort[] data, int channel, double x, double y)
    {
        x = Math.Clamp(x, 0, 7 - 1.001); y = Math.Clamp(y, 0, 5 - 1.001);
        var x0 = (int)Math.Floor(x); var y0 = (int)Math.Floor(y); var fx = x - x0; var fy = y - y0;
        double At(int xx, int yy) => data[(yy * 7 + xx) * 3 + channel];
        return At(x0, y0) * (1 - fx) * (1 - fy) + At(x0 + 1, y0) * fx * (1 - fy) + At(x0, y0 + 1) * (1 - fx) * fy + At(x0 + 1, y0 + 1) * fx * fy;
    }

    private static void Validate(LinearWorkingFrame frame, ushort[] samples, float[] matrix, bool vignette, double[]? planes)
    {
        double[] rec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
        var row = new float[28];
        for (var y = 0; y < 5; y++)
        {
            frame.ReadLinearRow(y, row);
            for (var x = 0; x < 7; x++)
            {
                var dx = x - 3; var dy = y - 2;
                var radius = Math.Sqrt(dx * dx + dy * dy) * .25;
                var gain = vignette ? 1 / Math.Max(.125, 1 - .2 * radius * radius * .6 * .6) : 1;
                var srgb = new double[3];
                for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++)
                {
                    var factor = r == 0 ? 1.08 + .03 * radius : .94 - .02 * radius;
                    var sample = r == 1 || dx == 0 && dy == 0 ? samples[(y * 7 + x) * 3 + c] : Sample(samples, c, 3 + dx * factor, 2 + dy * factor);
                    srgb[r] += matrix[r * 3 + c] * sample * gain * (planes?[c] ?? 1) * 2 / 65535;
                }
                for (var c = 0; c < 3; c++)
                {
                    var expected = rec[c * 3] * srgb[0] + rec[c * 3 + 1] * srgb[1] + rec[c * 3 + 2] * srgb[2];
                    if (Math.Abs(row[x * 4 + c] - expected) > Math.Max(1.2e-7, Math.Abs(expected) * .002)) throw new InvalidOperationException($"TCA mismatch at {x},{y}: {row[x * 4 + c]} != {expected}.");
                }
                if (row[x * 4 + 3] != 1 || frame.Domain != WorkingImageDomain.SceneLinear) throw new InvalidOperationException("TCA domain/alpha changed.");
            }
        }
    }

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        using var off = RawDecodeService.ReadSceneSource(path, true);
        using (var on = RawDecodeService.ReadSceneSource(path, true, true))
        {
            if (on.GeometryApplied || on.LensVignettingApplied != off.LensVignettingApplied) throw new InvalidOperationException("TCA changed other correction selections.");
            foreach (var ev in new[] { -2.0, 0 }) foreach (var enabled in new[] { false, true })
                Save(output, enabled ? on : off, name, ev, enabled ? "on" : "off");
            report.Checks.Add(new ProbeCheck($"Lens TCA / {name} / automatic", on.LensChromaticAberrationApplied ? "PASS" : "SKIP",
                $"Lens '{on.LensName}', measured TCA available/applied {on.LensChromaticAberrationApplied}; vignetting selection unchanged, distortion excluded. Missing profiles are not synthesized.", Required: on.LensChromaticAberrationApplied));
        }
        using var manual = RawDecodeService.ReadSceneSource(path, true, true, 100, -100);
        if (!manual.LensChromaticAberrationApplied) throw new InvalidOperationException("Manual TCA was not applied.");
        Save(output, manual, name, 0, "manual");
        report.Checks.Add(new ProbeCheck($"Lens TCA / {name} / manual", "PASS", "Original red +100 / blue -100 fine adjustment applied in addition to any automatic profile; diagnostic demonstration, not a suggested photo setting."));
    }

    private static void Save(string output, RawDecodeService.SceneSource source, string name, double ev, string state)
    {
        using var rendered = RawSceneDevelopment.Render(source, ev, true);
        var stem = $"{name}-tca-{(ev < 0 ? "minus2ev" : "neutral")}-{state}";
        SceneRenderingChecks.SavePreview(rendered, output, stem + ".png", ev == 0 ? stem + "-detail.png" : null);
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>TCA migration</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}.pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}figure{margin:0}img{max-width:100%;height:520px;object-fit:contain;background:#111}a{color:#bce0ff}h2{margin-top:40px}</style><h1>Transverse chromatic aberration: off / on</h1><p>Both sides include measured lens vignetting, highlight color repair and the accepted RAW exposure policy. Only TCA changes. Inspect colored edge fringes near corners; there is no distortion correction. Each neutral pair also has a top-right 1:1 detail. Click it for pixel-level viewing. Missing automatic profiles leave output unchanged.</p><p>The extra manual detail uses existing red +100 / blue -100 controls to demonstrate channel movement; it is not a recommended setting. <a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            if (!RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(images[i]), StringComparer.OrdinalIgnoreCase)) continue;
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2>");
            foreach (var variant in new[] { "minus2ev", "neutral", "neutral-detail" })
            {
                html.Append($"<h3>{variant}</h3><div class='pair'>");
                foreach (var state in new[] { "off", "on" })
                {
                    var file = $"image-{i + 1}-tca-{(variant == "neutral-detail" ? "neutral" : variant)}-{state}{(variant == "neutral-detail" ? "-detail" : "")}.png";
                    html.Append($"<figure><figcaption>TCA {state}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='TCA {state}'></a></figure>");
                }
                html.Append("</div>");
            }
            var manual = $"image-{i + 1}-tca-neutral-manual-detail.png";
            html.Append($"<h3>Manual-control demonstration (1:1 detail)</h3><a href='{manual}'><img loading='lazy' src='{manual}' alt='Manual TCA detail'></a>");
        }
        File.WriteAllText(Path.Combine(output, "lens-tca-review.html"), html.Append("</html>").ToString());
    }
}
