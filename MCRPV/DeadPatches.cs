using System.IO;
using System.Linq;
using System.Text;

namespace Mcrpv;

/// <summary>Eén patch die niets raakte, met de oorzaak die we ervoor konden vinden.</summary>
public sealed class DeadPatchEntry
{
    public string Op { get; init; } = "";
    public string Item { get; init; } = "";
    public string Source { get; init; } = "";
    public string Where { get; init; } = "";
    public int Line { get; init; }

    /// <summary>"gat", "nooit" of "naamvorm".</summary>
    public string Cause { get; init; } = "";
}

/// <summary>De dode patches van één script- of datapackbestand.</summary>
public sealed class DeadPatchGroup
{
    public string Source { get; init; } = "";
    public List<DeadPatchEntry> Entries { get; init; } = new();

    public int Total => Entries.Count;
    public int Gap => Entries.Count(e => e.Cause == "gat");
    public int Never => Entries.Count(e => e.Cause == "nooit");
    public int Name => Entries.Count(e => e.Cause == "naamvorm");

    public string Header =>
        $"{Source} — {Total}" + (Gap > 0 || Never > 0 || Name > 0
            ? $" (gat {Gap} · nooit {Never} · naamvorm {Name})"
            : "");
}

/// <summary>
/// De patches die een recept wilden aanpassen dat niet bestaat, uitgesplitst naar oorzaak:
/// <list type="bullet">
/// <item><b>gat</b> — het item komt wel als ingrediënt voor, maar heeft nergens een recept;
/// vermoedelijk ziet de dump of de parser het recept niet.</item>
/// <item><b>nooit</b> — het item bestaat in het geheel niet in de set: nooit toegevoegd,
/// verkeerde pack-modus, of de mod is weg.</item>
/// <item><b>naamvorm</b> — de notatie wijkt af (spatie, #, *, |, punt aan het eind), dus het
/// script en de set gebruiken een andere naam voor hetzelfde ding.</item>
/// </list>
/// Groeperen per bronbestand maakt er een buglijst per script of mod van.
/// </summary>
public sealed class DeadPatchReport
{
    public int Total { get; init; }

    /// <summary>Aantal meldingen vóór ontdubbeling (dezelfde regel kan twee keer gelezen zijn).</summary>
    public int Reports { get; init; }
    public int Gap { get; init; }
    public int Never { get; init; }
    public int Name { get; init; }

    /// <summary>Doel bestaat in de eindset: een latere patch zette het terug. Netto onschuldig.</summary>
    public int Double { get; init; }

    public List<DeadPatchGroup> Groups { get; init; } = new();

    public string Summary => Total == 0
        ? "geen dode patches"
        : $"{Total:N0} dode patch(es)" + (Reports > Total ? $" ({Reports:N0} meldingen)" : "") +
          $" · {Gap:N0} dump/parser-gat · {Never:N0} nooit toegevoegd · " +
          $"{Name:N0} naamvorm · {Double:N0} dubbel";

    public string Text { get; init; } = "";

    public static DeadPatchReport Build(IEnumerable<RecipeOp> deadOps, IReadOnlyDictionary<string, Recipe> recipes)
    {
        var ingredients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipe in recipes.Values)
            foreach (var ingredient in AllIngredients(recipe))
                if (ingredient.Item.Length > 0) ingredients.Add(ingredient.Item);

        var index = new NameIndex(recipes, ingredients);

        int reports = deadOps.Count();

        var entries = deadOps
            .DistinctBy(op => $"{op.Op}|{op.Out}|{op.Item}|{op.Src}|{op.Line}")
            .Select(op => new DeadPatchEntry
            {
                Op = op.Op,
                Item = op.Out.Length > 0 ? op.Out : op.Item,
                Source = op.Src,
                Where = op.Where,
                Line = op.Line,
                Cause = CauseOf(op, recipes, ingredients, index)
            })
            .ToList();

        var groups = entries
            .GroupBy(e => e.Source.Length > 0 ? e.Source : "onbekend")
            .Select(g => new DeadPatchGroup { Source = g.Key, Entries = g.OrderBy(e => e.Line).ToList() })
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var report = new DeadPatchReport
        {
            Total = entries.Count,
            Reports = reports,
            Gap = entries.Count(e => e.Cause == "gat"),
            Never = entries.Count(e => e.Cause == "nooit"),
            Name = entries.Count(e => e.Cause == "naamvorm"),
            Double = entries.Count(e => e.Cause == "dubbel"),
            Groups = groups
        };

