using System.Net;
using System.Text;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

internal static class LensVignetteChecks
{
    internal static void Run(ProbeReport report)
    {
        try
        {
            var bytes = new byte[3 * 3 * 6];
            var samples = Enumerable.Repeat((ushort)8192, 27).ToArray();
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            var original = bytes.ToArray();
            float[] camera = [1.4f, -.2f, -.2f, -.1f, 1.2f, -.1f, -.1f, -.2f, 1.3f];
            var lens = new LensDataService.Korrektur { HasVignetting = true, NormScale = 1 / Math.Sqrt(2), CornerScale = 1, Vk1 = -.5 };
            using var off = RawDecodeService.BuildSceneFrame(3, 3, bytes, camera, 1.5);
            foreach (var cap in new[] { false, true })
            {
                lens.Vk1 = cap ? -2 : -.5;
                using var on = RawDecodeService.BuildSceneFrame(3, 3, bytes, camera, 1.5, lens);
                Validate(on, camera, cap ? -2 : -.5, [1, 1, 1]);
            }
            lens.Vk1 = -.5;
            lens.GainMaps = new DngGainMapService.PlaneGainMaps { ActiveWidth = 3, ActiveHeight = 3 };
            lens.GainMaps.Maps.Add(new DngGainMapService.GainMap { Top = 0, Left = 0, Bottom = 3, Right = 3, Plane = 0, Planes = 3,
                RowPitch = 1, ColPitch = 1, PointsV = 1, PointsH = 1, SpacingV = 1, SpacingH = 1, MapPlanes = 3, Gains = [2, 3, 4] });
            lens.GainMaps.AfterWarp.Add(false);
            using (var mapped = RawDecodeService.BuildSceneFrame(3, 3, bytes, camera, 1.5, lens)) Validate(mapped, camera, -.5, [2, 3, 4]);
            lens.VignettingStrength = 0;
            using var unity = RawDecodeService.BuildSceneFrame(3, 3, bytes, camera, 1.5, lens);
            var a = new float[12]; var b = new float[12];
            for (var y = 0; y < 3; y++)
            {
                off.ReadLinearRow(y, a); unity.ReadLinearRow(y, b);
                if (!a.SequenceEqual(b)) throw new InvalidOperationException("Zero strength must preserve exact F16 pixels, including gain maps.");
            }
            if (!bytes.SequenceEqual(original)) throw new InvalidOperationException("Vignetting changed the shared RAW cache.");
            report.Checks.Add(new ProbeCheck("Lens vignette / radius, cap, camera-plane order and unity", "PASS",
                "Independent double reference at center/edges/corners; polynomial inverse falloff and 3-EV cap; RGB gain maps before non-diagonal camera matrix; alpha/domain/HDR retained; zero strength bit-identical; cache immutable."));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Lens vignette / radius, cap, camera-plane order and unity", ex.ToString())); }
    }

    private static void Validate(LinearWorkingFrame frame, float[] camera, double coefficient, double[] planeGains)
    {
        double[] toRec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
        var row = new float[12];
        for (var y = 0; y < 3; y++)
        {
            frame.ReadLinearRow(y, row);
            for (var x = 0; x < 3; x++)
            {
                var radiusSquared = ((x - 1) * (x - 1) + (y - 1) * (y - 1)) / 2.0;
                var falloff = Math.Max(.125, 1 + coefficient * radiusSquared);
                var srgb = new double[3];
                for (var r = 0; r < 3; r++)
                    for (var c = 0; c < 3; c++) srgb[r] += camera[r * 3 + c] * 8192 * planeGains[c] / falloff * 1.5 / 65535;
                for (var r = 0; r < 3; r++)
                {
                    var expected = toRec[r * 3] * srgb[0] + toRec[r * 3 + 1] * srgb[1] + toRec[r * 3 + 2] * srgb[2];
                    if (Math.Abs(row[x * 4 + r] - expected) > Math.Max(1.2e-7, Math.Abs(expected) * .002))
                        throw new InvalidOperationException($"Lens reference differs at {x},{y}: {row[x * 4 + r]} != {expected}.");
                }
                if (row[x * 4 + 3] != 1 || frame.Domain != WorkingImageDomain.SceneLinear) throw new InvalidOperationException("Wrong alpha/domain.");
            }
        }
    }

    internal static void Capture(ProbeReport report, string output, RawDecodeService.SceneSource off, string path, string name)
    {
        using var on = RawDecodeService.ReadSceneSource(path, true);
        if (on.LensChromaticAberrationApplied || on.GeometryApplied || on.ToneCurveApplied || on.LensCorrectionsApplied != on.LensVignettingApplied)
            throw new InvalidOperationException("Lens scene metadata is incorrect.");
        long changed = 0;
        var before = new float[off.Frame.Width * 4]; var after = new float[before.Length];
        if (off.Frame.Width != on.Frame.Width || off.Frame.Height != on.Frame.Height) throw new InvalidOperationException("Vignetting changed geometry.");
        for (var y = 0; y < on.Frame.Height; y++)
        {
            off.Frame.ReadLinearRow(y, before); on.Frame.ReadLinearRow(y, after);
            for (var x = 0; x < on.Frame.Width; x++)
            {
                if (after[x * 4 + 3] != 1) throw new InvalidOperationException("Lens alpha changed.");
                var at = x * 4;
                if (before[at] != after[at] || before[at + 1] != after[at + 1] || before[at + 2] != after[at + 2]) changed++;
            }
        }
        foreach (var ev in new[] { -2.0, 0 })
        foreach (var enabled in new[] { false, true })
        {
            using var rendered = RawSceneDevelopment.Render(enabled ? on : off, ev, true);
            SceneRenderingChecks.SavePreview(rendered, output, $"{name}-lens-{(ev < 0 ? "minus2ev" : "neutral")}-{(enabled ? "on" : "off")}.png");
        }
        report.Checks.Add(new ProbeCheck($"Lens vignette / {name}", on.LensVignettingApplied ? "PASS" : "SKIP",
            $"Lens '{on.LensName}'; correction available/applied {on.LensVignettingApplied}; {changed:N0} changed full-resolution scene pixels. TCA/distortion not applied. Missing measured correction is an explicit skip, not a fabricated profile.", Required: on.LensVignettingApplied));
        if (!on.LensVignettingApplied && changed != 0) throw new InvalidOperationException("Missing correction changed scene values.");
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Lens vignetting migration</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}.pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:520px;object-fit:contain;background:#111}a{color:#bce0ff}h2{margin-top:40px}</style><h1>Lens vignetting: off / on</h1><p>Left: accepted RAW pipeline without lens correction. Right: the same pipeline with measured lens falloff and available stage-3 gain maps, applied before the camera matrix and highlight repair. No distortion or TCA correction. Check corner brightness and color. Missing profiles produce identical images; see the <a href="report.md">validation report</a>.</p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            if (!RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(images[i]), StringComparer.OrdinalIgnoreCase)) continue;
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2>");
            foreach (var ev in new[] { "minus2ev", "neutral" })
            {
                html.Append($"<h3>{ev}</h3><div class='pair'>");
                foreach (var setting in new[] { "off", "on" })
                {
                    var file = $"image-{i + 1}-lens-{ev}-{setting}.png";
                    html.Append($"<figure><figcaption>Correction {setting}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='Correction {setting}'></a></figure>");
                }
                html.Append("</div>");
            }
        }
        File.WriteAllText(Path.Combine(output, "lens-vignette-review.html"), html.Append("</html>").ToString());
    }
}
