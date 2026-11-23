using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mcrpv;

/// <summary>Uitkomst van een automatische dump-import.</summary>
public sealed class DumpImport
{
    public Dictionary<string, Recipe> Recipes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Receptsleutel → echte itemnaam uit de lang-bestanden.</summary>
    public Dictionary<string, string> Names { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int Files { get; init; }
    public string DumpFolder { get; init; } = "";
    public DateTime Stamp { get; init; }

    /// <summary>True als het inlezen met ✖ STOP is afgebroken.</summary>
    public bool Cancelled { get; init; }
}

/// <summary>
/// Leest een JEIExporter-dump (nameMap.json + recipes/*.json) rechtstreeks uit een
/// Minecraft-instance en maakt daar recipes.json van. Dezelfde conversie als de
/// losse JEIConverter, maar dan in-process zodat de app het automatisch kan doen
/// zodra je een instance kiest.
/// </summary>
public static class RecipeDumpImporter
{
    private static readonly string[] DumpFolderNames =
    {
        "exports", "jei_exports", "dumps", "recipe_dump",
        Path.Combine("config", "jei"), Path.Combine("config", "jei", "dumps"),
        // JEIExporter schrijft standaard naar config/jeiexporter/exports
        Path.Combine("config", "jeiexporter"), Path.Combine("config", "jeiexporter", "exports")
    };

    /// <summary>Mappen die we in een instance afzoeken naar een dump, meest waarschijnlijke eerst.</summary>
    public static IEnumerable<string> CandidateDumpFolders(string instanceRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bases = new List<string>();

        void AddBase(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            string full = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!seen.Add(full)) return;
            bases.Add(full);
        }

        AddBase(instanceRoot);

