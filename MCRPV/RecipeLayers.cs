using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mcrpv;

/// <summary>Uit welke laag een recept komt. De fold past ze in deze volgorde toe.</summary>
public enum RecipeLayer
{
    Base,
    Pack,
    User
}

/// <summary>
/// Eén bewerking op de receptenset, met de bron erbij. De pack-laag en de gebruikerslaag
/// worden als lijst van deze operaties bewaard (patches.jsonl / user.jsonl), niet als set:
/// dan zie je achteraf wat een script deed en kun je het opnieuw toepassen zonder scan.
/// </summary>
public sealed class RecipeOp
{
    [JsonPropertyName("op")] public string Op { get; set; } = "add";
    [JsonPropertyName("out")] public string Out { get; set; } = "";
    [JsonPropertyName("recipe")] public Recipe? Recipe { get; set; }
    [JsonPropertyName("src")] public string Src { get; set; } = "";
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("prio")] public int Prio { get; set; }
    [JsonPropertyName("item")] public string Item { get; set; } = "";
    [JsonPropertyName("with")] public string With { get; set; } = "";
    [JsonPropertyName("msg")] public string Msg { get; set; } = "";

    public string Where => Line > 0 ? $"{Src}:{Line}" : Src;

    public string Describe()
    {
        string what = Op switch
        {
            "add" => $"+ {Out}",
            "replace" => $"~ {Out}",
            "remove" => $"- {Out}",
            "removeByMod" => $"- mod {Out}",
            "replaceIngredient" => $"{Item} → {With}",
            "tag" => $"tag {Out}",
            "warn" => Msg,
            _ => Op
        };

        return Where.Length > 0 ? $"{what}  ({Where})" : what;
    }
}

/// <summary>
/// Een bron van recepten of bewerkingen (PLAN.md §4). Een nieuwe loader of Minecraft-versie
/// is één extra implementatie; de fold en de app blijven gelijk.
/// </summary>
public interface IRecipeSource
{
    /// <summary>"jar-json", "runtime", "datapack", "crafttweaker", "kubejs".</summary>
    string Name { get; }

    RecipeLayer Layer { get; }

    bool Detect(string gameFolder);

    IEnumerable<RecipeOp> Read(string gameFolder, CancellationToken cancel);
}

/// <summary>De basis uit de jar-shards, met de modid als herkomst.</summary>
public sealed class JarJsonSource : IRecipeSource
{
    private readonly IReadOnlyList<JarShard> _shards;

    public JarJsonSource(IReadOnlyList<JarShard> shards) => _shards = shards;

    public string Name => "jar-json";
    public RecipeLayer Layer => RecipeLayer.Base;

    public bool Detect(string gameFolder) => _shards.Count > 0;

    public IEnumerable<RecipeOp> Read(string gameFolder, CancellationToken cancel)
    {
        foreach (var shard in _shards)
        {
            if (cancel.IsCancellationRequested) yield break;

            string src = "jar-JSON " + (shard.ModId.Length > 0 ? shard.ModId : shard.Jar);

            foreach (var kv in shard.Recipes)
            {
                var recipe = RecipeOpHelpers.Copy(kv.Value);
                recipe.Source = src;

                yield return new RecipeOp
                {
                    Op = "add",
                    Out = kv.Key,
                    Recipe = recipe,
                    Src = src,
                    Prio = 0
                };
            }
        }
    }
}

/// <summary>
/// De runtime-dump (JEIExporter) als basislaag: voor 1.12 zijn de code-recepten alleen via
/// het spel zelf te krijgen. De dump is een gewone laag met herkomst "runtime".
/// </summary>
public sealed class RuntimeDumpSource : IRecipeSource
{
    private readonly string? _dumpFolder;
    private readonly string? _cacheFile;
    private readonly bool _forceImport;

    public RuntimeDumpSource(string? dumpFolder = null, string? dataDirectory = null, bool forceImport = false)
    {
        _dumpFolder = dumpFolder;
        _cacheFile = string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.Combine(dataDirectory, "dump-shard.json");
        _forceImport = forceImport || !string.IsNullOrWhiteSpace(dumpFolder);
    }

    public string Name => "runtime";

    // De dump komt boven de jar-JSON's (die hebben in 1.12 bijna niets) maar onder de
    // pack-laag, zodat scripts de dump nog kunnen overschrijven.
    public RecipeLayer Layer => RecipeLayer.Base;

    public string? Folder { get; private set; }
    public bool FromCache { get; private set; }

    public bool Detect(string gameFolder)
    {
        // 1. Een expliciete map (--dump=<map>): altijd opnieuw inlezen.
        if (!string.IsNullOrWhiteSpace(_dumpFolder) && Directory.Exists(_dumpFolder))
        {
            Folder = _dumpFolder;
            FromCache = false;
            return true;
        }

        // 2. De dump-shard van de vorige keer: zelfde recepten, geen 25 seconden inlezen.
        if (!_forceImport && _cacheFile != null && File.Exists(_cacheFile))
        {
            Folder = "dump-shard";
            FromCache = true;
            return true;
        }

        // 3. Zelf zoeken in de instance (config/jeiexporter/exports, dumps, …).
        string? found = RecipeDumpImporter.FindDumpFolder(Path.GetDirectoryName(gameFolder) ?? gameFolder);
        if (string.IsNullOrWhiteSpace(found) || !Directory.Exists(found)) return false;

        Folder = found;
        FromCache = false;
        return true;
    }

