using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcrpv;

/// <summary>
/// Wie is deze instance? Packnaam, Minecraft-versie en loader komen uit het bestand dat
/// de launcher er zelf neerzet (Prism/MultiMC, CurseForge, Modrinth), met de mapnaam als
/// laatste redmiddel. Bewaard in pack.json, zodat de app weet bij welk pack een index hoort.
/// </summary>
public sealed class PackProfile
{
    public int Schema { get; set; } = 4;
    public string Pack { get; set; } = "";
    public string Minecraft { get; set; } = "";
    public string Loader { get; set; } = "";
    public string Instance { get; set; } = "";
    /// <summary>Volledig pad naar de instance, zodat --reindex=pack hem terugvindt.</summary>
    public string InstancePath { get; set; } = "";
    public string BaseFile { get; set; } = "";
    public string PatchFile { get; set; } = "";
    public int Jars { get; set; }
    public int BaseCount { get; set; }
    public int PackCount { get; set; }
    public int UserCount { get; set; }
    public int ResultCount { get; set; }
    public int Parser { get; set; }
    /// <summary>Vingerafdruk van de jars (aantal + nieuwste mtime) om pack-updates te zien.</summary>
    public string JarStamp { get; set; } = "";
    public DateTime Built { get; set; }

    /// <summary>
    /// Onthouden keuze per pack: true = de fold van dit pack laden, false = recipes.json.
    /// Zo geeft een herstart dezelfde set terug (gezet met 📥/📄 in het wijzigingen-venster).
    /// </summary>
    public bool UseFold { get; set; }

    public string Caption =>
        (Pack.Length > 0 ? Pack : "onbekend pack") +
        (Minecraft.Length > 0 ? $" · MC {Minecraft}" : "") +
        (Loader.Length > 0 ? $" · {Loader}" : "");

    public static PackProfile Detect(string instanceRoot)
    {
        var profile = new PackProfile
        {
            Instance = Path.GetFileName(instanceRoot.TrimEnd('\\', '/')),
            InstancePath = instanceRoot
        };

        ReadMultiMc(instanceRoot, profile);
        if (profile.Minecraft.Length == 0) ReadCurseForge(instanceRoot, profile);
        if (profile.Minecraft.Length == 0) ReadModrinth(instanceRoot, profile);

        profile.Pack = Slug(profile.Instance);
        return profile;
    }

    /// <summary>Prism/MultiMC: mmc-pack.json naast de instance-map, naam uit instance.cfg.</summary>
    private static void ReadMultiMc(string root, PackProfile profile)
    {
        string file = Path.Combine(root, "mmc-pack.json");
        if (!File.Exists(file)) return;

        try
        {
            var components = JsonNode.Parse(File.ReadAllText(file))?["components"]?.AsArray();
            foreach (var component in components ?? new JsonArray())
            {
                string uid = component?["uid"]?.ToString() ?? "";
                string version = component?["version"]?.ToString() ?? "";

                if (uid.Equals("net.minecraft", StringComparison.OrdinalIgnoreCase))
                    profile.Minecraft = version;
                else if (uid.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
                    profile.Loader = version.Length > 0 ? "forge " + version : "forge";
                else if (uid.Equals("net.fabricmc.fabric-loader", StringComparison.OrdinalIgnoreCase))
                    profile.Loader = version.Length > 0 ? "fabric " + version : "fabric";
                else if (uid.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase))
                    profile.Loader = version.Length > 0 ? "neoforge " + version : "neoforge";
            }
        }
        catch
        {
            // Kapotte mmc-pack.json: de mapnaam blijft over.
        }

        foreach (string line in ReadLines(Path.Combine(root, "instance.cfg")))
        {
            if (!line.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) continue;
            string name = line[5..].Trim();
            if (name.Length > 0) profile.Instance = name;
            break;
        }
    }

