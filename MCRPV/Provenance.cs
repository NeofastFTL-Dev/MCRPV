using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mcrpv;

/// <summary>Eén bewerking uit de patchlaag die dit item raakte, in fold-volgorde.</summary>
public sealed class ProvenanceLine
{
    public string Text { get; init; } = "";

    /// <summary>De bewerking die het recept in de fold uiteindelijk neerzette.</summary>
    public bool Winner { get; init; }
}

/// <summary>
/// Antwoord op "waardoor heeft dit item dit recept?": de laag en bron die wonnen, de
/// patch als bestand:regel, en elke bewerking uit de pack-laag of eigen laag die het
/// item raakte — inclusief de bewerkingen die het daarna weer overruleden.
/// </summary>
public sealed class ProvenanceReport
{
    public string Item { get; init; } = "";
    public string Layer { get; init; } = "";
    public string Source { get; init; } = "";
    public string Details { get; init; } = "";
    public int Ops { get; init; }

    /// <summary>Het recept komt uit de fold, omdat de geladen set zelf geen bron kent.</summary>
    public bool FromFold { get; init; }

    public List<ProvenanceLine> Lines { get; init; } = new();

    public static ProvenanceReport Explain(string dataDirectory, string item,
                                           IReadOnlyDictionary<string, Recipe> recipes)
    {
        recipes.TryGetValue(item, out var recipe);
        bool fromFold = false;

        // recipes.json van een oudere scan kent geen bron; de fold wel. Dan leggen we het
        // uit vanuit de fold, zodat de herkomst ook klopt als de app op recipes.json staat.
        if (recipe == null || (recipe.Source.Length == 0 && recipe.PatchId.Length == 0))
        {
            if (LoadFold(dataDirectory) is { } fold && fold.TryGetValue(item, out var folded))
            {
                recipe = folded;
                fromFold = true;
            }
        }

        var ops = LoadOps(dataDirectory)
            .Where(o => o.Out.Equals(item, StringComparison.OrdinalIgnoreCase) ||
                        o.Item.Equals(item, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(o => (o.Op, o.Where, o.Prio, o.Out, o.Item))
            .OrderBy(o => o.Prio)
            .ThenBy(o => o.Src, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Line)
            .ToList();

        string patch = recipe?.PatchId ?? "";
        bool fromUser = patch.Length > 0 && ops.Any(o => o.Prio >= 1000 && Same(o.Where, patch));

        string layer =
            recipe == null ? "geen recept"
            : fromUser ? "eigen laag"
            : Starts(recipe.Source, "jar-JSON") ? "basis (jar)"
            : Starts(recipe.Source, "runtime") ? "runtime-dump"
            : Starts(recipe.Source, "overlay") ? "overlay"
            : recipe.Source.Length > 0 ? "pack-laag (script of datapack)"
            : "onbekend";

        string details = recipe == null
            ? "Dit item heeft geen eigen recept, dus de planner stopt hier."
            : $"{recipe.Category} · {recipe.Output} per beurt · {recipe.Ingredients.Count} ingrediënt(en) · " +
              $"{recipe.Options.Count} manier(en)" + (patch.Length > 0 ? $" · patch {patch}" : "");

        var lines = ops.Select(op => new ProvenanceLine
        {
            Text = $"prio {op.Prio} · {op.Describe()}" + (patch.Length > 0 && Same(op.Where, patch) ? "   ← dit recept" : ""),
            Winner = patch.Length > 0 && Same(op.Where, patch)
        }).ToList();

        return new ProvenanceReport
        {
            Item = item,
            Layer = layer,
            Source = recipe?.Source ?? "",
            Details = details,
            Ops = ops.Count,
            FromFold = fromFold,
            Lines = lines
        };
    }

    private static bool Starts(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>De fold van het actieve pack, of null als die er nog niet is.</summary>
    private static Dictionary<string, Recipe>? LoadFold(string dataDirectory)
    {
        var pack = PackProfile.Load(Path.Combine(dataDirectory, "pack.json"));
        if (pack == null || pack.Pack.Length == 0) return null;

        try
        {
            string path = Path.Combine(dataDirectory, "folded-" + pack.Pack + ".json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>De bewerkingen van de actieve pack-laag plus de eigen laag (user.jsonl).</summary>
    private static List<RecipeOp> LoadOps(string dataDirectory)
    {
        var ops = new List<RecipeOp>();

        string? file = null;
        var profile = PackProfile.Load(Path.Combine(dataDirectory, "pack.json"));
        if (profile != null && profile.PatchFile.Length > 0)
        {
            string candidate = Path.Combine(dataDirectory, profile.PatchFile);
            if (File.Exists(candidate)) file = candidate;
        }

        // Zonder gegevensmap is er niets te doorzoeken; het venster en --herkomst horen
        // dan een lege lijst te krijgen in plaats van een DirectoryNotFoundException.
        if (file == null && Directory.Exists(dataDirectory))
            file = Directory.EnumerateFiles(dataDirectory, "patches-*.jsonl")
                              .OrderByDescending(File.GetLastWriteTimeUtc)
                              .FirstOrDefault();

        if (file != null) ReadJsonl(file, ops);

        string user = Path.Combine(dataDirectory, "user.jsonl");
        if (File.Exists(user)) ReadJsonl(user, ops);

        return ops;
    }

    private static void ReadJsonl(string path, List<RecipeOp> ops)
    {
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var op = JsonSerializer.Deserialize<RecipeOp>(line);
                if (op != null) ops.Add(op);
            }
        }
        catch
        {
            // Een onleesbare patchlaag mag het venster niet tegenhouden.
        }
    }
}