    public IEnumerable<RecipeOp> Read(string gameFolder, CancellationToken cancel)
    {
        if (FromCache)
        {
            JarShard? shard = null;

            try
            {
                if (_cacheFile != null && File.Exists(_cacheFile))
                    shard = JsonSerializer.Deserialize<JarShard>(File.ReadAllText(_cacheFile));
            }
            catch
            {
                shard = null;
            }

            if (shard != null)
            {
                foreach (var kv in shard.Recipes)
                {
                    if (cancel.IsCancellationRequested) yield break;

                    var recipe = RecipeOpHelpers.Copy(kv.Value);
                    recipe.Source = "runtime:" + shard.Jar;

                    yield return new RecipeOp
                    {
                        Op = "add", Out = kv.Key, Recipe = recipe, Src = "runtime:" + shard.Jar, Prio = 0
                    };
                }

                yield break;
            }

            // Shard onleesbaar: hieronder gewoon opnieuw inlezen.
            Folder ??= "dump";
        }

        if (string.IsNullOrWhiteSpace(Folder) || Folder == "dump-shard") yield break;

        DumpImport dump;
        try { dump = RecipeDumpImporter.Import(Folder!, null, cancel); }
        catch { yield break; }

        // De dump bewaren als basis-shard, zodat --reindex=pack hem niet opnieuw hoeft
        // in te lezen (PLAN.md fase 4: "dump wordt een base-shard als alle andere").
        try
        {
            if (_cacheFile != null && dump.Recipes.Count > 0)
            {
                var shard = new JarShard
                {
                    Jar = Path.GetFileName(Folder),
                    Sha256 = "",
                    ModId = "runtime",
                    DisplayName = "JEIExporter-dump",
                    JsonRecipes = dump.Recipes.Count,
                    Recipes = dump.Recipes,
                    Names = dump.Names
                };

                File.WriteAllText(_cacheFile, JsonSerializer.Serialize(shard));
            }
        }
        catch
        {
            // De cache is een versnelling, geen voorwaarde.
        }

        foreach (var kv in dump.Recipes)
        {
            if (cancel.IsCancellationRequested) yield break;

            var recipe = RecipeOpHelpers.Copy(kv.Value);
            recipe.Source = "runtime:" + Path.GetFileName(Folder);

            yield return new RecipeOp
            {
                Op = "add", Out = kv.Key, Recipe = recipe, Src = "runtime:" + Path.GetFileName(Folder), Prio = 0
            };
        }
    }
}

/// <summary>Datapacks (map of zip), OpenLoader en kubejs/data: JSON-recepten als pack-laag.</summary>
public sealed class DatapackSource : IRecipeSource
{
    public string Name => "datapack";
    public RecipeLayer Layer => RecipeLayer.Pack;

    private static IEnumerable<(string Root, string Label)> Roots(string gameFolder)
    {
        yield return (Path.Combine(gameFolder, "datapacks"), "datapacks");
        yield return (Path.Combine(gameFolder, "config", "openloader", "data"), "openloader");
        yield return (Path.Combine(gameFolder, "kubejs", "data"), "kubejs/data");
    }

    public bool Detect(string gameFolder) => Roots(gameFolder).Any(r => Directory.Exists(r.Root));

    public IEnumerable<RecipeOp> Read(string gameFolder, CancellationToken cancel)
    {
        foreach (var (root, label) in Roots(gameFolder))
        {
            if (!Directory.Exists(root)) continue;

            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
                {
                    if (cancel.IsCancellationRequested) yield break;

                    string full = file.Replace('\\', '/');
                    if (!Regex.IsMatch(full, @"/data/[^/]+/recipes?/", RegexOptions.IgnoreCase)) continue;

                    foreach (var op in RecipeOpHelpers.ParseJsonFile(file, label + "/" + Path.GetFileName(dir)))
                        yield return op;
                }
            }

            foreach (string zip in Directory.GetFiles(root, "*.zip").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (cancel.IsCancellationRequested) yield break;

                foreach (var op in RecipeOpHelpers.ParseDatapackZip(zip, label))
                    yield return op;
            }
        }
    }
}

/// <summary>CraftTweaker: scripts/**/*.zs, één bewerkingenlijst per bestand.</summary>
public sealed class CraftTweakerSource : IRecipeSource
{
    private readonly IReadOnlyList<string> _folders;

    public CraftTweakerSource(IReadOnlyList<string> scriptFolders) => _folders = scriptFolders;

    public string Name => "crafttweaker";
    public RecipeLayer Layer => RecipeLayer.Pack;

    public bool Detect(string gameFolder) => Files(gameFolder).Any();

    internal IEnumerable<string> Files(string gameFolder)
    {
        foreach (string folder in _folders)
        {
            if (!Directory.Exists(folder)) continue;

            foreach (string file in Directory.GetFiles(folder, "*.zs", SearchOption.AllDirectories)
                                              .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                yield return file;
        }
    }

    public IEnumerable<RecipeOp> Read(string gameFolder, CancellationToken cancel)
    {
        foreach (string file in Files(gameFolder))
        {
            if (cancel.IsCancellationRequested) yield break;

            foreach (var op in RecipeOpHelpers.ParseScript(file, false))
                yield return op;
        }
    }
}

/// <summary>KubeJS: kubejs/server_scripts/**/*.js.</summary>
public sealed class KubeJsSource : IRecipeSource
{
    private readonly IReadOnlyList<string> _folders;

    public KubeJsSource(IReadOnlyList<string> scriptFolders) => _folders = scriptFolders;

    public string Name => "kubejs";
    public RecipeLayer Layer => RecipeLayer.Pack;

    public bool Detect(string gameFolder) => Files(gameFolder).Any();

