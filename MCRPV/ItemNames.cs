using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Mcrpv;

/// <summary>
/// Mooie itemnamen. De echte naam komt uit de lang-bestanden van de instance
/// (assets/&lt;mod&gt;/lang/en_us.lang in mods/*.jar en resourcepacks) en wordt tijdens de
/// import aan de receptsleutel gekoppeld. Zonder vertaling valt de app terug op een
/// nette Titel Case van de registry-id ("iron_ingot" → "Iron Ingot").
/// </summary>
public static class ItemNames
{
    // "mod:item" → "Iron Ingot" (uit de lang-bestanden). Niet readonly: de index wordt op
    // de achtergrond opgebouwd en daarna in één keer vervangen, zodat een lezer nooit een
    // half gevulde tabel ziet.
    private static Dictionary<string, string> ById = new(StringComparer.OrdinalIgnoreCase);

    // receptsleutel ("iron_ingot") → "Iron Ingot"
    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Aantal namen dat aan een receptsleutel gekoppeld is.</summary>
    public static int Count => ByName.Count;

    /// <summary>Aantal namen uit de lang-bestanden (per registry-id).</summary>
    public static int IdCount => ById.Count;

    public static IReadOnlyDictionary<string, string> Map => ByName;

    public static void LoadMap(IReadOnlyDictionary<string, string> map)
    {
        ByName.Clear();

        foreach (var kv in map)
            if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                ByName[kv.Key] = kv.Value;
    }

    /// <summary>Vult de id-tabel met de namen uit de jars/resourcepacks van de instance.</summary>
    public static int LoadFromInstance(string? instanceRoot) => Publish(Build(instanceRoot));

    /// <summary>
    /// Zelfde index, maar op de achtergrond: alle jars doorlezen kost bij een grote instance
    /// seconden en mag het laden van de bookmarks niet laten hangen. De tabel wordt pas
    /// vervangen als hij klaar is; tot die tijd blijven de bestaande namen staan.
    /// </summary>
    public static async Task<int> LoadFromInstanceAsync(string? instanceRoot)
    {
        var names = await Task.Run(() => Build(instanceRoot));
        return Publish(names);
    }

    private static int Publish(Dictionary<string, string> names)
    {
        ById = names;
        return names.Count;
    }