        // Is dit de game-map zelf (instance/minecraft of instance/.minecraft)? Dan ook
        // de instance-map erboven bekijken, want daar kan de dump ook staan.
        string trimmed = (instanceRoot ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(trimmed);
        if (leaf.Equals("minecraft", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
            AddBase(Path.GetDirectoryName(trimmed));

        // En andersom: een instance-map kan juist een minecraft/-submap hebben.
        for (int i = 0; i < bases.Count; i++)
        {
            AddBase(Path.Combine(bases[i], "minecraft"));
            AddBase(Path.Combine(bases[i], ".minecraft"));
        }

        foreach (var b in bases)
        {
            yield return b;

            foreach (var name in DumpFolderNames)
            {
                string dir = Path.Combine(b, name);
                if (seen.Add(dir)) yield return dir;
            }
        }
    }

    /// <summary>Eerste map in de instance die er als een JEIExporter-dump uitziet.</summary>
    public static string? FindDumpFolder(string instanceRoot)
    {
        if (string.IsNullOrWhiteSpace(instanceRoot)) return null;

        foreach (var dir in CandidateDumpFolders(instanceRoot))
        {
            if (LooksLikeDump(dir)) return dir;
        }

        return null;
    }

    /// <summary>
    /// Streng checken: een instance-map bevat allerlei JSON, dus alleen een nameMap
    /// of een recipes/-map met JSON-bestanden telt als dump.
    /// </summary>
    public static bool LooksLikeDump(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;

            foreach (var map in new[] { "nameMap.json", "namemap.json", "itemMap.json" })
            {
                if (File.Exists(Path.Combine(dir, map))) return true;
            }

            string recipesSub = Path.Combine(dir, "recipes");
            if (Directory.Exists(recipesSub))
            {
                return Directory.GetFiles(recipesSub, "*.json", SearchOption.AllDirectories)
                                .Any(f => !Path.GetFileName(f).Contains("bookmark", StringComparison.OrdinalIgnoreCase));
            }

            // JEIExporter schrijft de recepten als losse bestanden in de export-map:
            // {"category": "SAG Mill", "recipes": [...]}. Naast die bestanden staan
            // er ook items/ en fluids/ met tienduizenden plaatjes, dus alleen de
            // bovenste map bekijken.
            foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly).Take(8))
            {
                if (Path.GetFileName(file).Contains("bookmark", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    using var reader = new StreamReader(file);
                    char[] buffer = new char[2048];
                    int read = reader.Read(buffer, 0, buffer.Length);
                    string head = new(buffer, 0, read);

                    if (head.Contains("\"recipes\"", StringComparison.Ordinal) &&
                        head.Contains("\"category\"", StringComparison.Ordinal))
                        return true;
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// De JSON-bestanden met recepten. Ligt er een recipes/-submap, dan daarin (recursief);
    /// anders de bestanden direct in de dump-map (JEIExporter zet daar ook items/ met
    /// honderdduizenden plaatjes neer — die slaan we zo over).
    /// </summary>
    private static IEnumerable<string> RecipeFiles(string dumpDir)
    {
        string recipesSub = Path.Combine(dumpDir, "recipes");

        return Directory.Exists(recipesSub)
            ? Directory.GetFiles(recipesSub, "*.json", SearchOption.AllDirectories)
            : Directory.GetFiles(dumpDir, "*.json", SearchOption.TopDirectoryOnly);
    }

    /// <summary>Laatste wijzigingstijd van de dump-bestanden (leeg = onbekend).</summary>
    public static DateTime DumpStamp(string dumpDir)
    {
        try
        {
            DateTime stamp = DateTime.MinValue;

            foreach (var file in RecipeFiles(dumpDir))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("nameMap.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("namemap.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("itemMap.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                DateTime t = File.GetLastWriteTime(file);
                if (t > stamp) stamp = t;
            }

            return stamp;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Converteert de dump naar recipes (keys al genormaliseerd).</summary>
    public static DumpImport Import(string dumpDir, Action<string>? progress = null,
                                   CancellationToken cancel = default)
    {
        // Gelijk afgebroken: dan hoeft er niets gelezen te worden.
        if (cancel.IsCancellationRequested)
            return new DumpImport { DumpFolder = dumpDir, Cancelled = true };

        var recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nameMap = LoadNameMap(dumpDir);

        var jsonFiles = RecipeFiles(dumpDir)
                                 .Where(f => !Path.GetFileName(f).Equals("nameMap.json", StringComparison.OrdinalIgnoreCase)
                                          && !Path.GetFileName(f).Equals("namemap.json", StringComparison.OrdinalIgnoreCase)
                                          && !Path.GetFileName(f).Equals("itemMap.json", StringComparison.OrdinalIgnoreCase)
                                          && !Path.GetFileName(f).Contains("bookmark", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(f => Priority(Path.GetFileName(f)))
                                 .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

        DateTime stamp = DateTime.MinValue;
        int processed = 0;

        progress?.Invoke($"{jsonFiles.Count} dump-bestand(en)…");

        bool cancelled = false;

        foreach (var file in jsonFiles)
        {
            if (cancel.IsCancellationRequested) { cancelled = true; break; }

            progress?.Invoke(Path.GetFileName(file));

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file));
                if (node == null) continue;

                ProcessNode(node, recipes, nameMap, displayNames);
                processed++;

                DateTime t = File.GetLastWriteTime(file);
                if (t > stamp) stamp = t;
            }
            catch
            {
                // Eén kapot dump-bestand mag de rest niet tegenhouden.
            }
        }

        return new DumpImport
        {
            Recipes = recipes,
            Names = displayNames,
            Files = processed,
            DumpFolder = dumpDir,
            Stamp = stamp,
            Cancelled = cancelled
        };
    }

    // ===== Parsing (zelfde aanpak als JEIConverter) =====

    private static Dictionary<string, string> LoadNameMap(string dir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string[] possible =
        {
            Path.Combine(dir, "nameMap.json"),
            Path.Combine(dir, "namemap.json"),
            Path.Combine(dir, "itemMap.json"),
            Path.Combine(dir, "recipes", "nameMap.json")
        };

        foreach (var path in possible)
        {
            if (!File.Exists(path)) continue;

            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
                {
                    foreach (var kv in obj)
                    {
                        if (kv.Value != null)
                            map[kv.Key] = kv.Value.ToString() ?? kv.Key;
                    }
                }

                if (map.Count > 0) break;
            }
            catch { }
        }

        // JEIExporter bewaart de échte namen per variant in tooltipMap.json
        // ("thermalfoundation:material:0" → "Pulverized Iron") en de omgekeerde tabel in
        // lookupMap.json. Zonder die twee heet elk subtype van zo'n item "Material".
        LoadStackNames(Path.Combine(dir, "tooltipMap.json"), map);
        LoadStackNames(Path.Combine(dir, "lookupMap.json"), map, reversed: true);

        return map;
    }

    /// <summary>
    /// Leest een id→naam-tabel (of naam→id bij <paramref name="reversed"/>) uit de export.
    /// De bestanden zijn groot, dus we lopen de tekst met een regex af in plaats van er een
    /// complete JSON-boom van te bouwen.
    /// </summary>
    private static void LoadStackNames(string path, Dictionary<string, string> map, bool reversed = false)
    {
        if (!File.Exists(path)) return;

        try
        {
            string text = File.ReadAllText(path);

            foreach (Match match in Regex.Matches(text, "\"(?<key>[^\"]+)\"\\s*:\\s*\"(?<value>[^\"]*)\""))
            {
                string key = match.Groups["key"].Value;
                string value = match.Groups["value"].Value;

                if (key.Length == 0 || value.Length == 0) continue;

                string id = NormalizeStackId(reversed ? value : key);
                if (id.Length == 0 || !id.Contains(':')) continue;

                map.TryAdd(id, (reversed ? key : value).Trim());
            }
        }
        catch
        {
            // Grote of kapotte naam-tabel: dan blijven de basisnamen staan.
        }
    }

    /// <summary>"mod:item:3:1a2b3c4d" → "mod:item:3" (de NBT-hash hoort niet bij het item).</summary>
    private static string NormalizeStackId(string id)
    {
        string s = (id ?? "").Trim();
        if (s.Length == 0) return "";

        var parts = s.Split(':');
        if (parts.Length <= 3) return s;

        return int.TryParse(parts[2], out _) ? parts[0] + ":" + parts[1] + ":" + parts[2]
                                             : parts[0] + ":" + parts[1];
    }

    private static int ProcessNode(JsonNode node, Dictionary<string, Recipe> recipes, Dictionary<string, string> nameMap,
                                   Dictionary<string, string> displayNames)
    {
        // De JEI-tab staat als "category" bovenin het dump-bestand ("Alloy Smelter", "Crafting", …).
        string category = CleanText((node as JsonObject)?["category"]?.ToString() ?? "");

        int added = 0;

        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item != null)
                    added += TryExtractRecipe(item, recipes, nameMap, displayNames, category);
            return added;
        }

        if (node is JsonObject obj)
        {
            if (obj["recipes"] is JsonArray recipesArr)
            {
                foreach (var item in recipesArr)
                    if (item != null)
                        added += TryExtractRecipe(item, recipes, nameMap, displayNames, category);
                return added;
            }

            foreach (var kv in obj)
            {
                if (kv.Value is JsonArray subArr)
                {
                    foreach (var item in subArr)
                        if (item != null)
                            added += TryExtractRecipe(item, recipes, nameMap, displayNames, category);
                }
                else if (kv.Value is JsonObject subObj)
                {
                    added += TryExtractRecipe(subObj, recipes, nameMap, displayNames, category);
                }
            }
        }

        return added;
    }

    private static int TryExtractRecipe(JsonNode node, Dictionary<string, Recipe> recipes, Dictionary<string, string> nameMap,
                                        Dictionary<string, string> displayNames, string category = "")
    {
        if (node is not JsonObject obj) return 0;

        string? outputItem = GetItemId(obj["output"] ?? obj["result"] ?? obj["outputs"] ?? obj["out"] ?? obj["product"]);

        if (outputItem == null && obj["output"] is JsonArray outArr && outArr.Count > 0)
            outputItem = GetItemId(outArr[0]);
        if (outputItem == null && obj["outputs"] is JsonArray outsArr && outsArr.Count > 0)
            outputItem = GetItemId(outsArr[0]);

        if (outputItem == null && obj["slots"] is JsonArray slots)
        {
            foreach (var slot in slots)
            {
                if (slot is JsonObject slotObj && IsOutputSlot(slotObj))
                {
                    outputItem = GetItemId(slotObj["item"] ?? slotObj["ingredient"] ?? slotObj);
                    if (outputItem != null) break;
                }
            }
        }

        // JEIExporter-dump: slots met "in": false zijn de uitvoer, en "amount" zegt
        // hoeveel stuks het recept per beurt maakt (9 tarwe uit één hooibaal, 4 trappen,
        // 3 pompoen). Zonder dat aantal lijkt elk recept 1-op-1 en kiest de boom
        // omgekeerde recepten.
        int outputCount = 1;
        string? slotOutput = OutputStack(obj["ingredientItems"], out outputCount);

        // Smeltery/melter-recepten leveren soms alleen een fluid op (geen item-slot):
        // bewaar die als "item" (molten_iron), anders verdwijnen die hele tabbladen.
        int fluidCount = 1;
        string? fluidOutput = OutputFluid(obj["ingredientFluids"], out fluidCount);
        bool fromFluid = false;

        if (string.IsNullOrWhiteSpace(outputItem))
            outputItem = slotOutput;

        if (string.IsNullOrWhiteSpace(outputItem) && !string.IsNullOrWhiteSpace(fluidOutput))
        {
            outputItem = FluidKey(fluidOutput, nameMap);
            outputCount = fluidCount;
            fromFluid = true;
        }

        if (string.IsNullOrWhiteSpace(outputItem)) return 0;

        string niceName = CleanName(outputItem, nameMap);
        RecordName(displayNames, outputItem, niceName, nameMap);

        // Fluids heten in de boom "Iron (fluid)" zodat ze niet met het echte item te
        // verwarren zijn. Elk recept van hetzelfde fluid komt hier langs, dus de suffix mag
        // er maar één keer bij: anders werd het "Molten Iron (fluid) (fluid) (fluid)".
        if (fromFluid && displayNames.TryGetValue(niceName, out var fluidName) &&
            !fluidName.EndsWith(FluidSuffix, StringComparison.Ordinal))
            displayNames[niceName] = fluidName + FluidSuffix;

        var ingredients = new List<Ingredient>();

        if (obj["slots"] is JsonArray slotArr)
        {
            foreach (var slot in slotArr)
            {
                if (slot is not JsonObject slotObj) continue;
                if (IsOutputSlot(slotObj)) continue;

                string? id = GetItemId(slotObj["item"] ?? slotObj["ingredient"] ?? slotObj);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ingredients.Add(new Ingredient { Item = CleanName(id, nameMap), Count = GetCount(slotObj) });
                }
            }
        }

        var inputNodes = new[] { obj["inputs"], obj["ingredients"], obj["input"], obj["catalysts"] };
        foreach (var inputNode in inputNodes)
        {
            if (inputNode is JsonArray inArr)
            {
                foreach (var ing in inArr)
                {
                    string? id = GetItemId(ing);
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ingredients.Add(new Ingredient { Item = CleanName(id, nameMap), Count = GetCount(ing) });
                    }
                }
            }
            else if (inputNode is JsonObject inObj)
            {
                string? id = GetItemId(inObj);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ingredients.Add(new Ingredient { Item = CleanName(id, nameMap), Count = GetCount(inObj) });
                }
            }
        }

        // JEIExporter-dump: "ingredientItems" met "in": true is de invoer.
        foreach (var slot in Slots(obj["ingredientItems"], wantInput: true))
        {
            if (slot["stacks"] is not JsonArray stacks || stacks.Count == 0) continue;

            string? id = GetItemId(stacks[0]);
            if (string.IsNullOrWhiteSpace(id)) continue;

            ingredients.Add(new Ingredient { Item = CleanName(id, nameMap), Count = GetCount(slot) });
        }

        // Fluids als ingrediënt (casting: gesmolten metaal + cast). Zonder dit vallen alle
        // castrecepten per metaal samen tot één recept en zie je de smeltery-keten niet.
        foreach (var slot in FluidSlots(obj["ingredientFluids"], wantInput: true))
        {
            string? id = FluidId(slot);
            if (string.IsNullOrWhiteSpace(id)) continue;

            ingredients.Add(new Ingredient { Item = FluidKey(id, nameMap), Count = FluidUnits(GetCount(slot)) });
        }

        if (ingredients.Count == 0) return 0;

        var merged = ingredients
            .GroupBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Ingredient { Item = g.Key, Count = g.Sum(x => x.Count) })
            .Where(i => !string.IsNullOrWhiteSpace(i.Item))
            .ToList();

