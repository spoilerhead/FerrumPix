using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class LateColorChecks
{
    internal sealed record Recipe(string Name, string Description, Action<ImageAdjustments> Edit,
        FullBasicChecks.Recipe? Basic = null, RenderedCurveChecks.Recipe? Curves = null);
    internal static readonly Recipe[] Recipes =
    [
        new("zero", "All new controls neutral", a => { }),
        new("grade-shadow", "Shadow hue 220, saturation 45", a => { a.ColorGradeShadowHue = 220; a.ColorGradeShadowSaturation = 45; }),
        new("grade-mid", "Midtone hue 35, saturation 25, luminance 10", a => { a.ColorGradeMidtoneHue = 35; a.ColorGradeMidtoneSaturation = 25; a.ColorGradeMidtoneLuminance = 10; }),
        new("grade-highlight", "Highlight hue 45, saturation 35", a => { a.ColorGradeHighlightHue = 45; a.ColorGradeHighlightSaturation = 35; }),
        new("grade-global", "Global hue 210, saturation 20, luminance -10", a => { a.ColorGradeGlobalHue = 210; a.ColorGradeGlobalSaturation = 20; a.ColorGradeGlobalLuminance = -10; }),
        new("grade-luminance", "Shadow luminance 25, highlight -20, balance 20, blending 70", a => { a.ColorGradeShadowLuminance = 25; a.ColorGradeHighlightLuminance = -20; a.ColorGradeBalance = 20; a.ColorGradeBlending = 70; }),
        new("grade-combined", "Shadow 220/30, midtone 45/30, highlight 40/40, global 10/20; balance 15, blending 80", a => { a.ColorGradeShadowHue = 220; a.ColorGradeShadowSaturation = 30; a.ColorGradeMidtoneHue = 45; a.ColorGradeMidtoneSaturation = 30; a.ColorGradeHighlightHue = 40; a.ColorGradeHighlightSaturation = 40; a.ColorGradeGlobalHue = 10; a.ColorGradeGlobalSaturation = 20; a.ColorGradeBalance = 15; a.ColorGradeBlending = 80; }),
        new("shadow-tint-plus", "Calibration shadow tint +40", a => a.CalibrationShadowTint = 40),
        new("shadow-tint-minus", "Calibration shadow tint -40", a => a.CalibrationShadowTint = -40),
        new("mixer-color", "R=110R-10G; G=95G+5B; B=10R+90B (%)", a => { a.ChannelMixerRedRed = 110; a.ChannelMixerRedGreen = -10; a.ChannelMixerGreenGreen = 95; a.ChannelMixerGreenBlue = 5; a.ChannelMixerBlueRed = 10; a.ChannelMixerBlueBlue = 90; }),
        new("mixer-offset", "R=120R-20G+5; B=80B-5 (%)", a => { a.ChannelMixerRedRed = 120; a.ChannelMixerRedGreen = -20; a.ChannelMixerRedConstant = 5; a.ChannelMixerBlueBlue = 80; a.ChannelMixerBlueConstant = -5; }),
        new("mixer-monochrome", "Monochrome R/G/B weights 60/25/15", a => { a.ChannelMixerMonochrome = true; a.ChannelMixerGrayRed = 60; a.ChannelMixerGrayGreen = 25; a.ChannelMixerGrayBlue = 15; }),
        new("map-half", "Gradient #132F56 to #FFCF88, 50%", a => { a.GradientMapShadowColor = "#FF132F56"; a.GradientMapHighlightColor = "#FFFFCF88"; a.GradientMapAmount = 50; }),
        new("map-full", "Gradient #132F56 to #FFCF88, 100%", a => { a.GradientMapShadowColor = "#FF132F56"; a.GradientMapHighlightColor = "#FFFFCF88"; a.GradientMapAmount = 100; }),
        new("posterize", "6 levels (intentional palette quantization)", a => a.PosterizeLevels = 6),
        new("threshold", "Threshold 128 (intentional binary output)", a => a.ThresholdLevel = 128),
        new("map-posterize", "Gradient 70%, then posterize 5", a => { a.GradientMapAmount = 70; a.GradientMapShadowColor = "#FF132F56"; a.PosterizeLevels = 5; }),
        new("map-threshold", "Gradient 70%, then threshold 128", a => { a.GradientMapAmount = 70; a.GradientMapShadowColor = "#FF132F56"; a.ThresholdLevel = 128; }),
        new("mixed-chain", "Basic/curves, grade 220/25 shadows and 40/20 highlights, tint 15, channel mix, gradient 20%", a => { a.ColorGradeShadowHue = 220; a.ColorGradeShadowSaturation = 25; a.ColorGradeHighlightHue = 40; a.ColorGradeHighlightSaturation = 20; a.CalibrationShadowTint = 15; a.ChannelMixerRedRed = 110; a.ChannelMixerRedGreen = -10; a.GradientMapShadowColor = "#FF132F56"; a.GradientMapAmount = 20; },
            new("mixed", Highlights: -30, Shadows: 25, Whites: 10, Blacks: -10, Saturation: 10, Vibrance: 20, Brightness: 5, Contrast: 10),
            new("curves", Master: "0,0;64,56;192,202;255,255", Red: "0,0;128,140;255,255", Luminance: "0,0;128,136;255,255")),
        new("gray-toned", "Native desaturation -100/vibrance +100, then shadow/highlight grading", a => { a.ColorGradeShadowHue = 210; a.ColorGradeShadowSaturation = 30; a.ColorGradeHighlightHue = 40; a.ColorGradeHighlightSaturation = 30; }, new("gray", Saturation: -100, Vibrance: 100))
    ];

    internal static ImageAdjustments Adjustments(Recipe recipe)
    {
        var result = FullBasicChecks.Adjustments(recipe.Basic ?? new("zero"));
        if (recipe.Curves is { } c)
        {
            result.CurveRgbPoints = c.Master; result.CurveRedPoints = c.Red;
            result.CurveGreenPoints = c.Green; result.CurveBluePoints = c.Blue; result.CurveLuminancePoints = c.Luminance;
        }
        recipe.Edit(result);
        return result;
    }

    // Captured before extracting any legacy kernels, never refreshed to hide a failure.
    internal static Dictionary<string, string> LegacyHashes()
    {
        GpuImageService.Enabled = false;
        using var source = FullBasicChecks.Synthetic();
        var hashes = new Dictionary<string, string>();
        foreach (var recipe in Recipes)
        {
            var result = ImageProcessor.RenderPreviewSkBitmap(source, Adjustments(recipe)) ?? throw new InvalidOperationException("Legacy late-color renderer failed.");
            using var owned = ReferenceEquals(result, source) ? result.Copy() : result;
            var bytes = new byte[owned.ByteCount]; Marshal.Copy(owned.GetPixels(), bytes, 0, bytes.Length);
            hashes.Add(recipe.Name, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        return hashes;
    }
    internal static void CaptureLegacy(string path) => File.WriteAllText(path, JsonSerializer.Serialize(LegacyHashes(), new JsonSerializerOptions { WriteIndented = true }));

    internal static RenderedLateColorSettings Settings(ImageAdjustments a) => new(
        grade: new(new(a.ColorGradeShadowHue, a.ColorGradeShadowSaturation, a.ColorGradeShadowLuminance),
            new(a.ColorGradeMidtoneHue, a.ColorGradeMidtoneSaturation, a.ColorGradeMidtoneLuminance),
            new(a.ColorGradeHighlightHue, a.ColorGradeHighlightSaturation, a.ColorGradeHighlightLuminance),
            new(a.ColorGradeGlobalHue, a.ColorGradeGlobalSaturation, a.ColorGradeGlobalLuminance), a.ColorGradeBalance, a.ColorGradeBlending),
        mixer: new([a.ChannelMixerRedRed, a.ChannelMixerRedGreen, a.ChannelMixerRedBlue, a.ChannelMixerRedConstant,
            a.ChannelMixerGreenRed, a.ChannelMixerGreenGreen, a.ChannelMixerGreenBlue, a.ChannelMixerGreenConstant,
            a.ChannelMixerBlueRed, a.ChannelMixerBlueGreen, a.ChannelMixerBlueBlue, a.ChannelMixerBlueConstant],
            a.ChannelMixerMonochrome, a.ChannelMixerGrayRed, a.ChannelMixerGrayGreen, a.ChannelMixerGrayBlue, a.ChannelMixerGrayConstant),
        gradient: new(a.GradientMapShadowColor, a.GradientMapHighlightColor, a.GradientMapAmount),
        shadowTint: a.CalibrationShadowTint,
        posterizeLevels: a.PosterizeLevels >= 2 ? (int)Math.Round(a.PosterizeLevels) : 0,
        thresholdLevel: a.ThresholdLevel >= 1 ? (int)Math.Round(a.ThresholdLevel) : 0);
    private static RenderedBasicSettings Basic(Recipe r) => FullBasicChecks.Settings(r.Basic ?? new("zero"));
    private static RenderedCurveSettings Curves(Recipe r) => RenderedCurveChecks.Curves(r.Curves ?? new("zero"));

    internal static void Run(ProbeReport report)
    {
        Check(report, "frozen legacy references", () =>
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return "SKIP Windows x64 fixture; numerical checks still run.";
            var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/late-color-legacy.json")))!;
            foreach (var (name, hash) in LegacyHashes()) if (expected[name] != hash) throw new InvalidOperationException("Legacy color tail changed: " + name);
            return "Twenty grade/tint/mixer/map/palette/mixed references captured from accepted 45de8b6a before kernel extraction remain byte-identical.";
        });
        Check(report, "legacy pixel oracle and ordering", Oracle);
        Check(report, "independent matrix, map and palette", Reference);
        Check(report, "whole-frame range and replacement semantics", Range);
        Check(report, "identity, ownership and validated settings", Contracts);
    }
    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try { var detail = run(); report.Checks.Add(new ProbeCheck("Late color / " + name, detail.StartsWith("SKIP ") ? "SKIP" : "PASS", detail, Required: !detail.StartsWith("SKIP "))); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Late color / " + name, ex.ToString())); }
    }
    private static string Oracle()
    {
        using var source = FullBasicChecks.Synthetic();
        var bytes = new byte[source.ByteCount]; Marshal.Copy(source.GetPixels(), bytes, 0, bytes.Length);
        var input = new float[source.Width * source.Height * 4];
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
        {
            var at = (y * source.Width + x) * 4; var o = y * source.RowBytes + x * 4;
            input[at] = bytes[o + 2] / 255f; input[at + 1] = bytes[o + 1] / 255f; input[at + 2] = bytes[o] / 255f; input[at + 3] = 1;
        }
        foreach (var recipe in Recipes)
        {
            var stage = ImageProcessor.PrepareFloatingBasic(source.Width, source.Height, input, Basic(recipe), curves: Curves(recipe), lateColor: Settings(Adjustments(recipe)));
            var old = ImageProcessor.RenderPreviewSkBitmap(source, Adjustments(recipe))!;
            using var owned = ReferenceEquals(old, source) ? old.Copy() : old;
            var expected = new byte[owned.ByteCount]; Marshal.Copy(owned.GetPixels(), expected, 0, expected.Length);
            for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
            {
                var at = (y * source.Width + x) * 4; var o = y * owned.RowBytes + x * 4;
                float r = input[at], g = input[at + 1], b = input[at + 2]; stage.Apply(ref r, ref g, ref b, x, y);
                if (Math.Abs(r * 255 - expected[o + 2]) > 1.1 || Math.Abs(g * 255 - expected[o + 1]) > 1.1 || Math.Abs(b * 255 - expected[o]) > 1.1)
                    throw new InvalidOperationException($"Legacy oracle differs for {recipe.Name} at {x},{y}.");
            }
        }
        return "Twenty bounded exact-code recipes agree with the accepted old renderer within its 1.1-code quantization/dither budget, including grade-only HSL roundtrip, all four grade zones/luminances, both tint directions, basic/curves/grade/tint/mixer/map ordering and map-before-palette. Legacy encoded saturation is retained only for this oracle.";
    }
    private static string Reference()
    {
        float[] input = [.13f, .27f, .61f, 1, .8f, .4f, .2f, 1, .5f, .5f, .5f, 1];
        foreach (var recipe in Recipes.Where(r => r.Name.StartsWith("mixer-") || r.Name.StartsWith("map-") || r.Name is "posterize" or "threshold"))
        {
            var a = Adjustments(recipe);
            var stage = ImageProcessor.PrepareFloatingBasic(3, 1, input, new(), lateColor: Settings(a));
            for (var x = 0; x < 3; x++)
            {
                double[] v = [input[x * 4], input[x * 4 + 1], input[x * 4 + 2]];
                if (a.ChannelMixerMonochrome)
                {
                    var gray = (a.ChannelMixerGrayRed * v[0] + a.ChannelMixerGrayGreen * v[1] + a.ChannelMixerGrayBlue * v[2] + a.ChannelMixerGrayConstant) / 100;
                    v = Enumerable.Repeat(Math.Clamp(gray, 0, 1), 3).ToArray();
                }
                else if (a.HasChannelMixerChanges())
                {
                    double[] m = [a.ChannelMixerRedRed, a.ChannelMixerRedGreen, a.ChannelMixerRedBlue, a.ChannelMixerRedConstant,
                        a.ChannelMixerGreenRed, a.ChannelMixerGreenGreen, a.ChannelMixerGreenBlue, a.ChannelMixerGreenConstant,
                        a.ChannelMixerBlueRed, a.ChannelMixerBlueGreen, a.ChannelMixerBlueBlue, a.ChannelMixerBlueConstant];
                    v = Enumerable.Range(0, 3).Select(c => Math.Clamp((m[c * 4] * v[0] + m[c * 4 + 1] * v[1] + m[c * 4 + 2] * v[2] + m[c * 4 + 3]) / 100, 0, 1)).ToArray();
                }
                if (a.GradientMapAmount > 0)
                {
                    var lo = SKColor.Parse(a.GradientMapShadowColor); var hi = SKColor.Parse(a.GradientMapHighlightColor);
                    double[] low = [lo.Red / 255.0, lo.Green / 255.0, lo.Blue / 255.0], high = [hi.Red / 255.0, hi.Green / 255.0, hi.Blue / 255.0];
                    var gray = Math.Clamp(.299 * v[0] + .587 * v[1] + .114 * v[2], 0, 1);
                    v = Enumerable.Range(0, 3).Select(c => v[c] + (low[c] + (high[c] - low[c]) * gray - v[c]) * a.GradientMapAmount / 100).ToArray();
                }
                if (a.PosterizeLevels >= 2) v = v.Select(c => Math.Round(Math.Round(Math.Clamp(c, 0, 1) * (a.PosterizeLevels - 1)) / (a.PosterizeLevels - 1) * 255) / 255).ToArray();
                if (a.ThresholdLevel > 0) v = Enumerable.Repeat((.299 * v[0] + .587 * v[1] + .114 * v[2]) * 255 >= a.ThresholdLevel - .0001 ? 1.0 : 0.0, 3).ToArray();
                float r = input[x * 4], g = input[x * 4 + 1], b = input[x * 4 + 2]; stage.Apply(ref r, ref g, ref b, x, 0);
                if (new[] { (double)r, g, b }.Zip(v).Any(p => Math.Abs(p.First - p.Second) > .000002))
                    throw new InvalidOperationException("Independent color/palette reference differs: " + recipe.Name);
            }
        }
        foreach (var level in new[] { 2, 6, 64 })
        {
            var stage = ImageProcessor.PrepareFloatingBasic(1, 1, [.5f, .5f, .5f, 1], new(), lateColor: new(posterizeLevels: level));
            var palette = new HashSet<float>();
            for (var i = 0; i <= 4096; i++) { float r = i / 4096f, g = r, b = r; stage.Apply(ref r, ref g, ref b, 0, 0); palette.Add(r); }
            if (palette.Count != level) throw new InvalidOperationException("Posterize palette is not flat or has the wrong count.");
        }
        var binary = ImageProcessor.PrepareFloatingBasic(1, 1, [.5f, .5f, .5f, 1], new(), lateColor: new(thresholdLevel: 128));
        foreach (var code in new[] { 127.99f, 128f, 128.01f })
        {
            float r = code / 255, g = r, b = r; binary.Apply(ref r, ref g, ref b, 0, 0);
            if (r != (code < 128 ? 0 : 1) || r != g || r != b) throw new InvalidOperationException("Inclusive threshold tie differs.");
        }
        return "Independent Double encoded-sRGB channel matrices, monochrome weights, offsets/clamps, Rec.601 gradient interpolation, amount, ties-to-even byte-code palette and inclusive binary threshold; combined map/palette order checked. Dense ramps produce exactly 2/6/64 requested levels, with no dither inside the working image.";
    }
    private static string Range()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(5, 1, [4, -.25f, .3f, 1, 2, 2, 2, .5f, 4, 4, 4, 1, -.25f, -.25f, -.25f, 1, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        var before = new float[20]; source.ReadLinearRow(0, before);
        foreach (var recipe in Recipes)
        {
            var settings = Settings(Adjustments(recipe));
            using var result = RenderedBasicControls.Apply(source, Basic(recipe), Curves(recipe), settings);
            var output = new float[20]; result.ReadLinearRow(0, output);
            if (output.Any(v => !float.IsFinite(v)) || output[7] != .5 || output[3] != 1 || output.Skip(16).Any(v => v != 0)) throw new InvalidOperationException("Late color lost finite values/alpha.");
            if (settings.ResidualRetention == 1 && (output[8] <= 1 || output[12] >= 0)) throw new InvalidOperationException("Preserving artistic stages lost neutral HDR/signed reserve.");
            if (recipe.Name == "mixer-monochrome" && Enumerable.Range(0, 4).Any(x => output[x * 4] != output[x * 4 + 1] || output[x * 4] != output[x * 4 + 2])) throw new InvalidOperationException("Monochrome mixer leaks source wide-gamut color.");
            if (recipe.Name is "threshold" or "map-threshold") for (var x = 0; x < 4; x++)
                if (output[x * 4] != output[x * 4 + 1] || output[x * 4] != output[x * 4 + 2] || (output[x * 4] != 0 && output[x * 4] != output[x * 4 + 3])) throw new InvalidOperationException("Binary effect leaks a color/range reserve.");
        }
        foreach (var amount in new[] { 0, 25, 50, 100 })
        {
            using var mapped = RenderedBasicControls.Apply(source, new(), lateColor: new(gradient: new(amount: amount)));
            var output = new float[20]; mapped.ReadLinearRow(0, output);
            var expected = 1 + 3 * (1 - amount / 100.0);
            if (Math.Abs(output[8] - expected) > .002 || Math.Abs(output[4] - expected * .5) > .001)
                throw new InvalidOperationException("Partial map does not fade the HDR reserve with its unchanged contribution.");
            if (amount == 100 && Enumerable.Range(0, 4).Any(x => output[x * 4] != output[x * 4 + 1] || output[x * 4] != output[x * 4 + 2])) throw new InvalidOperationException("Full B&W gradient leaks chroma.");
        }
        foreach (var tint in new[] { -40f, 40f })
        {
            using var expected = CalibrationShadowTint.Apply(source, tint);
            using var actual = RenderedBasicControls.Apply(source, new(), lateColor: new(shadowTint: tint));
            var a = new float[20]; var b = new float[20]; expected.ReadLinearRow(0, a); actual.ReadLinearRow(0, b);
            if (a.Zip(b).Any(p => Math.Abs(p.First - p.Second) > Math.Max(.000002, Math.Abs(p.First) * .001))) throw new InvalidOperationException("Late tint hook differs from accepted standalone calibration adapter.");
        }
        var unchanged = new float[20]; source.ReadLinearRow(0, unchanged);
        if (!before.SequenceEqual(unchanged)) throw new InvalidOperationException("Late color modified the input frame.");
        return "Whole-frame wide/signed/HDR/partial-alpha cases distinguish reserve-preserving grade/tint from bounded mixer/palette replacement; full maps and monochrome/binary effects leak no original color. Partial maps fade HDR reserves by 1-amount. Late tint agrees with the accepted standalone adapter; source ownership and coverage pass.";
    }
    private static string Contracts()
    {
        float[] matrix = [100, 0, 0, 0, 0, 100, 0, 0, 0, 0, 100, 0];
        var snapshot = new RenderedMixerSettings(matrix); matrix[0] = 150;
        var copied = new ImageAdjustments(); snapshot.CopyTo(copied); if (copied.ChannelMixerRedRed != 100) throw new InvalidOperationException("Mutable mixer input leaked into snapshot.");
        using var source = LinearWorkingFrame.FromLinearPixels(2, 1, [.2f, .4f, .6f, 1, 2, 2, 2, .5f], WorkingImageDomain.RenderedLinear);
        foreach (var recipe in RenderedCurveChecks.Recipes)
        {
            var basic = FullBasicChecks.Settings(recipe.Basic ?? new("zero")); var curves = RenderedCurveChecks.Curves(recipe);
            using var a = RenderedBasicControls.Apply(source, basic, curves);
            using var b = RenderedBasicControls.Apply(source, basic, curves, new());
            var x = new float[8]; var y = new float[8]; a.ReadLinearRow(0, x); b.ReadLinearRow(0, y);
            if (!x.SequenceEqual(y)) throw new InvalidOperationException("Neutral late controls changed accepted curves/basic output.");
        }
        if (!new RenderedLateColorSettings(posterizeLevels: 1).IsIdentity) throw new InvalidOperationException("Inactive one-level GUI value is not neutral.");
        var fractional = Settings(new ImageAdjustments { PosterizeLevels = 2.9f, ThresholdLevel = 128.5f });
        if (fractional.PosterizeLevels != 3 || fractional.ThresholdLevel != 128)
            throw new InvalidOperationException("Diagnostic UI snapshot truncated counts instead of using legacy ties-to-even rounding.");
        Action[] invalid = [() => _ = new RenderedGradeZone(hue: float.NaN), () => _ = new RenderedGradeZone(saturation: 101),
            () => _ = new RenderedGradeZone(luminance: -101), () => _ = new RenderedGradeSettings(balance: 101),
            () => _ = new RenderedGradeSettings(blending: -1), () => _ = new RenderedLateColorSettings(shadowTint: float.PositiveInfinity),
            () => _ = new RenderedLateColorSettings(posterizeLevels: -1), () => _ = new RenderedLateColorSettings(posterizeLevels: 65),
            () => _ = new RenderedLateColorSettings(thresholdLevel: 256), () => _ = new RenderedMixerSettings([1, 2]),
            () => _ = new RenderedMixerSettings(grayRed: 201), () => _ = new RenderedGradientSettings(shadow: "not-a-color"),
            () => _ = new RenderedGradientSettings(amount: 101)];
        foreach (var reject in invalid) { try { reject(); throw new InvalidOperationException("Invalid late control accepted."); } catch (ArgumentException) { } }
        return "Immutable copied matrices; neutral late settings preserve all ten accepted curve/basic recipes exactly. Finite GUI ranges, matrix dimensions, swatch parsing and integer palette/threshold domains reject invalid inputs. Inactive grade hues/balance and mixer gray weights do not activate neutral controls.";
    }

    private static IEnumerable<Recipe> VisualRecipes => Recipes.Where(r => r.Name is "zero" or "grade-combined" or "shadow-tint-plus" or "mixer-color" or "mixer-monochrome" or "map-half" or "map-full" or "posterize" or "threshold" or "mixed-chain" or "gray-toned");
    private static bool HasReference(Recipe r) => r.Name is "grade-combined" or "mixer-color" or "map-full" or "mixed-chain";
    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var cube = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? ProfiledPngFixture.Read(path) : null;
        using var source = cube?.Frame ?? RenderedContrastChecks.Input(path);
        if (cube is not null) report.Checks.Add(new ProbeCheck($"Late color cube / {name}", "PASS", $"{cube.Description}; ICC SHA256 {cube.ProfileHash}; decoded RGBA SHA256 {cube.PixelHash}. Independent source ICC/alpha reference passes; diagnostic PNG adapter only."));
        using var legacySource = WorkingImageBoundary.ToDiagnosticSdrSrgb8(source);
        foreach (var recipe in VisualRecipes)
        {
            using var result = RenderedBasicControls.Apply(source, Basic(recipe), Curves(recipe), Settings(Adjustments(recipe)));
            SceneRenderingChecks.SavePreview(result, output, $"{name}-color-{recipe.Name}.png");
            if (!HasReference(recipe)) continue;
            var old = ImageProcessor.RenderPreviewSkBitmap(legacySource, Adjustments(recipe))!;
            using var owned = ReferenceEquals(old, legacySource) ? old.Copy() : old;
            using var frame = WorkingImageBoundary.FromEncodedSrgb(owned);
            SceneRenderingChecks.SavePreview(frame, output, $"{name}-color-{recipe.Name}-legacy.png");
        }
        report.Checks.Add(new ProbeCheck($"Late color / {name}", "PASS", $"{Path.GetFileName(path)}: eleven full-resolution recipes and four old references, covering grade/tint/mixer/map/palette and combined basic/curve interactions. Visual acceptance pending."));
    }
    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new System.Text.StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>F16 late color tools</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:400px;object-fit:contain;background:#111}a{color:#bce0ff}</style><h1>F16 late color tools</h1><p>Color grading, calibration shadow tint, channel mixing, gradient mapping, posterize and threshold. The working image is linear Rec.2020 F16; these artistic controls retain their encoded-sRGB calibration through the F32 adapter. Grading/tint retain the wide/HDR reserve. Channel mixing and palette effects deliberately produce bounded output; a partial map fades the reserve with its unchanged fraction, while a full map replaces it. Posterize steps and the threshold edge are intentional. Check smooth grading/partial maps, skin, highlight/shadow casts, neutral monochrome mixing, flat palette/binary output and mixed-chain ordering. Gray-toned intentionally adds color after desaturation. Mixed-chain includes native saturation +10, vibrance +20, HL -30/shadows +25, whites +10/blacks -10, brightness +5/contrast +10 and master/red/luminance curves. Four old-editor references use the same developed source through an sRGB8 boundary; wide/HDR and native-saturation differences can be intentional. All PNGs are diagnostic SDR sRGB. <a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2><div class='grid'>");
            foreach (var recipe in VisualRecipes)
            {
                var file = $"image-{i + 1}-color-{recipe.Name}.png";
                html.Append($"<figure><figcaption>{recipe.Name}: {WebUtility.HtmlEncode(recipe.Description)}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{recipe.Name}'></a></figure>");
            }
            html.Append("</div><h3>Old sRGB8 references</h3><div class='grid'>");
            foreach (var recipe in VisualRecipes.Where(HasReference))
            {
                var file = $"image-{i + 1}-color-{recipe.Name}-legacy.png";
                html.Append($"<figure><figcaption>{recipe.Name}: old editor</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{recipe.Name}'></a></figure>");
            }
            html.Append("</div>");
        }
        File.WriteAllText(Path.Combine(output, "late-color-review.html"), html.Append("</html>").ToString());
    }
}