    private static Dictionary<string, string> Build(string? instanceRoot)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(instanceRoot) || !Directory.Exists(instanceRoot)) return names;

        var gameDirs = new List<string> { instanceRoot };
        foreach (var sub in new[] { "minecraft", ".minecraft" })
        {
            string dir = Path.Combine(instanceRoot, sub);
            if (Directory.Exists(dir)) gameDirs.Add(dir);
        }

        foreach (var gameDir in gameDirs)
        {
            foreach (var sub in new[] { "mods", "resourcepacks" })
            {
                string dir = Path.Combine(gameDir, sub);
                if (!Directory.Exists(dir)) continue;

                var archives = Directory.GetFiles(dir, "*.jar", SearchOption.TopDirectoryOnly)
                                        .Concat(Directory.GetFiles(dir, "*.zip", SearchOption.TopDirectoryOnly));

                foreach (var file in archives)
                    ReadArchive(file, names);
            }
        }

        return names;
    }

    /// <summary>Echte naam bij een registry-id ("thermallogistics:material"), of null.</summary>
    public static string? NameForId(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId)) return null;

        string id = rawId.Trim();

        int brace = id.IndexOf('{');
        if (brace > 0) id = id[..brace];

        // Eerst de variant zelf: lang-bestanden noemen subtypes soms apart
        // (item.mod.material.ingotCopper.name).
        if (ById.TryGetValue(id, out var exact) && !string.IsNullOrWhiteSpace(exact)) return exact;

        // "mod:path:0" → "mod:path" (metadata hoort bij hetzelfde item)
        var parts = id.Split(':');
        if (parts.Length > 2)
        {
            string baseId = parts[0] + ":" + parts[1];
            if (ById.TryGetValue(baseId, out var viaBase) && !string.IsNullOrWhiteSpace(viaBase)) return viaBase;
        }

        return null;
    }

    /// <summary>Voegt namen uit de import toe (bestaande namen blijven staan).</summary>
    public static void Merge(IReadOnlyDictionary<string, string>? names)
    {
        if (names == null) return;

        foreach (var kv in names)
            if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                ByName[kv.Key] = kv.Value;
    }

    /// <summary>Naam voor een sleutel uit recipes.json of een bookmark.</summary>
    public static string Display(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";

        if (ByName.TryGetValue(key, out var nice) && !string.IsNullOrWhiteSpace(nice)) return nice;

        string norm = RecipePlanner.NormalizeName(key);
        if (norm.Length > 0 && ByName.TryGetValue(norm, out var viaNorm) && !string.IsNullOrWhiteSpace(viaNorm))
            return viaNorm;

        return Pretty(key);
    }

    /// <summary>"iron_ingot" → "Iron Ingot".</summary>
    public static string Pretty(string key)
    {
        string text = (key ?? "").Replace('_', ' ').Trim();
        if (text.Length == 0) return "";

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => w.Length <= 2 && w.All(char.IsDigit)
                                     ? w
                                     : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());

        return string.Join(' ', words);
    }

    private static void ReadArchive(string path, Dictionary<string, string> names)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName;
                if (!name.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) continue;

                bool plain = name.EndsWith("/lang/en_us.lang", StringComparison.OrdinalIgnoreCase);
                bool json = name.EndsWith("/lang/en_us.json", StringComparison.OrdinalIgnoreCase);
                if (!plain && !json) continue;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                string text = reader.ReadToEnd();

                if (json) ReadJsonLang(text, names);
                else ReadPlainLang(text, names);
            }
        }
        catch
        {
            // Eén kapotte jar mag de rest niet tegenhouden.
        }
    }

    private static readonly Regex LangLine = new(
        @"^(?:item|tile|block)\.(?<id>[A-Za-z0-9_.:]+?)\.name\s*=\s*(?<name>.+)$",
        RegexOptions.Compiled);

    private static void ReadPlainLang(string text, Dictionary<string, string> names)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var match = LangLine.Match(rawLine.Trim());
            if (match.Success) Add(names, match.Groups["id"].Value, match.Groups["name"].Value);
        }
    }

    private static void ReadJsonLang(string text, Dictionary<string, string> names)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject obj) return;

            foreach (var kv in obj)
            {
                var match = Regex.Match(kv.Key, @"^(?:item|tile|block)\.(?<id>.+)$");
                if (!match.Success) continue;

                Add(names, match.Groups["id"].Value, kv.Value?.ToString() ?? "");
            }
        }
        catch { }
    }

    private static void Add(Dictionary<string, string> names, string rawId, string rawName)
    {
        string name = Clean(rawName);
        if (name.Length == 0) return;

        // 1.12-lang gebruikt punten: "thermallogistics.material" → "thermallogistics:material"
        string id = rawId.Replace('.', ':').Trim();

        // "mod:path:0" → "mod:path" (metadata hoort bij hetzelfde item)
        var parts = id.Split(':');
        if (parts.Length > 2 && parts.Skip(2).All(p => int.TryParse(p, out _)))
            id = parts[0] + ":" + parts[1];

        if (id.Length == 0) return;

        names.TryAdd(id, name);
    }

    private static string Clean(string raw)
    {
        string name = (raw ?? "").Trim();

        name = Regex.Replace(name, "§.", "");              // kleur-/opmaakcodes
        name = name.Replace("\\n", " ").Replace("%s", "");

        // De lang-bestanden en tooltipMap bewaren een apostrophe als \u0027; die escape
        // hoort niet in de UI ("Engineer\u0027s Hammer" werd letterlijk zo getoond).
        name = Regex.Replace(name, @"\\u([0-9A-Fa-f]{4})",
                             m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

        name = name.Trim(' ', '"', ',');

        if (name.Length == 0) return "";
        if (name.Contains('%')) return "";                  // "Crafting Table %s" is niet bruikbaar

        return Regex.Replace(name, @"\s+", " ");
    }
}
