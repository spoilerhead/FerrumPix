using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;
internal static class HslPointChecks
{
    internal sealed record Recipe(string Name, Action<ImageAdjustments> Edit);
    internal static readonly string[] Bands = ["Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"];
    internal static readonly Recipe[] Recipes = BuildRecipes();
    private static Recipe[] BuildRecipes()
    {
        var recipes = new List<Recipe> { new("zero", _ => { }) };
        foreach (var band in Bands) foreach (var (component, value) in new[] { ("Hue", 25f), ("Saturation", -60f), ("Luminance", 30f) })
        {
            var property = typeof(ImageAdjustments).GetProperty(band + component)!;
            recipes.Add(new(band.ToLowerInvariant() + "-" + component.ToLowerInvariant(), a => property.SetValue(a, value)));
        }
        recipes.Add(new("point-color", a => a.PointColors = PointRecipe(false)));
        recipes.Add(new("point-visualize", a => a.PointColors = PointRecipe(true)));
        recipes.Add(new("mixed-curves", a => { a.OrangeHue = -15; a.AquaSaturation = -45; a.BlueLuminance = -25; a.CurveRgbPoints = "0,0;64,52;192,208;255,255"; a.CurveRedPoints = "0,0;128,145;255,255"; a.CurveLuminancePoints = "0,0;128,136;255,255"; a.PointColors = PointRecipe(false); a.ColorGradeShadowHue = 220; a.ColorGradeShadowSaturation = 20; a.Highlights = -30; a.ShadowsLevel = 25; a.Vibrance = 20; }));
        recipes.Add(new("gray-bands", a => { a.Saturation = -100; a.Vibrance = 100; a.RedHue = 50; a.BlueSaturation = 70; a.AquaLuminance = 40; a.PointColors = PointRecipe(false); }));
        return recipes.ToArray();
    }
    internal static string PointRecipe(bool visualize)
    {
        var p = PointColorSwatch.SampleFromSrgb(.75, .25, .12)!;
        p.HueShift = .2; p.SaturationShift = -.25; p.LuminanceShift = .15; p.Visualize = visualize;
        return PointColorSwatch.FormatList([p], true);
    }
    internal static ImageAdjustments Adjustments(Recipe recipe) { var a = new ImageAdjustments(); recipe.Edit(a); return a; }
    internal static IEnumerable<(string, ImageAdjustments)> LegacyRecipes() => Recipes.Select(r => (r.Name, Adjustments(r)));

