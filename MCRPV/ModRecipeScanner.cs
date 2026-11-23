using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mcrpv;

/// <summary>Eén mod uit de instance, zoals hij in de mods-database staat.</summary>
public sealed class InstanceMod
{
    public string Jar { get; set; } = "";
    public string ModId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    /// <summary>Aantal recept-JSON's in de jar (1.13+ packs; 1.12 heeft ze in de code).</summary>
    public int JsonRecipes { get; set; }

    /// <summary>Aantal CraftTweaker-scripts ín de jar.</summary>
    public int Scripts { get; set; }

    public string Caption =>
        (DisplayName.Length > 0 ? DisplayName : Path.GetFileNameWithoutExtension(Jar)) +
        (Version.Length > 0 ? " " + Version : "");
}

/// <summary>
/// Index van de mods van één instance: welk jar hoort bij welke mod en hoeveel recepten
/// erin zitten. Wordt als mods.json bewaard, zodat een tweede scan niet alle 271 jars
/// opnieuw hoeft te openen (alleen nieuwe of gewijzigde jars worden gelezen).
/// </summary>
public sealed class ModDatabase
{
    public string InstanceRoot { get; set; } = "";
    public DateTime BuiltAt { get; set; }
    public List<InstanceMod> Mods { get; set; } = new();

    public int JarRecipes => Mods.Sum(m => m.JsonRecipes);
    public int ScriptJars => Mods.Count(m => m.Scripts > 0);
    public int RecipeJars => Mods.Count(m => m.JsonRecipes > 0);

    public static ModDatabase Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ModDatabase();
            return JsonSerializer.Deserialize<ModDatabase>(File.ReadAllText(path)) ?? new ModDatabase();
        }
        catch
        {
            return new ModDatabase();
        }
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Een leesbare map is niet erg: de volgende scan bouwt de index opnieuw op.
        }
    }
}

/// <summary>Uitkomst van een mods-scan.</summary>
public sealed class ModScanResult
{
    public Dictionary<string, Recipe> Recipes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Names { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int Mods { get; init; }
    public int ModsWithRecipes { get; init; }
    public int JarRecipes { get; init; }
    public int ScriptRecipes { get; init; }

    /// <summary>Aantal recepten uit datapacks (instance/datapacks, openloader, kubejs/data).</summary>
    public int DatapackRecipes { get; init; }

    public int Removed { get; init; }
    public int Added { get; init; }

    /// <summary>Aantal ore-/tag-ingrediënten dat naar een echt item is omgezet.</summary>
    public int ResolvedGroups { get; init; }
    public List<string> ScriptSets { get; init; } = new();
    public TimeSpan Duration { get; init; }
    public string? Error { get; init; }

    /// <summary>True als de gebruiker de scan met ✖ STOP heeft afgebroken: geen fout, niets overgenomen.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Volledige foutmelding (met stack) voor het scanrapport.</summary>
    public string? Diagnostics { get; init; }

    public string Summary
    {
        get
        {
            if (Error != null) return "scan mislukt: " + Error;

            var sb = new StringBuilder();
            sb.Append($"{Recipes.Count} items · {Added} recepten uit {Mods} mods");

            if (JarRecipes > 0) sb.Append($" ({JarRecipes} uit jar-JSON, {ModsWithRecipes} mods)");
            if (DatapackRecipes > 0) sb.Append($" · {DatapackRecipes} uit datapacks");
            if (ScriptRecipes > 0) sb.Append($" · {ScriptRecipes} uit scripts");
            if (Removed > 0) sb.Append($" · {Removed} weggehaald");
            if (ResolvedGroups > 0) sb.Append($" · {ResolvedGroups} ore-groepen opgelost");
            if (ScriptSets.Count > 0) sb.Append(" · scripts: " + string.Join(", ", ScriptSets));
            sb.Append($" · {Duration.TotalSeconds:0.0}s");

            return sb.ToString();
        }
    }
}

/// <summary>
/// Leest recepten rechtstreeks uit de instance in plaats van uit een JEIExporter-dump:
///   • recept-JSON's in de mod-jars (data/&lt;mod&gt;/recipes/**/*.json, 1.13+ stijl);
///   • CraftTweaker-scripts (scripts/**/*.zs én .zs-bestanden in jars);
///   • KubeJS-scripts (kubejs/server_scripts/**/*.js).
/// Verwijderingen (recipes.remove, event.remove) halen recepten van mods weg; wat een
/// script zelf toevoegt blijft staan. Zo werken de pack-correcties zoals in-game.
/// </summary>
public static class ModRecipeScanner
{
    /// <summary>Pad van een recept in een jar of datapack: 1.13+ "recipes", 1.21+ "recipe".</summary>
    private const string RecipePathPattern = @"^data/[^/]+/recipes?/.+\.json$";

    private static readonly string[] RecipeMethods =
    {
        "addShaped", "addShapeless", "addSmelting", "addFurnace", "addBlasting", "addSmithing",
        "removeShaped", "removeShapeless", "remove", "removeByRecipeName", "removeByModid"
    };

    // ========== Mappen ==========

    /// <summary>Game-map van de instance (instance/minecraft of instance/.minecraft).</summary>
    public static string? GameFolder(string? instanceRoot)
    {
        if (string.IsNullOrWhiteSpace(instanceRoot) || !Directory.Exists(instanceRoot)) return null;

        foreach (var sub in new[] { "minecraft", ".minecraft" })
        {
            string dir = Path.Combine(instanceRoot, sub);
            if (Directory.Exists(Path.Combine(dir, "mods"))) return dir;
        }

        return Directory.Exists(Path.Combine(instanceRoot, "mods")) ? instanceRoot : null;
    }

    /// <summary>
    /// De script-mappen van deze instance. Project Ozone 3 heeft per pack-mode een eigen
    /// map (normal/titan/kappa/mythic) plus all_modes: die mogen niet door elkaar lopen.
    /// </summary>
    public static List<string> ScriptFolders(string? instanceRoot)
    {
        var result = new List<string>();
        string? game = GameFolder(instanceRoot);
        if (game == null) return result;

        string scripts = Path.Combine(game, "scripts");
        if (Directory.Exists(scripts))
        {
            // Losse .zs-bestanden direct in scripts/ (bijv. recipes.zs)
            if (Directory.GetFiles(scripts, "*.zs", SearchOption.TopDirectoryOnly).Length > 0)
                result.Add(scripts);

            foreach (var dir in Directory.GetDirectories(scripts).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(".") || name.Equals("debug", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.GetFiles(dir, "*.zs", SearchOption.AllDirectories).Length == 0) continue;
                result.Add(dir);
            }
        }

        string kube = Path.Combine(game, "kubejs", "server_scripts");
        if (Directory.Exists(kube) && Directory.GetFiles(kube, "*.js", SearchOption.AllDirectories).Length > 0)
            result.Add(kube);

        string kubeData = Path.Combine(game, "kubejs", "data");
        if (Directory.Exists(kubeData)) result.Add(kubeData);

        return result;
    }

