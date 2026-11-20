using System.Runtime.InteropServices;
using System.Text.Json;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

internal static class SpatialChecks
{
    internal static void Run(ProbeReport report)
    {
        Check(report, "frozen legacy blur arithmetic", () =>
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return "SKIP Windows x64 frozen arithmetic fixture.";
            var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/spatial-legacy.json")))!;
            foreach (var (key, hash) in SpatialLegacyFixture.Hashes()) if (expected[key] != hash) throw new InvalidOperationException("Legacy blur arithmetic changed: " + key);
            return "36 pre-change blur-plane hashes preserved across dimensions, odd/even windows, large padding and both normalized/byte units.";
        });
        Check(report, "independent three-box kernel and alpha reference", Reference);
        Check(report, "unsharp straight-color/coverage and HDR reference", Unsharp);
        Check(report, "identity, large-to-shadow cancellation and contracts", Contracts);
    }
    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try { var s = run(); report.Checks.Add(new ProbeCheck("Working spatial / " + name, s.StartsWith("SKIP ") ? "SKIP" : "PASS", s, Required: !s.StartsWith("SKIP "))); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Working spatial / " + name, ex.ToString())); }
    }
    private static float[] Pixels(LinearWorkingFrame frame)
    {
        var result = new float[frame.Width * frame.Height * 4]; var row = new float[frame.Width * 4];
        for (var y = 0; y < frame.Height; y++) { frame.ReadLinearRow(y, row); row.CopyTo(result, y * row.Length); }
        return result;
    }
    private static LinearWorkingFrame Source(WorkingImageDomain domain = WorkingImageDomain.RenderedLinear) => LinearWorkingFrame.Create(7, 5, domain, (y, row) =>
    {
        for (var x = 0; x < 7; x++)
        {
            var i = x * 4; var a = (x + y) % 4 == 0 ? 0f : (x + y) % 3 == 0 ? .25f : 1f;
            row[i] = ((x + y) % 7 - 2) * a; row[i + 1] = .000001f * (x + 1) * a;
            row[i + 2] = (.2f + y * .1f) * a; row[i + 3] = a;
        }
    });
    // Build the full 1D convolution kernel in Double, then apply it directly to
    // the original edge-extended image. No running sums, production blur routines
    // or intermediate edge clamping are used by this independent oracle.
    private static double[] Reference(float[] pixels, int w, int h, float sigma)
    {
        var window = Math.Max(1, (int)Math.Floor(sigma * 3.0 * Math.Sqrt(2 * Math.PI) / 4 + .5));
        Dictionary<int, double> kernel = new() { [0] = 1 };
        if (window > 1)
        {
            var half = window / 2;
            var boxes = window % 2 == 1 ? new[] { (half, half), (half, half), (half, half) } : new[] { (half, half - 1), (half - 1, half), (half, half) };
            foreach (var (before, after) in boxes)
            {
                Dictionary<int, double> next = [];
                foreach (var (offset, weight) in kernel)
                for (var i = -before; i <= after; i++) next[offset + i] = next.GetValueOrDefault(offset + i) + weight / (before + after + 1);
                kernel = next;
            }
        }
        var horizontal = new double[pixels.Length]; var output = new double[pixels.Length];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) for (var c = 0; c < 4; c++)
            foreach (var (offset, weight) in kernel) horizontal[(y * w + x) * 4 + c] += weight * pixels[(y * w + Math.Clamp(x + offset, 0, w - 1)) * 4 + c];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) for (var c = 0; c < 4; c++)
            foreach (var (offset, weight) in kernel) output[(y * w + x) * 4 + c] += weight * horizontal[(Math.Clamp(y + offset, 0, h - 1) * w + x) * 4 + c];
        return output;
    }
    private static void Compare(double expected, float actual, string context)
    {
        if (!float.IsFinite(actual) || Math.Abs(expected - actual) > Math.Max(1.2e-7, Math.Abs(expected) * .0012))
            throw new InvalidOperationException($"{context}: expected {expected:G9}, got {actual:G9}.");
    }
    private static string Reference()
    {
        foreach (var domain in Enum.GetValues<WorkingImageDomain>())
        {
            using var source = Source(domain); var input = Pixels(source);
            foreach (var sigma in new[] { .2f, .5f, 1f, 1.3f, 2f, 7f, 20f })
            {
                var expected = Reference(input, 7, 5, sigma);
                using var result = WorkingSpatial.Blur(source, sigma); var actual = Pixels(result);
                for (var i = 0; i < actual.Length; i++) Compare(expected[i], actual[i], $"Blur {domain}/{sigma}/{i}");
                if (result.Domain != domain) throw new InvalidOperationException("Blur changed domain.");
            }
            if (!input.SequenceEqual(Pixels(source))) throw new InvalidOperationException("Blur modified source.");
        }
        return "Independent full Double convolution agrees with separable F32/Double running filters within 0.12% half tolerance; both domains, signed/HDR/partial alpha, odd/even windows and kernels wider than the image pass.";
    }
    private static string Unsharp()
    {
        using var source = Source(); var input = Pixels(source); var blurred = Reference(input, 7, 5, 1.3f);
        foreach (var amount in new[] { .5f, 1f, 3f })
        {
            using var result = WorkingSpatial.Unsharp(source, 1.3f, amount); var actual = Pixels(result);
            for (var i = 0; i < 35; i++)
            {
                var a = input[i * 4 + 3]; var ba = blurred[i * 4 + 3];
                if (actual[i * 4 + 3] != a) throw new InvalidOperationException("Unsharp changed source coverage.");
                for (var c = 0; c < 3; c++)
                {
                    var expected = a <= 0 || ba <= 0 ? input[i * 4 + c] :
                        (input[i * 4 + c] / (double)a + amount * (input[i * 4 + c] / (double)a - blurred[i * 4 + c] / ba)) * a;
                    Compare(expected, actual[i * 4 + c], "Unsharp");
                }
            }
            if (actual.Where((_, i) => i % 4 != 3).Max() <= 1 || actual.Min() >= 0) throw new InvalidOperationException("Unsharp clamped HDR or negative overshoot.");
        }
        using var alphaEdge = LinearWorkingFrame.Create(11, 3, WorkingImageDomain.RenderedLinear, (_, row) =>
        {
            for (var x = 0; x < 11; x++) { var a = x < 3 ? 0 : x < 7 ? .25f : 1; row[x * 4] = 4 * a; row[x * 4 + 1] = -a; row[x * 4 + 2] = .25f * a; row[x * 4 + 3] = a; }
        });
        using var sharpened = WorkingSpatial.Unsharp(alphaEdge, 2, 3);
        if (!Pixels(alphaEdge).SequenceEqual(Pixels(sharpened))) throw new InvalidOperationException("Flat straight color changed across alpha edge.");
        return "Straight-color unsharp matches independent Double reference, retains source alpha and signed/HDR overshoot; constant color across alpha edges remains exact. This is a primitive, not the editor's calibrated sharpen replacement.";
    }
    private static string Contracts()
    {
        using var source = Source(); var before = Pixels(source);
        foreach (var result in new[] { WorkingSpatial.Blur(source, 0), WorkingSpatial.Unsharp(source, 2, 0), WorkingSpatial.RenderedBlur(source, 1) })
        {
            using (result) if (ReferenceEquals(source, result) || !before.SequenceEqual(Pixels(result))) throw new InvalidOperationException("Neutral spatial operation changed samples/ownership.");
        }
        using var extreme = LinearWorkingFrame.Create(257, 1, WorkingImageDomain.SceneLinear, (_, row) =>
        {
            for (var x = 0; x < 257; x++) { row[x * 4] = x == 0 ? 65504 : .000001f; row[x * 4 + 1] = -.000001f; row[x * 4 + 2] = 0; row[x * 4 + 3] = 1; }
        });
        using var smooth = WorkingSpatial.Blur(extreme, 2); var values = Pixels(smooth);
        for (var x = 20; x < 257; x++)
            if (Math.Abs(values[x * 4] - .000001f) > 1.2e-7 || Math.Abs(values[x * 4 + 1] + .000001f) > 1.2e-7)
                throw new InvalidOperationException("Running-sum cancellation left an HDR-to-shadow trail.");
        Action[] invalid = [
            () => { using var r = WorkingSpatial.Blur(source, float.NaN); }, () => { using var r = WorkingSpatial.Blur(source, -1); },
            () => { using var r = WorkingSpatial.Unsharp(source, 1, 6); }, () => { using var r = WorkingSpatial.RenderedBlur(source, 101); },
            () => { using var r = WorkingSpatial.RenderedBlur(extreme, 20); }];
        foreach (var reject in invalid) { try { reject(); throw new Exception("Invalid spatial controls accepted."); } catch (ArgumentException) { } }
        return "Neutral identities/ownership, GUI blur threshold, invalid domains/controls and 65504-to-1e-6 cancellation tail pass. Working blur uses Double differences; legacy arithmetic stays frozen.";
    }

    private static readonly string[] Names = ["zero", "blur-10", "blur-35", "blur-70", "unsharp-fine", "unsharp-wide"];
    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var cube = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? ProfiledPngFixture.Read(path) : null;
        using var source = cube?.Frame ?? RenderedContrastChecks.Input(path);
        using var legacy = WorkingImageBoundary.ToDiagnosticSdrSrgb8(source);
        foreach (var recipe in Names)
        {
            var amount = recipe.StartsWith("blur-") ? float.Parse(recipe[5..]) : 0;
            using var result = recipe switch {
                "unsharp-fine" => WorkingSpatial.Unsharp(source, .8f, 1.2f),
                "unsharp-wide" => WorkingSpatial.Unsharp(source, 2.4f, 1),
                _ => WorkingSpatial.RenderedBlur(source, amount) };
            SceneRenderingChecks.SavePreview(result, output, $"{name}-spatial-{recipe}.png");
            if (recipe is not ("blur-35" or "blur-70")) continue;
            var old = ImageProcessor.RenderPreviewSkBitmap(legacy, new ImageAdjustments { Blur = amount })!;
            using var owned = ReferenceEquals(old, legacy) ? old.Copy() : old;
            using var frame = WorkingImageBoundary.FromEncodedSrgb(owned);
            SceneRenderingChecks.SavePreview(frame, output, $"{name}-spatial-{recipe}-legacy.png");
        }
        report.Checks.Add(new ProbeCheck($"Working spatial / {name}", "PASS", "Native linear GUI blur and unsharp primitive previews generated; two old encoded-sRGB blur references. Appearance awaits review."));
    }
    internal static void WriteReview(string output, IReadOnlyList<string> images) =>
        StageReview.Write(output, images, "spatial", "spatial-review.html", "Native linear blur and unsharp primitive",
            "Blur uses the existing amount-to-sigma law and edge-extended three-box Gaussian approximation, now on linear Rec.2020 premultiplied samples. Bright/transparent edges therefore differ from old encoded-sRGB blur. Inspect highlight glow, smooth wide-gamut gradients and dark detail. Unsharp is a new low-level linear primitive with original coverage, not a port of all existing sharpen methods/detail/masking controls. No GPU spatial kernel or editor routing is enabled.",
            Names.Select(n => (n, n is "blur-35" or "blur-70")));
}
