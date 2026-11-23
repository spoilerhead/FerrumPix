using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

internal static class Program
{
    // A separate process makes a driver failure and all native allocations independent of the editor.
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--capture-lights-legacy-fixture")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(LightingLegacy.Hashes(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--capture-effects-legacy-fixture")
        {
            StageLegacyFixture.Capture(args[1], EffectRecipes.LegacyRecipes());
            return 0;
        }
        if (args.Length == 2 && args[0] == "--capture-spatial-legacy-fixture")
        {
            SpatialLegacyFixture.Capture(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--capture-hsl-point-fixture")
        {
            StageLegacyFixture.Capture(args[1], HslPointChecks.LegacyRecipes());
            return 0;
        }
        if (args.Length == 2 && args[0] == "--capture-rendered-input-fixture")
        {
            StageLegacyFixture.Capture(args[1], RenderedInputChecks.LegacyRecipes());
            return 0;
        }
        if (args.Length == 2 && args[0] == "--capture-late-color-fixture")
        {
            LateColorChecks.CaptureLegacy(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--capture-curves-fixture")
        {
            RenderedCurveChecks.CaptureLegacy(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--capture-full-basic-fixture")
        {
            FullBasicChecks.CaptureLegacy(args[1]);
            return 0;
        }
        var output = Path.GetFullPath("imaging-probe-results");
        var cpuOnly = false;
        var rawScene = false;
        var renderScene = false;
        var compareRepair = false;
        var compareExposure = false;
        var compareLens = false;
        var compareTca = false;
        var compareWhiteBalance = false;
        var compareCalibration = false;
        var compareProfiledJpeg = false;
        var compareProfiledTiff = false;
        var compareContrast = false;
        var compareBasicTone = false;
        var compareFullBasic = false;
        var compareCurves = false;
        var compareLateColor = false;
        var compareHslPoint = false;
        var compareRenderedInput = false;
        var compareGeometry = false;
        var compareComposite = false;
        var compareSpatial = false;
        var compareEditor = false;
        var compareEffects = false;
        var effectsFullSmoke = false;
        var effectsMaximum = 1024;
        var images = new List<string>();
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--output": output = Path.GetFullPath(args[++i]); break;
                    case "--image": images.Add(Path.GetFullPath(args[++i])); break;
                    case "--cpu-only": cpuOnly = true; break;
                    case "--raw-scene": rawScene = true; break;
                    case "--render-scene": rawScene = true; renderScene = true; break;
                    case "--compare-highlight-repair": rawScene = true; renderScene = true; compareRepair = true; break;
                    case "--compare-raw-exposure": rawScene = true; renderScene = true; compareExposure = true; break;
                    case "--compare-lens-vignetting": rawScene = true; renderScene = true; compareLens = true; break;
                    case "--compare-lens-tca": rawScene = true; renderScene = true; compareTca = true; break;
                    case "--compare-raw-white-balance": rawScene = true; renderScene = true; compareWhiteBalance = true; break;
                    case "--compare-camera-calibration": rawScene = true; renderScene = true; compareCalibration = true; break;
                    case "--compare-profiled-jpeg": compareProfiledJpeg = true; break;
                    case "--compare-profiled-tiff": compareProfiledTiff = true; break;
                    case "--compare-rendered-contrast": compareContrast = true; break;
                    case "--compare-basic-tone": compareBasicTone = true; break;
                    case "--compare-full-basic": compareFullBasic = true; break;
                    case "--compare-curves": compareCurves = true; break;
                    case "--compare-late-color": compareLateColor = true; break;
                    case "--compare-hsl-point": compareHslPoint = true; break;
                    case "--compare-rendered-input": compareRenderedInput = true; break;
                    case "--compare-geometry": compareGeometry = true; break;
                    case "--compare-composite": compareComposite = true; break;
                    case "--effects-full-smoke": effectsFullSmoke = true; break;
                    case "--compare-editor": compareEditor = true; break;
                    case "--compare-effects": compareEffects = true; break;
                    case "--effects-max-dimension": effectsMaximum = int.Parse(args[++i]); break;
                    case "--compare-spatial": compareSpatial = true; break;
                    default: throw new ArgumentException($"Unknown argument: {args[i]}");
                }
            }
            if (effectsFullSmoke && images.Count != 1) throw new ArgumentException("--effects-full-smoke requires exactly one input.");
            if (compareEffects && (rawScene || compareSpatial || compareProfiledJpeg || compareProfiledTiff || compareContrast || compareBasicTone || compareFullBasic || compareCurves || compareLateColor || compareHslPoint || compareRenderedInput || compareGeometry || compareComposite || images.Count == 0 || effectsMaximum < 0))
                throw new ArgumentException("--compare-effects requires inputs, nonnegative --effects-max-dimension (0 means full resolution), and runs separately.");
            if (compareSpatial && (rawScene || compareProfiledJpeg || compareProfiledTiff || compareContrast || compareBasicTone || compareFullBasic || compareCurves || compareLateColor || compareHslPoint || compareRenderedInput || compareGeometry || compareComposite || images.Count == 0))
                throw new ArgumentException("--compare-spatial requires inputs and runs separately from other comparisons.");
            if (compareComposite && (rawScene || compareProfiledJpeg || compareProfiledTiff || compareContrast || compareBasicTone || compareFullBasic || compareCurves || compareLateColor || compareHslPoint || compareRenderedInput || compareGeometry || images.Count == 0))
                throw new ArgumentException("--compare-composite requires inputs and runs separately from other comparisons.");
            if (compareGeometry && (rawScene || compareProfiledJpeg || compareProfiledTiff || compareContrast || compareBasicTone || compareFullBasic || compareCurves || compareLateColor || compareHslPoint || compareRenderedInput || images.Count == 0))
                throw new ArgumentException("--compare-geometry requires inputs and runs separately from other comparisons.");
            if (compareRenderedInput && (rawScene || compareProfiledJpeg || compareProfiledTiff || compareContrast || compareBasicTone || compareFullBasic || compareCurves || compareLateColor || compareHslPoint || images.Count == 0))
                throw new ArgumentException("--compare-rendered-input requires non-RAW inputs and runs separately from other comparisons.");
            if (compareHslPoint && (rawScene || compareProfiledJpeg || compareProfiledTiff || compareContrast || compareBasicTone || compareFullBasic || compareCurves || compareLateColor || images.Count == 0))
                throw new ArgumentException("--compare-hsl-point requires inputs and runs separately from other comparisons.");
            if (compareLateColor && (rawScene || compareProfiledJpeg || compareProfiledTiff || compareContrast || compareBasicTone || compareFullBasic || compareCurves || images.Count == 0))
                throw new ArgumentException("--compare-late-color requires inputs and runs separately from other comparisons.");
            if (compareCurves && (rawScene || compareProfiledJpeg || compareProfiledTiff || compareContrast || compareBasicTone || compareFullBasic || images.Count == 0))
                throw new ArgumentException("--compare-curves requires inputs and runs separately from other comparisons.");
            if (compareFullBasic && (rawScene || compareProfiledJpeg || compareProfiledTiff || compareContrast || compareBasicTone || images.Count == 0))
                throw new ArgumentException("--compare-full-basic requires inputs and runs separately from other comparisons.");
            if (compareBasicTone && (rawScene || compareProfiledJpeg || compareProfiledTiff || compareContrast || images.Count == 0))
                throw new ArgumentException("--compare-basic-tone requires inputs and runs separately from other comparisons.");
            if (compareContrast && (rawScene || compareProfiledJpeg || compareProfiledTiff || images.Count == 0))
                throw new ArgumentException("--compare-rendered-contrast requires inputs and runs separately from other comparisons.");
            if (rawScene && !images.Any(p => RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException("--raw-scene requires at least one --image RAW source.");
            if (compareProfiledTiff && (rawScene || compareProfiledJpeg || images.Count == 0))
                throw new ArgumentException("--compare-profiled-tiff requires TIFF inputs and cannot be combined with RAW/JPEG comparisons.");
            if (compareProfiledJpeg && (rawScene || images.Count == 0))
                throw new ArgumentException("--compare-profiled-jpeg requires image inputs and cannot be combined with RAW scene comparisons.");
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Usage: --output <folder> [--cpu-only] [--raw-scene] [--render-scene] [--compare-highlight-repair] [--compare-raw-exposure] [--compare-lens-vignetting] [--compare-lens-tca] [--compare-raw-white-balance] [--compare-camera-calibration] [--compare-profiled-jpeg] [--compare-profiled-tiff] [--compare-rendered-contrast] [--compare-basic-tone] [--compare-full-basic] [--compare-curves] [--compare-late-color] [--compare-hsl-point] [--compare-rendered-input] [--compare-effects] [--effects-max-dimension <pixels; 0=full>] [--effects-full-smoke] [--image <path>] ...");
            return 2;
        }

        Directory.CreateDirectory(output);
        var report = new ProbeReport
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            ApplicationVersion = typeof(ImageProcessor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
            CpuOnly = cpuOnly,
            RawSceneRequested = rawScene,
            SceneRenderingRequested = renderScene,
            HighlightRepairComparisonRequested = compareRepair,
            RawExposureComparisonRequested = compareExposure,
            LensVignettingComparisonRequested = compareLens,
            LensTcaComparisonRequested = compareTca,
            RawWhiteBalanceComparisonRequested = compareWhiteBalance,
            CalibrationComparisonRequested = compareCalibration,
            ProfiledJpegComparisonRequested = compareProfiledJpeg,
            ProfiledTiffComparisonRequested = compareProfiledTiff,
            RenderedContrastComparisonRequested = compareContrast,
            BasicToneComparisonRequested = compareBasicTone,
            FullBasicComparisonRequested = compareFullBasic,
            CurvesComparisonRequested = compareCurves,
            LateColorComparisonRequested = compareLateColor,
            HslPointComparisonRequested = compareHslPoint,
            RenderedInputComparisonRequested = compareRenderedInput,
            GeometryComparisonRequested = compareGeometry,
            CompositeComparisonRequested = compareComposite,
            SpatialComparisonRequested = compareSpatial,
            EffectsComparisonRequested = compareEffects
        };

        try
        {
            PrecisionChecks.Run(report, null, "CPU");
            CpuFallbackChecks.Run(report);
            WorkingFrameChecks.Run(report);
            RawSceneChecks.Run(report);
            SceneRenderingChecks.Run(report);
            HighlightRepairChecks.Run(report);
            RawExposureChecks.Run(report);
            LensVignetteChecks.Run(report);
            LensTcaChecks.Run(report);
            RawWhiteBalanceChecks.Run(report);
            CalibrationChecks.Run(report);
            ProfiledJpegChecks.Run(report);
            ProfiledTiffChecks.Run(report, output);
            RenderedContrastChecks.Run(report);
            BasicToneChecks.Run(report);
            FullBasicChecks.Run(report);
            RenderedCurveChecks.Run(report);
            LateColorChecks.Run(report);
            HslPointChecks.Run(report);
            RenderedInputChecks.Run(report);
            FilmMeasurementChecks.Run(report);
            GeometryChecks.Run(report);
            CompositeChecks.Run(report);
            SpatialChecks.Run(report);
            EffectChecks.Run(report);
            ExtraEffectChecks.Run(report);
            ExtensionLinearChecks.Run(report);
            WorkingEditorChecks.Run(report, output);
            BaselineChecks.Run(report, output, images, rawScene, renderScene, compareRepair, compareExposure, compareLens, compareTca, compareWhiteBalance, compareCalibration);
            if (compareRepair) HighlightRepairChecks.WriteReview(output, images);
            if (compareExposure) RawExposureChecks.WriteReview(output, images);
            if (compareLens) LensVignetteChecks.WriteReview(output, images);
            if (compareTca) LensTcaChecks.WriteReview(output, images);
            if (compareWhiteBalance) RawWhiteBalanceChecks.WriteReview(output, images);
            if (compareCalibration) CalibrationChecks.WriteReview(output, images);
            if (compareProfiledJpeg)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { ProfiledJpegChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Profiled JPEG / image-{i + 1}", ex.ToString())); }
                }
                ProfiledJpegChecks.WriteReview(output, images);
            }
            if (compareProfiledTiff)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { ProfiledTiffChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Profiled TIFF / image-{i + 1}", ex.ToString())); }
                }
                ProfiledTiffChecks.WriteReview(output, images);
            }
            if (compareContrast)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { RenderedContrastChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Rendered contrast / image-{i + 1}", ex.ToString())); }
                }
                RenderedContrastChecks.WriteReview(output, images);
            }
            if (compareBasicTone)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { BasicToneChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Basic tone / image-{i + 1}", ex.ToString())); }
                }
                BasicToneChecks.WriteReview(output, images);
            }
            if (compareFullBasic)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { FullBasicChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Full basic / image-{i + 1}", ex.ToString())); }
                }
                FullBasicChecks.WriteReview(output, images);
            }
            if (compareCurves)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { RenderedCurveChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Rendered curves / image-{i + 1}", ex.ToString())); }
                }
                RenderedCurveChecks.WriteReview(output, images);
            }
            if (compareLateColor)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { LateColorChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Late color / image-{i + 1}", ex.ToString())); }
                }
                LateColorChecks.WriteReview(output, images);
            }
            if (compareHslPoint)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { HslPointChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"HSL/point / image-{i + 1}", ex.ToString())); }
                }
                HslPointChecks.WriteReview(output, images);
            }
            if (compareRenderedInput)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { RenderedInputChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Rendered input / image-{i + 1}", ex.ToString())); }
                }
                RenderedInputChecks.WriteReview(output, images);
            }
            if (compareGeometry)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { GeometryChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Working geometry / image-{i + 1}", ex.ToString())); }
                }
                GeometryChecks.WriteReview(output, images);
            }
            if (compareComposite)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { CompositeChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Working composite / image-{i + 1}", ex.ToString())); }
                }
                CompositeChecks.WriteReview(output, images);
            }
            if (compareSpatial)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { SpatialChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Working spatial / image-{i + 1}", ex.ToString())); }
                }
                SpatialChecks.WriteReview(output, images);
            }
            if (compareEditor)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { WorkingEditorChecks.Capture(report, output, images[i], $"image-{i + 1}"); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Editor bridge photo / image-{i + 1}", ex.ToString())); }
                }
                WorkingEditorChecks.WriteReview(output, images);
            }
            if (effectsFullSmoke)
            {
                try { EffectReview.FullResolutionSmoke(report, output, images[0]); }
                catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Effects full-resolution mixed chain", ex.ToString())); }
            }
            if (compareEffects)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    try { EffectReview.Capture(report, output, images[i], $"image-{i + 1}", effectsMaximum); }
                    catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Effect photo / image-{i + 1}", ex.ToString())); }
                }
                EffectReview.Index(output, images);
            }
            if (!cpuOnly)
            {
                // The real GPU service obtains its context from Avalonia. A minimal application
                // initializes that platform without opening the editor, catalog, or a photo window.
                GpuImageService.ApplyRenderOffloadEnvironment();
                AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
                GpuImageService.Enabled = true;
                report.GpuRenderer = GpuImageService.Renderer;
                if (GpuImageService.IsAvailable)
                {
                    report.GpuRenderer = GpuImageService.Renderer;
                    var jobsBefore = GpuImageService.CompletedJobs;
                    List<ProbeCheck>? checks = null;
                    var ran = GpuImageService.TryRun(gr =>
                    {
                        var measured = PrecisionChecks.Measure(gr, "GPU");
                        measured.Add(CompositeChecks.MeasureGpu(gr));
                        return measured;
                    }, ref checks);
                    report.GpuJobs = GpuImageService.CompletedJobs - jobsBefore;
                    if (!ran || checks is null || report.GpuJobs < 1)
                        report.Checks.Add(ProbeCheck.Failure("GPU execution", GpuImageService.FailureReason));
                    else
                        report.Checks.AddRange(checks);
                }
                else
                    report.Checks.Add(ProbeCheck.Skip("GPU execution", GpuImageService.FailureReason));
            }
        }
        catch (Exception ex)
        {
            report.Checks.Add(ProbeCheck.Failure("Probe execution", ex.ToString()));
        }
        finally
        {
            // No image-processing preference is persisted by this tool.
            GpuImageService.Enabled = false;
            report.PeakWorkingSetBytes = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
            report.CpuFallbackValidated = report.Checks.Any(c => c.Name.StartsWith("CPU fallback /")) &&
                report.Checks.Where(c => c.Name.StartsWith("CPU fallback /")).All(c => c.Status == "PASS");
            report.Passed = report.Checks.All(c => !c.Required || c.Status != "FAIL");
            report.GpuValidated = report.GpuJobs > 0 && report.Checks.Any(c => c.Name.StartsWith("GPU /")) &&
                                  !report.Checks.Any(c => c.Name.StartsWith("GPU") && c.Status != "PASS" &&
                                      !(c.Status == "SKIP" && c.Name.Contains("RgbaF32")));
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "report.md"), report.ToMarkdown());
        }

        Console.WriteLine(report.ToMarkdown());
        Console.WriteLine($"Results: {output}");
        return report.Passed && (cpuOnly || report.GpuValidated) ? 0 : 1;
    }
}