    /// <summary>Mooie naam voor een script-map ("all_modes" → "all_modes", kubejs → "KubeJS").</summary>
    public static string FolderLabel(string folder) =>
        folder.EndsWith(Path.Combine("kubejs", "server_scripts"), StringComparison.OrdinalIgnoreCase) ||
        folder.EndsWith(Path.Combine("kubejs", "data"), StringComparison.OrdinalIgnoreCase)
            ? "KubeJS " + Path.GetFileName(folder)
            : Path.GetFileName(folder);

    // ========== Database ==========

    /// <summary>Leest de mods-database, of werkt hem bij voor de gewijzigde jars.</summary>
    public static ModDatabase BuildDatabase(string instanceRoot, string databasePath, Action<string>? progress = null,
                                           CancellationToken cancel = default)
    {
        string? game = GameFolder(instanceRoot) ?? throw new DirectoryNotFoundException("Geen mods-map gevonden in deze instance");
        var cached = ModDatabase.Load(databasePath);

        var byJar = cached.Mods.ToDictionary(m => m.Jar, StringComparer.OrdinalIgnoreCase);
        var database = new ModDatabase { InstanceRoot = instanceRoot, BuiltAt = DateTime.Now };

        var jars = Directory.GetFiles(Path.Combine(game, "mods"), "*.jar", SearchOption.AllDirectories)
                            .OrderBy(j => Path.GetFileName(j), StringComparer.OrdinalIgnoreCase)
                            .ToList();

        int done = 0;
        foreach (var jar in jars)
        {
            if (cancel.IsCancellationRequested) break;

            var info = new FileInfo(jar);
            string name = info.Name;

            if (byJar.TryGetValue(name, out var known) && known.Size == info.Length && known.Modified == info.LastWriteTime)
            {
                database.Mods.Add(known);
                done++;
                continue;
            }

            database.Mods.Add(ReadJar(info));
            done++;

            if (done % 25 == 0) progress?.Invoke($"mods lezen… {done}/{jars.Count}");
        }

        // Bij ✖ STOP bewaren we de half gevulde tabel niet: de volgende scan begint dan
        // gewoon opnieuw met dezelfde cache van voor de scan.
        if (!cancel.IsCancellationRequested) database.Save(databasePath);
        return database;
    }

