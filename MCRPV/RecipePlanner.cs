using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mcrpv;

// ========== Data models ==========

public class Recipe
{
    /// <summary>JEI-tab/machine waarin dit recept zit ("Crafting", "Alloy Smelter", …).</summary>
    public string Category { get; set; } = "";

    /// <summary>Hoeveel stuks dit recept per beurt maakt: 9 tarwe uit één hooibaal, 4 trappen, …</summary>
    public int Output { get; set; } = 1;

    public List<Ingredient> Ingredients { get; set; } = new();

    /// <summary>Andere manieren om hetzelfde item te maken, uit andere JEI-tabs.</summary>
    public List<Recipe> Alternatives { get; set; } = new();

    /// <summary>
    /// Waar dit recept vandaan komt: "jar-JSON immersiveengineering",
    /// "scripts/mythic/runes.zs:42", "runtime:exports", "overlay". Leeg = onbekend.
    /// Wordt niet weggeschreven als hij leeg is, zodat recipes.json compact blijft.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string Source { get; set; } = "";

    /// <summary>De patch die dit recept neerzette (bron:regel); leeg voor basisrecepten.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string PatchId { get; set; } = "";

    /// <summary>Het hoofdrecept eerst, daarna de alternatieven.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<Recipe> Options
    {
        get
        {
            if (Alternatives.Count == 0) return new[] { this };

            var list = new List<Recipe>(Alternatives.Count + 1) { this };

            // Een bewerkte recipes.json kan een lege plek in de lijst hebben
            // ("Alternatives": [null]); die overslaan.
            list.AddRange(Alternatives.Where(alternative => alternative != null));
            return list;
        }
    }
}

public class Ingredient
{
    public string Item { get; set; } = "";
    public int Count { get; set; } = 1;
}

public class RecipeNode
{
    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public bool IsCycle { get; set; }
    public bool IsTruncated { get; set; }
    public ObservableCollection<RecipeNode> Children { get; set; } = new();

    /// <summary>Machine/tab van het recept dat voor deze knoop gebruikt is.</summary>
    public string Category { get; set; } = "";

    /// <summary>Welk van de recepten gekozen is (0 = het hoofdrecept).</summary>
    public int OptionIndex { get; set; }

    /// <summary>Hoeveel recepten dit item heeft; bij meer dan één kun je klikken.</summary>
    public int OptionCount { get; set; }

    /// <summary>Het recept waarmee deze knoop is uitgerekend (voor het stappenplan).</summary>
    public Recipe? Used { get; set; }

    /// <summary>Herkomst van het gebruikte recept, voor de tooltip ("uit scripts/…:42").</summary>
    public string Source { get; set; } = "";

    /// <summary>Deze machine komt uit een voorkeur (★) voor dit item, niet uit een losse klik.</summary>
    public bool Preferred { get; set; }

    public bool HasChoice => OptionCount > 1;

    public string MachineText =>
        (Preferred ? "★ " : "") + (OptionCount > 1 ? $"{Category} · {OptionIndex + 1}/{OptionCount}" : Category);

    public string DisplayName =>
        IsCycle ? $"{ItemNames.Display(Name)} ♻️ (cycle)"
        : IsTruncated ? $"{ItemNames.Display(Name)} …"
        : ItemNames.Display(Name);
    public string QuantityText => Quantity > 1 ? $"×{Quantity}" : "";
}

/// <summary>
/// Korte weergave van de keten die één receptkeuze oplevert: hoeveel stappen en
/// basismaterialen eronder hangen, en de eerste paar tussenstappen als tekst.
/// </summary>
public sealed class ChainPreview
{
    public string Lines { get; init; } = "";
    public int Steps { get; init; }
    public int BaseMaterials { get; init; }
    public int Depth { get; init; }
}

/// <summary>Eén JEI bookmark: item + gewenste hoeveelheid.</summary>
public class Bookmark
{
    public string Item { get; set; } = "";
    public int Count { get; set; } = 1;
}

/// <summary>Bookmark zoals hij in de keuzelijst van de app staat.</summary>
public class BookmarkChoice
{
    public string Item { get; set; } = "";
    public int Count { get; set; } = 1;
    public string Key { get; set; } = "";
    public bool Known { get; set; }

    public string Display => Known
        ? $"{ItemNames.Display(Item)} ×{Count}"
        : $"{ItemNames.Display(Item)} ×{Count}  ⚠";

    // Zodat ook een fallback (sortering, logging, UI Automation) de mooie naam toont.
    public override string ToString() => Display;
}

/// <summary>
/// Alle rekenwerk zonder UI: bookmarks lezen en het stappenplan
/// "van basis tot eindproduct" opstellen.
/// </summary>
public static class RecipePlanner
{
    // ========== Namen ==========

    /// <summary>Maakt van "minecraft:Iron Ingot{...}" iets als "iron_ingot".</summary>
    public static string NormalizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string s = raw.Trim().Trim('"', '\'', ',', ';', ' ');

        int cut = s.IndexOfAny(new[] { '{', '[', '@' });
        if (cut > 0) s = s[..cut];

        // JEI bewaart stacks soms als "minecraft:stone 1 0" (id damage count)
        var stackText = Regex.Match(s, @"^(?<id>\S+)(?:\s+\d+)+$");
        if (stackText.Success) s = stackText.Groups["id"].Value;

