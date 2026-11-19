using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;
internal static class RenderedInputChecks
{
    internal sealed record Recipe(string Name, Action<ImageAdjustments> Edit);
    internal static readonly Recipe[] Recipes =
    [
        new("zero", _ => { }),
        new("legacy-warm", a => { a.WhiteBalanceModel = 1; a.Temperature = 30; a.Tint = 15; }),
        new("legacy-cool", a => { a.WhiteBalanceModel = 1; a.Temperature = -30; a.Tint = -15; }),
        new("model2-upgrade", a => { a.WhiteBalanceModel = 2; a.WhiteBalanceKelvin = 4200; a.WhiteBalanceKelvinTint = 12; }),
        new("adapt-warm", a => { a.WhiteBalanceModel = 4; a.Temperature = 25; a.Tint = 10; }),
        new("adapt-cool", a => { a.WhiteBalanceModel = 3; a.Temperature = -25; a.Tint = -10; }),
        new("kelvin", a => { a.WhiteBalanceModel = 4; a.WhiteBalanceKelvin = 4200; a.WhiteBalanceKelvinTint = 12; }),
        new("negative", a => { a.NegativeEnabled = true; a.NegativeBaseColor = "#FFF0B478"; a.NegativeDensityColor = "#FF301810"; }),
        new("negative-mono", a => { a.NegativeEnabled = true; a.NegativeMonochrome = true; a.NegativeBaseColor = "#FFF0B478"; a.NegativeDensityColor = "#FF301810"; a.NegativeGamma = 25; }),
        new("negative-wb", a => { a.NegativeEnabled = true; a.NegativeBaseColor = "#FFF0B478"; a.NegativeDensityColor = "#FF301810"; a.WhiteBalanceModel = 4; a.Temperature = 20; a.Tint = -10; })
    ];
    internal static ImageAdjustments Original(Recipe r) { var a = new ImageAdjustments(); r.Edit(a); return a; }
    internal static ImageAdjustments Adjustments(Recipe r) { var a = Original(r); ImageProcessor.UpgradeWhiteBalanceModel(a); return a; }
    internal static IEnumerable<(string, ImageAdjustments)> LegacyRecipes() => Recipes.Select(r => (r.Name, Adjustments(r)));