    internal IEnumerable<string> Files(string gameFolder)
    {
        foreach (string folder in _folders)
        {
            if (!Directory.Exists(folder)) continue;

            foreach (string file in Directory.GetFiles(folder, "*.js", SearchOption.AllDirectories)
                                              .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                yield return file;
        }
    }

    public IEnumerable<RecipeOp> Read(string gameFolder, CancellationToken cancel)
    {
        foreach (string file in Files(gameFolder))
        {
            if (cancel.IsCancellationRequested) yield break;

            foreach (var op in RecipeOpHelpers.ParseScript(file, true))
                yield return op;
        }
    }
}

/// <summary>Hulpfuncties die de bestaande parsers per bestand aanroepen.</summary>
internal static class RecipeOpHelpers
{
    /// <summary>Een recept kopiëren inclusief varianten, zodat lagen elkaar niet raken.</summary>
    public static Recipe Copy(Recipe recipe)
    {
        var copy = new Recipe
        {
            Category = recipe.Category,
            Output = recipe.Output,
            Source = recipe.Source,
            PatchId = recipe.PatchId
        };

        foreach (var ingredient in recipe.Ingredients)
            copy.Ingredients.Add(new Ingredient { Item = ingredient.Item, Count = ingredient.Count });

        foreach (var alternative in recipe.Alternatives)
            copy.Alternatives.Add(Copy(alternative));

        return copy;
    }

    /// <summary>
    /// Een recept klaarzetten voor de set: ingrediënten per item optellen en de varianten
    /// plat slaan (geen alternatief-in-alternatief). Zonder dit loopt de diepte bij de
    /// planner op en wordt de fold bij 14 000 dump-recepten traag.
    /// </summary>
    public static Recipe Normalize(Recipe recipe)
    {
        var result = new Recipe
        {
            Category = recipe.Category,
            Output = Math.Clamp(recipe.Output, 1, 64),
            Source = recipe.Source,
            PatchId = recipe.PatchId,
            Ingredients = Group(recipe.Ingredients)
        };

        Flatten(result, recipe, 0);
        return result;
    }

    private static void Flatten(Recipe target, Recipe source, int depth)
    {
        if (depth > 4) return;

        foreach (var alternative in source.Alternatives)
        {
            var flat = new Recipe
            {
                Category = alternative.Category,
                Output = Math.Clamp(alternative.Output, 1, 64),
                Source = alternative.Source,
                PatchId = alternative.PatchId,
                Ingredients = Group(alternative.Ingredients)
            };

            if (SameShape(target, flat)) continue;
            if (target.Alternatives.Any(a => SameShape(a, flat))) continue;

            target.Alternatives.Add(flat);
            Flatten(target, alternative, depth + 1);
        }
    }

    private static List<Ingredient> Group(List<Ingredient> ingredients) =>
        ingredients.Where(i => !string.IsNullOrWhiteSpace(i.Item))
                   .GroupBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
                   .Select(g => new Ingredient { Item = g.Key, Count = g.Sum(i => i.Count) })
                   .ToList();

    private static bool SameShape(Recipe a, Recipe b)
    {
        if (!string.Equals(a.Category, b.Category, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.Output != b.Output || a.Ingredients.Count != b.Ingredients.Count) return false;

        var left = a.Ingredients.OrderBy(i => i.Item, StringComparer.OrdinalIgnoreCase).ToList();
        var right = b.Ingredients.OrderBy(i => i.Item, StringComparer.OrdinalIgnoreCase).ToList();

        for (int i = 0; i < left.Count; i++)
            if (!left[i].Item.Equals(right[i].Item, StringComparison.OrdinalIgnoreCase) ||
                left[i].Count != right[i].Count) return false;

        return true;
    }

    /// <summary>
    /// Eén scriptbestand → bewerkingen. De bestaande parser vult een lege set; wat erin
    /// staat is "add", wat hij weg wil is "remove". Zware of niet-ondersteunde aanroepen
    /// worden een waarschuwing in plaats van stil genegeerd.
    /// </summary>
    public static List<RecipeOp> ParseScript(string path, bool kubeJs)
    {
        var ops = new List<RecipeOp>();
        string text;

        try { text = File.ReadAllText(path); }
        catch { return ops; }

        string src = Relative(path);
        var recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scriptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (kubeJs) ModRecipeScanner.ParseKubeJs(text, recipes, names, removals, scriptKeys);
            else ModRecipeScanner.ParseCraftTweaker(text, recipes, names, removals, scriptKeys);
        }
        catch
        {
            ops.Add(new RecipeOp { Op = "warn", Src = src, Msg = "script kon niet gelezen worden" });
            return ops;
        }

        foreach (var kv in recipes)
        {
            var recipe = Copy(kv.Value);
            recipe.Source = src;

            ops.Add(new RecipeOp
            {
                Op = "add",
                Out = kv.Key,
                Recipe = recipe,
                Src = src,
                Line = FindLine(text, kv.Key),
                Prio = 10
            });
        }

        foreach (string key in removals)
        {
            if (scriptKeys.Contains(key)) continue;

            ops.Add(new RecipeOp
            {
                Op = "remove",
                Out = key,
                Src = src,
                Line = FindLine(text, key)
            });
        }

        ops.AddRange(Warnings(text, src));
        return ops;
    }

    public static List<RecipeOp> ParseJsonFile(string path, string label)
    {
        var ops = new List<RecipeOp>();
        var recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try { ModRecipeScanner.ParseJsonRecipe(File.ReadAllText(path), recipes, names); }
        catch { return ops; }

        string src = label + "/" + Path.GetFileName(path);
        foreach (var kv in recipes)
        {
            var recipe = Copy(kv.Value);
            recipe.Source = src;

            ops.Add(new RecipeOp { Op = "add", Out = kv.Key, Recipe = recipe, Src = src, Prio = 10 });
        }

        return ops;
    }

