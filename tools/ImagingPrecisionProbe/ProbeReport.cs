using System.Text;

namespace ImagingPrecisionProbe;

internal sealed record ProbeCheck(string Name, string Status, string Details, double Milliseconds = 0, bool Required = true)
{
    public static ProbeCheck Failure(string name, string details) => new(name, "FAIL", details);
    public static ProbeCheck Skip(string name, string details) => new(name, "SKIP", details);
}

internal sealed class ProbeReport
{
    public DateTimeOffset CreatedUtc { get; init; }
    public string Os { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Framework { get; init; } = "";
    public string ApplicationVersion { get; init; } = "";
    public bool CpuOnly { get; init; }
    public bool RawSceneRequested { get; init; }
    public bool SceneRenderingRequested { get; init; }
    public string GpuRenderer { get; set; } = "";
    public long GpuJobs { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public bool Passed { get; set; }
    public bool GpuValidated { get; set; }
    public bool CpuFallbackValidated { get; set; }
    public List<ProbeCheck> Checks { get; } = [];

    public string ToMarkdown()
    {
        var text = new StringBuilder("# Imaging precision baseline and capability probe\n\n");
        text.AppendLine($"UTC: {CreatedUtc:O}  ");
        text.AppendLine($"System: {Os}; {Architecture}; {Framework}  ");
        text.AppendLine($"FerrumPix: {ApplicationVersion}  ");
        text.AppendLine($"GPU: {GpuRenderer}; completed jobs: {GpuJobs}; validated: {GpuValidated}  ");
        text.AppendLine($"CPU fallback (F32 shaders, explicit F16 storage): validated: {CpuFallbackValidated}  ");
        text.AppendLine($"Peak process working set: {PeakWorkingSetBytes / 1_000_000.0:F1} MB\n");
        text.AppendLine("These are diagnostic measurements, not a migrated renderer or proof of HDR display support.");
        text.AppendLine("Skipped GPU execution is not GPU PASS. Unsupported GPU F32 targets and direct Skia CPU F16 comparisons are optional; their failures remain visible. Baseline hashes describe this build; they are not universal golden values.\n");
        text.AppendLine("| Check | Result | Required | Time (ms) | Details |");
        text.AppendLine("|---|---|---|---:|---|");
        foreach (var check in Checks)
            text.AppendLine($"| {Escape(check.Name)} | {check.Status} | {(check.Required ? "yes" : "no")} | {check.Milliseconds:F2} | {Escape(check.Details)} |");
        text.AppendLine($"\nRequired correctness checks: {(Passed ? "PASS" : "FAIL")}. GPU migration gate: {(GpuValidated ? "PASS" : "NOT PASSED")}.");
        text.AppendLine($"Direct/comparison diagnostics with failures: {Checks.Count(c => c.Status == "FAIL" && !c.Required)}. The production renderer is unchanged.");
        return text.ToString();
    }

    private static string Escape(string text) => text.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
}