        // "minecraft:stone:1" → "minecraft:stone" (alleen als de extra delen cijfers zijn)
        var colonParts = s.Split(':');
        if (colonParts.Length >= 3 && colonParts.Skip(2).All(p => int.TryParse(p, out _)))
            s = colonParts[0] + ":" + colonParts[1];

        if (s.Contains(':')) s = s[(s.LastIndexOf(':') + 1)..];

        s = s.ToLowerInvariant().Replace(' ', '_').Replace('-', '_').Replace('.', '_');
        s = Regex.Replace(s, @"[^a-z0-9_]", "_");
        s = Regex.Replace(s, "_+", "_").Trim('_');
        return s;
    }

    public static Dictionary<string, string> BuildLookup(IEnumerable<string> recipeKeys)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in recipeKeys)
        {
            string norm = NormalizeName(key);
            if (norm.Length > 0 && !lookup.ContainsKey(norm))
                lookup[norm] = key;
        }
        return lookup;
    }

    /// <summary>Zoekt de recipes.json-sleutel die bij een bookmark hoort.</summary>
    public static string ResolveKey(string raw, Dictionary<string, string> lookup, IReadOnlyCollection<string> recipeKeys)
    {
        string cleaned = raw?.Trim() ?? "";
        if (cleaned.Length == 0) return "";

        foreach (var key in recipeKeys)
            if (string.Equals(key, cleaned, StringComparison.OrdinalIgnoreCase))
                return key;

        string norm = NormalizeName(cleaned);
        if (lookup.TryGetValue(norm, out var found))
            return found;

        return norm;
    }

    // ========== Bookmarks lezen ==========

    /// <summary>
    /// Leest een bookmarks-bestand. Accepteert o.a.:
    /// ["diamond", "iron_ingot"] · [{"Item":"diamond","Count":2}] ·
    /// {"bookmarks":[…]} · {"diamond":2} · platte tekst (één item per regel).
    /// </summary>
    public static List<Bookmark> ParseBookmarks(string text)
    {
        var result = new List<Bookmark>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? raw, int count)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            string name = NormalizeName(raw);
            if (name.Length == 0 || name == "unknown") return;

            if (seen.Add(name))
                result.Add(new Bookmark { Item = name, Count = count < 1 ? 1 : count });
        }

        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return result;

        if (trimmed[0] == '[' || trimmed[0] == '{')
        {
            // Geldige JSON, of een licht beschadigde variant (dubbele quotes)
            foreach (var candidate in new[] { trimmed, trimmed.Replace("\"\"", "\""), trimmed.TrimStart('\uFEFF') })
            {
                try
                {
                    CollectFromJson(JsonNode.Parse(candidate), Add);
                }
                catch { }

                if (result.Count > 0) return result;
            }

            // Kapotte JSON: nog wel de item-namen eruit halen (geen onzin uit regels)
            RecoverQuotedItems(trimmed, Add);
            return result;
        }

        // JEI bookmarks.ini (1.12.2): regels als
        // T:{id:"minecraft:diamond",Count:3,Damage:0s} — dat is geen geldige JSON.
        if (trimmed.Contains("id:", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Contains("Count:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match match in Regex.Matches(trimmed, @"id\s*:\s*""([^""\r\n]*)""[^\r\n]*"))
            {
                int count = 1;
                var countMatch = Regex.Match(match.Value, @"Count\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                if (countMatch.Success) int.TryParse(countMatch.Groups[1].Value, out count);

                Add(match.Groups[1].Value, count);
            }

            if (result.Count > 0) return result;
        }

        // Platte tekst: één item per regel
        foreach (var rawLine in trimmed.Split('\n'))
        {
            string line = rawLine.Trim().Trim(',', ';', '"', ' ');
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

            int count = 1;
            var countMatch = Regex.Match(line, @"\s+[xX]?(\d+)$");
            if (countMatch.Success)
            {
                int.TryParse(countMatch.Groups[1].Value, out count);
                line = line[..countMatch.Index];
            }

            Add(line, count);
        }

        // Laatste redmiddel: registry-ids uit de tekst vissen
        if (result.Count == 0)
            foreach (Match m in Regex.Matches(trimmed, @"[A-Za-z0-9_.\-]+:[A-Za-z0-9_./\-]+"))
                Add(m.Value, 1);

        return result;
    }

    private static readonly HashSet<string> JsonFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Item", "Count", "item", "count", "id", "Id", "ID", "name", "Name",
        "amount", "Amount", "quantity", "Quantity", "registry_name", "registryName",
        "stack", "itemStack", "itemstack", "bookmarks", "bookmark", "version", "format", "type"
    };

    /// <summary>Vangnet voor beschadigde JSON-bestanden: alleen quoted item-namen overhouden.</summary>
    private static void RecoverQuotedItems(string text, Action<string?, int> add)
    {
        foreach (Match match in Regex.Matches(text, "\"([A-Za-z0-9_.:\\- ]+)\""))
        {
            string token = match.Groups[1].Value.Trim();
            if (token.Length == 0 || JsonFieldNames.Contains(token)) continue;
            add(token, 1);
        }
    }

    private static void CollectFromJson(JsonNode? node, Action<string?, int> add)
    {
        switch (node)
        {
            case JsonArray arr:
                foreach (var child in arr)
                    CollectFromJson(child, add);
                break;

            case JsonObject obj:
            {
                // JEI-stack: { "stack": { "id": "minecraft:diamond", "Count": 2 } }
                foreach (var stackKey in new[] { "stack", "itemStack", "itemstack" })
                {
                    if (obj[stackKey] is JsonObject nested)
                    {
                        string? nestedId = FirstString(nested, "item", "Item", "id", "Id", "registry_name", "registryName", "name", "Name");
                        if (nestedId != null)
                        {
                            add(nestedId, FirstInt(nested, "count", "Count", "amount", "Amount", "quantity", "Quantity", "stackSize", "size"));
                            return;
                        }
                    }
                    else if (obj[stackKey] is JsonValue stackValue)
                    {
                        // Oudere JEI: { "itemStack": "minecraft:stone 1 0" }
                        string? stackText = stackValue.ToString();
                        if (!string.IsNullOrWhiteSpace(stackText) && stackText != "null")
                        {
                            add(stackText, FirstInt(obj, "count", "Count"));
                            return;
                        }
                    }
                }

                string? id = FirstString(obj, "item", "Item", "id", "Id", "ID",
                    "registry_name", "registryName", "registryname", "name", "Name",
                    "output", "result");

                int count = FirstInt(obj, "count", "Count", "amount", "Amount", "quantity", "Quantity", "stackSize", "size");

                if (id != null)
                {
                    add(id, count);
                    break;
                }

                // Simpele map: { "minecraft:diamond": 2 }
                bool allScalar = obj.Count > 0 && obj.All(kv => kv.Value is JsonValue);
                bool nameLikeKeys = obj.Count > 0 && obj.All(kv => LooksLikeItemName(kv.Key) && !JsonFieldNames.Contains(kv.Key));
                if (allScalar && nameLikeKeys)
                {
                    foreach (var kv in obj)
                        add(kv.Key, kv.Value is JsonValue v && int.TryParse(v.ToString(), out int c) ? c : 1);
                    break;
                }

                foreach (var kv in obj)
                    CollectFromJson(kv.Value, add);
                break;
            }

            case JsonValue val:
            {
                string? s = val.ToString();
                if (!string.IsNullOrWhiteSpace(s) && s != "null" && !Regex.IsMatch(s, @"^\d+$"))
                    add(s, 1);
                break;
            }
        }
    }

    private static bool LooksLikeItemName(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (key.Contains(':')) return true;
        return Regex.IsMatch(key, @"^[a-z0-9_][a-z0-9_ .\-]*$", RegexOptions.IgnoreCase);
    }

    private static string? FirstString(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = obj[key];
            if (value == null) continue;

            if (value is JsonValue val)
            {
                string? s = val.ToString();
                if (!string.IsNullOrWhiteSpace(s) && s != "null") return s;
            }

            // Bijv. "stack": { "id": … }
            if (value is JsonObject sub)
            {
                string? nested = FirstString(sub, "item", "id", "name", "registry_name", "registryName");
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static int FirstInt(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = obj[key];
            if (value is JsonValue val && int.TryParse(val.ToString(), out int parsed) && parsed > 0)
                return parsed;
        }
        return 1;
    }

    /// <summary>Aantal keren dat een recept moet draaien om <paramref name="amount"/> stuks te maken.</summary>
    public static long CraftsFor(long amount, Recipe recipe) =>
        recipe.Output > 1 ? (amount + recipe.Output - 1) / recipe.Output : amount;

    // ========== Boom opbouwen ==========

    /// <summary>Echte packs hebben cycles en diepe ketens: zonder plafond wordt de boom
    /// tienduizenden knooppunten groot en bevriest de UI tijdens het opbouwen.</summary>
    public const int MaxTreeNodes = 3_000;
    public const int MaxTreeDepth = 16;


    /// <summary>
    /// Bouwt de boom voor één item, met dezelfde regels als de UI: voorkeursmachine (★)
    /// wint, dan de gemaakte keuze, anders het hoofdrecept — en zonder expliciete keuze
    /// liever geen recept dat zijn eigen product opeet of een omgekeerd recept is.
    /// </summary>
    public static RecipeNode Build(string item, int quantity, IReadOnlyDictionary<string, Recipe> recipes,
                                   IReadOnlyDictionary<string, string> preferences, IReadOnlyDictionary<string, int> choices,
                                   ref int budget, PlannerLimits? limits = null)
    {
        limits = (limits ?? new PlannerLimits()).Clamped();

        // De grenzen gelden per gepland item: vraag je 64 stuks, dan mag een stap ook
        // 64× zoveel beurten kosten en het plan 64× zoveel basis-materialen gebruiken.
        long products = Math.Max(1, quantity);
        long baseLeft = products * limits.BaseItemsPerProduct;
        long craftsLimit = products * limits.MaxCraftsPerStep;

        return Build(item, quantity, recipes, preferences, choices, ref budget, ref baseLeft, craftsLimit,
                     new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0,
                     new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
    }

    private static RecipeNode Build(string item, int quantity, IReadOnlyDictionary<string, Recipe> recipes,
                                   IReadOnlyDictionary<string, string> preferences, IReadOnlyDictionary<string, int> choices,
                                   ref int budget, ref long baseLeft, long craftsLimit, HashSet<string> path, int depth, Dictionary<string, int> chainCosts)
    {
        var node = new RecipeNode { Name = item, Quantity = quantity };

        if (path.Contains(item))
        {
            node.IsCycle = true;
            return node;
        }

        // Elk knooppunt kost budget, ook eentje dat op de diepte stopt. Eerder kortte de
        // dieptecheck het budget niet in, waardoor een brede keten doorgroeide tot ver
        // boven MaxTreeNodes (5.943 knopen bij obsidian) en het plan onbruikbaar werd.
        if (budget-- <= 0 || depth >= MaxTreeDepth || baseLeft <= 0)
        {
            node.IsTruncated = true;
            return node;
        }

        // Eindhalte van de keten: dit item komt uit de wereld, van een boer of uit een
        // machine. Het telt mee voor de basis-begroting, zodat één absurde tak de rest
        // van het plan niet opblaast.
        if (!recipes.TryGetValue(item, out var recipe) || recipe.Ingredients.Count == 0)
        {
            baseLeft -= quantity;
            return node;
        }

        var options = recipe.Options;
        int optionIndex = 0;
        bool preferred = false;
        bool chosenByHand = false;

        if (preferences.TryGetValue(item, out var wantedMachine))
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (!string.Equals(options[i].Category, wantedMachine, StringComparison.OrdinalIgnoreCase)) continue;

                optionIndex = i;
                preferred = true;
                break;
            }
        }

        if (!preferred && choices.TryGetValue(item, out int chosen))
        {
            optionIndex = Math.Clamp(chosen, 0, options.Count - 1);
            chosenByHand = true;
        }

        if (!preferred && !chosenByHand)
            optionIndex = PickSaneOption(item, options, optionIndex, recipes, chainCosts);

        // Options laat lege plekken in Alternatives vallen, dus dit is altijd een recept.
        var used = options[optionIndex];

        node.Used = used;
        node.Source = used.Source;
        node.Category = used.Category;
        node.OptionIndex = optionIndex;
        node.OptionCount = options.Count;
        node.Preferred = preferred;

        // Boeren, ziften, kopen en omzetten zijn geen craft-stappen: dat is verzamelen.
        // De keten stopt daar, anders wordt "Tarwe" een keten van zaadzakken en
        // markt-recepten en loopt het plan de honderdduizenden in. Kies je zo'n recept
        // zelf (of zet je er een ★ op), dan rekent de boom hem gewoon door.
        if (!preferred && !chosenByHand && IsGathering(used))
        {
            baseLeft -= quantity;
            return node;
        }

        int perCraft = Math.Max(1, used.Output);
        int crafts = (int)Math.Ceiling(quantity / (double)perCraft);

        // 440 blokken lood voor één piston is geen planstap maar bulk: zo'n tak wordt
        // afgekapt in plaats van tientallen verdiepingen dieper uitgerekend.
        if (crafts > craftsLimit)
        {
            node.IsTruncated = true;
            return node;
        }

        // Omzettingen in de pack kunnen per ronde vermenigvuldigen; zonder plafond
        // schiet één tak naar miljoenen items voordat het kringetje rond is.
        const int MaxQuantity = 9_999;

        path.Add(item);

        foreach (var ing in used.Ingredients)
        {
            long needed = (long)ing.Count * crafts;

            // Ketens waarin elke stap weer een veelvoud vraagt (essence-trappen,
            // pyrotheum, platinum) lopen anders op tot honderdduizenden items.
            // Liever eerlijk afkappen dan een onbruikbaar plan tonen.
            if (needed > MaxQuantity)
            {
                node.Children.Add(new RecipeNode { Name = ing.Item, Quantity = MaxQuantity, IsTruncated = true });
                continue;
            }

            node.Children.Add(Build(ing.Item, (int)needed, recipes, preferences, choices, ref budget, ref baseLeft, craftsLimit, path, depth + 1, chainCosts));
        }

        path.Remove(item);
        return node;
    }

    /// <summary>Tabbladen waarin je een item verkrijgt in plaats van het te craften.</summary>
    private static readonly string[] NaturalCategories =
    {
        "Crop Produce", "Crop Mutation", "Tier 6 Crop", "Bonsai Trees",
        "Sieve", "Auto Sieve", "Sluice Box", "Fluid Sieving Machine",
        // Ertsen delven: een eindhalte, je gaat geen houweel uitrekenen.
        RecipeOverlay.MiningCategory
    };

    /// <summary>Tabbladen die een item omzetten in iets anders (transmutatie, chiselen, …).</summary>
    private static readonly string[] TransformCategories =
    {
        "Transmutation", "World Transmutation", "Starlight Transmutation",
        "Enstabulation Apparatus", "Chiseling", "Factorizer - Combine", "Factorizer - Split",
        "Alchemy Array", "Alchemy Array (Binding)", "Lootbag", "Coin Engraving",
        "Materialization", "Crystallization"
    };

    /// <summary>Tabbladen waarin je een item koopt; die komen pas als laatste aan bod.</summary>
    private static readonly string[] BuyCategories = { "Market", "Amadron Tablet" };

    /// <summary>
    /// Rangschikking van een recept: 0 = natuurlijk (boeren, ziften, vinden), 1 = gewoon
    /// craften, 2 = omzetten (essence, transmutatie, chiselen), 3 = kopen. Het plan kiest
    /// het laagste nummer, zodat er niet standaard een essence-keten in de boom staat.
    /// </summary>
    private static int CategoryTier(Recipe recipe)
    {
        if (BuyCategories.Any(c => string.Equals(c, recipe.Category, StringComparison.OrdinalIgnoreCase))) return 3;
        if (NaturalCategories.Any(c => string.Equals(c, recipe.Category, StringComparison.OrdinalIgnoreCase))) return 0;
        if (TransformCategories.Any(c => string.Equals(c, recipe.Category, StringComparison.OrdinalIgnoreCase))) return 2;

        // "8 Nature Essence → 1 Pumpkin" staat onder Crafting, maar is een omzetting.
        if (recipe.Ingredients.Count > 0 &&
            recipe.Ingredients.All(i => i.Item.Contains("essence", StringComparison.OrdinalIgnoreCase)))
            return 2;

        return 1;
    }

    /// <summary>
    /// Kiest de beste optie: geen recept dat zijn eigen product opeet, en bij twijfel het
    /// natuurlijke recept boven craften, omzetten of kopen.
    /// </summary>
    private static int PickSaneOption(string item, IReadOnlyList<Recipe> options, int start,
                                      IReadOnlyDictionary<string, Recipe> recipes,
                                      Dictionary<string, int> chainCosts)
    {
        if (options.Count < 2) return start;

        // Hetzelfde item komt in elke tak opnieuw langs: één keer beslissen is genoeg.
        string memoKey = PickMemoPrefix + item;
        if (chainCosts.TryGetValue(memoKey, out int remembered) && remembered >= 0 && remembered < options.Count)
            return remembered;

        int best = -1;
        int bestTier = int.MaxValue;
        int bestCost = int.MaxValue;
        int bestOutput = 0;

        for (int i = 0; i < options.Count; i++)
        {
            if (!IsSaneOption(item, options[i], recipes)) continue;

            int tier = CategoryTier(options[i]);
            int output = Math.Max(1, options[i].Output);

            // Alleen binnen de beste tier naar de ketenlengte kijken. Vroeger won daar de
            // eerste de beste route, en dat was vaak een essence-, Blood Altar- of
            // matter-keten voor een doodgewoon blok (zie de afgekapte plannen).
            int cost = tier > bestTier ? int.MaxValue : OptionChainCost(item, options[i], recipes, chainCosts);

            bool better = tier < bestTier ||
                          (tier == bestTier &&
                           (cost < bestCost || (cost == bestCost && output > bestOutput)));

            if (!better) continue;

            best = i;
            bestTier = tier;
            bestCost = cost;
            bestOutput = output;
        }

        if (best < 0) best = start;

        chainCosts[memoKey] = best;
        return best;
    }

    private const string PickMemoPrefix = "@pick:";

    /// <summary>Hoe diep de lookahead gaat bij het schatten van een keten.</summary>
    private const int ChainLookahead = 4;

    /// <summary>
    /// Ruwe schatting van hoe groot de keten achter dit recept wordt: elk ingrediënt met
    /// een eigen recept telt mee tot <see cref="ChainLookahead"/> stappen diep. Basis- en
    /// verzamelitems kosten niets en kringetjes worden afgekapt, dus dit is goedkoop
    /// genoeg om bij elke knoop te doen.
    /// </summary>
    private static int OptionChainCost(string item, Recipe option, IReadOnlyDictionary<string, Recipe> recipes,
                                       Dictionary<string, int> memo)
    {
        if (option.Ingredients.Count == 0) return 0;

        int cost = 1;
        var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { item };

        foreach (var ing in option.Ingredients)
            cost += 1 + EstimateChainCost(ing.Item, recipes, memo, path, ChainLookahead);

        return cost;
    }

    private static int EstimateChainCost(string item, IReadOnlyDictionary<string, Recipe> recipes,
                                         Dictionary<string, int> memo, HashSet<string> path, int depth)
    {
        if (depth <= 0 || path.Contains(item)) return 0;
        if (memo.TryGetValue(item, out int known)) return known;
        if (!recipes.TryGetValue(item, out var recipe) || recipe.Ingredients.Count == 0) return 0;

        int best = int.MaxValue;
        path.Add(item);

        foreach (var option in recipe.Options)
        {
            if (!IsSaneOption(item, option, recipes)) continue;

            int cost = 1;
            foreach (var ing in option.Ingredients)
                cost += 1 + EstimateChainCost(ing.Item, recipes, memo, path, depth - 1);

            if (cost < best) best = cost;
        }

        path.Remove(item);

        if (best == int.MaxValue) best = 1;
        memo[item] = best;
        return best;
    }

    /// <summary>
    /// Verzamelen in plaats van craften: boeren, ziften, kopen of een omzetting. Zulke
    /// recepten zijn het einde van de keten, niet een stap die je doorrekent.
    /// </summary>
    private static bool IsGathering(Recipe recipe)
    {
        if (NaturalCategories.Any(c => string.Equals(c, recipe.Category, StringComparison.OrdinalIgnoreCase))) return true;
        if (BuyCategories.Any(c => string.Equals(c, recipe.Category, StringComparison.OrdinalIgnoreCase))) return true;
        if (TransformCategories.Any(c => string.Equals(c, recipe.Category, StringComparison.OrdinalIgnoreCase))) return true;

        return recipe.Ingredients.Count > 0 &&
               recipe.Ingredients.All(i => i.Item.Contains("essence", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Een recept mag zijn eigen product niet als ingrediënt hebben: in de JEI-dump
    /// gebeurt dat bij items met dezelfde naam uit een andere mod (HarvestCraft-pompoen
    /// → Pumpkin). Zulke recepten zijn geen echte stap en leveren alleen kringetjes op.
    /// Craftrecepten mogen ook niet het spiegelbeeld zijn van een recept van hun eigen
    /// ingrediënt (Californium-blok ↔ Californium, Glas ↔ Glasscherf): dat pompt de
    /// aantallen op. Natuurlijke recepten zijn uitgezonderd, want die stoppen de keten
    /// toch al — anders zou "Pumpkin Seeds ← Pumpkin" het boeren onmogelijk maken.
    /// </summary>
    private static bool IsSaneOption(string item, Recipe recipe, IReadOnlyDictionary<string, Recipe> recipes)
    {
        if (recipe.Ingredients.Any(i => string.Equals(i.Item, item, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (IsGathering(recipe)) return true;

        return ReversePairWith(item, recipe, recipes) == null;
    }

    /// <summary>
    /// Ingrediënt waarmee dit recept een omgekeerd paar vormt: dat ingrediënt heeft een
    /// recept dat dit item weer als ingrediënt heeft (Californium-blok ← Californium
    /// terwijl Californium ← Californium-blok bestaat). Geeft null als er geen paar is.
    /// Zulke paren laten de keten rondjes draaien en pompen de aantallen op.
    /// </summary>
    public static string? ReversePairWith(string item, Recipe recipe, IReadOnlyDictionary<string, Recipe> recipes)
    {
        foreach (var ing in recipe.Ingredients)
        {
            // Het item zelf is geen omgekeerd paar maar een naamconflict (Pumpkin ← Pumpkin);
            // daar is de eerste controle van IsSaneOption al voor.
            if (string.Equals(ing.Item, item, StringComparison.OrdinalIgnoreCase)) continue;

            if (!recipes.TryGetValue(ing.Item, out var back) || back.Ingredients.Count == 0) continue;

            if (back.Options.Any(o => o.Ingredients.Any(b => string.Equals(b.Item, item, StringComparison.OrdinalIgnoreCase))))
                return ing.Item;
        }

        return null;
    }

    /// <summary>Machine waarin een stap gebeurt, voor de blokken in het stappenplan.</summary>
    private static string MachineOf(Recipe? recipe) =>
        recipe == null || string.IsNullOrWhiteSpace(recipe.Category) ? Lang.T("plan.machine.other") : recipe.Category;

    // ========== Stappenplan: basis → eindproduct ==========

    // ========== Ketenvoorbeeld ==========

    /// <summary>
    /// Rekent voor één receptoptie uit wat hij aan keten oplevert, met dezelfde regels als de
    /// boom: voorkeursmachine (★) wint, anders de gemaakte keuze, anders het hoofdrecept.
    /// Alleen bedoeld als voorbeeld in het keuzepaneel, dus met een klein budget.
    /// </summary>
    public static ChainPreview PreviewChain(
        string itemKey,
        Recipe option,
        IReadOnlyDictionary<string, Recipe> recipes,
        IReadOnlyDictionary<string, string> preferences,
        IReadOnlyDictionary<string, int> choices,
        int maxDepth = 3,
        int maxRows = 7)
    {
        if (option.Ingredients.Count == 0)
            return new ChainPreview();

        var sb = new StringBuilder();
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { itemKey };

        int steps = 1;
        int rows = 0;
        int depth = 0;
        bool cut = false;

        void Walk(Recipe recipe, string indent, int level)
        {
            foreach (var ing in recipe.Ingredients)
            {
                if (rows >= maxRows) { cut = true; return; }

                Recipe? next = null;
                if (!path.Contains(ing.Item) &&
                    recipes.TryGetValue(ing.Item, out var candidate) &&
                    candidate.Ingredients.Count > 0)
                    next = Choose(candidate, ing.Item, preferences, choices);

                rows++;
                string label = $"{ItemNames.Display(ing.Item)} ×{ing.Count}";

                if (next == null)
                {
                    bases.Add(ing.Item);
                    sb.AppendLine(Lang.T("preview.base", indent + label));
                    continue;
                }

                steps++;
                depth = Math.Max(depth, level);
                sb.AppendLine($"{indent}{label} → " +
                              (next.Category.Length > 0 ? next.Category : Lang.T("plan.machine.recipe")));

                if (level < maxDepth)
                {
                    path.Add(ing.Item);
                    Walk(next, indent + "   ", level + 1);
                    path.Remove(ing.Item);
                }
            }
        }

        Walk(option, "", 1);

        string lines = sb.ToString().TrimEnd();
        if (cut && lines.Length > 0) lines += Lang.T("preview.cut");

        return new ChainPreview { Lines = lines, Steps = steps, BaseMaterials = bases.Count, Depth = depth };
    }

    /// <summary>Recept dat de boom voor dit item zou gebruiken (voorkeur, anders keuze, anders hoofdrecept).</summary>
    private static Recipe Choose(Recipe recipe, string item,
        IReadOnlyDictionary<string, string> preferences, IReadOnlyDictionary<string, int> choices)
    {
        var options = recipe.Options;
        if (options.Count == 1) return options[0];

        if (preferences.TryGetValue(item, out var machine))
        {
            for (int i = 0; i < options.Count; i++)
                if (string.Equals(options[i].Category, machine, StringComparison.OrdinalIgnoreCase)) return options[i];
        }

        if (choices.TryGetValue(item, out int chosen)) return options[Math.Clamp(chosen, 0, options.Count - 1)];

        return options[0];
    }

    public static string BuildStepPlan(RecipeNode root, IReadOnlyDictionary<string, Recipe> recipes)
    {
        // long: echte dumps kunnen met cycles/hoeveelheden veel groter worden dan int.
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var craftable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Afgekapte takken zijn geen basis-materiaal: je moet ze los uitrekenen of een
        // ander recept kiezen. Ze worden daarom apart gemarkeerd in plaats van stil in
        // de verzamellijst te belanden.
        var cut = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        void Walk(RecipeNode node, int depth)
        {
            totals[node.Name] = totals.GetValueOrDefault(node.Name) + node.Quantity;

            if (!depths.TryGetValue(node.Name, out int known) || depth > known)
                depths[node.Name] = depth;

            if (node.IsTruncated)
                cut[node.Name] = cut.GetValueOrDefault(node.Name) + node.Quantity;

            if (node.Children.Count > 0)
                craftable.Add(node.Name);

            foreach (var child in node.Children)
                Walk(child, depth + 1);
        }

        Walk(root, 0);

        // De gekozen machine per item: het stappenplan moet dezelfde recepten volgen als
        // de boom die je op dat moment ziet.
        var usedByItem = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);

        void CollectUsed(RecipeNode node)
        {
            if (node.Used != null) usedByItem[node.Name] = node.Used;
            foreach (var child in node.Children) CollectUsed(child);
        }

        CollectUsed(root);

        // Snelle lookups op genormaliseerde namen (dumps kunnen duizenden recepten hebben)
        var totalsNorm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in totals.Keys)
        {
            string norm = NormalizeName(key);
            if (norm.Length > 0 && !totalsNorm.ContainsKey(norm))
                totalsNorm[norm] = key;
        }

        var recipeNorm = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in recipes)
        {
            string norm = NormalizeName(kv.Key);
            if (norm.Length > 0 && !recipeNorm.ContainsKey(norm))
                recipeNorm[norm] = kv.Value;
        }

        Recipe? GetRecipe(string name)
        {
            if (usedByItem.TryGetValue(name, out var used)) return used;
            if (recipes.TryGetValue(name, out var direct)) return direct;
            return recipeNorm.TryGetValue(NormalizeName(name), out var found) ? found : null;
        }

        string Canonical(string name)
        {
            if (totals.ContainsKey(name))
                return totals.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

            return totalsNorm.TryGetValue(NormalizeName(name), out var hit) ? hit : NormalizeName(name);
        }

        var steps = craftable.Where(totals.ContainsKey).ToList();
        var indegree = steps.ToDictionary(s => s, _ => 0, StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            var recipe = GetRecipe(step);
            if (recipe == null) continue;

            foreach (var ing in recipe.Ingredients)
            {
                string ingKey = Canonical(ing.Item);
                if (!craftable.Contains(ingKey)) continue;
                if (!steps.Contains(ingKey, StringComparer.OrdinalIgnoreCase)) continue;
                if (string.Equals(ingKey, step, StringComparison.OrdinalIgnoreCase)) continue; // cycle

                indegree[step]++;
                if (!dependents.TryGetValue(ingKey, out var list))
                    dependents[ingKey] = list = new List<string>();
                list.Add(step);
            }
        }

        // Kahn met diepste stappen eerst → je leest van basis naar eindproduct
        var ready = steps.Where(s => indegree[s] == 0).ToList();
        var ordered = new List<string>();

        while (ready.Count > 0)
        {
            ready.Sort((a, b) =>
            {
                int byDepth = depths.GetValueOrDefault(b).CompareTo(depths.GetValueOrDefault(a));
                return byDepth != 0 ? byDepth : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            string next = ready[0];
            ready.RemoveAt(0);

            if (ordered.Contains(next, StringComparer.OrdinalIgnoreCase)) continue;
            ordered.Add(next);

            if (dependents.TryGetValue(next, out var deps))
                foreach (var dep in deps)
                {
                    indegree[dep]--;
                    if (indegree[dep] == 0) ready.Add(dep);
                }
        }

        // Cycles of ontbrekende recepten: die stappen achteraan tonen
        foreach (var leftover in steps)
            if (!ordered.Contains(leftover, StringComparer.OrdinalIgnoreCase))
                ordered.Add(leftover);

        var baseMaterials = totals.Keys
            .Where(k => !craftable.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"🌸 {ItemNames.Display(root.Name).ToUpperInvariant()} × {root.Quantity}");
        string cutNote = cut.Count > 0 ? Lang.T("plan.cutnote", cut.Count) : string.Empty;
        sb.AppendLine(Lang.T("plan.summary", ordered.Count, baseMaterials.Count, cutNote));
        sb.AppendLine(new string('═', 72));
        sb.AppendLine();

        sb.AppendLine(Lang.T("plan.base"));
        if (baseMaterials.Count == 0)
            sb.AppendLine(Lang.T("plan.base.none"));
        else
            foreach (var item in baseMaterials)
            {
                string note = cut.ContainsKey(item) ? Lang.T("plan.cut") : string.Empty;
                sb.AppendLine($"   {ItemNames.Display(item),-26} × {totals[item]}{note}");
            }

        sb.AppendLine();
        sb.AppendLine(Lang.T("plan.steps"));

        if (ordered.Count == 0)
        {
            sb.AppendLine(Lang.T("plan.steps.norecipe"));
        }
        else
        {
            // Eén blok per machine, in de volgorde waarin die machine voor het eerst
            // langskomt: je leest nog steeds van basis naar eindproduct, maar alles wat
            // dezelfde machine doet staat bij elkaar ("wat moet er in de Altar?")
            var machineOrder = new List<string>();
            var byMachine = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var lastStep = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < ordered.Count; i++)
            {
                string step = ordered[i];
                string machine = MachineOf(GetRecipe(step));

                if (!byMachine.TryGetValue(machine, out var list))
                {
                    byMachine[machine] = list = new List<string>();
                    machineOrder.Add(machine);
                }

                list.Add(step);
                lastStep[machine] = i;
            }

            // Blokken in de volgorde waarin de machine voor het láátst langskomt: een
            // machine die alleen grondstoffen levert staat dan vóór de machine die het
            // eindproduct maakt, ook als hij toevallig later in de lijst begon.
            machineOrder.Sort((a, b) => lastStep[a].CompareTo(lastStep[b]));

            sb.AppendLine(Lang.T("plan.steps.count", ordered.Count, machineOrder.Count));

            foreach (var machine in machineOrder)
            {
                var list = byMachine[machine];

                sb.AppendLine();
                sb.AppendLine(Lang.T("plan.machine", machine, list.Count));
                sb.AppendLine("   " + new string('─', 64));

                int index = 1;
                foreach (var step in list)
                {
                    var recipe = GetRecipe(step);

                    string line = $"   {index++,2}. {ItemNames.Display(step),-24} × {totals[step]}";

                    if (recipe != null && recipe.Ingredients.Count > 0)
                    {
                        long crafts = CraftsFor(totals[step], recipe);
                        var parts = recipe.Ingredients
                            .Select(ing => $"{ItemNames.Display(ing.Item)} × {ing.Count * crafts}");
                        line += $"   ← {string.Join(", ", parts)}";
                    }

                    sb.AppendLine(line);
                }
            }
        }

        if (cut.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Lang.T("plan.cut.warn", cut.Count));
            sb.AppendLine(Lang.T("plan.cut.advice"));
        }

        sb.AppendLine();
        sb.AppendLine(Lang.T("plan.product", ItemNames.Display(root.Name), root.Quantity));
        return sb.ToString();
    }

    // ========== Boodschappenlijst: alle bookmarks bij elkaar ==========

    /// <summary>
    /// Telt de basis-materialen van alle bookmarks samen op tot één boodschappenlijst.
    /// Basis-materiaal = een item zonder recept in de boom (dus wat je moet verzamelen).
    /// </summary>
    public static string BuildShoppingList(IReadOnlyList<RecipeNode> roots, IReadOnlyDictionary<string, Recipe> recipes)
    {
        // long: echte dumps kunnen met cycles/hoeveelheden veel groter worden dan int.
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var craftable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(RecipeNode node)
        {
            totals[node.Name] = totals.GetValueOrDefault(node.Name) + node.Quantity;

            if (node.Children.Count > 0)
                craftable.Add(node.Name);

            foreach (var child in node.Children)
                Walk(child);
        }

        foreach (var root in roots)
            Walk(root);

        var baseMaterials = totals
            .Where(kv => !craftable.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        long totalItems = baseMaterials.Sum(b => b.Value);
        var withoutRecipe = roots
            .Where(r => !recipes.ContainsKey(r.Name))
            .Select(r => r.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine(Lang.T("shop.header", roots.Count));
        sb.AppendLine(new string('═', 64));
        sb.AppendLine();

        if (roots.Count > 0)
        {
            sb.AppendLine(Lang.T("shop.bookmarks"));
            foreach (var root in roots)
                sb.AppendLine($"   {ItemNames.Display(root.Name),-26} × {root.Quantity}");
            sb.AppendLine();
        }

        sb.AppendLine(Lang.T("shop.base"));
        if (baseMaterials.Count == 0)
            sb.AppendLine(Lang.T("shop.base.none"));
        else
            foreach (var (item, count) in baseMaterials)
                sb.AppendLine($"   {ItemNames.Display(item),-26} × {count}");

        sb.AppendLine();
        sb.AppendLine(Lang.T("shop.total", baseMaterials.Count, totalItems));

        if (withoutRecipe.Count > 0)
            sb.AppendLine(Lang.T("shop.norecipe", string.Join(", ", withoutRecipe.Select(ItemNames.Display))));

        return sb.ToString();
    }
}