        return new DeadPatchReport
        {
            Total = report.Total, Reports = report.Reports, Gap = report.Gap, Never = report.Never, Name = report.Name,
            Double = report.Double, Groups = groups,
            Text = Render(report)
        };
    }

    /// <summary>Korte samenvatting per groep voor in het indexrapport.</summary>
    public List<string> GroupSummary(int max)
    {
        return Groups.Take(max).Select(g => g.Header).ToList();
    }

    private static string Render(DeadPatchReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("dode patches: " + report.Summary);
        text.AppendLine();
        text.AppendLine("oorzaken:");
        text.AppendLine("  gat       het item komt alleen als ingrediënt voor en heeft geen eigen recept —");
        text.AppendLine("            dump- of parsergat, of bewust weggehaald door de pack");
        text.AppendLine("  nooit     het item bestaat nergens in de set — nooit toegevoegd, mod weg of verkeerde naam");
        text.AppendLine("  naamvorm  andere schrijfwijze: oredict-tag (ore:, #, *), spatie, of een korte naam");
        text.AppendLine("            tegenover de volledige id van hetzelfde item");
        text.AppendLine("  dubbel    het doel bestaat in de eindset: een latere patch zette het terug — netto onschuldig");
        text.AppendLine();

        foreach (var group in report.Groups)
        {
            text.AppendLine(group.Header);

            foreach (var entry in group.Entries)
            {
                string cause = entry.Cause switch
                {
                    "gat" => "gat",
                    "naamvorm" => "naamvorm",
                    "dubbel" => "dubbel",
                    _ => "nooit"
                };

                text.AppendLine($"  {entry.Op,-16} {entry.Item,-40} {entry.Where}  ({cause})");
            }

            text.AppendLine();
        }

        if (report.Total == 0) text.AppendLine("Elke patch raakte een bestaand recept.");
        return text.ToString();
    }

    private static string CauseOf(RecipeOp op, IReadOnlyDictionary<string, Recipe> recipes,
                                  HashSet<string> ingredients, NameIndex index)
    {
        string item = op.Out.Length > 0 ? op.Out : op.Item;
        if (item.Length == 0) return "nooit";

        if (recipes.ContainsKey(item)) return "dubbel";     // een latere patch zette het terug
        if (ingredients.Contains(item)) return "gat";       // bestaat wel als ingrediënt

        // Notaties die de set nooit als sleutel heeft: oredict-tags (ore:, #), spaties,
        // jokertekens. Dat is een naamvorm-verschil, geen ontbrekend recept.
        if (LooksOdd(item)) return "naamvorm";

        // Zelfde ding, andere vorm: script schrijft "mod:naam" en de set "naam", of andersom.
        if (index.SameNameElsewhere(item)) return "naamvorm";

        // Ingrediënt met dezelfde naam in een andere vorm: het bestaat dus wel.
        if (index.IngredientSameName(item)) return "gat";

        return "nooit";
    }

    /// <summary>Naamvorm-hulpjes: de korte naam en de volledige id van hetzelfde item.</summary>
    private sealed class NameIndex
    {
        private readonly HashSet<string> _keys;
        private readonly HashSet<string> _shortOfKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _longOfKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _shortOfIngredient = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _longOfIngredient = new(StringComparer.OrdinalIgnoreCase);

        public NameIndex(IReadOnlyDictionary<string, Recipe> recipes, IEnumerable<string> ingredients)
        {
            _keys = new HashSet<string>(recipes.Keys, StringComparer.OrdinalIgnoreCase);
            Fill(recipes.Keys, _shortOfKey, _longOfKey);
            Fill(ingredients, _shortOfIngredient, _longOfIngredient);
        }

        private static void Fill(IEnumerable<string> names, HashSet<string> shortNames, HashSet<string> longNames)
        {
            foreach (string name in names)
            {
                int colon = name.LastIndexOf(':');
                if (colon > 0) shortNames.Add(name[(colon + 1)..]);
                else if (name.Length > 0) longNames.Add(name);
            }
        }

        /// <summary>"minecraft:iron_ingot" terwijl de set "iron_ingot" heeft, of andersom.</summary>
        public bool SameNameElsewhere(string item)
        {
            int colon = item.LastIndexOf(':');
            if (colon > 0)
            {
                string shortName = item[(colon + 1)..];
                return _keys.Contains(shortName) || _longOfKey.Contains(shortName);
            }

            return _keys.Any(k => k.EndsWith(":" + item, StringComparison.OrdinalIgnoreCase));
        }

        public bool IngredientSameName(string item)
        {
            int colon = item.LastIndexOf(':');
            if (colon > 0) return _longOfIngredient.Contains(item[(colon + 1)..]);

            return _shortOfIngredient.Contains(item);
        }
    }

    private static IEnumerable<Ingredient> AllIngredients(Recipe recipe)
    {
        foreach (var ingredient in recipe.Ingredients) yield return ingredient;

        foreach (var alternative in recipe.Alternatives)
            foreach (var ingredient in AllIngredients(alternative))
                yield return ingredient;
    }

    /// <summary>Notaties die een script kan schrijven maar de set nooit als sleutel heeft.</summary>
    private static bool LooksOdd(string item) =>
        item.StartsWith("ore:", StringComparison.OrdinalIgnoreCase) ||
        item.Contains(' ') || item.Contains('#') || item.Contains('*') || item.Contains('|') ||
        item.EndsWith('.') || item.Contains("..") || item.Trim().Length != item.Length;
}
