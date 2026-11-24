using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class WorkingPreviewGpuChecks
{
    internal static ProbeCheck Measure(GRContext context)
    {
        try
        {
            using var source = LinearWorkingFrame.Create(73, 49, WorkingImageDomain.RenderedLinear, (y, row) =>
            {
                for (var x = 0; x < 73; x++)
                {
                    var alpha = x < 6 ? 0f : x % 4 == 0 ? .25f : 1f;
                    row[4 * x] = alpha * (x - 32) / 17;
                    row[4 * x + 1] = alpha * (y + 1) / 27;
                    row[4 * x + 2] = alpha * MathF.Pow(2, -22 + x % 10);
                    row[4 * x + 3] = alpha;
                }
            });
            using var image = source.CreateNativeSnapshot();
            var planes = WorkingEffectPlanes.Load(source);
            foreach (var (width, height) in new[] { (37, 24), (11, 7), (73, 25) })
            {
                using var expected = planes.ResizeTo(width, height).ToFrame();
                using var actual = WorkingPreviewGpu.Reduce(context, image, source.Domain, width, height);
                Compare("area reduction", expected, actual);
            }
            foreach (var model in new[] { "poly3", "poly5", "ptlens" })
            {
                var correction = new LensDataService.Korrektur { HasDistortion = true, DistortionModel = model, Va = -.09, Vb = .025, Vc = .03, DistortionStrength = .75 };
                var norm = LensDataService.NormScaleFor(correction, source.Width, source.Height);
                var zoom = RawDecodeService.DistortionFillScale(correction, source.Width, source.Height, norm);
                using var expected = WorkingGeometry.CorrectLensDistortion(source, correction, default);
                using var actual = WorkingPreviewGpu.Distort(context, image, source.Domain,
                    WorkingPreviewGpu.DistortionCoefficients(correction), norm, zoom);
                Compare(model, expected, actual);
            }
            foreach (var (id, stage) in new[] { ("example.light-falloff", "before-grain"), ("example.curve-toning", "after-grain") })
            {
                var entry = new ExtensionEntry { Id = id };
                if (id == "example.light-falloff") entry.Values["amount"] = -85;
                var recipe = new ImageAdjustments { ExtensionSettings = ExtensionRecipe.Serialize([entry]) };
                var definition = ImageProcessor.WorkingExtensionDefinition(id);
                using var expected = ImageProcessor.ApplyWorkingExtensions(source, recipe, stage, default);
                using var actual = ImageProcessor.RenderWorkingExtensionGpu(context, image, source.Domain,
                    definition.NativeEffect, ImageProcessor.ExtensionInputsFor(definition, entry));
                Compare(id, expected, actual);
            }
            return new ProbeCheck("GPU / native preview stages", "PASS",
                "F16 area reduction, three polynomial lens models and both position/curve extensions match CPU samples with signed/HDR colors, subnormal shadows and partial alpha.");
        }
        catch (Exception ex) { return ProbeCheck.Failure("GPU / native preview stages", ex.ToString()); }
    }

    // Run outside the worker so each stage has to submit its own preview job.
    internal static ProbeCheck MeasureDispatch()
    {
        const string name = "GPU / native preview dispatch";
        try
        {
            using var source = LinearWorkingFrame.Create(32, 24, WorkingImageDomain.RenderedLinear, (_, row) =>
            {
                for (var x = 0; x < 32; x++)
                {
                    row[4 * x] = x / 15f;
                    row[4 * x + 3] = 1;
                }
            });
            var correction = new LensDataService.Korrektur { HasDistortion = true, DistortionModel = "poly3", Va = -.09 };
            var entry = new ExtensionEntry { Id = "example.light-falloff" };
            entry.Values["amount"] = -85;
            var recipe = new ImageAdjustments { ExtensionSettings = ExtensionRecipe.Serialize([entry]) };
            var jobs = GpuImageService.CompletedJobs;

            using var reduced = WorkingPreviewGpu.TryReduce(source, 16, 12);
            using var expectedReduction = WorkingEffectPlanes.Load(source).ResizeTo(16, 12).ToFrame();
            Compare("preview reduction", expectedReduction, reduced);
            using var distorted = WorkingGeometry.CorrectLensDistortion(source, correction, default, allowGpu: true);
            using var expectedDistortion = WorkingGeometry.CorrectLensDistortion(source, correction, default);
            Compare("preview distortion", expectedDistortion, distorted);
            using var extended = ImageProcessor.ApplyWorkingExtensions(source, recipe, "before-grain", default, allowGpu: true);
            using var expectedExtension = ImageProcessor.ApplyWorkingExtensions(source, recipe, "before-grain", default);
            Compare("preview extension", expectedExtension, extended);

            if (GpuImageService.CompletedJobs - jobs != 3)
                return ProbeCheck.Failure(name, "The preview did not dispatch all three GPU stages.");
            return new ProbeCheck(name, "PASS", "The preview entry points dispatched three GPU jobs and matched CPU output.");
        }
        catch (Exception ex) { return ProbeCheck.Failure(name, ex.ToString()); }
    }

    private static void Compare(string stage, LinearWorkingFrame expected, LinearWorkingFrame? actual)
    {
        if (actual is null || actual.Width != expected.Width || actual.Height != expected.Height || actual.Domain != expected.Domain)
            throw new InvalidOperationException(stage + " returned the wrong frame.");
        var a = new float[expected.Width * 4];
        var b = new float[a.Length];
        for (var y = 0; y < expected.Height; y++)
        {
            expected.ReadLinearRow(y, a);
            actual.ReadLinearRow(y, b);
            for (var i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > Math.Max(1.2e-7, Math.Abs(a[i]) * .003))
                    throw new InvalidOperationException($"{stage} differs at row {y}, sample {i}: {a[i]} vs {b[i]}. RGBA {string.Join(",", a[(i / 4 * 4)..(i / 4 * 4 + 4)])} vs {string.Join(",", b[(i / 4 * 4)..(i / 4 * 4 + 4)])}.");
        }
    }
}
