using FerrumPix.Services;

namespace ImagingPrecisionProbe;
internal static class EffectRecipes
{
    internal sealed record Recipe(string Name, Action<ImageAdjustments> Edit);
    internal static readonly Recipe[] All = [
        new("zero", _ => { }),
        new("soft-focus", a => { a.FilterPreset = "weich"; a.FilterStrength = 65; }),
        new("clarity", a => a.Clarity = 35), new("clarity-soft", a => a.Clarity = -35),
        new("structure", a => a.Structure = 35), new("structure-soft", a => a.Structure = -35),
        new("haze", a => a.Haze = 35), new("dehaze", a => a.Haze = -35),
        new("noise-gaussian", a => { a.NoiseReduction = 60; a.NoiseReductionDetail = 35; a.NoiseReductionMethod = NoiseReductionMethod.Gaussian; }),
        new("noise-median", a => { a.NoiseReduction = 60; a.NoiseReductionMethod = NoiseReductionMethod.Median; }),
        new("noise-guided", a => { a.NoiseReduction = 60; a.NoiseReductionDetail = 35; a.NoiseReductionMethod = NoiseReductionMethod.Guided; }),
        new("noise-adaptive", a => { a.NoiseReduction = 60; a.NoiseReductionDetail = 35; a.NoiseReductionMethod = NoiseReductionMethod.AdaptiveGuided; }),
        new("color-noise", a => a.ColorNoiseReduction = 60),
        new("color-coarse", a => { a.FarbrauschGrob = 60; a.ColorNoiseCoarseScale = 70; }),
        new("color-add", a => a.ColorNoiseAdd = 25),
        new("dust", a => a.DustScratches = 60), new("dust-remove", a => a.DustScratches = -60),
        new("glow", a => a.Glow = 30), new("glow-minus", a => a.Glow = -30),
        new("unsharp", a => { a.Sharpness = 75; a.SharpenRadius = 45; a.SharpenDetail = 35; }),
        new("unsharp-masked", a => { a.Sharpness = 75; a.SharpenRadius = 45; a.SharpenDetail = 35; a.SharpenMasking = 80; }),
        new("high-pass", a => { a.Sharpness = 75; a.SharpenRadius = 45; a.SharpenMethod = SharpenMethod.HighPass; }),
        new("deconvolution", a => { a.Sharpness = 65; a.SharpenRadius = 40; a.SharpenDetail = 25; a.SharpenMasking = 35; a.SharpenMethod = SharpenMethod.Deconvolution; }),
        new("tilt-shift", a => { a.TiltShiftAmount = 65; a.TiltShiftAngle = 25; a.TiltShiftFocus = 10; a.TiltShiftTransition = 45; }),
        new("vignette-color", a => a.Vignette = 75),
        new("vignette-highlights", a => { a.Vignette = 75; a.VignetteStyle = VignetteStyle.HighlightPriority; }),
        new("vignette-paint", a => { a.Vignette = 75; a.VignetteStyle = VignetteStyle.PaintOverlay; }),
        new("vignette-bright", a => a.Vignette = -75),
        new("grain-mono", a => { a.Grain = 35; a.GrainSize = 35; a.GrainFrequency = 65; }),
        new("grain-color", a => { a.Grain = 35; a.GrainSize = 35; a.GrainFrequency = 65; a.GrainColor = 60; }),
        new("add-noise", a => a.AddNoise = 25), new("minus-noise", a => a.AddNoise = -40),
        new("mixed", a => { a.Clarity = 15; a.Structure = 10; a.NoiseReduction = 30; a.ColorNoiseReduction = 25; a.Sharpness = 55; a.SharpenMasking = 50; a.Vignette = 30; a.Grain = 15; })
    ];
    internal static ImageAdjustments Settings(Recipe recipe) { var a = new ImageAdjustments(); recipe.Edit(a); return a; }
    internal static IEnumerable<(string, ImageAdjustments)> LegacyRecipes() => All.Select(r => (r.Name, Settings(r)));
}