    internal static InstanceMod ReadJar(FileInfo jar)
    {
        var mod = new InstanceMod
        {
            Jar = jar.Name,
            Size = jar.Length,
            Modified = jar.LastWriteTime
        };

        try
        {
            using var zip = ZipFile.OpenRead(jar.FullName);
            var names = zip.Entries.Select(e => e.FullName).ToList();

            // 1.13+ gebruikt data/<mod>/recipes/, 1.21+ data/<mod>/recipe/.
            mod.JsonRecipes = names.Count(n => Regex.IsMatch(n, RecipePathPattern, RegexOptions.IgnoreCase));
            mod.Scripts = names.Count(n => n.EndsWith(".zs", StringComparison.OrdinalIgnoreCase));

            var meta = zip.GetEntry("mcmod.info");
            if (meta != null)
            {
                string json = ReadEntry(meta);
                try
                {
                    var node = JsonNode.Parse(json);
                    var first = node is JsonArray arr ? arr.FirstOrDefault() : node;
                    if (first is JsonObject obj)
                    {
                        mod.ModId = obj["modid"]?.ToString() ?? "";
                        mod.DisplayName = obj["name"]?.ToString() ?? "";
                        mod.Version = obj["version"]?.ToString() ?? "";
                    }
                }
                catch
                {
                    // Kapotte mcmod.info komt vaak voor; dan blijft de bestandsnaam over.
                    var id = Regex.Match(json, "\"modid\"\\s*:\\s*\"([^\"]+)\"");
                    if (id.Success) mod.ModId = id.Groups[1].Value;
                    var nm = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                    if (nm.Success) mod.DisplayName = nm.Groups[1].Value;
                }
            }

            if (mod.ModId.Length == 0)
            {
                var toml = zip.GetEntry("META-INF/mods.toml") ?? zip.GetEntry("META-INF/neoforge.mods.toml");
                if (toml != null)
                {
                    string text = ReadEntry(toml);
                    var id = Regex.Match(text, @"modId\s*=\s*""([^""]+)""");
                    if (id.Success) mod.ModId = id.Groups[1].Value;
                    var ver = Regex.Match(text, @"version\s*=\s*""([^""]+)""");
                    if (ver.Success) mod.Version = ver.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Beschadigde jar: hij telt mee in de lijst, maar levert geen recepten.
        }

        return mod;
    }

    /// <summary>Resultaat voor een scan die met ✖ STOP is afgebroken.</summary>
    private static ModScanResult Stopped(DateTime started) =>
        new() { Cancelled = true, Duration = DateTime.Now - started };

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ========== Scannen ==========

    /// <summary>
    /// Scant de instance. <paramref name="baseRecipes"/> (de JEI-dump of de meegeleverde
    /// set) blijft de basis; scripts en mod-JSON's vullen aan en overschrijven.
    /// </summary>
    public static ModScanResult Scan(string instanceRoot, IReadOnlyCollection<string> scriptFolders,
                                     string databasePath, IDictionary<string, Recipe>? baseRecipes = null,
                                     Action<string>? progress = null, CancellationToken cancel = default)
    {
        var started = DateTime.Now;

        // ✖ STOP: meteen klaar, zonder ook maar iets van de instance aan te raken.
        if (cancel.IsCancellationRequested) return Stopped(started);
        var recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (baseRecipes != null)
            {
                foreach (var kv in baseRecipes)
                    recipes[kv.Key] = Clone(kv.Value);
            }

            progress?.Invoke("mods indexeren…");
            var database = BuildDatabase(instanceRoot, databasePath, progress, cancel);
            if (cancel.IsCancellationRequested) return Stopped(started);

            // 1. Recept-JSON's uit de jars (1.13+ packs; PO3 is 1.12 en heeft er geen).
            string? game = GameFolder(instanceRoot);
            int jarRecipes = 0;

            foreach (var mod in database.Mods.OrderByDescending(m => m.JsonRecipes + m.Scripts))
            {
                if (cancel.IsCancellationRequested) return Stopped(started);
                if (mod.JsonRecipes == 0 && mod.Scripts == 0) continue;

                progress?.Invoke($"lezen: {mod.Jar}");
                jarRecipes += ReadJarRecipes(Path.Combine(game!, "mods"), mod, recipes, names);
            }

            // 1b. Datapacks (1.16+): instance/datapacks, OpenLoader en kubejs/data. Nieuwe
            //     Minecraft-versies hebben geen JEIExporter, dus dit is daar de bron.
            int datapackRecipes = ReadDatapackRecipes(game!, recipes, names, progress);

            // 2. Scripts: CraftTweaker (.zs) en KubeJS (.js).
            var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scriptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int scriptRecipes = 0;
            int scriptFiles = 0;

            foreach (var folder in scriptFolders)
            {
                if (!Directory.Exists(folder)) continue;

                var files = Directory.GetFiles(folder, "*.zs", SearchOption.AllDirectories)
                                     .Concat(Directory.GetFiles(folder, "*.js", SearchOption.AllDirectories))
                                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                     .ToList();

                foreach (var file in files)
                {
                    if (cancel.IsCancellationRequested) return Stopped(started);

                    progress?.Invoke($"script: {Path.GetFileName(file)}");
                    string text;
                    try { text = File.ReadAllText(file); }
                    catch { continue; }

                    scriptFiles++;
                    scriptRecipes += file.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                        ? ParseKubeJs(text, recipes, names, removals, scriptKeys)
                        : ParseCraftTweaker(text, recipes, names, removals, scriptKeys);
                }
            }

            // 3. Verwijderingen toepassen. Wat een script zélf toevoegt blijft staan,
            //    zodat een pack dat een recept vervangt niet meteen leegloopt.
            int removed = 0;
            foreach (var key in removals)
            {
                if (scriptKeys.Contains(key)) continue;
                if (recipes.Remove(key)) removed++;
            }

            // 4. Ore-dictionary- en tag-ingrediënten naar een echt item vertalen, zodat de
            //    boom geen "ore:ingotIron"-knopen meer toont.
            int resolvedGroups = ResolveGroupIngredients(recipes, names);

            return new ModScanResult
            {
                Recipes = recipes,
                Names = names,
                Mods = database.Mods.Count,
                ModsWithRecipes = database.RecipeJars,
                JarRecipes = jarRecipes,
                DatapackRecipes = datapackRecipes,
                ScriptRecipes = scriptRecipes,
                Removed = removed,
                Added = jarRecipes + datapackRecipes + scriptRecipes,
                ResolvedGroups = resolvedGroups,
                ScriptSets = scriptFolders.Select(FolderLabel).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Duration = DateTime.Now - started
            };
        }
        catch (Exception ex)
        {
            return new ModScanResult
            {
                Recipes = recipes,
                Names = names,
                Mods = 0,
                Error = ex.Message,
                Diagnostics = ex.ToString(),
                Duration = DateTime.Now - started
            };
        }
    }

    /// <summary>
    /// Voegt één recept samen met dezelfde regels als een scan (varianten blijven plat).
    /// De index gebruikt dit om shards tot de basis te combineren.
    /// </summary>
    internal static void MergeInto(Dictionary<string, Recipe> recipes, Dictionary<string, string> names,
                                   string key, Recipe recipe, string source = "", string patchId = "")
    {
        AddRecipe(recipes, names, key, recipe.Category, recipe.Ingredients, recipe.Output, source, patchId);

        foreach (var alternative in recipe.Alternatives)
            AddRecipe(recipes, names, key, alternative.Category, alternative.Ingredients, alternative.Output,
                      source, patchId);
    }

    // ========== Ore-dictionary en tags ==========

    /// <summary>Is dit een groepsaanduiding (ore-dictionary of tag) in plaats van een item?</summary>
    private static bool IsGroupKey(string key) =>
        key.StartsWith("ore:", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("tag:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Vervangt alle ore-/tag-ingrediënten door een concreet item uit de dataset.
    /// "ore:ingotIron" wordt "iron_ingot", "forge:storage_blocks/iron" wordt "iron_block".
    /// </summary>
    public static int ResolveGroupIngredients(Dictionary<string, Recipe> recipes, Dictionary<string, string> names,
                                              HashSet<string>? only = null)
    {
        var known = new HashSet<string>(recipes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var key in names.Keys) known.Add(key);
        known.RemoveWhere(IsGroupKey);

        // "clumposmium" naast "clump_osmium": zonder streepjes vergelijken vangt dat af.
        var compact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in known.OrderBy(k => k.Length))
            compact.TryAdd(key.Replace("_", ""), key);

        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int resolved = 0;

        // Alleen de recepten die deze ronde veranderden doorlopen; de rest is al eerder
        // opgelost (dump en basis) en dat scheelt bij 14 000 recepten seconden werk.
        foreach (var kv in recipes)
            if (only == null || only.Contains(kv.Key))
                resolved += RewriteIngredients(kv.Value, known, compact, names, cache);

        // De "ore ingot Iron"-namen horen na het oplossen niet meer in de lijst.
        foreach (var key in names.Keys.Where(IsGroupKey).ToList()) names.Remove(key);

        return resolved;
    }

    private static int RewriteIngredients(Recipe recipe, HashSet<string> known, Dictionary<string, string> compact,
                                          Dictionary<string, string> names, Dictionary<string, string?> cache)
    {
        int resolved = 0;
        var merged = new Dictionary<string, Ingredient>(StringComparer.OrdinalIgnoreCase);

        foreach (var ingredient in recipe.Ingredients)
        {
            string key = ingredient.Item;

            if (IsGroupKey(key))
            {
                string? target = ResolveGroup(key, known, compact, cache);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    key = target;
                    resolved++;
                    RecordName(names, "minecraft:" + target, target);
                }
            }

            if (key.Length == 0) continue;

            if (merged.TryGetValue(key, out var existing))
                existing.Count += ingredient.Count;
            else
                merged[key] = new Ingredient { Item = key, Count = ingredient.Count };
        }

        recipe.Ingredients = merged.Values.ToList();

        foreach (var alternative in recipe.Alternatives)
            resolved += RewriteIngredients(alternative, known, compact, names, cache);

        return resolved;
    }

    /// <summary>Soort-woorden waarmee een ore-dictionarynaam kan beginnen.</summary>
    private static readonly string[] OreTypePrefixes =
    {
        "storage_block", "storageblock", "block", "ingot", "nugget", "dust", "gem", "ore",
        "log", "plank", "stick", "chest", "dye", "crop", "seed", "crystal", "rod", "gear",
        "plate", "string", "wool", "glass", "sand", "stone", "sapling", "leaves", "flower",
        "coal", "quartz", "item", "food"
    };

    /// <summary>"ore:ingotIron" → ("ingot", "iron"); "tag:forge:storage_blocks_iron" → ("storage_blocks", "iron").</summary>
    private static (string Type, string Material) SplitGroup(string groupKey)
    {
        string body = groupKey;

        if (body.StartsWith("ore:", StringComparison.OrdinalIgnoreCase))
        {
            body = body[4..];

            // CamelCase: het eerste woord is de soort, de rest het materiaal.
            var camel = Regex.Split(body, "(?<=[a-z0-9])(?=[A-Z])")
                             .Where(t => t.Length > 0)
                             .ToList();

            if (camel.Count > 1)
                return (camel[0].ToLowerInvariant(), string.Join("_", camel.Skip(1)).ToLowerInvariant());

            // De receptsleutel is lowercase gemaakt ("ingotiron"), dus herkennen we de
            // soort aan het begin — langste treffer eerst ("storage_block" vóór "block").
            string lower = body.ToLowerInvariant();
            foreach (var prefix in OreTypePrefixes.OrderByDescending(p => p.Length))
            {
                if (lower.StartsWith(prefix, StringComparison.Ordinal) && lower.Length > prefix.Length)
                    return (prefix, lower[prefix.Length..]);
            }

            return (lower, "");
        }

        body = body[4..];
        if (body.StartsWith("forge:", StringComparison.OrdinalIgnoreCase)) body = body[6..];
        body = body.Replace('/', '_');

        var parts = body.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ("", "");
        if (parts.Length == 1) return (parts[0].ToLowerInvariant(), "");

        return (string.Join("_", parts.Take(parts.Length - 1)).ToLowerInvariant(), parts[^1].ToLowerInvariant());
    }

    private static string Singular(string type)
    {
        string t = type.ToLowerInvariant();

        if (t.StartsWith("storage_block", StringComparison.Ordinal)) return "block";
        if (t.EndsWith("ss", StringComparison.Ordinal)) return t;
        if (t.Length > 3 && t.EndsWith("s", StringComparison.Ordinal)) return t[..^1];

        return t;
    }

    /// <summary>Kandidaat-items voor een groep, meest waarschijnlijke eerst.</summary>
    private static IEnumerable<string> CandidateKeys(string type, string material)
    {
        string t = Singular(type);
        var list = new List<string>();

        if (t == "block" && material.Length > 0) list.Add(material + "_block");

        if (material.Length > 0)
        {
            list.Add(material + "_" + t);
            list.Add(t + "_" + material);
            list.Add(material);
        }

        if (t.Length > 0) list.Add(t);

        return list.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Het item dat deze groep vertegenwoordigt. Geeft altijd iets terug, zodat er
    /// nooit een ore:- of tag:-knoop in de boom overblijft.</summary>
    private static string? ResolveGroup(string groupKey, HashSet<string> known, Dictionary<string, string> compact,
                                        Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(groupKey, out var cached)) return cached;

        var (type, material) = SplitGroup(groupKey);
        string t = Singular(type);
        string? result = null;

        foreach (var candidate in CandidateKeys(type, material))
        {
            if (known.Contains(candidate))
            {
                result = candidate;
                break;
            }
        }

        // Zonder streepjes zoeken: "clumposmium" → "clump_osmium".
        if (result == null)
        {
            foreach (var candidate in new[] { material + t, t + material, material + "_" + t, t + "_" + material })
            {
                string squeezed = candidate.Replace("_", "");
                if (squeezed.Length > 0 && compact.TryGetValue(squeezed, out var hit))
                {
                    result = hit;
                    break;
                }
            }
        }

        // Aaneengeschreven namen in twee woorden knippen: "alloybasic" → "basic_alloy".
        if (result == null && material.Length == 0 && t.Length > 5)
        {
            for (int i = 2; i < t.Length - 1; i++)
            {
                string first = t[..i];
                string second = t[i..];

                if (known.Contains(first + "_" + second)) { result = first + "_" + second; break; }
                if (known.Contains(second + "_" + first)) { result = second + "_" + first; break; }
            }
        }

        // Nog niets: zoek een bekend item dat beide woorden bevat ("plankWood" →
        // "wood_planks"). Kortste naam eerst, dat is meestal de basisvariant.
        if (result == null && material.Length > 0)
        {
            result = known.Where(k => k.Contains(material, StringComparison.OrdinalIgnoreCase) &&
                                      t.Length > 1 && k.Contains(t, StringComparison.OrdinalIgnoreCase))
                          .OrderBy(k => k.Length)
                          .FirstOrDefault();
        }

        // Laatste redmiddel: een nette itemnaam verzinnen. Liever een concreet item in de
        // boom dan een zichtbare ore:-groep.
        if (string.IsNullOrWhiteSpace(result))
        {
            result = material.Length > 0 ? material + "_" + t
                        : t.Length > 0 ? t
                        : RecipePlanner.NormalizeName(groupKey[(groupKey.IndexOf(':') + 1)..]);
        }

        if (string.IsNullOrWhiteSpace(result)) result = null;

        cache[groupKey] = result;
        return result;
    }

    private static Recipe Clone(Recipe recipe)
    {
        var copy = new Recipe
        {
            Category = recipe.Category,
            Ingredients = recipe.Ingredients.Select(i => new Ingredient { Item = i.Item, Count = i.Count }).ToList()
        };

        foreach (var alt in recipe.Alternatives) copy.Alternatives.Add(Clone(alt));
        return copy;
    }

    // ========== Jar-recepten ==========

    internal static int ReadJarRecipes(string modsFolder, InstanceMod mod, Dictionary<string, Recipe> recipes,
                                      Dictionary<string, string> names)
    {
        string path = Path.Combine(modsFolder, mod.Jar);
        if (!File.Exists(path))
        {
            // Submappen (mods/1.12.2/…) worden ook doorzocht.
            path = Directory.GetFiles(modsFolder, mod.Jar, SearchOption.AllDirectories).FirstOrDefault() ?? "";
        }

        if (path.Length == 0 || !File.Exists(path)) return 0;

        int added = 0;

        try
        {
            using var zip = ZipFile.OpenRead(path);

            foreach (var entry in zip.Entries.Where(e => Regex.IsMatch(e.FullName, RecipePathPattern, RegexOptions.IgnoreCase)))
            {
                try
                {
                    added += ParseJsonRecipe(ReadEntry(entry), recipes, names);
                }
                catch { }
            }

            // Sommige mods leveren hun eigen CraftTweaker-scripts mee.
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var scriptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    added += ParseCraftTweaker(ReadEntry(entry), recipes, names, removals, scriptKeys);
                    foreach (var key in removals)
                        if (!scriptKeys.Contains(key)) recipes.Remove(key);
                }
                catch { }
            }
        }
        catch { }