    internal static RenderedHslPointSettings Settings(ImageAdjustments a, float sigma = 1.5f) => new(
        Bands.SelectMany(b => new[] { "Hue", "Saturation", "Luminance" }.Select(c => (float)typeof(ImageAdjustments).GetProperty(b + c)!.GetValue(a)!)),
        PointColorSwatch.ParseList(a.PointColors), sigma);
    internal static RenderedBasicSettings Basic(ImageAdjustments a) => new(a.Highlights, a.ShadowsLevel, a.Whites, a.Blacks, a.Saturation, a.Vibrance, a.Brightness, a.Contrast);
    internal static RenderedCurveSettings Curves(ImageAdjustments a) => new(a.CurveRgbPoints, a.CurveRedPoints, a.CurveGreenPoints, a.CurveBluePoints, a.CurveLuminancePoints);
    internal static LinearWorkingFrame Apply(LinearWorkingFrame frame, ImageAdjustments a) =>
        RenderedBasicControls.Apply(frame, Basic(a), Curves(a), LateColorChecks.Settings(a), Settings(a));
    internal static void Run(ProbeReport report)
    {
        Check(report, "frozen legacy spatial/color recipes", () =>
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return "SKIP Windows x64 fixture; independent checks still run.";
            var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/hsl-point-legacy.json")))!;
            foreach (var (name, hash) in StageLegacyFixture.Hashes(LegacyRecipes())) if (expected[name] != hash) throw new InvalidOperationException("Legacy HSL/point-color changed: " + name);
            return "29 accepted 5f403cf7 recipes are unchanged, including all eight band axes, Skia's original spatial guide, point selections/visualization and deferred curve/grade order.";
        });
        Check(report, "shared point math and no-guide legacy oracle", Oracle);
        Check(report, "independent Gaussian, clamp edges and alpha", Gaussian);
        Check(report, "identity, owned settings and wide-range behavior", Contracts);
    }
    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try { var detail = run(); report.Checks.Add(new ProbeCheck("HSL/point / " + name, detail.StartsWith("SKIP ") ? "SKIP" : "PASS", detail, Required: !detail.StartsWith("SKIP "))); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("HSL/point / " + name, ex.ToString())); }
    }
    private static string Oracle()
    {
        var oldSigma = ImageProcessor.MixerGuideSigma;
        ImageProcessor.MixerGuideSigma = 0;
        try
        {
            using var source = FullBasicChecks.Synthetic();
            var bytes = new byte[source.ByteCount]; Marshal.Copy(source.GetPixels(), bytes, 0, bytes.Length);
            var pixels = new float[source.Width * source.Height * 4];
            for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
            {
                var at = (y * source.Width + x) * 4; var o = y * source.RowBytes + x * 4;
                pixels[at] = bytes[o + 2] / 255f; pixels[at + 1] = bytes[o + 1] / 255f; pixels[at + 2] = bytes[o] / 255f; pixels[at + 3] = 1;
            }
            foreach (var recipe in Recipes)
            {
                var a = Adjustments(recipe);
                var stage = ImageProcessor.PrepareFloatingBasic(source.Width, source.Height, pixels, Basic(a), curves: Curves(a), lateColor: LateColorChecks.Settings(a), hslPoint: Settings(a, 0));
                var old = ImageProcessor.RenderPreviewSkBitmap(source, a)!;
                using var owned = ReferenceEquals(old, source) ? old.Copy() : old;
                var expected = new byte[owned.ByteCount]; Marshal.Copy(owned.GetPixels(), expected, 0, expected.Length);
                for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
                {
                    var at = (y * source.Width + x) * 4; var o = y * owned.RowBytes + x * 4;
                    float r = pixels[at], g = pixels[at + 1], b = pixels[at + 2]; stage.Apply(ref r, ref g, ref b, x, y);
                    if (Math.Abs(r * 255 - expected[o + 2]) > 1.1 || Math.Abs(g * 255 - expected[o + 1]) > 1.1 || Math.Abs(b * 255 - expected[o]) > 1.1)
                        throw new InvalidOperationException($"Point/color order mismatch {recipe.Name} at {x},{y}.");
                }
            }
            return "All 29 no-guide recipes match the legacy renderer within its 1.1-code quantization/dither budget. This isolates unchanged band/selection/gate math and deferred RGB curves from the intentional F32 Gaussian guide change.";
        }
        finally { ImageProcessor.MixerGuideSigma = oldSigma; }
    }
    private static string Gaussian()
    {
        const int width = 5, height = 17;
        var pixels = new float[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = i % 7 / 6f; pixels[i * 4 + 1] = i % 5 / 4f; pixels[i * 4 + 2] = i % 3 / 2f;
            pixels[i * 4 + 3] = i % 4 == 0 ? 0 : i % 4 == 1 ? .25f : 1;
        }
        foreach (var sigma in new[] { .7f, 1.5f })
        {
            var guide = FloatingMixerGuide.Build(width, height, pixels, sigma);
            var radius = (int)Math.Ceiling(3 * sigma);
            var weights = Enumerable.Range(-radius, 2 * radius + 1).Select(k => Math.Exp(-k * (double)k / (2 * sigma * (double)sigma))).ToArray();
            var sum = weights.Sum(); weights = weights.Select(w => w / sum).ToArray();
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var expected = new double[4];
                // Independent full 2-D convolution; production uses separable cached rows.
                for (var ky = -radius; ky <= radius; ky++) for (var kx = -radius; kx <= radius; kx++)
                {
                    var at = (Math.Clamp(y + ky, 0, height - 1) * width + Math.Clamp(x + kx, 0, width - 1)) * 4;
                    var w = weights[ky + radius] * weights[kx + radius]; var alpha = pixels[at + 3];
                    for (var c = 0; c < 3; c++) expected[c] += pixels[at + c] * (double)alpha * w;
                    expected[3] += alpha * w;
                }
                var actual = guide.ReadPremultiplied(x, y);
                if (new[] { actual.R, actual.G, actual.B, actual.A }.Zip(expected).Any(v => Math.Abs(v.First - v.Second) > .000001))
                    throw new InvalidOperationException("F32 Gaussian differs from 2-D Double reference or ring rows were overwritten.");
            }
        }
        var lo = Enumerable.Range(0, 8).SelectMany(_ => new[] { .5001f, .5001f, .5001f, 1f }).ToArray();
        var hi = Enumerable.Range(0, 8).SelectMany(_ => new[] { .5002f, .5002f, .5002f, 1f }).ToArray();
        if (FloatingMixerGuide.Build(8, 1, lo, 1.5f).ReadPremultiplied(4, 0).R == FloatingMixerGuide.Build(8, 1, hi, 1.5f).ReadPremultiplied(4, 0).R)
            throw new InvalidOperationException("Guide lost sub-RGB8 detail.");
        var hidden = new float[] { 1, 0, 1, 0, 0, 0, 0, 1 };
        var clean = FloatingMixerGuide.Build(2, 1, hidden, 1.5f).ReadPremultiplied(1, 0);
        if (clean.R != 0 || clean.B != 0) throw new InvalidOperationException("Hidden RGB contaminated the guide.");
        return "Independent full 2-D Double Gaussian at sigma .7/1.5 matches all clamped-edge, ring-wrap, semi-transparent and hidden-color cases within 1e-6. Constant sub-code guide differences survive; no RGB8 analysis bitmap or extra full-frame horizontal image.";
    }
    private static string Contracts()
    {
        float[] bands = new float[24]; bands[0] = 25;
        var point = PointColorSwatch.SampleFromSrgb(.75, .25, .12)!; point.HueShift = .2;
        var snapshot = new RenderedHslPointSettings(bands, [point]);
        var a = new ImageAdjustments(); snapshot.CopyTo(a); var original = a.PointColors;
        bands[0] = 100; point.HueRange[0] = .2; point.HueShift = -.5;
        var b = new ImageAdjustments(); snapshot.CopyTo(b);
        if (b.RedHue != 25 || b.PointColors != original) throw new InvalidOperationException("Mutable HSL/point state escaped the snapshot.");
        using var source = LinearWorkingFrame.FromLinearPixels(5, 1, [4, -.25f, .3f, 1, 4, 4, 4, 1, -.25f, -.25f, -.25f, 1, .01f, .02f, .03f, .5f, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        var before = new float[20]; source.ReadLinearRow(0, before);
        foreach (var recipe in Recipes)
        {
            var settings = Adjustments(recipe);
            using var result = Apply(source, settings); var output = new float[20]; result.ReadLinearRow(0, output);
            if (output.Any(v => !float.IsFinite(v)) || output[15] != .5 || output.Skip(16).Any(v => v != 0)) throw new InvalidOperationException("HSL/point lost range/alpha.");
            if (recipe.Name == "zero" && !before.SequenceEqual(output)) throw new InvalidOperationException("Neutral HSL/point changed source.");
            if (recipe.Name != "point-visualize" && (output[4] <= 1 || output[8] >= 0)) throw new InvalidOperationException("HSL/point lost neutral HDR/signed reserve.");
        }
        using var gray = RenderedBasicControls.Apply(source, new(saturation: -100, vibrance: 100), hslPoint: new(Enumerable.Range(0, 8).SelectMany(_ => new[] { 50f, 70f, 40f })));
        var mono = new float[20]; gray.ReadLinearRow(0, mono);
        for (var i = 0; i < 4; i++) if (Math.Abs(mono[i * 4] - mono[i * 4 + 1]) > .001 || Math.Abs(mono[i * 4] - mono[i * 4 + 2]) > .001) throw new InvalidOperationException("Bands invented color after native desaturation.");
        foreach (Action invalid in new Action[] { () => _ = new RenderedHslPointSettings([0]), () => _ = new RenderedHslPointSettings(guideSigma: float.NaN), () => _ = new RenderedHslPointSettings(pointColors: [new PointColorSwatch { Hue = double.NaN }]) })
        { try { invalid(); throw new InvalidOperationException("Invalid mixer snapshot accepted."); } catch (ArgumentException) { } }
        var unchanged = new float[20]; source.ReadLinearRow(0, unchanged); if (!before.SequenceEqual(unchanged)) throw new InvalidOperationException("HSL/point mutated source.");
        return "Copied bands/deep point ranges, exact neutral identity, finite settings, source ownership, partial alpha and signed/HDR reserves pass. Native desaturation stays neutral under active bands; selection visualization is an explicit bounded preview replacement.";
    }
    private static IEnumerable<Recipe> VisualRecipes => Recipes.Where(r => r.Name is "zero" or "red-hue" or "green-saturation" or "blue-luminance" or "point-color" or "point-visualize" or "mixed-curves" or "gray-bands");
    private static bool HasReference(Recipe r) => r.Name is "red-hue" or "point-color" or "mixed-curves";
    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var cube = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? ProfiledPngFixture.Read(path) : null;
        using var source = cube?.Frame ?? RenderedContrastChecks.Input(path);
        if (cube is not null) report.Checks.Add(new ProbeCheck($"HSL cube / {name}", "PASS", $"{cube.Description}; ICC {cube.ProfileHash}; RGBA {cube.PixelHash}; exact source-profile/alpha checks pass."));
        using var legacySource = WorkingImageBoundary.ToDiagnosticSdrSrgb8(source);
        foreach (var recipe in VisualRecipes)
        {
            var a = Adjustments(recipe); using var result = Apply(source, a);
            SceneRenderingChecks.SavePreview(result, output, $"{name}-hsl-{recipe.Name}.png");
            if (!HasReference(recipe)) continue;
            var old = ImageProcessor.RenderPreviewSkBitmap(legacySource, a)!; using var owned = ReferenceEquals(old, legacySource) ? old.Copy() : old;
            using var frame = WorkingImageBoundary.FromEncodedSrgb(owned); SceneRenderingChecks.SavePreview(frame, output, $"{name}-hsl-{recipe.Name}-legacy.png");
        }
        report.Checks.Add(new ProbeCheck($"HSL/point / {name}", "PASS", "Eight full-resolution F16 recipes, three old spatial/point references. F32 alpha-aware Gaussian selection guide; point visualization and native gray-band invariance. Visual review pending."));
    }
    internal static void WriteReview(string output, IReadOnlyList<string> images) => StageReview.Write(output, images, "hsl", "hsl-point-review.html", "HSL and point color",
        "Eight HSL bands and linear-ProPhoto point selections share the old calibrated math. The new guide is an alpha-aware F32 Gaussian (sigma 1.5 pixels), replacing Skia's RGB8 approximate blur. Inspect noise/edges and band transitions, point selection/visualization, mixed-curves ordering and gray-bands neutrality. Point visualization is a bounded preview overlay. Wide/HDR reserves otherwise remain; all previews are diagnostic SDR sRGB. Mixed-curves includes orange hue -15, aqua saturation -45, blue luminance -25, master/red/luminance curves, a point selection, shadow grading, HL -30/shadows +25 and vibrance +20.",
        VisualRecipes.Select(r => (r.Name, HasReference(r))));
}
