using System.Net;
namespace ImagingPrecisionProbe;
internal static class StageReview
{
    internal static void Write(string output, IReadOnlyList<string> images, string prefix, string fileName, string title, string notes, IEnumerable<(string Name, bool Legacy)> recipes)
    {
        var cases = recipes.ToArray();
        var html = new System.Text.StringBuilder($"<!doctype html><html lang='en'><meta charset='utf-8'><title>{WebUtility.HtmlEncode(title)}</title><style>body{{background:#202326;color:#eee;font:16px system-ui;margin:24px}}.grid{{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}}figure{{margin:0}}img{{width:100%;height:400px;object-fit:contain;background:#111}}a{{color:#bce0ff}}</style><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(notes)} <a href='report.md'>Validation report</a></p>");
        for (var i = 0; i < images.Count; i++)
        {
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2><div class='grid'>");
            foreach (var (name, _) in cases) Add(name, false);
            html.Append("</div><h3>Old sRGB8 references</h3><div class='grid'>");
            foreach (var (name, legacy) in cases) if (legacy) Add(name, true);
            html.Append("</div>");
            void Add(string name, bool legacy)
            {
                var file = $"image-{i + 1}-{prefix}-{name}{(legacy ? "-legacy" : "")}.png";
                html.Append($"<figure><figcaption>{name}{(legacy ? " - old editor" : "")}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{name}'></a></figure>");
            }
        }
        File.WriteAllText(Path.Combine(output, fileName), html.Append("</html>").ToString());
    }
}
