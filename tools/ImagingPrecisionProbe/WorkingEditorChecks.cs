using System.Runtime.InteropServices;
using System.Text.Json;
using BitMiracle.LibTiff.Classic;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class WorkingEditorChecks
{
    internal static void Run(ProbeReport report, string output)
    {
        Check(report, "recipe bridge, precision and sole display boundary", () => Bridge(output));
        Check(report, "actual native extension host", Extensions);
        Check(report, "default-off, unsupported recipes and cancellation", () => Contracts(output));
        Check(report, "full-resolution native export and 16-bit samples", () => Export(output));
    }
    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try { report.Checks.Add(new ProbeCheck("Editor integration / " + name, "PASS", run())); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Editor integration / " + name, ex.ToString())); }
    }
    private static float[] Pixels(LinearWorkingFrame frame)
    {
        var all = new float[frame.Width * frame.Height * 4]; var row = new float[frame.Width * 4];
        for (var y = 0; y < frame.Height; y++) { frame.ReadLinearRow(y, row); row.CopyTo(all, y * row.Length); }
        return all;
    }
    private static string Fixture(string output)
    {
        var path = Path.Combine(output, "editor-native-source.png");
        using var source = FullBasicChecks.Synthetic();
        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray()); return path;
    }
    private static string Bridge(string output)
    {
        var path = Fixture(output);
        using var source = FullBasicChecks.Synthetic();
        using var input = WorkingImageBoundary.FromEncodedSrgb(source);
        using var neutral = WorkingEditorPipeline.Render(path, new(), 0, default);
        if (!Pixels(input).SequenceEqual(Pixels(neutral))) throw new InvalidOperationException("Neutral editor bridge changed native samples.");
        var a = new ImageAdjustments { Brightness = 15, Contrast = 20, Highlights = -25, ShadowsLevel = 20, Saturation = -20, Vibrance = 15, Sharpness = 55, NoiseReduction = 25 };
        using var prepared = WorkingEffects.Prepare(input, new(a));
        using var toned = HslPointChecks.Apply(prepared, a);
        using var expected = WorkingEffects.Apply(toned, new(a));
        using var actual = WorkingEditorPipeline.Render(path, a, 0, default);
        if (!Pixels(actual).SequenceEqual(Pixels(expected))) throw new InvalidOperationException("Editor bridge differs from accepted independent stage composition.");
        using var before = WorkingEditorPipeline.Render(path, a, 0, default, before: true);
        if (!Pixels(before).SequenceEqual(Pixels(input))) throw new InvalidOperationException("Native before view inherited editing controls.");
        var legacyKelvin = new ImageAdjustments { WhiteBalanceKelvin = 4500, WhiteBalanceKelvinTint = 10 };
        var currentKelvin = legacyKelvin.Clone();
        currentKelvin.WhiteBalanceModel = ImageProcessor.CurrentWhiteBalanceModel;
        using var olderWb = WorkingEditorPipeline.Render(path, legacyKelvin, 0, default);
        using var newerWb = WorkingEditorPipeline.Render(path, currentKelvin, 0, default);
        if (!Pixels(olderWb).SequenceEqual(Pixels(newerWb)) || Pixels(olderWb).SequenceEqual(Pixels(input)) || legacyKelvin.WhiteBalanceModel != 0)
            throw new InvalidOperationException("Stored Kelvin without a model did not render like current WB without mutating the recipe.");
        var crop = new ImageAdjustments { CropLeftPercent = 10, CropRightPercent = 10, FlipHorizontal = true };
        using var geometry = WorkingGeometry.Apply(input, crop);
        using var cropped = WorkingEditorPipeline.Render(path, crop, 0, default);
        if (!Pixels(geometry).SequenceEqual(Pixels(cropped))) throw new InvalidOperationException("Editor geometry bridge changed samples/order.");
        using var display = WorkingEditorPipeline.RenderDisplay(path, a, 0, default);
        using var sdr = WorkingImageBoundary.ToDiagnosticSdrSrgb8(actual);
        using var rgba = sdr.Copy(SKColorType.Rgba8888);
        var actualBytes = new byte[display.ByteCount]; var expectedBytes = new byte[rgba.ByteCount];
        Marshal.Copy(display.GetPixels(), actualBytes, 0, actualBytes.Length); Marshal.Copy(rgba.GetPixels(), expectedBytes, 0, expectedBytes.Length);
        if (display.ColorType != SKColorType.Rgba8888 || !actualBytes.SequenceEqual(expectedBytes)) throw new InvalidOperationException("Editor display/blit boundary differs.");
        var tiff = Path.Combine(output, "tiff16-fixtures", "precision-wl-NONE.tif");
        using var tiffInput = ProfiledTiffInput.Decode(tiff);
        using var tiffResult = WorkingEditorPipeline.Render(tiff, new(), 0, default);
        if (!Pixels(tiffInput.Frame).SequenceEqual(Pixels(tiffResult))) throw new InvalidOperationException("Native RGB16 source lost fractional/shadow precision.");
        return "Neutral PNG/RGB16 TIFF preserve native samples exactly; a mixed basic/sharpen/denoise recipe equals accepted stage composition; crop/flip, native before view and exact RGBA display bytes pass. No working image enters RGB8 caches.";
    }
    private static string Extensions()
    {
        using var source = LinearWorkingFrame.Create(9, 3, WorkingImageDomain.RenderedLinear, (_, row) =>
        {
            for (var x = 0; x < 9; x++) { var alpha = x % 3 == 0 ? .25f : 1; row[x * 4] = 2 * alpha; row[x * 4 + 1] = -.1f * alpha; row[x * 4 + 2] = .00001f * alpha; row[x * 4 + 3] = alpha; }
        });
        var entry = new ExtensionEntry { Id = "spoilerhead.gradfilter", Values = new() { ["strengthEv"] = 2, ["hardness"] = 100, ["offset"] = -1, ["highlightThreshEv"] = 0 } };
        var recipe = new ImageAdjustments { ExtensionSettings = ExtensionRecipe.Serialize([entry]) };
        using var result = ImageProcessor.ApplyWorkingExtensions(source, recipe, "before-grain", default);
        var input = Pixels(source); var actual = Pixels(result);
        for (var i = 0; i < input.Length; i++)
        {
            var expected = i % 4 == 3 ? input[i] : input[i] / 4;
            if (Math.Abs(expected - actual[i]) > Math.Max(1.2e-7, Math.Abs(expected) * .002)) throw new InvalidOperationException("Native host clamps/converts image or changes alpha.");
        }
        if (!input.SequenceEqual(Pixels(source))) throw new InvalidOperationException("Extension modified source.");
        using var noStage = ImageProcessor.ApplyWorkingExtensions(source, recipe, "after-grain", default);
        if (!input.SequenceEqual(Pixels(noStage))) throw new InvalidOperationException("Extension ran in wrong stage.");
        return "Actual shipped HP gradfilter runs through the native host, retaining negative/HDR/subnormal samples and partial alpha with exact exposure scaling; stage filtering and source ownership pass. Installed legacy extensions are not rewritten.";
    }
    private static string Contracts(string output)
    {
        if (new AppSettings().NativePipelinePreviewEnabled || JsonSerializer.Deserialize<AppSettings>("{}")!.NativePipelinePreviewEnabled) throw new InvalidOperationException("New/old settings enable experimental pipeline.");
        foreach (var recipe in new ImageAdjustments[] { new() { LutPath = "look.cube" }, new() { FilterPreset = "Sepia" }, new() { SelectionScopeEnabled = true }, new() { RawSensorEdgeCrop = true }, new() { PixelLayerHidden = true } })
        {
            try { WorkingEditorPipeline.Validate(recipe); throw new Exception("Unsupported edit silently accepted."); } catch (NotSupportedException) { }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { using var result = WorkingEditorPipeline.Render(Fixture(output), new(), 0, cancelled.Token); throw new Exception("Cancelled request rendered."); } catch (OperationCanceledException) { }
        return "Old/new settings default off; unsupported features fail explicitly, and pre-cancelled requests return no image. Normal editor branch and installed files remain untouched when off.";
    }

    private static string Export(string output)
    {
        var source = Fixture(output);
        var recipe = new ImageAdjustments { Brightness = 17, CropLeftPercent = 12, FlipHorizontal = true };
        using var frame = WorkingEditorPipeline.Render(source, recipe, 0, default);
        using var expected = WorkingImageBoundary.ToExportSdrSrgb16(frame);
        var expectedRow = new byte[frame.Width * 8];
        Marshal.Copy(expected.GetPixels(), expectedRow, 0, expectedRow.Length);
        var tiffPath = Path.Combine(output, "editor-native-export16.tif");
        var pngPath = Path.Combine(output, "editor-native-export16.png");
        var jpgPath = Path.Combine(output, "editor-native-export8.jpg");
        foreach (var (path, depth) in new[] { (tiffPath, 16), (pngPath, 16), (jpgPath, 8) })
        {
            var outcome = new ImageProcessor.SaveOutcome();
            if (!ImageProcessor.SaveImage(source, path, recipe, 95, preserveMetadata: false, bitDepth: depth, outcome: outcome, nativePipeline: true)
                || outcome.WrittenBitDepth != depth) throw new InvalidOperationException($"Native export failed for {path}.");
        }
        using (var tiff = Tiff.Open(tiffPath, "r") ?? throw new InvalidOperationException("Could not read exported TIFF."))
        {
            var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            var bits = tiff.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
            var channels = tiff.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt();
            if (width != frame.Width || height != frame.Height || bits != 16 || channels is not (3 or 4))
                throw new InvalidOperationException("TIFF geometry or precision differs from the native render.");
            var row = new byte[tiff.ScanlineSize()];
            if (!tiff.ReadScanline(row, 0)) throw new InvalidOperationException("Could not read exported TIFF samples.");
            for (var x = 0; x < width; x++)
                if (!row.AsSpan(x * channels * 2, channels * 2).SequenceEqual(expectedRow.AsSpan(x * 8, channels * 2)))
                    throw new InvalidOperationException("TIFF samples were altered after native quantization.");
            if (!Enumerable.Range(0, row.Length / 2).Any(i => BitConverter.ToUInt16(row, i * 2) % 257 != 0))
                throw new InvalidOperationException("TIFF has only expanded 8-bit values.");
        }
        var png = File.ReadAllBytes(pngPath);
        if (png[24] != 16) throw new InvalidOperationException("PNG IHDR is not 16-bit.");
        using (var jpeg = SKBitmap.Decode(jpgPath))
            if (jpeg is null || jpeg.Width != frame.Width || jpeg.Height != frame.Height)
                throw new InvalidOperationException("JPEG dimensions differ from the native render.");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        if (ImageProcessor.SaveImage(source, pngPath, recipe, 95, preserveMetadata: false, cancel: cancel.Token, bitDepth: 16, nativePipeline: true)
            || !png.SequenceEqual(File.ReadAllBytes(pngPath)))
            throw new InvalidOperationException("Cancelled export replaced an existing file.");
        return "JPEG, PNG16 and TIFF16 go through the normal SaveImage entry point. TIFF scanline samples match the final native frame and contain values between 8-bit codes. Crop dimensions and cancellation preserving the existing destination pass.";
    }

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var recipes = new (string Name, ImageAdjustments Settings)[] {
            ("zero", new()),
            ("tone-detail", new() { Highlights = -35, ShadowsLevel = 25, Whites = 10, Blacks = -10, Saturation = -10, Vibrance = 15, Contrast = 15, Sharpness = 55, SharpenMasking = 50, NoiseReduction = 30 }),
            ("minus2ev", new() { Exposure = (float)(-2 * ImageAdjustments.ExposurePointsPerStop) }),
            ("wb", new() { WhiteBalanceKelvin = 4500, WhiteBalanceKelvinTint = 10 }),
            ("extensions", new() { ExtensionSettings = ExtensionRecipe.Serialize([
                new() { Id = "spoilerhead.silicon-bonk", Values = new() { ["contrast"] = 25, ["luminance"] = 35 } },
                new() { Id = "spoilerhead.vignette", Values = new() { ["strengthEv"] = 2 } },
                new() { Id = "spoilerhead.fat-toni", Values = new() { ["highlightSaturation"] = 10, ["shadowHue"] = 220, ["shadowSaturation"] = 15 } }]) }) };
        foreach (var (label, a) in recipes)
        {
            a.LensDistortion = true;
            using var result = WorkingEditorPipeline.RenderDisplay(path, a, 1536, default);
            using var image = SKImage.FromBitmap(result); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(output, $"{name}-editor-{label}.png"), data.ToArray());
            report.Checks.Add(new ProbeCheck($"Editor bridge photo / {name}/{label}", "PASS", $"{result.Width}x{result.Height} native editor-entry recipe with one final SDR conversion with lens distortion enabled where available."));
        }
        using var before = WorkingEditorPipeline.RenderDisplay(path, recipes[1].Settings, 1536, default, before: true);
        using var beforeImage = SKImage.FromBitmap(before); using var encoded = beforeImage.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(output, $"{name}-editor-before.png"), encoded.ToArray());
    }
    internal static void WriteReview(string output, IReadOnlyList<string> images) => StageReview.Write(output, images, "editor", "editor-review.html",
        "Native editor bridge", "The same native preview entry point wired to the editor. Source geometry is applied before preview reduction; global tools, WB, -2 EV and native extensions are shown. RAW lens distortion is enabled where a correction profile is available. Native before is unedited source development. The viewing images use SDR sRGB.",
        new[] { "zero", "tone-detail", "minus2ev", "wb", "extensions", "before" }.Select(n => (n, false)));
}
