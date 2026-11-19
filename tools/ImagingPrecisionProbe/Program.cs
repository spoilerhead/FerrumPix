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
                    default: throw new ArgumentException($"Unknown argument: {args[i]}");
                }
            }
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
            Console.Error.WriteLine("Usage: --output <folder> [--cpu-only] [--raw-scene] [--render-scene] [--compare-highlight-repair] [--compare-raw-exposure] [--compare-lens-vignetting] [--compare-lens-tca] [--compare-raw-white-balance] [--compare-camera-calibration] [--compare-profiled-jpeg] [--compare-profiled-tiff] [--compare-rendered-contrast] [--compare-basic-tone] [--image <path>] ...");
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
            BasicToneComparisonRequested = compareBasicTone
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
                    var ran = GpuImageService.TryRun(gr => PrecisionChecks.Measure(gr, "GPU"), ref checks);
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
