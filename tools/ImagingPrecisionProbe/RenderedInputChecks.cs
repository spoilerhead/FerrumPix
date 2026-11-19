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
}
