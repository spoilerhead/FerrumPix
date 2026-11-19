using System.Net;
using System.Text;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

internal static class RawWhiteBalanceChecks
{
    private static readonly double[] ToSrgb = [1.660491002, -.587641139, -.072849863, -.124550475, 1.132899897, -.008349422, -.018150763, -.100578898, 1.118729661];
    private static readonly double[] ToRec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];

    private static ImageAdjustments Settings(double kelvin = 0, double tint = 0) => new()
    {
        WhiteBalanceModel = ImageProcessor.CurrentWhiteBalanceModel,
        WhiteBalanceAnchorX = WhiteBalanceAdaptation.D65.X,
        WhiteBalanceAnchorY = WhiteBalanceAdaptation.D65.Y,
        WhiteBalanceKelvin = kelvin,
        WhiteBalanceKelvinTint = tint
    };

    internal static void Run(ProbeReport report)
    {
        try
        {
            using var source = LinearWorkingFrame.FromLinearPixels(3, 2,
                [.001f, .002f, .001f, 1, .3f, .1f, .04f, 1, 2, 1, 4, 1, -.02f, .15f, .6f, 1, .1f, .04f, .02f, .5f, 0, 0, 0, 0], WorkingImageDomain.SceneLinear);
            var identity = RawWhiteBalanceSettings.FromAdjustments(Settings());
            if (!identity.IsIdentity) throw new InvalidOperationException("As-shot must be a true no-op.");
            using var plain = SceneFrameRenderer.Render(source, .28);
            using var unchanged = SceneFrameRenderer.Render(source, .28, .003, .45, identity);
            CompareExact(plain, unchanged);
            var cases = new[] { Settings(3200), Settings(7500), Settings(0, -25), Settings(0, 25), Settings() };
            cases[^1].Temperature = 20; cases[^1].Tint = -10;
            foreach (var adj in cases)
            {
                var wb = RawWhiteBalanceSettings.FromAdjustments(adj);
                using var result = SceneFrameRenderer.Render(source, .28, .003, .45, wb);
                Reference(source, result, ImageProcessor.BuildWhiteBalanceMatrix(adj));
                var copy = wb.CopyToneMatrix(); if (copy is null) throw new InvalidOperationException("Non-neutral WB missing.");
                copy[0] = 12345;
                if (wb.CopyToneMatrix()[0] == 12345) throw new InvalidOperationException("WB exposes mutable state.");
            }
            var precedence = Settings(3200);
            precedence.Temperature = 40;
            precedence.Tint = -20;
            if (!RawWhiteBalanceSettings.FromAdjustments(precedence).CopyToneMatrix().SequenceEqual(
                    RawWhiteBalanceSettings.FromAdjustments(Settings(3200)).CopyToneMatrix()))
                throw new InvalidOperationException("Absolute Kelvin must take precedence over relative controls.");
            var legacy = Settings();
            legacy.WhiteBalanceModel = 3;
            Reject(() => RawWhiteBalanceSettings.FromAdjustments(legacy));
            var negative = Settings();
            negative.NegativeEnabled = true;
            Reject(() => RawWhiteBalanceSettings.FromAdjustments(negative));
            var unanchored = Settings();
            unanchored.WhiteBalanceAnchorX = 0;
            Reject(() => RawWhiteBalanceSettings.FromAdjustments(unanchored));
            var nan = Settings();
            nan.WhiteBalanceKelvinTint = double.NaN;
            Reject(() => RawWhiteBalanceSettings.FromAdjustments(nan));
            report.Checks.Add(new ProbeCheck("RAW white balance / reference, identity and guards", "PASS",
                "Independent double ramp/ProPhoto-matrix/curve reference over warm/cool, tint-only and relative controls, HDR/signed inputs and partial/zero alpha; as-shot exact bypass; immutable settings; legacy/negative/missing-anchor/non-finite settings rejected."));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("RAW white balance / reference, identity and guards", ex.ToString())); }
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return; }
        throw new InvalidOperationException("Unsupported WB accepted.");
    }

    private static double[] Matrix(double[] matrix, double[] rgb) => [
        matrix[0] * rgb[0] + matrix[1] * rgb[1] + matrix[2] * rgb[2],
        matrix[3] * rgb[0] + matrix[4] * rgb[1] + matrix[5] * rgb[2],
        matrix[6] * rgb[0] + matrix[7] * rgb[1] + matrix[8] * rgb[2]
    ];

    private static double[] RgbTone(double[] values, Func<double, double> map)
    {
        var hi = values.Max(); var lo = values.Min(); var top = map(hi); var bottom = map(lo);
        return values.Select(v => hi - lo < 1e-7 ? top : bottom + (v - lo) * (top - bottom) / (hi - lo)).ToArray();
    }

    private static void Reference(LinearWorkingFrame source, LinearWorkingFrame result, float[] wb)
    {
        var input = new float[source.Width * 4]; var output = new float[input.Length];
        var slope = 1 / (Math.Pow(2, -.28) - .003); var radius = Math.Min(.0015, 1 / (16 * slope));
        double Toe(double x) => x <= .003 - radius ? 0 : x >= .003 + radius ? (x - .003) * slope : slope / (4 * radius) * Math.Pow(x - .003 + radius, 2);
        double Curve(double x) => RawDecodeService.Acr3CurveAt(x <= .45 ? x : 1 - .55 * Math.Exp(-(x - .45) / .55));
        for (var y = 0; y < source.Height; y++)
        {
            source.ReadLinearRow(y, input); result.ReadLinearRow(y, output);
            for (var x = 0; x < source.Width; x++)
            {
                var at = x * 4; var alpha = input[at + 3];
                if (alpha != output[at + 3]) throw new InvalidOperationException("WB changed alpha.");
                if (alpha == 0) continue;
                double[] rgb = [input[at] / (double)alpha, input[at + 1] / (double)alpha, input[at + 2] / (double)alpha];
                var pro = Matrix(PointColorMath.SrgbToProPhoto, Matrix(ToSrgb, rgb)).Select(v => Math.Max(0, v)).ToArray();
                var ramp = RgbTone(pro, Toe);
                // Deliberately use separate color transforms instead of the renderer's composed matrix.
                var srgbRamp = Matrix(PointColorMath.ProPhotoToSrgb, ramp);
                var srgbAdapted = Matrix(wb.Select(v => (double)v).ToArray(), srgbRamp);
                var adapted = Matrix(PointColorMath.SrgbToProPhoto, srgbAdapted).Select(v => Math.Max(0, v)).ToArray();
                var toned = RgbTone(adapted, Curve);
                var expected = Matrix(ToRec, Matrix(PointColorMath.ProPhotoToSrgb, toned));
                for (var c = 0; c < 3; c++) if (!float.IsFinite(output[at + c]) || Math.Abs(output[at + c] - expected[c] * alpha) > Math.Max(.0003, Math.Abs(expected[c] * alpha) * .004))
                    throw new InvalidOperationException($"WB reference mismatch at {x},{y}: {output[at + c]} != {expected[c] * alpha}.");
            }
        }
    }

    private static void CompareExact(LinearWorkingFrame a, LinearWorkingFrame b)
    {
        var ar = new float[a.Width * 4];
        var br = new float[ar.Length];
        for (var y = 0; y < a.Height; y++)
        {
            a.ReadLinearRow(y, ar);
            b.ReadLinearRow(y, br);
            if (!ar.SequenceEqual(br)) throw new InvalidOperationException("As-shot changed F16 pixels.");
        }
    }

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var capture = CaptureWhiteBalanceService.ForRawFile(path) ?? throw new InvalidOperationException("No capture WB anchor; refusing to invent one for RAW.");
        using var source = RawDecodeService.ReadSceneSource(path, true, true);
        var cases = new (string Name, double Kelvin, double Tint, float RelativeTemp, double Ev)[] {
            ("as-shot",0,0,0,0), ("3200k",3200,0,0,0), ("7500k",7500,0,0,0),
            ("tint-minus25",0,-25,0,0), ("tint-plus25",0,25,0,0), ("relative-plus20",0,0,20,0), ("3200k-minus2ev",3200,0,0,-2)
        };
        foreach (var item in cases)
        {
            var adj = Settings(item.Kelvin, item.Tint);
            adj.WhiteBalanceAnchorX = capture.X;
            adj.WhiteBalanceAnchorY = capture.Y;
            adj.WhiteBalanceCamera = capture.Camera;
            adj.Temperature = item.RelativeTemp;
            var settings = RawWhiteBalanceSettings.FromAdjustments(adj);
            using var rendered = RawSceneDevelopment.Render(source, item.Ev, true, .45, true, settings);
            SceneRenderingChecks.SavePreview(rendered, output, $"{name}-wb-{item.Name}.png");
        }
        report.Checks.Add(new ProbeCheck($"RAW white balance / {name}", "PASS", $"Capture xy {capture.X:G9},{capture.Y:G9}; camera '{capture.Camera}'; seven current-model WB/exposure previews; as-shot uses the capture anchor directly without Kelvin round-trip or duplicate LibRaw WB. Lens corrections, repair and exposure precede the ramp WB; calibration/legacy models excluded."));
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Editable RAW white balance</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:440px;object-fit:contain;background:#111}a{color:#bce0ff}h2{margin-top:40px}</style><h1>Editable RAW white balance</h1><p>All previews use the accepted lens, highlight repair and exposure stages. WB uses the actual capture anchor and current app Kelvin/tint model, directly on the pre-curve ramp. As-shot must match the previous TCA-on neutral preview. Check skin/neutral colors, warm/cool shifts, tint-only changes and highlights. The last preview also applies -2 EV. This is not the old clipped RGB8/inverse-LUT WB route. Camera calibration, old WB models, film negatives and the eyedropper are not migrated.</p><p><a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            if (!RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(images[i]), StringComparer.OrdinalIgnoreCase)) continue;
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2><div class='grid'>");
            foreach (var setting in new[] { "as-shot", "3200k", "7500k", "tint-minus25", "tint-plus25", "relative-plus20", "3200k-minus2ev" })
            { var file = $"image-{i + 1}-wb-{setting}.png"; html.Append($"<figure><figcaption>{setting}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{setting}'></a></figure>"); }
            html.Append("</div>");
        }
        File.WriteAllText(Path.Combine(output, "raw-white-balance-review.html"), html.Append("</html>").ToString());
    }
}