    public static List<RecipeOp> ParseDatapackZip(string path, string label)
    {
        var ops = new List<RecipeOp>();

        try
        {
            using var zip = ZipFile.OpenRead(path);

            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                if (!Regex.IsMatch(entry.FullName, @"/data/[^/]+/recipes?/", RegexOptions.IgnoreCase)) continue;

                var recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    using var reader = new StreamReader(entry.Open());
                    ModRecipeScanner.ParseJsonRecipe(reader.ReadToEnd(), recipes, names);
                }
                catch { continue; }

                string src = label + "/" + Path.GetFileName(path) + "!" + entry.FullName;

                foreach (var kv in recipes)
                {
                    var recipe = Copy(kv.Value);
                    recipe.Source = src;

                    ops.Add(new RecipeOp { Op = "add", Out = kv.Key, Recipe = recipe, Src = src, Prio = 10 });
                }
            }
        }
        catch
        {
            // Kapotte zip: geen recepten, geen crash.
        }

        return ops;
    }

    /// <summary>Wat een script doet maar de patchlog niet kan uitdrukken, wordt gemeld.</summary>
    private static IEnumerable<RecipeOp> Warnings(string text, string src)
    {
        var patterns = new (string Needle, string Message)[]
        {
            ("removeByRecipeName", "removeByRecipeName wordt niet ondersteund"),
            ("removeByModid", "removeByModid wordt niet ondersteund"),
            ("removeAll", "removeAll wordt niet ondersteund"),
            ("replaceInput", "replaceInput wordt niet ondersteund"),
            ("replaceOutput", "replaceOutput wordt niet ondersteund")
        };

        foreach (var (needle, message) in patterns)
        {
            int line = FindLine(text, needle, true);
            if (line == 0) continue;

            yield return new RecipeOp { Op = "warn", Src = src, Line = line, Msg = message };
        }
    }

    /// <summary>Regelnummer van de eerste regel met dit item (of dit woord).</summary>
    private static int FindLine(string text, string key, bool raw = false)
    {
        string needle = key;
        if (!raw)
        {
            int colon = needle.LastIndexOf(':');
            if (colon >= 0 && colon + 1 < needle.Length) needle = needle[(colon + 1)..];
            if (needle.Length < 3) return 0;
        }

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                return i + 1;

        return 0;
    }

    /// <summary>Pad zoals het in het rapport moet staan: scripts/mythic/runes.zs.</summary>
    public static string Relative(string path)
    {
        string full = path.Replace('\\', '/');
        int index = full.IndexOf("/minecraft/", StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return full[(index + "/minecraft/".Length)..];

        index = full.IndexOf("/instance/", StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return full[(index + "/instance/".Length)..];

        return Path.GetFileName(path);
    }
}

public sealed class PatchLogResult
{
    public string Hash { get; init; } = "";
    public string Path { get; init; } = "";
    public List<RecipeOp> Ops { get; } = new();
    public int Files { get; init; }
    public int Sources { get; init; }
    public bool FromCache { get; init; }

    public int Adds => Ops.Count(o => o.Op == "add");
    public int Removes => Ops.Count(o => o.Op == "remove");
    public int Warnings => Ops.Count(o => o.Op == "warn");

    public IEnumerable<string> WarningLines =>
        Ops.Where(o => o.Op == "warn").Select(w => $"⚠ {w.Where}: {w.Msg}");

    public string Summary =>
        $"{Adds} toegevoegd · {Removes} weggehaald · {Warnings} waarschuwing(en) · {Files} bestand(en)";
}

/// <summary>
/// De pack-laag als bewerkingenlog. De hash loopt over de inhoud van alle script- en
/// datapackbestanden plus de parser-versie, dus een ongewijzigde pack-laag wordt nooit
/// opnieuw gelezen (PLAN.md §5).
/// </summary>
public static class PatchLog
{
    /// <summary>Ophogen bij elke wijziging in de script-parser; dwingt een nieuwe patchlaag af.</summary>
    public const int Parser = 1;

    public static PatchLogResult Build(string gameFolder, string dataDirectory,
                                       IReadOnlyList<string> scriptFolders,
                                       Action<string>? progress = null,
                                       CancellationToken cancel = default)
    {
        Directory.CreateDirectory(dataDirectory);

        var sources = new IRecipeSource[]
        {
            new DatapackSource(),
            new CraftTweakerSource(scriptFolders),
            new KubeJsSource(scriptFolders)
        };

        var files = new List<string>();
        foreach (var source in sources)
        {
            if (!source.Detect(gameFolder)) continue;

            files.AddRange(source switch
            {
                DatapackSource => DatapackFiles(gameFolder),
                CraftTweakerSource zs => zs.Files(gameFolder),
                KubeJsSource js => js.Files(gameFolder),
                _ => Array.Empty<string>()
            });
        }

        files = files.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                     .ToList();

        var hashInput = new StringBuilder();
        foreach (string file in files)
        {
            hashInput.Append(RecipeOpHelpers.Relative(file)).Append('|')
                     .Append(JarIndex.HashFile(file)).Append('\n');
        }

        hashInput.Append("|parser:").Append(Parser);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString())))[..16]
                             .ToLowerInvariant();

        string path = Path.Combine(dataDirectory, "patches-" + hash + ".jsonl");
        var result = new PatchLogResult { Hash = hash, Path = path, Files = files.Count, Sources = sources.Length };

        if (File.Exists(path))
        {
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var op = JsonSerializer.Deserialize<RecipeOp>(line);
                    if (op != null) result.Ops.Add(op);
                }

