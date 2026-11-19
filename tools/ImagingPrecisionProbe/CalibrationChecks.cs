using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class CalibrationChecks
{
    private static readonly double[] ToSrgb = [1.660491002, -.587641139, -.072849863, -.124550475, 1.132899897, -.008349422, -.018150763, -.100578898, 1.118729661];
    private static readonly double[] ToRec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
    private static readonly string[] PrimaryControls = ["CalibrationRedHue", "CalibrationRedSaturation", "CalibrationGreenHue", "CalibrationGreenSaturation", "CalibrationBlueHue", "CalibrationBlueSaturation"];
    private sealed record Recipe(string Name, string? Property = null, float Value = 0, float ShadowTint = 0, double Kelvin = 0, bool Combined = false);
    private static readonly Recipe[] Recipes = [
        new("zero"),
        new("red-hue", "CalibrationRedHue", 40), new("red-saturation", "CalibrationRedSaturation", -40),
        new("green-hue", "CalibrationGreenHue", 40), new("green-saturation", "CalibrationGreenSaturation", -40),
        new("blue-hue", "CalibrationBlueHue", 40), new("blue-saturation", "CalibrationBlueSaturation", -40),
        new("shadow-minus", ShadowTint: -50), new("shadow-plus", ShadowTint: 50),
        new("combined-wb", ShadowTint: 20, Kelvin: 3200, Combined: true)
    ];

    internal static ImageAdjustments Settings(double kelvin = 0) => new()
    {
        WhiteBalanceModel = ImageProcessor.CurrentWhiteBalanceModel,
        WhiteBalanceAnchorX = .312727,
        WhiteBalanceAnchorY = .329024,
        WhiteBalanceKelvin = kelvin
    };

    internal static void Run(ProbeReport report)
    {
        Check(report, "primary reference, WB order and identity", PrimaryReference);
        Check(report, "shadow-tint reference, range and domain", ShadowReference);
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            Check(report, "accepted legacy editor hashes", LegacyHashes);
        else report.Checks.Add(new ProbeCheck("Calibration / accepted legacy editor hashes", "SKIP", "Reference captured on Windows x64; numerical references still run.", Required: false));
    }

    private static void Check(ProbeReport report, string name, Func<string> test)
    {
        try { report.Checks.Add(new ProbeCheck("Calibration / " + name, "PASS", test())); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Calibration / " + name, ex.ToString())); }
    }

    private static double[] Matrix(double[] m, double[] v) => [
        m[0]*v[0]+m[1]*v[1]+m[2]*v[2], m[3]*v[0]+m[4]*v[1]+m[5]*v[2], m[6]*v[0]+m[7]*v[1]+m[8]*v[2]
    ];

    private static double[] RgbTone(double[] values, Func<double, double> map)
    {
        var hi = values.Max(); var lo = values.Min(); var top = map(hi); var bottom = map(lo);
        return values.Select(v => hi - lo < 1e-7 ? top : bottom + (v - lo) * (top - bottom) / (hi - lo)).ToArray();
    }

    private static string PrimaryReference()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(3, 2, [.3f, .1f, .02f, 1, .02f, .4f, .1f, 1, .1f, .02f, 2, 1, .2f, .2f, .2f, 1, .08f, .03f, .01f, .5f, 0, 0, 0, 0], WorkingImageDomain.SceneLinear);
        var zero = Settings(3200);
        var wb = RawWhiteBalanceSettings.FromAdjustments(zero);
        using var plain = SceneFrameRenderer.Render(source, .28, .003, .45, wb);
        using var identity = SceneFrameRenderer.Render(source, .28, .003, .45, wb, RawPrimaryCalibrationSettings.FromAdjustments(zero));
        CompareExact(plain, identity);
        var cases = new List<ImageAdjustments>();
        foreach (var property in PrimaryControls) foreach (var value in new[] { -100f, 100f })
        {
            var adj = Settings(); typeof(ImageAdjustments).GetProperty(property)!.SetValue(adj, value); cases.Add(adj);
        }
        var combined = Settings(3200);
        combined.CalibrationRedHue = 40; combined.CalibrationRedSaturation = 20; combined.CalibrationGreenHue = -25; combined.CalibrationBlueHue = -30; combined.CalibrationBlueSaturation = 20;
        cases.Add(combined);
        var orderDifference = 0.0;
        foreach (var adj in cases)
        {
            using var rendered = SceneFrameRenderer.Render(source, .28, .003, .45, RawWhiteBalanceSettings.FromAdjustments(adj), RawPrimaryCalibrationSettings.FromAdjustments(adj));
            var old = ImageProcessor.BuildCalibrationMatrix(adj);
            double[] cal = [old[0], old[1], old[2], old[5], old[6], old[7], old[10], old[11], old[12]];
            var oldWb = ImageProcessor.BuildWhiteBalanceMatrix(adj);
            var before = new float[12]; var after = new float[12];
            for (var y = 0; y < 2; y++)
            {
                source.ReadLinearRow(y, before); rendered.ReadLinearRow(y, after);
                for (var x = 0; x < 3; x++)
                {
                    var at = x * 4; var alpha = before[at + 3];
                    if (alpha != after[at + 3]) throw new InvalidOperationException("Calibration changed alpha.");
                    if (alpha == 0) continue;
                    double[] rgb = [before[at] / (double)alpha, before[at + 1] / (double)alpha, before[at + 2] / (double)alpha];
                    var pro = Matrix(PointColorMath.SrgbToProPhoto, Matrix(ToSrgb, rgb)).Select(v => Math.Max(0, v)).ToArray();
                    var slope = 1 / (Math.Pow(2, -.28) - .003); var radius = Math.Min(.0015, 1 / (16 * slope));
                    double Toe(double v) => v <= .003 - radius ? 0 : v >= .003 + radius ? (v - .003) * slope : slope / (4 * radius) * Math.Pow(v - .003 + radius, 2);
                    var ramp = Matrix(PointColorMath.ProPhotoToSrgb, RgbTone(pro, Toe));
                    var balanced = oldWb is null ? ramp : Matrix(oldWb.Select(v => (double)v).ToArray(), ramp);
                    var calibrated = Matrix(cal, balanced);
                    if (ReferenceEquals(adj, combined))
                    {
                        var wrong = Matrix(oldWb!.Select(v => (double)v).ToArray(), Matrix(cal, ramp));
                        orderDifference = Math.Max(orderDifference, Enumerable.Range(0, 3).Max(c => Math.Abs(wrong[c] - calibrated[c])));
                    }
                    var adapted = Matrix(PointColorMath.SrgbToProPhoto, calibrated).Select(v => Math.Max(0, v)).ToArray();
                    double Curve(double v) => RawDecodeService.Acr3CurveAt(v <= .45 ? v : 1 - .55 * Math.Exp(-(v - .45) / .55));
                    var expected = Matrix(ToRec, Matrix(PointColorMath.ProPhotoToSrgb, RgbTone(adapted, Curve)));
                    for (var c = 0; c < 3; c++) Close(after[at + c], expected[c] * alpha, "Primary calibration");
                }
            }
        }
        if (orderDifference < .001) throw new InvalidOperationException("WB/calibration ordering fixture is not sensitive.");
        var bad = Settings(); bad.CalibrationRedHue = float.NaN; Reject(() => RawPrimaryCalibrationSettings.FromAdjustments(bad));
        bad = Settings(); bad.CalibrationBlueSaturation = 101; Reject(() => RawPrimaryCalibrationSettings.FromAdjustments(bad));
        bad = Settings(); bad.WhiteBalanceModel = 3; Reject(() => RawPrimaryCalibrationSettings.FromAdjustments(bad));
        return "All six controls at +/-100 and a combined warm-WB recipe match independent double transforms in WB-then-calibration order; HDR/alpha, neutral identity, immutable frame and unsupported settings covered.";
    }

    private static void Close(double actual, double expected, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > Math.Max(.0003, Math.Abs(expected) * .004)) throw new InvalidOperationException($"{label}: {actual} != {expected}.");
    }

    private static string ShadowReference()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(3, 2, [.02f, .03f, .04f, 1, .3f, .2f, .1f, 1, 3, -.25f, .8f, 1, .1f, .03f, .02f, .5f, .9f, .9f, .9f, 1, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        using (var zero = CalibrationShadowTint.Apply(source, 0)) CompareExact(source, zero);
        double[] levels = [0, .05, .15, .27, .42, .6, .85, 1], weights = [.217, .483, 1, .977, .828, .51, .229, .038];
        foreach (var slider in new[] { -100f, -50, 50, 100 })
        {
            using var actual = CalibrationShadowTint.Apply(source, slider);
            var input = new float[12]; var output = new float[12];
            for (var y = 0; y < 2; y++)
            {
                source.ReadLinearRow(y, input); actual.ReadLinearRow(y, output);
                for (var x = 0; x < 3; x++)
                {
                    var at = x * 4; var alpha = input[at + 3]; if (alpha != output[at + 3]) throw new InvalidOperationException("Shadow tint changed alpha."); if (alpha == 0) continue;
                    double[] rgb = [input[at] / (double)alpha, input[at + 1] / (double)alpha, input[at + 2] / (double)alpha];
                    var srgb = Matrix(ToSrgb, rgb).Select(v => PointColorMath.SrgbEncode(Math.Clamp(v, 0, 1))).ToArray();
                    var brightness = .299 * srgb[0] + .587 * srgb[1] + .114 * srgb[2];
                    var i = 1; while (i < levels.Length - 1 && brightness > levels[i]) i++;
                    var t = Math.Clamp((brightness - levels[i - 1]) / (levels[i] - levels[i - 1]), 0, 1);
                    var weight = .062 * (weights[i - 1] + t * (weights[i] - weights[i - 1]));
                    var response = Math.CopySign(.95 * Math.Pow(Math.Abs(slider) / 100.0, .81), slider);
                    var dr = .7 * response * weight; var db = .483 * response * weight; var dark = (.42 * Math.Abs(response) + .044 * response) * weight;
                    double[] changed = [Math.Clamp(srgb[0] + dr - dark, 0, 1), Math.Clamp(srgb[1] - (.299 * dr + .114 * db) / .587 - dark, 0, 1), Math.Clamp(srgb[2] + db - dark, 0, 1)];
                    var delta = Matrix(ToRec, Enumerable.Range(0, 3).Select(c => PointColorMath.SrgbDecode(changed[c]) - PointColorMath.SrgbDecode(srgb[c])).ToArray());
                    for (var c = 0; c < 3; c++) Close(output[at + c], input[at + c] + delta[c] * alpha, "Shadow tint");
                }
            }
            actual.ReadLinearRow(0, output);
            if (output[8] <= 1 || output[9] >= 0) throw new InvalidOperationException("Shadow tint globally clipped signed/HDR values.");
        }
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        Reject(() => CalibrationShadowTint.Apply(scene, 10)); Reject(() => CalibrationShadowTint.Apply(source, float.NaN));
        return "Independent encoded-sRGB/Rec.601 curve and asymmetric darkening reference at +/-50/100; exact zero, alpha, domain guard and preserved out-of-range source values.";
    }

    private static void CompareExact(LinearWorkingFrame a, LinearWorkingFrame b)
    {
        var ar = new float[a.Width * 4]; var br = new float[ar.Length];
        for (var y = 0; y < a.Height; y++) { a.ReadLinearRow(y, ar); b.ReadLinearRow(y, br); if (!ar.SequenceEqual(br)) throw new InvalidOperationException("Zero calibration changed F16 pixels."); }
    }

    private static void Reject(Action call)
    {
        try { call(); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return; }
        throw new InvalidOperationException("Invalid calibration accepted.");
    }

    private sealed record LegacyCase(string Name, string Property, float Value, string Hash);
    private static string LegacyHashes()
    {
        GpuImageService.Enabled = false;
        using var source = new SKBitmap(new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Premul));
        var bytes = new byte[source.ByteCount];
        for (var y = 0; y < 32; y++) for (var x = 0; x < 64; x++) { var at = y * source.RowBytes + x * 4; bytes[at] = (byte)(y * 255 / 31); bytes[at + 1] = (byte)(x * 255 / 63); bytes[at + 2] = (byte)((x + y) * 255 / 94); bytes[at + 3] = 255; }
        Marshal.Copy(bytes, 0, source.GetPixels(), bytes.Length);
        var cases = JsonSerializer.Deserialize<LegacyCase[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "calibration-legacy.json")))!;
        foreach (var item in cases)
        {
            var adj = Settings(); typeof(ImageAdjustments).GetProperty(item.Property)!.SetValue(adj, item.Value);
            var result = ImageProcessor.RenderPreviewSkBitmap(source, adj) ?? throw new InvalidOperationException("Legacy renderer failed.");
            using var owned = ReferenceEquals(result, source) ? result.Copy() : result;
            var data = new byte[owned.ByteCount]; Marshal.Copy(owned.GetPixels(), data, 0, data.Length);
            if (Convert.ToHexString(SHA256.HashData(data)) != item.Hash) throw new InvalidOperationException($"Legacy calibration changed: {item.Name}.");
        }
        return $"{cases.Length} accepted pre-migration legacy editor hashes unchanged, including both shadow-tint directions.";
    }

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var capture = CaptureWhiteBalanceService.ForRawFile(path) ?? throw new InvalidOperationException("Missing capture anchor.");
        using var source = RawDecodeService.ReadSceneSource(path, true, true);
        foreach (var recipe in Recipes)
        {
            var adj = Settings(recipe.Kelvin);
            adj.WhiteBalanceAnchorX = capture.X;
            adj.WhiteBalanceAnchorY = capture.Y;
            adj.WhiteBalanceCamera = capture.Camera;
            adj.CalibrationShadowTint = recipe.ShadowTint;
            if (recipe.Property is not null)
                typeof(ImageAdjustments).GetProperty(recipe.Property)!.SetValue(adj, recipe.Value);
            if (recipe.Combined)
            {
                adj.CalibrationRedHue = 40;
                adj.CalibrationRedSaturation = 20;
                adj.CalibrationGreenHue = -25;
                adj.CalibrationBlueHue = -30;
                adj.CalibrationBlueSaturation = 20;
            }
            using var rendered = RawSceneDevelopment.Render(source, 0, true, .45, true, RawWhiteBalanceSettings.FromAdjustments(adj), RawPrimaryCalibrationSettings.FromAdjustments(adj));
            // Only calibration/WB are active here, so the intervening not-yet-migrated tools
            // are identities. In the full editor this adapter belongs at its later chain hook.
            using var tinted = CalibrationShadowTint.Apply(rendered, adj.CalibrationShadowTint);
            SceneRenderingChecks.SavePreview(tinted, output, $"{name}-calibration-{recipe.Name}.png");
        }
        report.Checks.Add(new ProbeCheck($"Calibration / {name}", "PASS", "Ten full-resolution recipes: zero, six individual primary controls, both shadow-tint signs and combined WB/calibration. Shared app control math, distinct ramp/rendered stages; other editing tools are identity, not migrated."));
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>F16 camera calibration</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:420px;object-fit:contain;background:#111}a{color:#bce0ff}h2{margin-top:40px}</style><h1>Camera calibration</h1><p>Zero must match the accepted as-shot WB preview. Six primary hue/saturation controls act on the pre-curve ramp after WB. Shadow Tint is a separate later encoded-sRGB operation; no intervening light/mixer tools are active here. Check neutral surfaces, skin and colored objects; compare green/magenta shadows and the combined 3200 K recipe. These are demonstration settings, not suggested edits. <a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            if (!RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(images[i]), StringComparer.OrdinalIgnoreCase)) continue;
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2><div class='grid'>");
            foreach (var recipe in Recipes)
            {
                var setting = recipe.Name;
                var file = $"image-{i + 1}-calibration-{setting}.png"; html.Append($"<figure><figcaption>{setting}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{setting}'></a></figure>");
            }
            html.Append("</div>");
        }
        File.WriteAllText(Path.Combine(output, "camera-calibration-review.html"), html.Append("</html>").ToString());
    }
}
