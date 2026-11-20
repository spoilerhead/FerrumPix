using System.Runtime.InteropServices;
using System.Text.Json;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;
internal static class EffectChecks
{
    internal static void Run(ProbeReport report)
    {
        Check(report, "frozen legacy recipes", () =>
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return "SKIP Windows x64 legacy fixture.";
            var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/effects-legacy.json")))!;
            foreach (var (name, hash) in StageLegacyFixture.Hashes(EffectRecipes.LegacyRecipes()))
                if (expected[name] != hash) throw new InvalidOperationException("Legacy effect changed: " + name);
            return $"{expected.Count} original legacy effect/sharpen/denoise recipes unchanged.";
        });
        Check(report, "all controls, native range, alpha, gray and ownership", AllTools);
        Check(report, "independent high-pass and unsharp references", SharpenReference);
        Check(report, "independent Richardson-Lucy reference", DeconvolutionReference);
        Check(report, "perceptual noise amplitude and native luminance", NoiseAmplitude);
        Check(report, "floating automatic noise measurement", NoiseMeasurement);
        Check(report, "denoise reduction, edge retention and chroma luminance", Denoise);
        Check(report, "settings, identity and extension order", Contracts);
    }
    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try { var s = run(); report.Checks.Add(new ProbeCheck("Working effects / " + name, s.StartsWith("SKIP ") ? "SKIP" : "PASS", s, Required: !s.StartsWith("SKIP "))); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Working effects / " + name, ex.ToString())); }
    }
    private static float[] Pixels(LinearWorkingFrame f)
    {
        var result = new float[f.Width * f.Height * 4]; var row = new float[f.Width * 4];
        for (var y = 0; y < f.Height; y++) { f.ReadLinearRow(y, row); row.CopyTo(result, y * row.Length); }
        return result;
    }
    internal static LinearWorkingFrame Apply(LinearWorkingFrame source, ImageAdjustments recipe)
    {
        var settings = new WorkingEffectSettings(recipe);
        using var prepared = WorkingEffects.Prepare(source, settings);
        return WorkingEffects.Apply(prepared, settings);
    }
    private static LinearWorkingFrame Synthetic(bool gray = false) => LinearWorkingFrame.Create(96, 80, WorkingImageDomain.RenderedLinear, (y, row) =>
    {
        for (var x = 0; x < 96; x++)
        {
            var at = x * 4; var alpha = x < 2 || y < 2 ? 0 : (x + y) % 7 == 0 ? .5f : 1f;
            var value = x > 65 ? 3f : .02f + x / 96f;
            row[at] = (gray ? value : x < 25 ? -.1f : value) * alpha;
            row[at + 1] = (gray ? value : value * .8f) * alpha;
            row[at + 2] = (gray ? value : value * .5f + y / 80f * .1f) * alpha;
            row[at + 3] = alpha;
        }
    });
    private static string AllTools()
    {
        using var source = Synthetic(); var input = Pixels(source);
        using var gray = Synthetic(true);
        foreach (var recipe in EffectRecipes.All)
        {
            var a = EffectRecipes.Settings(recipe);
            using var result = Apply(source, a); var pixels = Pixels(result);
            if (result.Width != source.Width || result.Height != source.Height || result.Domain != source.Domain || pixels.Any(v => !float.IsFinite(v)))
                throw new InvalidOperationException("Invalid native effect output: " + recipe.Name);
            var coverageChanges = recipe.Name is "soft-focus" or "tilt-shift" or "dust";
            for (var i = 3; i < pixels.Length; i += 4)
                if (pixels[i] < 0 || pixels[i] > 1 || (!coverageChanges && pixels[i] != input[i])) throw new InvalidOperationException("Coverage changed: " + recipe.Name);
            if (pixels.Where((_, i) => i % 4 != 3).Max() <= 1) throw new InvalidOperationException("HDR range lost: " + recipe.Name);
            if (recipe.Name is not ("haze" or "color-add" or "grain-color" or "add-noise" or "mixed"))
            {
                using var neutral = Apply(gray, a); var values = Pixels(neutral);
                for (var i = 0; i < values.Length; i += 4)
                {
                    var spread = Math.Max(values[i], Math.Max(values[i + 1], values[i + 2])) - Math.Min(values[i], Math.Min(values[i + 1], values[i + 2]));
                    if (spread > Math.Max(.000002, Math.Abs(values[i]) * .0015)) throw new InvalidOperationException("Unintended gray cast: " + recipe.Name);
                }
            }
        }
        if (!input.SequenceEqual(Pixels(source))) throw new InvalidOperationException("Effects mutated source.");
        return $"All {EffectRecipes.All.Length} tool recipes retain native dimensions/domain/HDR and valid alpha; source ownership and expected gray neutrality pass. Intentional warm haze/color-noise cases excluded from neutrality.";
    }
    private static string SharpenReference()
    {
        float[] gray = [.1f, .1f, .15f, .25f, .65f, .8f, 2, 2, 2];
        using var source = LinearWorkingFrame.Create(9, 1, WorkingImageDomain.RenderedLinear, (_, row) =>
        {
            for (var x = 0; x < 9; x++) { row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = gray[x]; row[x * 4 + 3] = 1; }
        });
        var input = Pixels(source);
        double[] Values(float[] kernel)
        {
            var sum = kernel.Sum(v => (double)v); var output = new double[9]; var radius = kernel.Length / 2;
            for (var x = 0; x < 9; x++) for (var k = -radius; k <= radius; k++) output[x] += kernel[k + radius] * input[Math.Clamp(x + k, 0, 8) * 4] / sum;
            return output;
        }
        foreach (var method in new[] { SharpenMethod.HighPass, SharpenMethod.UnsharpMask })
        {
            var a = new ImageAdjustments { Sharpness = 75, SharpenRadius = 45, SharpenDetail = 35, SharpenMethod = method };
            using var result = Apply(source, a); var actual = Pixels(result);
            double[] smooth, inner; double gain, halo = .11, dark = .4;
            if (method == SharpenMethod.HighPass)
            {
                var p = ImageProcessor.HighPassParameters(.75f, .45f); var half = ImageProcessor.HighPassWeights(p.Sigma);
                smooth = Values(Enumerable.Range(0, half.Length * 2 - 1).Select(i => half[Math.Abs(i - half.Length + 1)]).ToArray());
                inner = Enumerable.Range(0, 9).Select(i => (double)input[i * 4]).ToArray(); gain = p.Gain;
            }
            else
            {
                var p = ImageProcessor.SharpenParameters(.75f, .45f, .35f); var k = ImageProcessor.SharpenInnerWeights(p.InnerSigma);
                inner = Values([k.W2, k.W1, k.W0, k.W1, k.W2]);
                var rgba = SpatialChecks.ConvolutionReference(input, 9, 1, p.Sigma);
                smooth = Enumerable.Range(0, 9).Select(i => rgba[i * 4]).ToArray(); gain = p.Gain; halo = p.Halo; dark = p.Dark;
            }
            for (var x = 0; x < 9; x++)
            {
                var d = inner[x] - smooth[x]; var shaped = method == SharpenMethod.HighPass ? d : d / (1 + Math.Pow(Math.Abs(d) / halo, 3)) * (d < 0 ? dark : 1);
                var expected = input[x * 4] + shaped * gain;
                if (Math.Abs(expected - actual[x * 4]) > Math.Max(.000002, Math.Abs(expected) * .0015))
                    throw new InvalidOperationException($"Independent {method} reference differs at {x}: {actual[x * 4]} vs {expected}.");
            }
        }
        return "Independent Double direct convolution and bandpass shaping match native high-pass and calibrated unsharp, including HDR. Only parameter mappings/weights are shared; output kernels are independent.";
    }
    private static string DeconvolutionReference()
    {
        // An independent, opaque 1-D oracle isolates iterative arithmetic from
        // alpha normalization and includes a signed pedestal plus HDR peaks.
        using var source = LinearWorkingFrame.Create(9, 1, WorkingImageDomain.RenderedLinear, (_, row) =>
        {
            float[] v = [-.02f, .03f, .15f, .9f, 2f, .5f, .12f, .04f, -.01f];
            for (var x = 0; x < 9; x++) { row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = v[x]; row[x * 4 + 3] = 1; }
        });
        var input = Pixels(source); var luma = Enumerable.Range(0, 9).Select(x => input[x * 4] * 255.0).ToArray();
        const double sigma = .9 + .4 * 1.1; var radius = (int)Math.Ceiling(3 * sigma);
        var kernel = Enumerable.Range(-radius, 2 * radius + 1).Select(k => Math.Exp(-k * k / (2 * sigma * sigma))).ToArray();
        var sum = kernel.Sum(); kernel = kernel.Select(w => w / sum).ToArray();
        double[] Convolve(double[] v) => Enumerable.Range(0, 9).Select(x => kernel.Select((w, k) => w * v[Math.Clamp(x + k - radius, 0, 8)]).Sum()).ToArray();
        var offset = Math.Min(0, luma.Min()); var observed = luma.Select(v => v - offset).ToArray(); var estimate = observed.ToArray();
        for (var iteration = 0; iteration < 15; iteration++)
        {
            var blurred = Convolve(estimate); var correction = Convolve(observed.Select((v, i) => v / Math.Max(.5, blurred[i])).ToArray());
            estimate = estimate.Select((v, i) => v * Math.Clamp(correction[i], 1 / 1.25, 1.25)).ToArray();
        }
        using var result = Apply(source, new() { Sharpness = 65, SharpenRadius = 40, SharpenDetail = 25, SharpenMethod = SharpenMethod.Deconvolution });
        var actual = Pixels(result);
        for (var x = 0; x < 9; x++)
        {
            var expected = input[x * 4] + (estimate[x] - observed[x]) * .65 * 1.5 / 255;
            if (Math.Abs(actual[x * 4] - expected) > Math.Max(.000002, Math.Abs(expected) * .0015)) throw new InvalidOperationException($"RL mismatch at {x}: {actual[x * 4]} vs {expected}.");
        }
        return "Independent Double Gaussian convolution and 15 Richardson-Lucy iterations match signed/HDR F16 output within 0.15%; no shared iterative kernel or SDR ceiling.";
    }
    private static string NoiseAmplitude()
    {
        using var source = LinearWorkingFrame.Create(11, 3, WorkingImageDomain.RenderedLinear, (_, row) =>
        {
            for (var x = 0; x < 11; x++) { var value = x == 0 ? -.01f : x == 10 ? 2f : .005f + x * .05f; row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = value; row[x * 4 + 3] = 1; }
        });
        var input = Pixels(source); var random = new Random(11 * 541 ^ 3 * 877);
        using var result = Apply(source, new() { AddNoise = 25 }); var actual = Pixels(result);
        for (var i = 0; i < 33; i++) for (var c = 0; c < 3; c++)
        {
            var delta = (random.NextDouble() * 2 - 1) * .25 * 72 / 255;
            var expected = RenderedContrastChecks.Decode(RenderedContrastChecks.Encode(input[i * 4 + c]) + delta);
            if (Math.Abs(actual[i * 4 + c] - expected) > Math.Max(.0000002, Math.Abs(expected) * .0012)) throw new InvalidOperationException("Perceptual added-noise reference differs.");
        }
        using var color = Apply(source, new() { ColorNoiseAdd = 50 }); var values = Pixels(color);
        for (var i = 0; i < 33; i++)
        {
            var y = .2627 * values[i * 4] + .678 * values[i * 4 + 1] + .0593 * values[i * 4 + 2];
            if (Math.Abs(y - input[i * 4]) > Math.Max(.000002, Math.Abs(input[i * 4]) * .0012)) throw new InvalidOperationException("Added chroma noise moved native Y.");
        }
        return "Independent continuous transfer/seed reference matches noise amplitude on shadow, signed and HDR samples; native primaries and unclipped range retained. Color-only noise preserves native linear Y within half precision.";
    }
    private static string NoiseMeasurement()
    {
        var levels = new List<float>();
        foreach (var amplitude in new[] { .001f, .01f, .04f })
        {
            var random = new Random(470);
            using var source = LinearWorkingFrame.Create(256, 256, WorkingImageDomain.RenderedLinear, (_, row) =>
            {
                for (var x = 0; x < 256; x++) { var v = .3f + amplitude * (float)(random.NextDouble() * 2 - 1); row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = v; row[x * 4 + 3] = 1; }
            });
            var level = ImageProcessor.EstimateWorkingNoiseLevel(source);
            if (!float.IsFinite(level) || level <= 0) throw new InvalidOperationException("Invalid floating noise estimate.");
            levels.Add(level);
            if (!DenoiseModelService.SuggestWorking(source).TooSmall) throw new InvalidOperationException("Small-image suggestion bypassed the 3MP gate.");
        }
        if (!(levels[0] < levels[1] && levels[1] < levels[2])) throw new InvalidOperationException("Noise estimate does not track amplitude.");
        return "Continuous F32 guide detects sub-byte noise and rises monotonically across three amplitudes; model auto-suggestion preserves the existing 3MP minimum.";
    }
    private static double Variance(float[] values, int width, int start, int end, int channel)
    {
        var samples = new List<double>();
        for (var y = 8; y < values.Length / (width * 4) - 8; y++) for (var x = start; x < end; x++) samples.Add(values[(y * width + x) * 4 + channel]);
        var mean = samples.Average(); return samples.Sum(v => (v - mean) * (v - mean)) / samples.Count;
    }
    private static string Denoise()
    {
        var random = new Random(1704);
        using var source = LinearWorkingFrame.Create(128, 128, WorkingImageDomain.RenderedLinear, (_, row) =>
        {
            for (var x = 0; x < 128; x++)
            {
                var value = (x < 64 ? .2f : .8f) + (float)(random.NextDouble() * .04 - .02);
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = value; row[x * 4 + 3] = 1;
            }
        });
        var input = Pixels(source); var originalVariance = Variance(input, 128, 8, 48, 0);
        foreach (var method in Enum.GetValues<NoiseReductionMethod>())
        {
            using var result = Apply(source, new() { NoiseReduction = 75, NoiseReductionDetail = 70, NoiseReductionMethod = method });
            var values = Pixels(result);
            if (Variance(values, 128, 8, 48, 0) >= originalVariance * .95) throw new InvalidOperationException("No measurable denoise: " + method);
            if (method != NoiseReductionMethod.Gaussian && Math.Abs(values[(64 * 128 + 62) * 4] - .2f) > .06) throw new InvalidOperationException("Guided/median edge was lost: " + method);
        }
        using var chroma = LinearWorkingFrame.Create(128, 64, WorkingImageDomain.RenderedLinear, (_, row) =>
        {
            for (var x = 0; x < 128; x++)
            {
                var dr = (float)(random.NextDouble() * .06 - .03); var db = (float)(random.NextDouble() * .06 - .03);
                row[x * 4] = .3f + dr; row[x * 4 + 1] = .3f - (.2627f * dr + .0593f * db) / .678f; row[x * 4 + 2] = .3f + db; row[x * 4 + 3] = 1;
            }
        });
        var before = Pixels(chroma);
        foreach (var recipe in new[] { new ImageAdjustments { ColorNoiseReduction = 80 }, new ImageAdjustments { FarbrauschGrob = 80, ColorNoiseCoarseScale = 75 } })
        {
            using var result = Apply(chroma, recipe); var after = Pixels(result);
            if (Variance(after, 128, 8, 120, 0) >= Variance(before, 128, 8, 120, 0) * .95) throw new InvalidOperationException("No chroma reduction.");
            for (var i = 0; i < before.Length; i += 4)
            {
                var oldY = .2627 * before[i] + .678 * before[i + 1] + .0593 * before[i + 2];
                var newY = .2627 * after[i] + .678 * after[i + 1] + .0593 * after[i + 2];
                if (Math.Abs(newY - oldY) > .0005) throw new InvalidOperationException("Chroma-only filtering shifted luminance.");
            }
        }
        return "All four luminance methods reduce flat-field variance; guided/median preserve a step edge. Fine/root and multiscale color denoise reduce chroma variance while preserving linear Rec.2020 Y within F16 storage tolerance.";
    }
    private static string Contracts()
    {
        using var source = Synthetic(); var input = Pixels(source);
        var a = new ImageAdjustments { Grain = 20 };
        var settings = new WorkingEffectSettings(a); a.Grain = 90;
        if (settings.Snapshot().Grain != 20) throw new InvalidOperationException("Settings alias recipe.");
        var snapshot = settings.Snapshot(); snapshot.Grain = 70;
        if (settings.Snapshot().Grain != 20) throw new InvalidOperationException("Settings expose mutable storage.");
        using var zero = Apply(source, new());
        if (ReferenceEquals(source, zero) || !input.SequenceEqual(Pixels(zero))) throw new InvalidOperationException("Neutral effects changed source.");
        foreach (var bad in new[] { new ImageAdjustments { Clarity = float.NaN }, new() { Grain = 101 }, new() { Sharpness = 151 }, new() { SharpenMethod = (SharpenMethod)99 } })
        {
            try { _ = new WorkingEffectSettings(bad); throw new Exception("Invalid effects accepted."); } catch (ArgumentException) { }
        }
        var trace = new List<string>();
        using var output = WorkingEffects.Apply(source, settings,
            beforeGrain: frame => { trace.Add("before-called"); return frame; },
            afterGrain: frame => { trace.Add("after-called"); return frame; },
            trace: trace.Add);
        if (!trace.SequenceEqual(new[] { "before-grain", "before-called", "grain", "after-grain", "after-called" }))
            throw new InvalidOperationException("Extension/grain ordering changed.");
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        try { using var r = WorkingEffects.Apply(scene, settings); throw new Exception("Scene passed to rendered effects."); } catch (ArgumentException) { }
        using var first = WorkingEffects.Apply(source, settings); using var second = WorkingEffects.Apply(source, settings);
        if (!Pixels(first).SequenceEqual(Pixels(second))) throw new InvalidOperationException("Grain is nondeterministic.");
        return "Neutral identity, snapshot ownership, valid ranges/domain, deterministic procedural fields and real pre-grain/post-grain hook invocation order pass.";
    }
}
