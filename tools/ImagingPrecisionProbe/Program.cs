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
                    default: throw new ArgumentException($"Unknown argument: {args[i]}");
                }
            }
            if (rawScene && !images.Any(p => RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException("--raw-scene requires at least one --image RAW source.");
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Usage: --output <folder> [--cpu-only] [--raw-scene] [--render-scene] [--image <path>] ...");
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
            SceneRenderingRequested = renderScene
        };

        try
        {
            PrecisionChecks.Run(report, null, "CPU");
            CpuFallbackChecks.Run(report);
            WorkingFrameChecks.Run(report);
            RawSceneChecks.Run(report);
            SceneRenderingChecks.Run(report);
            BaselineChecks.Run(report, output, images, rawScene, renderScene);
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