    internal static RenderedWhiteBalanceSettings WhiteBalance(ImageAdjustments a) => new(a.WhiteBalanceModel, a.Temperature, a.Tint, a.WhiteBalanceKelvin, a.WhiteBalanceKelvinTint);
    internal static RenderedNegativeSettings Negative(ImageAdjustments a) => new(a.NegativeEnabled, a.NegativeBaseColor, a.NegativeDensityColor, a.NegativeGamma, a.NegativeMonochrome);
    private static readonly double[] ToSrgb = [1.660491002, -.587641139, -.072849863, -.124550475, 1.132899897, -.008349422, -.018150763, -.100578898, 1.118729661];
    private static readonly double[] ToRec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
    internal static void Run(ProbeReport report)
    {
        Check(report, "frozen legacy WB/negative recipes", () =>
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return "SKIP Windows x64 fixture; independent checks still run.";
            var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/rendered-input-legacy.json")))!;
            foreach (var (name, hash) in StageLegacyFixture.Hashes(LegacyRecipes())) if (expected[name] != hash) throw new InvalidOperationException("Legacy input recipe changed: " + name);
            return "Ten accepted 5f403cf7 pre-change legacy recipes remain unchanged; model 2 is loaded through the editor's existing upgrade.";
        });
        Check(report, "independent Bradford and legacy matrix reference", WhiteReference);
        Check(report, "independent negative tables and combined order", FilmReference);
        Check(report, "identity, alpha, linearity, ownership and contracts", Contracts);
    }
    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try { var detail = run(); report.Checks.Add(new ProbeCheck("Rendered input / " + name, detail.StartsWith("SKIP ") ? "SKIP" : "PASS", detail, Required: !detail.StartsWith("SKIP "))); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Rendered input / " + name, ex.ToString())); }
    }
    // Independent Double cone adaptation, using the already established public
    // white-point/recipe interpretation but no production matrix or fusion kernel.
    private static double[] Adapt(double[] rgb, ImageAdjustments a)
    {
        var anchor = ImageProcessor.WhiteBalanceAnchorOf(a); var claimed = ImageProcessor.ClaimedWhitePointOf(a);
        if (Math.Abs(anchor.X - claimed.X) < .0000005 && Math.Abs(anchor.Y - claimed.Y) < .0000005) return rgb;
        double[] bradford = [.8951, .2664, -.1614, -.7502, 1.7135, .0367, .0389, -.0685, 1.0296];
        double[] inverse = [.9869929, -.1470543, .1599627, .4323053, .5183603, .0492912, -.0085287, .0400428, .9684867];
        double[] toXyz = [.4124564, .3575761, .1804375, .2126729, .7151522, .0721750, .0193339, .1191920, .9503041];
        double[] fromXyz = [3.2404542, -1.5371385, -.4985314, -.9692660, 1.8760108, .0415560, .0556434, -.2040259, 1.0572252];
        var ca = RenderedContrastChecks.Matrix(bradford, [anchor.X / anchor.Y, 1, (1 - anchor.X - anchor.Y) / anchor.Y]);
        var cc = RenderedContrastChecks.Matrix(bradford, [claimed.X / claimed.Y, 1, (1 - claimed.X - claimed.Y) / claimed.Y]);
        double[] Raw(double[] value)
        {
            var cones = RenderedContrastChecks.Matrix(bradford, RenderedContrastChecks.Matrix(toXyz, value));
            return RenderedContrastChecks.Matrix(fromXyz, RenderedContrastChecks.Matrix(inverse, cones.Select((v, i) => v * ca[i] / cc[i]).ToArray()));
        }
        var white = Raw([1, 1, 1]); var norm = .2126729 * white[0] + .7151522 * white[1] + .0721750 * white[2];
        return Raw(rgb).Select(v => v / norm).ToArray();
    }
    private static string WhiteReference()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(5, 1, [.2f, .3f, .1f, 1, 4, -.25f, .3f, 1, 2, 2, 2, .5f, -.1f, .2f, .4f, .5f, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        var input = new float[20]; source.ReadLinearRow(0, input);
        foreach (var recipe in Recipes.Where(r => !r.Name.StartsWith("negative")))
        {
            var original = Original(recipe); var a = Adjustments(recipe);
            using var result = RenderedInputControls.Apply(source, WhiteBalance(original)); var output = new float[20]; result.ReadLinearRow(0, output);
            for (var x = 0; x < 4; x++)
            {
                var at = x * 4; var alpha = input[at + 3]; double[] baseline = [input[at] / (double)alpha, input[at + 1] / (double)alpha, input[at + 2] / (double)alpha];
                var srgb = RenderedContrastChecks.Matrix(ToSrgb, baseline); double[] expected;
                if (a.WhiteBalanceModel < 2)
                {
                    var p = srgb.Select(v => (double)(float)RenderedContrastChecks.Encode(Math.Clamp(v, 0, 1))).ToArray();
                    double[] gains = [1 + a.Temperature / 200.0, 1 + a.Tint / 200.0, 1 - a.Temperature / 200.0];
                    var delta = RenderedContrastChecks.Matrix(ToRec, p.Select((v, c) => RenderedContrastChecks.Decode(Math.Clamp(v * gains[c], 0, 1)) - RenderedContrastChecks.Decode(v)).ToArray());
                    expected = baseline.Zip(delta).Select(v => v.First + v.Second).ToArray();
                }
                else expected = RenderedContrastChecks.Matrix(ToRec, Adapt(srgb, a));
                for (var c = 0; c < 3; c++) if (Math.Abs(output[at + c] - expected[c] * alpha) > Math.Max(.000003, Math.Abs(expected[c] * alpha) * .0012))
                    throw new InvalidOperationException($"Independent WB reference differs at {recipe.Name}, {x}/{c}: {output[at + c]} vs {expected[c] * alpha}.");
                if (output[at + 3] != alpha) throw new InvalidOperationException("WB changed alpha.");
            }
        }
        return "Independent Double Bradford cone/XYZ/sRGB computation and existing luminance normalization, plus legacy encoded gains/delta, match fused Rec.2020 results on signed/HDR/partial-alpha cases (0.12% half/reference budget). Model-2 load-time semantics, relative/absolute controls and neutral paths covered.";
    }
    private static double NegativeNode(int node, byte baseCode, byte densityCode, float gammaSlider)
    {
        var baseValue = Math.Max(2.0, baseCode); var density = Math.Min(Math.Max(1.0, densityCode), baseValue - 1);
        var gamma = (float)Math.Pow(2, gammaSlider / 100.0);
        var v = Math.Log(baseValue / Math.Max(1, node / 4096.0 * 255)) / Math.Log(baseValue / density);
        return Math.Pow(Math.Clamp(v, 0, 1), 1 / Math.Max(.05, gamma));
    }
    private static double SampleNegative(float value, byte baseCode, byte densityCode, float gamma)
    {
        var pos = value * 4096; var lo = Math.Min(4095, (int)Math.Floor(pos)); var t = pos - lo;
        return (float)NegativeNode(lo, baseCode, densityCode, gamma) +
            ((float)NegativeNode(lo + 1, baseCode, densityCode, gamma) - (float)NegativeNode(lo, baseCode, densityCode, gamma)) * t;
    }
    private static string FilmReference()
    {
        foreach (var gamma in new[] { -100f, 0, 25, 100 })
        {
            var film = new RenderedNegativeSettings(true, "#FFF0B478", "#FF301810", gamma);
            var tables = ImageProcessor.ExplicitNegativeTables(film);
            byte[] bases = [240, 180, 120], densities = [48, 24, 16];
            for (var c = 0; c < 3; c++) for (var i = 0; i <= 4096; i++)
                if (Math.Abs(tables[c][i] - NegativeNode(i, bases[c], densities[c], gamma)) > .000001)
                    throw new InvalidOperationException("Independent negative logarithmic table differs.");
        }
        using var source = LinearWorkingFrame.FromLinearPixels(4, 1, [.2f, .3f, .1f, 1, .02f, .03f, .01f, .5f, 4, 4, 4, 1, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        var input = new float[16]; source.ReadLinearRow(0, input);
        foreach (var recipe in Recipes.Where(r => r.Name.StartsWith("negative")))
        {
            var original = Original(recipe); var a = Adjustments(recipe);
            using var result = RenderedInputControls.Apply(source, WhiteBalance(original), Negative(a));
            var output = new float[16]; result.ReadLinearRow(0, output);
            for (var x = 0; x < 3; x++)
            {
                var at = x * 4; var alpha = input[at + 3];
                var srgb = RenderedContrastChecks.Matrix(ToSrgb, [input[at] / (double)alpha, input[at + 1] / (double)alpha, input[at + 2] / (double)alpha]);
                byte[] bases = [240, 180, 120], densities = [48, 24, 16];
                var positive = srgb.Select((v, c) => SampleNegative((float)RenderedContrastChecks.Encode(Math.Clamp(v, 0, 1)), bases[c], densities[c], a.NegativeGamma)).ToArray();
                if (a.NegativeMonochrome) { var gray = .299 * positive[0] + .587 * positive[1] + .114 * positive[2]; positive = [gray, gray, gray]; }
                var linear = positive.Select(RenderedContrastChecks.Decode).ToArray();
                if (a.WhiteBalanceModel >= 2) linear = Adapt(linear, a);
                var expected = RenderedContrastChecks.Matrix(ToRec, linear);
                for (var c = 0; c < 3; c++) if (Math.Abs(output[at + c] - expected[c] * alpha) > Math.Max(.000003, Math.Abs(expected[c] * alpha) * .0012))
                    throw new InvalidOperationException("Negative/WB order or source primaries differ: " + recipe.Name);
                if (a.NegativeMonochrome && (output[at] != output[at + 1] || output[at] != output[at + 2])) throw new InvalidOperationException("Monochrome negative leaked original color.");
                if (output[at + 3] != alpha) throw new InvalidOperationException("Negative changed alpha.");
            }
        }
        return "Independent analytic log-density/gamma at all 4097 nodes, three channels and four gamma values matches within 1e-6; full-frame reference checks film normalization before monochrome and Bradford WB, bounded replacement of wide/HDR scan input, partial alpha and native monochrome output.";
    }
    private static string Contracts()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(4, 1, [.2f, .3f, .1f, 1, 4, 4, 4, 1, 2, 2, 2, .5f, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        var before = new float[16]; source.ReadLinearRow(0, before);
        for (var model = 0; model <= 4; model++)
        {
            using var zero = RenderedInputControls.Apply(source, new(model)); var after = new float[16]; zero.ReadLinearRow(0, after);
            if (!before.SequenceEqual(after)) throw new InvalidOperationException("Neutral WB model lost exact identity.");
        }
        var settings = new RenderedWhiteBalanceSettings(kelvin: 4200, kelvinTint: 12);
        var copy = settings.CopyLinearMatrix(); var saved = copy[0]; copy[0] = 100;
        if (settings.CopyLinearMatrix()[0] != saved) throw new InvalidOperationException("WB matrix is mutable through a copy.");
        using var ramp = LinearWorkingFrame.FromLinearPixels(2, 1, [1, 1, 1, 1, 2, 2, 2, 1], WorkingImageDomain.RenderedLinear);
        using var balanced = RenderedInputControls.Apply(ramp, settings); var values = new float[8]; balanced.ReadLinearRow(0, values);
        for (var c = 0; c < 3; c++) if (Math.Abs(values[c + 4] - 2 * values[c]) > .00001 || values[c + 4] <= 0) throw new InvalidOperationException("Linear WB clipped or failed exposure scaling.");
        if (values.Take(7).Max() <= 1) throw new InvalidOperationException("WB lost HDR range.");
        Action[] invalid = [() => _ = new RenderedWhiteBalanceSettings(model: 5), () => _ = new RenderedWhiteBalanceSettings(temperature: float.NaN),
            () => _ = new RenderedWhiteBalanceSettings(kelvin: 999), () => _ = new RenderedWhiteBalanceSettings(kelvinTint: double.PositiveInfinity),
            () => _ = new RenderedWhiteBalanceSettings(model: 1, kelvin: 4200), () => _ = new RenderedNegativeSettings(true),
            () => _ = new RenderedNegativeSettings(true, "not-a-color", "#FF000000"), () => _ = new RenderedNegativeSettings(true, "#FF102030", "#FF203040"),
            () => _ = new RenderedNegativeSettings(gamma: 101)];
        foreach (var reject in invalid) { try { reject(); throw new InvalidOperationException("Invalid input settings accepted."); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { } }
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        try { using var rejected = RenderedInputControls.Apply(scene); throw new InvalidOperationException("Scene input accepted by rendered adapter."); } catch (ArgumentException) { }
        var unchanged = new float[16]; source.ReadLinearRow(0, unchanged); if (!before.SequenceEqual(unchanged)) throw new InvalidOperationException("Input adapter modified source.");
        return "Models 0..4 neutral identity is exact; copied matrices, source ownership, partial alpha, native WB exposure linearity/HDR and invalid domain/control/explicit-film metadata checked. Missing automatic film calibration fails explicitly instead of making an RGB8 analysis image.";
    }
    private static bool HasReference(Recipe r) => r.Name is "legacy-warm" or "kelvin" or "negative" or "negative-wb";
    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        if (RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("This rendered-input comparison excludes RAW to avoid duplicating the accepted anchored scene WB.");
        var cube = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? ProfiledPngFixture.Read(path) : null;
        using var source = cube?.Frame ?? RenderedContrastChecks.Input(path);
        if (cube is not null) report.Checks.Add(new ProbeCheck($"Rendered input cube / {name}", "PASS", $"{cube.Description}; ICC {cube.ProfileHash}; RGBA {cube.PixelHash}; original source-profile/alpha checks pass."));
        using var legacySource = WorkingImageBoundary.ToDiagnosticSdrSrgb8(source);
        foreach (var recipe in Recipes)
        {
            var a = Original(recipe); using var result = RenderedInputControls.Apply(source, WhiteBalance(a), Negative(a));
            SceneRenderingChecks.SavePreview(result, output, $"{name}-input-{recipe.Name}.png");
            if (!HasReference(recipe)) continue;
            var old = ImageProcessor.RenderPreviewSkBitmap(legacySource, Adjustments(recipe))!; using var owned = ReferenceEquals(old, legacySource) ? old.Copy() : old;
            using var frame = WorkingImageBoundary.FromEncodedSrgb(owned); SceneRenderingChecks.SavePreview(frame, output, $"{name}-input-{recipe.Name}-legacy.png");
        }
        FilmMeasurementChecks.Capture(report, output, source, legacySource, name);
        report.Checks.Add(new ProbeCheck($"Rendered input / {name}", "PASS", "Ten native F16 preparation recipes and four old references, plus measured automatic color/mono inversion and old automatic reference. Appearance awaits review."));
    }
    internal static void WriteReview(string output, IReadOnlyList<string> images) => StageReview.Write(output, images, "input", "rendered-input-review.html", "Rendered WB and film inversion",
        "Rendered sources only; anchored RAW WB remains in the scene pipeline. Focus on auto/auto-mono versus zero and the old auto reference, especially scanner-holder edges, grain, deepest density and pale gradients. Auto uses floating 0.5/99.5 percentiles of linear-light block averages, excluding detected holder edges only from measurement. Calibration remains fractional through log inversion, with a 16-bit-equivalent logarithm floor. Explicit negative recipes still use illustrative fixed swatches, not per-image calibration. Cropping/eyedropper/editor routing remains future work. Film inversions of positive photos/cubes are mathematical stress cases, not intended finished photographs. All previews are diagnostic SDR sRGB.",
        Recipes.Select(r => (r.Name, HasReference(r))).Concat(new[] { ("auto", true), ("auto-mono", false) }));
}