        return added;
    }

    /// <summary>
    /// Recepten uit datapacks: instance/datapacks (mappen én .zip), config/openloader/data
    /// en kubejs/data. Nodig voor 1.16+ packs die geen JEI-dump hebben.
    /// </summary>
    private static int ReadDatapackRecipes(string gameFolder, Dictionary<string, Recipe> recipes,
                                          Dictionary<string, string> names, Action<string>? progress)
    {
        int added = 0;

        foreach (string root in new[]
                 {
                     Path.Combine(gameFolder, "datapacks"),
                     Path.Combine(gameFolder, "config", "openloader", "data"),
                     Path.Combine(gameFolder, "kubejs", "data")
                 })
        {
            if (!Directory.Exists(root)) continue;

            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                progress?.Invoke("datapack: " + Path.GetFileName(dir));
                added += ReadRecipeFolder(dir, recipes, names);
            }

            foreach (string zipPath in Directory.GetFiles(root, "*.zip").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                progress?.Invoke("datapack: " + Path.GetFileName(zipPath));
                added += ReadRecipeArchive(zipPath, recipes, names);
            }
        }

        return added;
    }

    /// <summary>Recept-JSON's in een uitgepakt datapack (map met data/&lt;mod&gt;/recipes?/).</summary>
    private static int ReadRecipeFolder(string dir, Dictionary<string, Recipe> recipes, Dictionary<string, string> names)
    {
        int added = 0;

        foreach (string file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            string full = file.Replace('\\', '/');
            if (!Regex.IsMatch(full, @"/data/[^/]+/recipes?/", RegexOptions.IgnoreCase)) continue;

            try { added += ParseJsonRecipe(File.ReadAllText(file), recipes, names); }
            catch { }
        }

        return added;
    }

    /// <summary>Recept-JSON's in een datapack-zip.</summary>
    private static int ReadRecipeArchive(string zipPath, Dictionary<string, Recipe> recipes, Dictionary<string, string> names)
    {
        int added = 0;

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);

            foreach (var entry in zip.Entries.Where(e => Regex.IsMatch(e.FullName, RecipePathPattern, RegexOptions.IgnoreCase)))
            {
                try { added += ParseJsonRecipe(ReadEntry(entry), recipes, names); }
                catch { }
            }
        }
        catch { }

        return added;
    }

    /// <summary>Recept-JSON in de 1.13+ stijl (crafting_shaped, smelting, …).</summary>
    internal static int ParseJsonRecipe(string json, Dictionary<string, Recipe> recipes, Dictionary<string, string> names)
    {
        if (JsonNode.Parse(json) is not JsonObject obj) return 0;

        string type = obj["type"]?.ToString() ?? "";
        string category = CategoryForJsonType(type);

        var output = ReadJsonStack(obj["result"] ?? obj["output"]);
        if (output.Id.Length == 0) return 0;

        // 1.20.5+ stonecutting: "result": "minecraft:stone_slab", "count": 2.
        if (obj["count"] is { } countNode && int.TryParse(countNode.ToString(), out int outputCount) && outputCount > 0)
            output = (output.Id, outputCount);

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Eén ingrediënt toevoegen; "multiplier" telt hoe vaak het in het pattern staat.
        void AddOne(JsonNode? node, int multiplier)
        {
            var parsed = ReadJsonStack(node);
            if (parsed.Id.Length == 0) return;

            // Moderne tags (#c:ingots/iron) wijzen naar een gewoon item; anders blijft
            // "tag:c_ingots_iron" als dode tak in de boom staan.
            string id = parsed.Id;
            if (id.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) && TagItemGuess(id) is { Length: > 0 } guess)
                id = guess;

            string key = KeyFor(id);
            if (key.Length == 0) return;

            int count = Math.Max(1, parsed.Count) * Math.Max(1, multiplier);
            seen[key] = seen.TryGetValue(key, out int prior) ? prior + count : count;
            RecordName(names, id, key);
        }

        // Shaped: het pattern bepaalt hóeveel van elke letter je nodig hebt (8 planken,
        // niet 1). Zonder dit rekende de boom 1 plank per recept.
        if (obj["pattern"] is JsonArray pattern && obj["key"] is JsonObject keys)
        {
            var used = new Dictionary<char, int>();
            foreach (var row in pattern)
            {
                foreach (char c in row?.ToString() ?? "")
                {
                    if (c == ' ') continue;
                    used[c] = used.TryGetValue(c, out int n) ? n + 1 : 1;
                }
            }

            foreach (var kv in used)
            {
                if (keys[kv.Key.ToString()] is { } node) AddOne(node, kv.Value);
            }
        }
        else if (obj["key"] is JsonObject keyMap)
        {
            foreach (var kv in keyMap) AddOne(kv.Value, 1);
        }

        // "ingredients" is een lijst ingrediënten; een geneste lijst is een keuze (één
        // ervan, bijv. een paar houtsoorten), dus daar pakken we het eerste item van.
        if (obj["ingredients"] is JsonArray ingredientList)
        {
            foreach (var node in ingredientList) AddOne(node, 1);
        }
        else if (obj["ingredient"] != null)
        {
            AddOne(obj["ingredient"], 1);
        }

        var ingredients = seen.Select(kv => new Ingredient { Item = kv.Key, Count = kv.Value }).ToList();

        return AddRecipe(recipes, names, output.Id, category, ingredients, output.Count);
    }

    /// <summary>
    /// Raadt het item achter een moderne tag (#c:ingots/iron → iron_ingot,
    /// #forge:raw_materials/gold → raw_gold, #c:storage_blocks/iron → iron_block).
    /// </summary>
    private static string TagItemGuess(string rawTag)
    {
        string tag = rawTag;
        if (tag.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) tag = tag[4..];
        tag = tag.TrimStart('#');
        if (tag.Contains(':')) tag = tag[(tag.IndexOf(':') + 1)..];

        var parts = tag.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";

        string kind = parts[0].TrimEnd('s');
        string material = parts[^1];
        if (kind.Length == 0 || material.Length == 0) return "";

        return kind switch
        {
            "storage_block" => material + "_block",
            "raw_material" => "raw_" + material,
            _ => material + "_" + kind
        };
    }

    /// <summary>JEI-tab/machine-naam voor een modern recepttype ("minecraft:smelting" → "Smelting").</summary>
    private static string CategoryForJsonType(string type)
    {
        string t = type.Contains(':') ? type[(type.IndexOf(':') + 1)..] : type;

        return t switch
        {
            "crafting_shaped" or "crafting_shapeless" or "crafting_special_armordye" => "Crafting",
            "smelting" => "Smelting",
            "blasting" => "Blasting",
            "smoking" => "Smoking",
            "campfire_cooking" => "Campfire Cooking",
            "stonecutting" => "Stonecutting",
            "smithing_transform" or "smithing_trim" => "Smithing",
            _ => t.Length > 0 ? t.Replace('_', ' ') : "Crafting"
        };
    }

    private static (string Id, int Count) ReadJsonStack(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray arr:
                return arr.Count > 0 ? ReadJsonStack(arr[0]) : ("", 1);

            case JsonObject obj:
            {
                string id = obj["item"]?.ToString() ?? obj["id"]?.ToString() ?? obj["name"]?.ToString() ?? "";
                if (id.Length == 0 && obj["tag"] != null) id = "tag:" + obj["tag"];

                int count = 1;
                foreach (var field in new[] { "count", "Count", "amount" })
                {
                    if (obj[field] != null && int.TryParse(obj[field]!.ToString(), out int c) && c > 0)
                    {
                        count = c;
                        break;
                    }
                }

                return (id, count);
            }

            case JsonValue value:
                return (value.ToString() ?? "", 1);

            default:
                return ("", 1);
        }
    }

    // ========== CraftTweaker (1.12-stijl .zs) ==========

    /// <summary>Leest alle bekende recept-aanroepen uit een CraftTweaker-script.</summary>
    internal static int ParseCraftTweaker(string text, Dictionary<string, Recipe> recipes,
                                        Dictionary<string, string> names, HashSet<string> removals,
                                        HashSet<string> scriptKeys)
    {
        int added = 0;

        foreach (Match match in Regex.Matches(text, @"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)*(?<method>addShaped|addShapeless|addSmelting|addFurnace|add[A-Za-z0-9_]*Recipe|removeShaped|removeShapeless|remove)\s*\("))
        {
            string method = match.Groups["method"].Value;
            int open = match.Index + match.Length - 1;
            int close = MatchParen(text, open);
            if (close < 0) continue;

            string call = text[(open + 1)..close];
            string head = Head(text, match.Groups["method"].Index);
            string category = CategoryFor(head, method);

            switch (method)
            {
                case "remove":
                case "removeShaped":
                case "removeShapeless":
                {
                    foreach (var stack in StackTokens(call))
                        removals.Add(KeyFor(stack.Id));
                    continue;
                }
            }

            // Machine-recepten: mods.<mod>.<Machine>.addRecipe(<uitvoer>, <invoer…>, energie).
            // De uitvoer is de eerste stack, ook als er een recept-id of getal vóór staat
            // (Astral Sorcery, Mekanism). Begint de eerste stack-parameter met een lijst, dan
            // staat de uitvoer elders (pressurechamber, NuclearCraft) en slaan we dit over.
            if (method.EndsWith("Recipe", StringComparison.OrdinalIgnoreCase))
            {
                var callArgs = SplitArgs(call);
                int outputIndex = -1;
                bool listFirst = false;

                for (int i = 0; i < callArgs.Count; i++)
                {
                    if (!StackTokens(callArgs[i]).Any()) continue;
                    if (callArgs[i].TrimStart().StartsWith('[')) { listFirst = true; continue; }

                    outputIndex = i;
                    break;
                }

                if (outputIndex < 0 || listFirst) continue;

                if (SingleStack(callArgs[outputIndex]) is not { } outStack) continue;

                string machineOutput = KeyFor(outStack.Id);

                // Een ore-/tag-groep is geen item dat je maakt.
                if (machineOutput.Length == 0 || IsGroupKey(machineOutput)) continue;

                var machineIngredients = new List<Ingredient>();

                foreach (var arg in callArgs.Skip(outputIndex + 1))
                {
                    foreach (var stack in StackTokens(arg))
                    {
                        string machineKey = KeyFor(stack.Id);
                        if (machineKey.Length == 0) continue;

                        // Een byproduct dat hetzelfde item is (Crusher, Pulverizer) is geen
                        // ingrediënt; anders zou een item zichzelf nodig hebben.
                        if (machineKey.Equals(machineOutput, StringComparison.OrdinalIgnoreCase)) continue;

                        machineIngredients.Add(new Ingredient { Item = machineKey, Count = stack.Count });
                        RecordName(names, stack.Id, machineKey);
                    }
                }

                if (machineIngredients.Count == 0) continue;

                RecordName(names, outStack.Id, machineOutput);
                added += AddRecipe(recipes, names, outStack.Id, category, machineIngredients, outStack.Count);
                scriptKeys.Add(machineOutput);
                continue;
            }

            var args = SplitArgs(call);
            if (args.Count == 0) continue;

            // De uitvoer is de eerste stack buiten een lijst (ExtendedCrafting heeft een
            // tier-getal vóór de uitvoer: addShaped(1, <item>, [[…]])).
            string outputArg = args.FirstOrDefault(a => a.Contains('<') && !a.Contains('[')) ?? "";
            var output = StackTokens(outputArg).FirstOrDefault();
            if (string.IsNullOrEmpty(output.Id)) continue;

            var ingredients = new List<Ingredient>();

            foreach (var arg in args)
            {
                if (ReferenceEquals(arg, outputArg)) continue;

                foreach (var stack in StackTokens(arg))
                {
                    string key = KeyFor(stack.Id);
                    ingredients.Add(new Ingredient { Item = key, Count = stack.Count });
                    RecordName(names, stack.Id, key);
                }
            }

            if (ingredients.Count == 0) continue;

            string outputKey = KeyFor(output.Id);
            RecordName(names, output.Id, outputKey);

            if (method is "addSmelting" or "addFurnace")
            {
                // addSmelting(invoer, uitvoer, xp): de eerste stack is de invoer.
                var input = StackTokens(args[0]).FirstOrDefault();
                if (string.IsNullOrEmpty(input.Id)) continue;

                ingredients.Clear();
                string inputKey = KeyFor(input.Id);
                ingredients.Add(new Ingredient { Item = inputKey, Count = input.Count });
                RecordName(names, input.Id, inputKey);
            }

            added += AddRecipe(recipes, names, output.Id, category, ingredients, output.Count);
            scriptKeys.Add(outputKey);
        }

        return added;
    }

    /// <summary>Eén stack-parameter ("<mod:item> * 2"), of null bij een lijst, getal of tekst.</summary>
    private static (string Id, int Count)? SingleStack(string arg)
    {
        string text = arg.Trim();
        if (!Regex.IsMatch(text, @"^<[^<>]+>(\s*\*\s*\d+)?$")) return null;

        var stack = StackTokens(text).FirstOrDefault();
        return string.IsNullOrEmpty(stack.Id) ? null : stack;
    }

    /// <summary>
    /// Is dit een echt item? Enchantments, gassen, vloeistoffen en energie zijn geen
    /// items en horen dus niet als knoop of ingrediënt in de boom.
    /// </summary>
    private static bool IsItemId(string id) =>
        !(id.StartsWith("liquid:", StringComparison.OrdinalIgnoreCase) ||
          id.StartsWith("fluid:", StringComparison.OrdinalIgnoreCase) ||
          id.StartsWith("gas:", StringComparison.OrdinalIgnoreCase) ||
          id.StartsWith("enchantment:", StringComparison.OrdinalIgnoreCase) ||
          id.StartsWith("energy:", StringComparison.OrdinalIgnoreCase));

    /// <summary>Alle &lt;mod:item:meta&gt;-stacks in een stuk tekst, met hun *-aantal.</summary>
    private static IEnumerable<(string Id, int Count)> StackTokens(string text)
    {
        foreach (Match match in Regex.Matches(text, @"<(?<id>[^<>]*)>(\s*\*\s*(?<count>\d+))?"))
        {
            string id = match.Groups["id"].Value.Trim();
            if (id.Length == 0 || id == "*") continue;
            if (!IsItemId(id)) continue;

            int count = 1;
            if (match.Groups["count"].Success) int.TryParse(match.Groups["count"].Value, out count);

            yield return (id, Math.Max(1, count));
        }
    }

    /// <summary>"mods.extendedcrafting.TableCrafting.addShaped" → "Extended Crafting Table".</summary>
    private static string CategoryFor(string head, string method)
    {
        if (method is "addSmelting" or "addFurnace" or "addBlasting") return "Smelting";

        if (head.Length == 0) return "Crafting";

        var parts = head.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1 || parts[0].Equals("recipes", StringComparison.OrdinalIgnoreCase))
            return "Crafting";

        // mods.<modid>.<Machine…>.method
        var machine = parts.Where(p => !p.Equals("mods", StringComparison.OrdinalIgnoreCase))
                           .Where(p => !p.Equals(method, StringComparison.OrdinalIgnoreCase))
                           .ToList();

        if (machine.Count == 0) return "Crafting";

        string last = machine[^1];
        string modId = machine.Count > 1 ? machine[0] : "";

        // Extended Crafting noemt zijn tafels "TableCrafting"; dat leest niet lekker.
        string label = last switch
        {
            "TableCrafting" => "Crafting Table",
            "Shaped" => "Crafting",
            "Shapeless" => "Crafting",
            _ => SplitCamel(last)
        };

        if (modId.Length == 0) return label;

        string mod = SplitCamel(modId);
        if (label.Equals("Crafting", StringComparison.OrdinalIgnoreCase)) return mod + " Crafting";
        return mod + " " + label;
    }

    private static string SplitCamel(string value)
    {
        string spaced = Regex.Replace(value, "(?<=[a-z0-9])(?=[A-Z])", " ");
        spaced = spaced.Replace('_', ' ');
        return Regex.Replace(spaced, @"\s+", " ").Trim();
    }

    // ========== KubeJS (1.16+ .js) ==========

    internal static int ParseKubeJs(string text, Dictionary<string, Recipe> recipes,
                                   Dictionary<string, string> names, HashSet<string> removals,
                                   HashSet<string> scriptKeys)
    {
        int added = 0;

        // event.remove({ output: 'minecraft:stick' }) / { id: 'mod:naam' }
        foreach (Match match in Regex.Matches(text, @"\.remove\s*\(\s*\{(?<body>[^}]*)\}"))
        {
            var output = Regex.Match(match.Groups["body"].Value, @"output\s*:\s*['""]([^'""]+)['""]");
            if (output.Success)
            {
                removals.Add(KeyFor(output.Groups[1].Value));
                continue;
            }

            // { id: 'mod:naam' } is een recept-id; die kunnen we niet naar een item herleiden.
        }

        // event.shaped('4x minecraft:stick', ['A', 'A'], { A: 'minecraft:oak_planks' })
        foreach (Match match in Regex.Matches(text, @"\.shaped\s*\("))
        {
            int open = match.Index + match.Length - 1;
            int close = MatchParen(text, open);
            if (close < 0) continue;

            var args = SplitArgs(text[(open + 1)..close]);
            if (args.Count < 3) continue;

            var output = ParseJsStack(args[0]);
            if (string.IsNullOrEmpty(output.Id)) continue;

            var keys = new Dictionary<char, string>();
            foreach (Match entry in Regex.Matches(args[^1], @"['""]?(?<key>[A-Za-z0-9_])['""]?\s*:\s*(?<value>[^,}]+)"))
                keys[entry.Groups["key"].Value[0]] = entry.Groups["value"].Value;

            var counts = new Dictionary<char, int>();
            foreach (Match row in Regex.Matches(args[1], @"['""](?<row>[^'""]*)['""]"))
            {
                foreach (char c in row.Groups["row"].Value)
                {
                    if (c == ' ') continue;
                    counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;
                }
            }

            var ingredients = new List<Ingredient>();
            foreach (var kv in counts)
            {
                if (!keys.TryGetValue(kv.Key, out var raw)) continue;
                var stack = ParseJsStack(raw);
                string key = KeyFor(stack.Id);
                if (key.Length == 0) continue;

                ingredients.Add(new Ingredient { Item = key, Count = kv.Value * stack.Count });
                RecordName(names, stack.Id, key);
            }

            if (ingredients.Count == 0) continue;

            added += AddRecipe(recipes, names, output.Id, "Crafting", ingredients);
            scriptKeys.Add(KeyFor(output.Id));
        }

        // event.shapeless('minecraft:stick', ['minecraft:x', 'minecraft:y'])
        foreach (Match match in Regex.Matches(text, @"\.shapeless\s*\("))
        {
            int open = match.Index + match.Length - 1;
            int close = MatchParen(text, open);
            if (close < 0) continue;

            var args = SplitArgs(text[(open + 1)..close]);
            if (args.Count < 2) continue;

            var output = ParseJsStack(args[0]);
            if (string.IsNullOrEmpty(output.Id)) continue;

            var ingredients = new List<Ingredient>();
            foreach (Match stackMatch in Regex.Matches(args[1], @"['""](?<value>[^'""]+)['""]"))
            {
                var stack = ParseJsStack(stackMatch.Groups["value"].Value);
                string key = KeyFor(stack.Id);
                if (key.Length == 0) continue;

                ingredients.Add(new Ingredient { Item = key, Count = stack.Count });
                RecordName(names, stack.Id, key);
            }

            if (ingredients.Count == 0) continue;

            added += AddRecipe(recipes, names, output.Id, "Crafting", ingredients);
            scriptKeys.Add(KeyFor(output.Id));
        }

        // event.smelting('minecraft:glass', 'minecraft:sand')
        foreach (Match match in Regex.Matches(text, @"\.(?:smelting|blasting|smoking)\s*\("))
        {
            int open = match.Index + match.Length - 1;
            int close = MatchParen(text, open);
            if (close < 0) continue;

            var args = SplitArgs(text[(open + 1)..close]);
            if (args.Count < 2) continue;

            var output = ParseJsStack(args[0]);
            var input = ParseJsStack(args[1]);
            if (string.IsNullOrEmpty(output.Id) || string.IsNullOrEmpty(input.Id)) continue;

            string inputKey = KeyFor(input.Id);
            RecordName(names, input.Id, inputKey);

            added += AddRecipe(recipes, names, output.Id, "Smelting",
                               new List<Ingredient> { new() { Item = inputKey, Count = input.Count } });

            scriptKeys.Add(KeyFor(output.Id));
        }

        return added;
    }

    /// <summary>"4x minecraft:stick" → id + aantal (KubeJS-notatie).</summary>
    private static (string Id, int Count) ParseJsStack(string raw)
    {
        string text = raw.Trim().Trim('\'', '"', ' ', '(', ')');

        var of = Regex.Match(text, @"Item\.of\s*\(\s*['""](?<value>[^'""]+)['""]");
        if (of.Success) text = of.Groups["value"].Value;

        var item = Regex.Match(text, @"item\s*:\s*(?:Item\.of\s*\(\s*)?['""](?<value>[^'""]+)['""]");
        if (item.Success) text = item.Groups["value"].Value;

        var tag = Regex.Match(text, @"tag\s*:\s*['""](?<value>[^'""]+)['""]");
        if (tag.Success) return ("tag:" + tag.Groups["value"].Value, 1);

        text = text.Trim('\'', '"', ' ').TrimStart('#');
        if (text.Length == 0) return ("", 1);

        int count = 1;
        var countMatch = Regex.Match(text, @"^\s*(?<n>\d+)\s*x\s*", RegexOptions.IgnoreCase);
        if (countMatch.Success)
        {
            int.TryParse(countMatch.Groups["n"].Value, out count);
            text = text[countMatch.Length..];
        }

        return (text.Trim(), Math.Max(1, count));
    }

    // ========== Gedeelde helpers ==========

    /// <summary>
    /// Zet een recept in de tabel. Een script/JSON-recept komt vóór het bestaande recept
    /// (dat is wat de pack wil), een tweede variant wordt een alternatief.
    /// </summary>
    private static int AddRecipe(Dictionary<string, Recipe> recipes, Dictionary<string, string> names,
                                 string rawOutputId, string category, List<Ingredient> ingredients,
                                 int outputCount = 1, string source = "", string patchId = "")
    {
        var merged = ingredients
            .Where(i => !string.IsNullOrWhiteSpace(i.Item))
            .GroupBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Ingredient { Item = g.Key, Count = g.Sum(x => x.Count) })
            .ToList();

        if (merged.Count == 0) return 0;

        string key = KeyFor(rawOutputId);
        if (key.Length == 0) return 0;

        // Een opbrengst boven een volle stapel ("* 1000") is een parsefout, geen recept.
        var recipe = new Recipe
        {
            Category = category,
            Output = Math.Clamp(outputCount, 1, 64),
            Ingredients = merged,
            Source = source,
            PatchId = patchId
        };

        if (!recipes.TryGetValue(key, out var existing))
        {
            recipes[key] = recipe;
            return 1;
        }

        if (SameRecipe(existing, recipe)) return 0;
        if (existing.Alternatives.Any(a => SameRecipe(a, recipe))) return 0;

        // Nieuwe variant vooraan, zodat de pack-versie de standaard wordt. De oudere
        // varianten gaan plat mee: eerder werden ze als ketting (alternatief-in-
        // alternatief) bewaard en dan liep de diepte bij veel overlappende scripts op
        // tot boven de 64 waar de JSON-serializer op afhaakt.
        var variants = new List<Recipe> { existing };
        variants.AddRange(existing.Alternatives);

        recipes[key] = recipe;

        foreach (var variant in variants)
        {
            if (SameRecipe(variant, recipe)) continue;
            if (recipe.Alternatives.Any(a => SameRecipe(a, variant))) continue;

            recipe.Alternatives.Add(Shallow(variant));
        }

        return 1;
    }

    /// <summary>Kopie zonder alternatieven, zodat de variantenlijst plat blijft.</summary>
    private static Recipe Shallow(Recipe recipe) => new()
    {
        Category = recipe.Category,
        Output = recipe.Output,
        // Herkomst mee kopiëren: anders weet de fold niet meer uit welke laag een
        // recept kwam zodra het als variant wordt samengevoegd.
        Source = recipe.Source,
        PatchId = recipe.PatchId,
        Ingredients = recipe.Ingredients.Select(i => new Ingredient { Item = i.Item, Count = i.Count }).ToList()
    };

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

    /// <summary>Receptsleutel zoals de app hem gebruikt ("minecraft:stick" → "stick").</summary>
    private static string KeyFor(string rawId)
    {
        string id = (rawId ?? "").Trim();
        if (id.Length == 0) return "";

        if (id.StartsWith("ore:", StringComparison.OrdinalIgnoreCase))
            return "ore:" + id[4..].Trim().ToLowerInvariant();

        if (id.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            return "tag:" + Regex.Replace(id[4..].Trim().ToLowerInvariant(), @"[/]", "_");

        return RecipePlanner.NormalizeName(id);
    }

    private static void RecordName(Dictionary<string, string> names, string rawId, string key)
    {
        if (key.Length == 0 || names.ContainsKey(key)) return;

        if (key.StartsWith("ore:", StringComparison.OrdinalIgnoreCase))
        {
            names[key] = "ore " + SplitCamel(key[4..]);
            return;
        }

        if (key.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            names[key] = "tag " + key[4..].Replace('_', ' ');
            return;
        }

        if (ItemNames.NameForId(rawId) is { Length: > 0 } nice)
            names[key] = nice.ToLowerInvariant();
    }

    /// <summary>Positie van de sluitende haak bij een openingshaak (string- en lijstbewust).</summary>
    private static int MatchParen(string text, int open)
    {
        int depth = 0;
        char quote = '\0';

        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];

            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    break;

                case '(':
                case '[':
                case '{':
                    depth++;
                    break;

                case ')':
                case ']':
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    /// <summary>Splitst argumenten op komma's die niet binnen haakjes of strings staan.</summary>
    private static List<string> SplitArgs(string call)
    {
        var result = new List<string>();
        int depth = 0;
        char quote = '\0';
        int start = 0;

        for (int i = 0; i < call.Length; i++)
        {
            char c = call[i];

            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    break;

                case '(':
                case '[':
                case '{':
                    depth++;
                    break;

                case ')':
                case ']':
                case '}':
                    depth--;
                    break;

                case ',':
                    if (depth == 0)
                    {
                        result.Add(call[start..i]);
                        start = i + 1;
                    }
                    break;
            }
        }

        result.Add(call[start..]);
        return result;
    }

    /// <summary>De aanroep-kop vóór de haak ("mods.extendedcrafting.TableCrafting.addShaped").</summary>
    private static string Head(string text, int index)
    {
        int start = index;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '.'))
            start--;

        return text[start..index];
    }
}