                return new PatchLogResult
                {
                    Hash = hash, Path = path, Files = files.Count, Sources = sources.Length, FromCache = true
                }.WithOps(result.Ops);
            }
            catch
            {
                // Onleesbare patchlaag: hieronder opnieuw opbouwen.
            }
        }

        foreach (var source in sources)
        {
            if (cancel.IsCancellationRequested) return result;

            progress?.Invoke("pack-laag: " + source.Name);

            foreach (var op in source.Read(gameFolder, cancel))
                result.Ops.Add(op);
        }

        Save(path, result.Ops);
        PrunePatches(dataDirectory, Path.GetFileName(path));
        return result;
    }

    /// <summary>Eigen correcties: hoogste voorrang, nooit door een pack overschreven.</summary>
    public static List<RecipeOp> LoadUser(string dataDirectory)
    {
        var ops = new List<RecipeOp>();
        string path = Path.Combine(dataDirectory, "user.jsonl");
        if (!File.Exists(path)) return ops;

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var op = JsonSerializer.Deserialize<RecipeOp>(line);
                if (op != null)
                {
                    op.Prio = 1000;
                    ops.Add(op);
                }
            }
        }
        catch
        {
            // Kapotte gebruikerslaag: negeren is beter dan de fold laten mislukken.
        }

        return ops;
    }

    /// <summary>Regels uit een .po3patch-bestand bij de gebruikerslaag zetten.</summary>
    public static int ImportUser(string dataDirectory, string file)
    {
        if (!File.Exists(file)) return 0;

        Directory.CreateDirectory(dataDirectory);
        string target = Path.Combine(dataDirectory, "user.jsonl");
        int added = 0;

        foreach (string line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var op = JsonSerializer.Deserialize<RecipeOp>(line);
                if (op == null) continue;

                op.Prio = 1000;
                File.AppendAllText(target, JsonSerializer.Serialize(op) + Environment.NewLine);
                added++;
            }
            catch
            {
                // Eén kapotte regel mag de rest niet tegenhouden.
            }
        }

        return added;
    }

    public static void Export(string sourcePath, string targetPath)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

        File.Copy(sourcePath, targetPath, true);
    }

    private static IEnumerable<string> DatapackFiles(string gameFolder)
    {
        foreach (string root in new[]
                 {
                     Path.Combine(gameFolder, "datapacks"),
                     Path.Combine(gameFolder, "config", "openloader", "data"),
                     Path.Combine(gameFolder, "kubejs", "data")
                 })
        {
            if (!Directory.Exists(root)) continue;

            foreach (string file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                                              .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                                          f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                yield return file;
        }
    }

    private static PatchLogResult WithOps(this PatchLogResult result, List<RecipeOp> ops)
    {
        result.Ops.AddRange(ops);
        return result;
    }

    private static void Save(string path, List<RecipeOp> ops)
    {
        var lines = new List<string>(ops.Count);
        foreach (var op in ops)
            lines.Add(JsonSerializer.Serialize(op));

        File.WriteAllLines(path, lines);
    }

    /// <summary>Alleen de nieuwste twee patchlagen bewaren.</summary>
    private static void PrunePatches(string dataDirectory, string keep)
    {
        try
        {
            var files = Directory.GetFiles(dataDirectory, "patches-*.jsonl")
                                 .OrderByDescending(File.GetLastWriteTimeUtc)
                                 .ToList();

            foreach (string file in files.Skip(2))
                if (!Path.GetFileName(file).Equals(keep, StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
        }
        catch
        {
            // Opruimen mag nooit een indexbeurt laten mislukken.
        }
    }
}

public sealed class FoldResult
{
    public Dictionary<string, Recipe> Recipes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int BaseCount { get; set; }
    public int DumpCount { get; set; }
    public int PackTouched { get; set; }
    public int UserTouched { get; set; }
    public int OverlayAdded { get; set; }
    public int DeadPatches { get; set; }

    /// <summary>De bewerkingen die niets raakten, in fold-volgorde (voor de buglijst).</summary>
    public List<RecipeOp> DeadOps { get; } = new();

    /// <summary>Tijd per foldstap in milliseconden (voor het rapport).</summary>
    public Dictionary<string, long> Timings { get; } = new();
    public List<string> Messages { get; } = new();

    public int Result => Recipes.Count;

    public string CountsText =>
        $"📦 {BaseCount:N0} basis" +
        (DumpCount > 0 ? $" · 🧱 {DumpCount:N0} dump" : "") +
        $" · 🧩 {PackTouched:N0} pack · 🖐 {UserTouched:N0} eigen";
}

/// <summary>
/// De fold: basis + dump + pack-laag + gebruikerslaag → één set, met per recept de bron.
/// Deterministisch: dezelfde lagen geven altijd dezelfde set (sorteren op prio, bron, regel).
/// </summary>
public static class Fold
{
    public static FoldResult Apply(IReadOnlyList<JarShard> shards,
                                   IEnumerable<RecipeOp> dumpOps,
                                   IEnumerable<RecipeOp> packOps,
                                   IEnumerable<RecipeOp> userOps,
                                   Action<string>? progress = null,
                                   CancellationToken cancel = default)
    {
        var result = new FoldResult();

        // 1. Basis uit de shards.
        foreach (var shard in shards)
        {
            if (cancel.IsCancellationRequested) return result;

            string src = "jar-JSON " + (shard.ModId.Length > 0 ? shard.ModId : shard.Jar);

            foreach (var kv in shard.Recipes)
            {
                var recipe = RecipeOpHelpers.Copy(kv.Value);
                recipe.Source = src;
                ModRecipeScanner.MergeInto(result.Recipes, result.Names, kv.Key, recipe, src);
            }

            foreach (var kv in shard.Names) result.Names.TryAdd(kv.Key, kv.Value);
        }

        return ApplyCore(result, shards, dumpOps, packOps, userOps, cancel);
    }

    /// <summary>Dezelfde fold, maar met een kant-en-klare basis uit base-&lt;hash&gt;.json.</summary>
    public static FoldResult Apply(BaseIndexResult basis,
                                   IEnumerable<RecipeOp> dumpOps,
                                   IEnumerable<RecipeOp> packOps,
                                   IEnumerable<RecipeOp> userOps,
                                   Action<string>? progress = null,
                                   CancellationToken cancel = default)
    {
        var result = new FoldResult();

        foreach (var kv in basis.Recipes)
        {
            var recipe = RecipeOpHelpers.Copy(kv.Value);
            if (recipe.Source.Length == 0) recipe.Source = "jar-JSON";

            result.Recipes[kv.Key] = recipe;
        }
        foreach (var kv in basis.Names) result.Names.TryAdd(kv.Key, kv.Value);

        return ApplyCore(result, null, dumpOps, packOps, userOps, cancel);
    }

    private static FoldResult ApplyCore(FoldResult result, IReadOnlyList<JarShard>? shards,
                                        IEnumerable<RecipeOp> dumpOps, IEnumerable<RecipeOp> packOps,
                                        IEnumerable<RecipeOp> userOps, CancellationToken cancel)
    {
        result.BaseCount = result.Recipes.Count;

        // 2. Dump (1.12 code-recepten) en daarna de pack-laag, elk met hun eigen prioriteit.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var userList = userOps.ToList();
        var userKeys = new HashSet<string>(userList.Where(o => o.Out.Length > 0).Select(o => o.Out),
                                          StringComparer.OrdinalIgnoreCase);

        // Sleutels die de pack-laag aanraakt (niet de dump): alleen die kunnen nog groepen
        // in hun ingrediënten hebben.
        var scriptKeys = new HashSet<string>(packOps.Concat(userList)
                                                    .Where(o => o.Out.Length > 0)
                                                    .Select(o => o.Out),
                                             StringComparer.OrdinalIgnoreCase);

        var ops = new List<(RecipeOp Op, int Index)>();
        int index = 0;

        foreach (var op in dumpOps.Concat(packOps)) ops.Add((op, index++));
        foreach (var op in userList) ops.Add((op, index++));

        var ordered = ops.OrderBy(o => o.Op.Prio)
                         .ThenBy(o => o.Op.Src, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(o => o.Op.Line)
                         .ThenBy(o => o.Index)
                         .ToList();

        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedByMod = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (op, _) in ordered)
        {
            if (cancel.IsCancellationRequested) return result;

            switch (op.Op)
            {
                case "add":
                    if (op.Recipe == null || op.Out.Length == 0) break;
                    ApplyAdd(result, op, touched);
                    break;

                case "replace":
                    if (op.Recipe == null || op.Out.Length == 0) break;
                    if (!result.Recipes.ContainsKey(op.Out))
                    {
                        result.DeadPatches++;
                        result.DeadOps.Add(op);
                    }

                    var replacement = RecipeOpHelpers.Copy(op.Recipe);
                    replacement.Source = op.Src;
                    replacement.PatchId = op.Where;
                    result.Recipes[op.Out] = replacement;
                    touched.Add(op.Out);
                    break;

                case "remove":
                    if (result.Recipes.Remove(op.Out)) touched.Add(op.Out);
                    else
                    {
                        result.DeadPatches++;
                        result.DeadOps.Add(op);
                    }
                    break;

                case "removeByMod":
                    removedByMod.Add(op.Out);
                    break;

                case "replaceIngredient":
                    ApplyReplaceIngredient(result.Recipes, op);
                    break;

                case "tag":
                    result.Messages.Add($"⚠ {op.Where}: tag-definitie niet toegepast");
                    break;
            }
        }

        // removeByMod pas na de andere operaties: een script mag eerst iets toevoegen.
        if (removedByMod.Count > 0) ApplyRemoveByMod(shards, removedByMod, result, touched);

        result.Timings["patches"] = clock.ElapsedMilliseconds;
        clock.Restart();

        // Laagverdeling van het eindresultaat: per item telt de laag die het laatst won.
        // Zo zie je in één regel hoeveel de basis, de dump, de pack-laag en de gebruiker
        // elk bijdroegen (PLAN.md §7).
        result.DumpCount = 0;
        result.UserTouched = 0;
        result.PackTouched = 0;

        foreach (var kv in result.Recipes)
        {
            string source = kv.Value.Source;

            if (userKeys.Contains(kv.Key)) result.UserTouched++;
            else if (source.StartsWith("runtime:", StringComparison.OrdinalIgnoreCase)) result.DumpCount++;
            else if (source.StartsWith("jar-JSON", StringComparison.OrdinalIgnoreCase) ||
                     source.StartsWith("overlay", StringComparison.OrdinalIgnoreCase) || source.Length == 0)
                result.BaseCount = result.BaseCount;
            else result.PackTouched++;
        }

        // 3. Overlay (mijnen, laser drill) als echte laag met herkomst.
        // Alleen wat de pack- en gebruikerslaag toevoegden kan nog een ore-/tag-groep
        // bevatten; de basis en de dump zijn al opgelost. Dat scheelt bij 14 000 items
        // seconden werk (PLAN.md §8: fold < 1 s).
        var resolveThese = new HashSet<string>(scriptKeys, StringComparer.OrdinalIgnoreCase);
        ModRecipeScanner.ResolveGroupIngredients(result.Recipes, result.Names, resolveThese);
        result.OverlayAdded = RecipeOverlay.Apply(result.Recipes);
        if (result.OverlayAdded > 0)
        {
            foreach (var kv in result.Recipes)
                if (kv.Value.Source.Length == 0) kv.Value.Source = "overlay";
        }

        if (result.OverlayAdded > 0)
            result.Messages.Add($"overlay: {result.OverlayAdded} recept(en) toegevoegd (mijnen, laser drill)");

        result.Timings["nawerk"] = clock.ElapsedMilliseconds;

        // Een recept dat na alle nabewerkingen nog geen bron heeft, komt uit de basis.
        foreach (var kv in result.Recipes)
            if (kv.Value.Source.Length == 0) kv.Value.Source = "jar-JSON";

        return result;
    }

    private static void ApplyAdd(FoldResult result, RecipeOp op, HashSet<string> touched)
    {
        var recipe = RecipeOpHelpers.Copy(op.Recipe!);
        recipe.Source = op.Src;
        recipe.PatchId = op.Where;

        // De dump levert per item één samengevat recept; alleen als de sleutel al bestaat
        // moet hij als variant samengevoegd worden. Dat scheelt bij 14 000 dump-recepten
        // het grootste deel van de foldtijd.
        if (!result.Recipes.ContainsKey(op.Out))
        {
            // Nieuwe sleutel: normaliseren zonder de bestaande set te doorlopen. Dat is
            // de snelste route en houdt dezelfde vorm als de scan (varianten plat).
            result.Recipes[op.Out] = RecipeOpHelpers.Normalize(recipe);
            result.Names.TryAdd(op.Out, op.Out);
            touched.Add(op.Out);
            return;
        }

        bool existed = true;
        ModRecipeScanner.MergeInto(result.Recipes, result.Names, op.Out, recipe, op.Src, op.Where);

        // MergeInto zet een nieuw recept vooraan; de bron van het hoofdrecept is de patch.
        if (result.Recipes.TryGetValue(op.Out, out var merged))
        {
            merged.Source = op.Src;
            merged.PatchId = op.Where;
        }

        touched.Add(op.Out);
        _ = existed;
    }

    private static void ApplyReplaceIngredient(Dictionary<string, Recipe> recipes, RecipeOp op)
    {
        if (op.Item.Length == 0 || op.With.Length == 0) return;

        foreach (var recipe in recipes.Values)
        {
            foreach (var ingredient in recipe.Ingredients)
                if (ingredient.Item.Equals(op.Item, StringComparison.OrdinalIgnoreCase))
                    ingredient.Item = op.With;

            foreach (var alternative in recipe.Alternatives)
                foreach (var ingredient in alternative.Ingredients)
                    if (ingredient.Item.Equals(op.Item, StringComparison.OrdinalIgnoreCase))
                        ingredient.Item = op.With;
        }
    }

    private static void ApplyRemoveByMod(IReadOnlyList<JarShard>? shards, HashSet<string> mods,
                                         FoldResult result, HashSet<string> touched)
    {
        if (shards == null) return;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var shard in shards)
            if (mods.Contains(shard.ModId))
                foreach (string key in shard.Recipes.Keys)
                    keys.Add(key);

        foreach (string key in keys)
            if (result.Recipes.Remove(key)) touched.Add(key);
    }
}

/// <summary>Eén verschil, zodat het venster kan filteren en doorklikken naar de herkomst.</summary>
public sealed class DiffEntry
{
    public string Kind { get; init; } = "";      // +, -, ~
    public string Item { get; init; } = "";
    public string Source { get; init; } = "";

    public string Label() => Source.Length > 0 ? $"{Kind} {Item}  ← {Source}" : $"{Kind} {Item}";
}

public sealed class DiffReport
{
    /// <summary>Hoeveel regels het rapport als tekst meekrijgt; de entries zijn compleet.</summary>
    private const int LineLimit = 200;

    public int Added { get; set; }
    public int Removed { get; set; }
    public int Changed { get; set; }
    public List<string> Lines { get; } = new();
    public List<DiffEntry> Entries { get; } = new();

    public bool Empty => Added == 0 && Removed == 0 && Changed == 0;

    public string Summary =>
        Empty ? "geen verschillen" : $"+{Added} · -{Removed} · ~{Changed}";

    /// <summary>Vergelijkt de nieuwe set met de vorige en noemt per regel de bron.</summary>
    public static DiffReport Compare(Dictionary<string, Recipe>? previous, Dictionary<string, Recipe> current)
    {
        var report = new DiffReport();
        previous ??= new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in current)
        {
            if (!previous.TryGetValue(kv.Key, out var old))
            {
                report.Added++;
                report.Record("+", kv.Key, Source(kv.Value));
                continue;
            }

            if (Signature(old) == Signature(kv.Value)) continue;

            report.Changed++;
            report.Record("~", kv.Key, Source(kv.Value));
        }

        foreach (var key in previous.Keys)
        {
            if (current.ContainsKey(key)) continue;

            report.Removed++;
            report.Record("-", key, "");
        }

        return report;
    }

    private void Record(string kind, string item, string source)
    {
        Entries.Add(new DiffEntry { Kind = kind, Item = item, Source = source });

        if (Lines.Count < LineLimit) Lines.Add(new DiffEntry { Kind = kind, Item = item, Source = source }.Label());
    }

    public static string Source(Recipe recipe) =>
        recipe.Source.Length > 0 ? recipe.Source : "onbekend";

    public static string Signature(Recipe recipe)
    {
        var parts = new List<string>
        {
            recipe.Category,
            recipe.Output.ToString(),
            string.Join(",", recipe.Ingredients.OrderBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
                                               .Select(i => i.Item + "x" + i.Count))
        };

        parts.AddRange(recipe.Alternatives.Select(Signature));
        return string.Join("|", parts);
    }
}

