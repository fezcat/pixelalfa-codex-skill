global using System.Drawing;
using System.Text.Json;
using PixelAlfa;

var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var cases = JsonSerializer.Deserialize<Case[]>(Console.In.ReadToEnd(), options)
    ?? throw new InvalidDataException("Missing test input.");
var results = new List<object>();
foreach (var item in cases)
{
    var marks = item.Marks.ToDictionary(
        mark => mark.Y * item.Width + mark.X,
        mark => mark.Mode switch
        {
            "remove" => CleanupMark.Remove,
            "protect" => CleanupMark.Protect,
            _ => throw new InvalidDataException("Unknown mark mode.")
        });
    var automatic = LocalCleanup.Automatic(item.Pixels, item.Width, item.Height);
    var guided = GuidedCleanup.Find(item.Pixels, item.Width, marks, 55, default);
    results.Add(new
    {
        item.Name,
        automatic = automatic.Regions.Where(region => region.Suggested)
            .SelectMany(region => region.Pixels).Order().ToArray(),
        guided = guided.Regions.SelectMany(region => region.Pixels).Order().ToArray(),
        uncheckedEdges = automatic.Regions.Count(region =>
            region.Kind == RegionKind.InteriorEdgeReview && !region.Suggested)
    });
}
Console.WriteLine(JsonSerializer.Serialize(results, options));

sealed record Case(string Name, int Width, int Height, int[] Pixels, Mark[] Marks);
sealed record Mark(int X, int Y, string Mode);