    /// <summary>CurseForge: manifest.json met minecraft.version en minecraft.modLoaders.</summary>
    private static void ReadCurseForge(string root, PackProfile profile)
    {
        string file = Path.Combine(root, "manifest.json");
        if (!File.Exists(file)) return;

        try
        {
            var rootNode = JsonNode.Parse(File.ReadAllText(file));
            profile.Minecraft = rootNode?["minecraft"]?["version"]?.ToString() ?? "";

            string loader = rootNode?["minecraft"]?["modLoaders"]?.AsArray()?.FirstOrDefault()?["id"]?.ToString() ?? "";
            if (loader.Length > 0) profile.Loader = loader;

            string name = rootNode?["name"]?.ToString() ?? "";
            if (name.Length > 0) profile.Instance = name;
        }
        catch
        {
            // Onleesbaar manifest: niet erg, de rest vult aan.
        }
    }

    /// <summary>Modrinth: modrinth.index.json met dependencies.minecraft en de loader.</summary>
    private static void ReadModrinth(string root, PackProfile profile)
    {
        string file = Path.Combine(root, "modrinth.index.json");
        if (!File.Exists(file)) return;

        try
        {
            var rootNode = JsonNode.Parse(File.ReadAllText(file));
            var dependencies = rootNode?["dependencies"];
            profile.Minecraft = dependencies?["minecraft"]?.ToString() ?? "";

            foreach (string loader in new[] { "forge", "neoforge", "fabric-loader", "quilt-loader" })
            {
                string version = dependencies?[loader]?.ToString() ?? "";
                if (version.Length == 0) continue;

                profile.Loader = loader.Replace("-loader", "") + " " + version;
                break;
            }

            string name = rootNode?["name"]?.ToString() ?? "";
            if (name.Length > 0) profile.Instance = name;
        }
        catch
        {
            // Onleesbare index: niet erg.
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();

        try { return File.ReadAllLines(path); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>"Project Ozone 3 A New Way Forward" → "project-ozone-3-a-new-way-forward".</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        return sb.ToString().Trim('-');
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Een ontbrekend pack.json is niet fataal: de volgende indexbeurt schrijft hem.
        }
    }

    public static PackProfile? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PackProfile>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>De inhoud van één jar, opgeslagen onder zijn inhoud-hash.</summary>
public sealed class JarShard
{
    public int Schema { get; set; } = 1;
    public string Jar { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string ModId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public int JsonRecipes { get; set; }
    public int Scripts { get; set; }
    public Dictionary<string, Recipe> Recipes { get; set; } = new();
    public Dictionary<string, string> Names { get; set; } = new();
}

public sealed class JarIndexResult
{
    public List<JarShard> Shards { get; } = new();
    public int Read;
    public int Cached;
    public int Hashed;

    public string Summary =>
        $"{Shards.Count} jars · {Read} gelezen · {Cached} uit cache · {Hashed} gehasht";
}

/// <summary>
/// Index van alle jars, met een shard per inhoud-hash. Twee packs die dezelfde jar delen
/// (zelfde hash) hergebruiken elkaars werk; een tweede run opent geen enkele zip meer.
/// </summary>
public static class JarIndex
{
    private sealed class Entry
    {
        public string File { get; set; } = "";
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public string Sha { get; set; } = "";
    }

    private sealed class Manifest
    {
        public int Schema { get; set; } = 1;
        public List<Entry> Jars { get; set; } = new();
    }

    public static JarIndexResult Build(string instanceRoot, string shardDir, Action<string>? progress = null,
                                       CancellationToken cancel = default)
    {
        string? game = ModRecipeScanner.GameFolder(instanceRoot)
                       ?? throw new DirectoryNotFoundException("Geen mods-map gevonden in deze instance");
        string modsFolder = Path.Combine(game, "mods");
        Directory.CreateDirectory(shardDir);

        var jars = Directory.GetFiles(modsFolder, "*.jar", SearchOption.AllDirectories)
                            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                            .ToList();

        var known = LoadManifest(shardDir);
        var result = new JarIndexResult();
        var manifest = new Manifest();

        foreach (string jar in jars)
        {
            if (cancel.IsCancellationRequested) return result;

            var info = new FileInfo(jar);
            string sha;

            // Grootte én tijd gelijk: dan hoeft het bestand niet gelezen te worden om te
            // weten dat het dezelfde shard is.
            if (known.TryGetValue(info.Name, out var entry) &&
                entry.Size == info.Length && entry.Modified == info.LastWriteTime)
            {
                sha = entry.Sha;
            }
            else
            {
                sha = HashFile(jar);
                result.Hashed++;
            }

            string shardPath = Path.Combine(shardDir, sha + ".json");
            var shard = LoadShard(shardPath);

            if (shard == null)
            {
                progress?.Invoke($"jar lezen: {info.Name}");

                var mod = ModRecipeScanner.ReadJar(info);
                shard = new JarShard
                {
                    Jar = info.Name,
                    Sha256 = sha,
                    Size = info.Length,
                    ModId = mod.ModId,
                    DisplayName = mod.DisplayName,
                    Version = mod.Version,
                    JsonRecipes = mod.JsonRecipes,
                    Scripts = mod.Scripts
                };

                ModRecipeScanner.ReadJarRecipes(modsFolder, mod, shard.Recipes, shard.Names);
                SaveShard(shardPath, shard);
                result.Read++;
            }
            else
            {
                result.Cached++;
            }

            // Dezelfde jar kan onder een andere naam staan; de shard blijft inhoudelijk gelijk.
            shard.Jar = info.Name;
            result.Shards.Add(shard);

            manifest.Jars.Add(new Entry
            {
                File = info.Name,
                Size = info.Length,
                Modified = info.LastWriteTime,
                Sha = sha
            });
        }

        SaveManifest(shardDir, manifest);
        PruneShards(shardDir, manifest.Jars.Select(j => j.Sha).ToHashSet(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static Dictionary<string, Entry> LoadManifest(string shardDir)
    {
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(shardDir, "index.json");
        if (!File.Exists(path)) return result;

        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
            foreach (var entry in manifest?.Jars ?? new List<Entry>())
                if (entry.File.Length > 0 && entry.Sha.Length > 0)
                    result[entry.File] = entry;
        }
        catch
        {
            // Kapotte index: alles wordt gewoon opnieuw gelezen.
        }

        return result;
    }

    private static void SaveManifest(string shardDir, Manifest manifest)
    {
        try
        {
            File.WriteAllText(Path.Combine(shardDir, "index.json"),
                              JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Volgende run leest de jars opnieuw; geen fout voor de gebruiker.
        }
    }

    private static JarShard? LoadShard(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<JarShard>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveShard(string path, JarShard shard)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(shard, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Shards van jars die niet meer in de instance staan opruimen.</summary>
    private static void PruneShards(string shardDir, HashSet<string> keep)
    {
        try
        {
            foreach (string file in Directory.GetFiles(shardDir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Equals("index", StringComparison.OrdinalIgnoreCase)) continue;
                if (keep.Contains(name)) continue;

                File.Delete(file);
            }
        }
        catch
        {
            // Opruimen is luxe; een mislukte delete mag niets breken.
        }
    }
}

public sealed class BaseIndexResult
{
    public string Hash { get; init; } = "";
    public string Path { get; init; } = "";
    public Dictionary<string, Recipe> Recipes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Names { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int Mods { get; init; }
    public int JarRecipes { get; init; }
    public bool FromCache { get; init; }
}

/// <summary>
/// De basislaag: alle jar-shards samengevoegd tot één bestand. De bestandsnaam bevat een
/// hash over de shards en de parser-versie, dus een gewijzigde jar of een parserfix levert
/// automatisch een nieuwe basis op en nooit een oude.
/// </summary>
public static class BaseIndex
{
    /// <summary>Ophogen bij elke wijziging in de jar-parser; dwingt een nieuwe basis af.</summary>
    public const int Parser = 1;

    private sealed class BaseFile
    {
        public int Schema { get; set; } = 2;
        public int Parser { get; set; }
        public string Hash { get; set; } = "";
        public DateTime Built { get; set; }
        public int Mods { get; set; }
        public int JarRecipes { get; set; }
        public Dictionary<string, Recipe> Recipes { get; set; } = new();
        public Dictionary<string, string> Names { get; set; } = new();
    }

    public static string ComputeHash(IEnumerable<JarShard> shards)
    {
        string joined = string.Join("\n", shards.Select(s => s.Sha256).OrderBy(s => s, StringComparer.Ordinal)) +
                        "|parser:" + Parser;

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
    }

    public static BaseIndexResult Build(string dataDirectory, IReadOnlyList<JarShard> shards,
                                        Action<string>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(dataDirectory);
        string hash = ComputeHash(shards);
        string path = Path.Combine(dataDirectory, "base-" + hash + ".json");

        if (File.Exists(path))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<BaseFile>(File.ReadAllText(path));
                if (cached != null && string.Equals(cached.Hash, hash, StringComparison.Ordinal))
                    return new BaseIndexResult
                    {
                        Hash = hash,
                        Path = path,
                        Recipes = new Dictionary<string, Recipe>(cached.Recipes, StringComparer.OrdinalIgnoreCase),
                        Names = new Dictionary<string, string>(cached.Names, StringComparer.OrdinalIgnoreCase),
                        Mods = cached.Mods,
                        JarRecipes = cached.JarRecipes,
                        FromCache = true
                    };
            }
            catch
            {
                // Onleesbare basis: hieronder opnieuw opbouwen.
            }
        }

        var recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int jarRecipes = 0;

        foreach (var shard in shards)
        {
            if (cancel.IsCancellationRequested)
                return new BaseIndexResult { Hash = hash, Path = path };

            progress?.Invoke($"basis: {shard.Jar}");
            jarRecipes += shard.JsonRecipes;

            string source = "jar-JSON " + (shard.ModId.Length > 0 ? shard.ModId : shard.Jar);

            foreach (var kv in shard.Recipes)
                ModRecipeScanner.MergeInto(recipes, names, kv.Key, kv.Value, source);

            foreach (var kv in shard.Names) names.TryAdd(kv.Key, kv.Value);
        }

        // Zelfde nabewerking als een scan, anders wijkt de basis af bij tag-ingrediënten.
        ModRecipeScanner.ResolveGroupIngredients(recipes, names);

        var file = new BaseFile
        {
            Parser = Parser,
            Hash = hash,
            Built = DateTime.Now,
            Mods = shards.Count,
            JarRecipes = jarRecipes,
            Recipes = recipes,
            Names = names
        };

        File.WriteAllText(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        PruneBaseFiles(dataDirectory, Path.GetFileName(path));

        return new BaseIndexResult
        {
            Hash = hash,
            Path = path,
            Recipes = recipes,
            Names = names,
            Mods = shards.Count,
            JarRecipes = jarRecipes
        };
    }

    /// <summary>Een bestaand basisbestand inlezen (voor --reindex=pack, zonder shards).</summary>
    public static BaseIndexResult? Load(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var file = JsonSerializer.Deserialize<BaseFile>(File.ReadAllText(path));
            if (file == null) return null;

            return new BaseIndexResult
            {
                Hash = file.Hash,
                Path = path,
                Recipes = new Dictionary<string, Recipe>(file.Recipes, StringComparer.OrdinalIgnoreCase),
                Names = new Dictionary<string, string>(file.Names, StringComparer.OrdinalIgnoreCase),
                Mods = file.Mods,
                JarRecipes = file.JarRecipes,
                FromCache = true
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Oude basisbestanden opruimen: alleen de nieuwste twee blijven staan.</summary>
    private static void PruneBaseFiles(string dataDirectory, string keep)
    {
        try
        {
            var files = Directory.GetFiles(dataDirectory, "base-*.json")
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