public sealed class ValidationResult
{
    public List<string> Lines { get; } = new();
    public int Errors { get; set; }
    public int Warnings { get; set; }

    public string Summary => Errors > 0
        ? $"✖ {Errors} fout(en) · {Warnings} waarschuwing(en)"
        : $"✔ geen fouten · {Warnings} waarschuwing(en)";

    public static ValidationResult Run(Dictionary<string, Recipe> recipes, IEnumerable<RecipeOp>? patches = null,
                                       int deadPatches = 0)
    {
        var result = new ValidationResult();
        var selfIngredients = new List<string>();
        var twoCycles = new List<string>();
        var unknown = new List<string>();

        foreach (var kv in recipes)
        {
            foreach (var ingredient in kv.Value.Ingredients)
            {
                if (ingredient.Item.Length == 0) continue;

                // 1. Recept dat zichzelf als ingrediënt heeft. Packs doen dit soms expres
                //    (een "upgrade" die het vorige item van dezelfde naam opvreet); de
                //    planner slaat zo'n recept over, dus dit is een waarschuwing.
                if (ingredient.Item.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    selfIngredients.Add(kv.Key);
                    continue;
                }

                if (!recipes.TryGetValue(ingredient.Item, out var other))
                {
                    // 2. Ingrediënt dat nergens een recept heeft (tags en machines hebben
                    //    een dubbele punt en blijven buiten beschouwing).
                    if (!ingredient.Item.Contains(':') || ingredient.Item.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
                        unknown.Add($"{kv.Key} → {ingredient.Item}");

                    continue;
                }

                // 3. Twee recepten die elkaar maken.
                if (other.Ingredients.Any(i => i.Item.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                    twoCycles.Add($"{kv.Key} ↔ {ingredient.Item}");
            }
        }

        AddGroup(result, selfIngredients, "gebruikt zichzelf als ingrediënt (planner slaat hem over)");
        AddGroup(result, twoCycles, "vormt een kringetje van twee");
        AddGroup(result, unknown, "heeft een ingrediënt zonder eigen recept");

        if (deadPatches > 0)
        {
            result.Warnings++;
            result.Lines.Add($"⚠ {deadPatches} patch(es) raken een recept dat niet bestaat");
        }

        if (patches != null)
            foreach (var warning in patches.Where(p => p.Op == "warn").Take(20))
            {
                result.Warnings++;
                result.Lines.Add($"⚠ {warning.Where}: {warning.Msg}");
            }

        return result;
    }

    /// <summary>Eén regel per soort melding, met de eerste voorbeelden erbij.</summary>
    private static void AddGroup(ValidationResult result, List<string> items, string message)
    {
        if (items.Count == 0) return;

        result.Warnings++;
        string examples = string.Join(", ", items.Distinct(StringComparer.OrdinalIgnoreCase).Take(3));

        result.Lines.Add($"⚠ {items.Count} recept(en) {message} — o.a. {examples}");
    }
}

/// <summary>De profielenlijst: welke packs zijn geïndexeerd en welke is actief (PLAN.md fase 5).</summary>
public sealed class ProfileBook
{
    public int Schema { get; set; } = 1;
    public string Active { get; set; } = "";
    public List<PackProfile> Packs { get; set; } = new();
}

public static class Profiles
{
    public static string FilePath(string dataDirectory) => Path.Combine(dataDirectory, "profiles.json");

    public static ProfileBook Load(string dataDirectory)
    {
        try
        {
            string path = FilePath(dataDirectory);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ProfileBook>(File.ReadAllText(path)) ?? new ProfileBook();
        }
        catch
        {
            // Kapotte profielenlijst: begin opnieuw.
        }

        return new ProfileBook();
    }

    public static void Save(string dataDirectory, ProfileBook book)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(FilePath(dataDirectory),
                              JsonSerializer.Serialize(book, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Profielen zijn een gemak, geen voorwaarde.
        }
    }

    /// <summary>
    /// Voegt het pack toe of werkt het bij, en maakt het actief. Geeft het vorige profiel
    /// terug zodat de aanroeper een pack-update kan melden.
    /// </summary>
    public static PackProfile? Upsert(string dataDirectory, PackProfile profile)
    {
        var book = Load(dataDirectory);
        var previous = book.Packs.FirstOrDefault(p => p.Pack.Equals(profile.Pack, StringComparison.OrdinalIgnoreCase));

        // "fold of recipes.json" is een keuze van de gebruiker; een herindex mag die niet wissen.
        if (previous != null) profile.UseFold = previous.UseFold;

        book.Packs.RemoveAll(p => p.Pack.Equals(profile.Pack, StringComparison.OrdinalIgnoreCase));
        book.Packs.Insert(0, profile);
        book.Active = profile.Pack;

        if (book.Packs.Count > 12) book.Packs.RemoveRange(12, book.Packs.Count - 12);

        Save(dataDirectory, book);
        return previous;
    }

    /// <summary>Onthoudt per pack of de fold of recipes.json gebruikt moet worden.</summary>
    public static void SetSource(string dataDirectory, string packSlug, bool useFold)
    {
        var book = Load(dataDirectory);
        var profile = book.Packs.FirstOrDefault(p => p.Pack.Equals(packSlug, StringComparison.OrdinalIgnoreCase));
        if (profile == null) return;

        profile.UseFold = useFold;
        Save(dataDirectory, book);

        // pack.json is de momentopname van het actieve pack; houd die gelijk.
        string packFile = Path.Combine(dataDirectory, "pack.json");
        var active = PackProfile.Load(packFile);
        if (active != null && active.Pack.Equals(packSlug, StringComparison.OrdinalIgnoreCase))
        {
            active.UseFold = useFold;
            active.Save(packFile);
        }
    }

    public static PackProfile? Find(string dataDirectory, string slugOrName)
    {
        var book = Load(dataDirectory);

        return book.Packs.FirstOrDefault(p => p.Pack.Equals(slugOrName, StringComparison.OrdinalIgnoreCase))
               ?? book.Packs.FirstOrDefault(p => p.Instance.Equals(slugOrName, StringComparison.OrdinalIgnoreCase));
    }
}