        if (merged.Count == 0) return 0;

        var recipe = new Recipe { Category = category, Ingredients = merged, Output = Math.Max(1, outputCount) };

        // Eerste recept wint (crafting gaat voor op smelting en machines). Komt hetzelfde
        // item later uit een andere JEI-tab, dan bewaren we die als alternatief, zodat je
        // in de bracket kunt kiezen welke machine het maakt.
        if (!recipes.TryGetValue(niceName, out var existing))
        {
            recipes[niceName] = recipe;
            return 1;
        }

        if (SameRecipe(existing, recipe)) return 0;
        if (existing.Alternatives.Any(a => SameRecipe(a, recipe))) return 0;

        if (existing.Category.Length == 0) existing.Category = category;
        existing.Alternatives.Add(recipe);

        return 0;
    }

    private static bool SameRecipe(Recipe a, Recipe b)
    {
        if (!string.Equals(a.Category, b.Category, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.Ingredients.Count != b.Ingredients.Count) return false;

        var left = a.Ingredients.OrderBy(i => i.Item, StringComparer.OrdinalIgnoreCase).ToList();
        var right = b.Ingredients.OrderBy(i => i.Item, StringComparer.OrdinalIgnoreCase).ToList();

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Item, right[i].Item, StringComparison.OrdinalIgnoreCase)) return false;
            if (left[i].Count != right[i].Count) return false;
        }

        return true;
    }

    private static string? GetItemId(JsonNode? node)
    {
        if (node == null) return null;

        if (node is JsonValue val)
        {
            string s = val.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(s) || s == "null") return null;
            return CleanRawId(s);
        }

        if (node is JsonObject obj)
        {
            string[] candidates = { "item", "id", "name", "registry_name", "registryName", "ingredient", "stack" };
            foreach (var c in candidates)
            {
                if (obj[c] != null)
                {
                    string? v = obj[c]?.ToString();
                    if (!string.IsNullOrWhiteSpace(v) && v != "null")
                        return CleanRawId(v);
                }
            }

            if (obj["stacks"] is JsonArray stacks && stacks.Count > 0)
                return GetItemId(stacks[0]);

            if (obj["value"] != null) return GetItemId(obj["value"]);
            if (obj["content"] != null) return GetItemId(obj["content"]);
        }

        return null;
    }

    private static int GetCount(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["count"] != null && int.TryParse(obj["count"]?.ToString(), out int c) && c > 0) return c;
            if (obj["amount"] != null && int.TryParse(obj["amount"]?.ToString(), out int a) && a > 0) return a;
            if (obj["Count"] != null && int.TryParse(obj["Count"]?.ToString(), out int big) && big > 0) return big;
        }

        return 1;
    }

    private static bool IsOutputSlot(JsonObject slot)
    {
        string role = (slot["role"] ?? slot["type"] ?? slot["kind"] ?? "").ToString()?.ToLowerInvariant() ?? "";
        return role.Contains("output") || role.Contains("result") || role.Contains("product");
    }

    /// <summary>Slots uit een JEIExporter-lijst (ingredientItems) met de gevraagde in/uit-vlag.</summary>
    private static IEnumerable<JsonObject> Slots(JsonNode? node, bool wantInput)
    {
        if (node is not JsonArray arr) yield break;

        foreach (var slot in arr)
        {
            if (slot is not JsonObject obj) continue;
            if (!TryGetInputFlag(obj, out bool isInput) || isInput != wantInput) continue;

            yield return obj;
        }
    }

    private static bool TryGetInputFlag(JsonObject slot, out bool isInput)
    {
        isInput = true;

        if (slot["in"] is not JsonValue value) return false;

        string raw = value.ToString();
        if (bool.TryParse(raw, out bool parsed))
        {
            isInput = parsed;
            return true;
        }

        if (raw == "1" || raw == "0")
        {
            isInput = raw == "1";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Onthoudt de echte itemnaam (uit de lang-bestanden) bij een receptsleutel. Alleen
    /// voor de uitvoer van een recept: ingrediënten kunnen dezelfde sleutelnaam uit een
    /// andere mod hebben (chisel:diamond vs minecraft:diamond) en zouden hem kapen.
    /// </summary>
    private static void RecordName(Dictionary<string, string> names, string? rawId, string key,
                                   Dictionary<string, string> nameMap)
    {
        if (string.IsNullOrWhiteSpace(rawId) || string.IsNullOrWhiteSpace(key)) return;
        if (names.ContainsKey(key)) return;

        // De naam van precies deze variant gaat voor: "Pulverized Iron" in plaats van de
        // basisnaam "Material" die de lang-bestanden voor het hele item geven.
        foreach (var candidate in NameCandidates(rawId))
        {
            if (!nameMap.TryGetValue(candidate, out var variant) || string.IsNullOrWhiteSpace(variant)) continue;

            names[key] = CleanText(variant);
            return;
        }

        string? nice = ItemNames.NameForId(rawId);
        if (!string.IsNullOrWhiteSpace(nice)) names[key] = CleanText(nice);
    }

    /// <summary>
    /// Het uitvoer-slot van een JEIExporter-recept: de stack plus het aantal per beurt.
    /// </summary>
    private static string? OutputStack(JsonNode? slots, out int amount)
    {
        amount = 1;

        foreach (var slot in Slots(slots, wantInput: false))
        {
            if (slot["stacks"] is not JsonArray stacks || stacks.Count == 0) continue;

            string? id = GetItemId(stacks[0]);
            if (string.IsNullOrWhiteSpace(id)) continue;

            amount = Math.Max(1, GetCount(slot));
            return id;
        }

        return null;
    }

    /// <summary>Achtervoegsel waarmee een fluid in de boom van een echt item te onderscheiden is.</summary>
    private const string FluidSuffix = " (fluid)";

    /// <summary>Fluids krijgen een eigen sleutel ("fluid_iron"), zodat ze nooit een echt item kapen.</summary>
    private static string FluidKey(string rawId, Dictionary<string, string> nameMap) =>
        "fluid_" + CleanName(rawId, nameMap);

    /// <summary>Fluid-slots uit een JEIExporter-lijst (key "fluids" in plaats van "stacks").</summary>
    private static IEnumerable<JsonObject> FluidSlots(JsonNode? node, bool wantInput)
    {
        if (node is not JsonArray arr) yield break;

        foreach (var slot in arr)
        {
            if (slot is not JsonObject obj) continue;
            if (!TryGetInputFlag(obj, out bool isInput) || isInput != wantInput) continue;

            yield return obj;
        }
    }

    private static string? FluidId(JsonObject slot)
    {
        if (slot["fluids"] is not JsonArray fluids || fluids.Count == 0) return null;

        string? id = GetItemId(fluids[0]);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>Het fluid dat dit recept oplevert (melter/smeltery), of null.</summary>
    private static string? OutputFluid(JsonNode? slots, out int amount)
    {
        amount = 1;

        foreach (var slot in FluidSlots(slots, wantInput: false))
        {
            string? id = FluidId(slot);
            if (string.IsNullOrWhiteSpace(id)) continue;

            amount = FluidUnits(GetCount(slot));
            return id;
        }

        return null;
    }

    /// <summary>Fluidhoeveelheden omgerekend naar eenheden van 144 mB (één ingot), minimaal 1.</summary>
    private static int FluidUnits(int milliBuckets) =>
        Math.Max(1, (int)Math.Round((milliBuckets <= 0 ? 144 : milliBuckets) / 144.0));

    /// <summary>Eerste item-stack van de eerste passende slot (JEIExporter-lijst).</summary>
    private static string? SlotStack(JsonNode? slots, bool wantInput)
    {
        foreach (var slot in Slots(slots, wantInput))
        {
            if (slot["stacks"] is not JsonArray stacks || stacks.Count == 0) continue;

            string? id = GetItemId(stacks[0]);
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }

        return null;
    }

    private static string CleanRawId(string raw)
    {
        raw = raw.Trim().Trim('"', '\'', ' ', '\t');

        // JEIExporter noemt fluids "fluid:water" / "fluid:tconstruct:molten_iron".
        if (raw.StartsWith("fluid:", StringComparison.OrdinalIgnoreCase))
            raw = raw[6..];

        int brace = raw.IndexOf('{');
        if (brace > 0) raw = raw[..brace];

        // "minecraft:stone 1 0" (id damage count) → "minecraft:stone"
        raw = Regex.Replace(raw, @"(?:\s+\d+)+$", "");

        if (raw.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
            raw = raw[5..];

        // "mod:item:0" blijft "mod:item:0": juist die metadata bepaalt hoe het item heet
        // (material:4567 = "Poke Ingot", material:0 = "Material"). Een NBT-hash of aantal
        // erachter vervalt wel.
        var parts = raw.Split(':');
        if (parts.Length > 2)
        {
            raw = parts[0] + ":" + parts[1];
            if (int.TryParse(parts[2].Trim(), out _)) raw += ":" + parts[2].Trim();
        }

        return raw.Trim();
    }

    private static string CleanName(string id, Dictionary<string, string> nameMap)
    {
        // Eerst de naam van precies deze variant (mod:item:meta): items met metadata heten
        // per variant anders ("material:4567" is "Poke Ingot", niet "Material"). Pas als
        // die ontbreekt zakken we naar de basisnaam.
        foreach (var candidate in NameCandidates(id))
        {
            if (nameMap.TryGetValue(candidate, out var nice) && !string.IsNullOrWhiteSpace(nice))
                return Sanitize(CleanText(nice));
        }

        // De lang-bestanden kennen varianten vaak ook (item.mod.material.ingotCopper.name).
        foreach (var candidate in NameCandidates(id))
        {
            if (ItemNames.NameForId(candidate) is { Length: > 0 } lang)
                return Sanitize(CleanText(lang));
        }

        string tail = id.Contains(':') ? id[(id.LastIndexOf(':') + 1)..] : id;
        return Sanitize(CleanText(tail));
    }

    /// <summary>
    /// tooltipMap.json bevat de naam zoals JEI hem toont, dus met kleur-/opmaakcodes
    /// ("§7Patient Bag", "§cSupremium Coal"). Die horen niet in een itemnaam.
    /// </summary>
    private static string CleanText(string raw)
    {
        // De kleurcode van de tooltipMap komt soms als vervangingsteken (U+FFFD) binnen
        // in plaats van §; dan blijft de codeletter ("6Multi Helmet") staan.
        string name = Regex.Replace(raw ?? "", @"[\u00A7\uFFFD].", "");
        name = name.Replace('\u00A7', ' ').Replace('\uFFFD', ' ');
        return Regex.Replace(name, @"\s+", " ").Trim();
    }

    /// <summary>"mod:item:4567" → ook "mod:item" en "item", in die volgorde.</summary>
    private static IEnumerable<string> NameCandidates(string id)
    {
        string full = (id ?? "").Trim();
        if (full.Length == 0) yield break;

        yield return full;

        var parts = full.Split(':');
        if (parts.Length > 2)
        {
            yield return parts[0] + ":" + parts[1];
            yield return parts[1];
        }
        else if (parts.Length == 2)
        {
            yield return parts[1];
        }
    }

    private static string Sanitize(string name)
    {
        name = name.ToLowerInvariant();
        name = Regex.Replace(name, @"[^a-z0-9_]", "_");
        name = Regex.Replace(name, @"_+", "_").Trim('_');
        return string.IsNullOrEmpty(name) ? "unknown" : name;
    }

    // Crafting en smelting eerst, dan machines, dan de rest.
    private static int Priority(string filename)
    {
        filename = filename.ToLowerInvariant();

        if (filename.Contains("crafting") || filename.Contains("workbench") ||
            filename.Contains("shaped") || filename.Contains("shapeless"))
            return 0;

        if (filename.Contains("furnace") || filename.Contains("smelting") || filename.Contains("smelt"))
            return 1;

        if (filename.Contains("alloy") || filename.Contains("infuser") || filename.Contains("crusher"))
            return 2;

        return 10;
    }
}
