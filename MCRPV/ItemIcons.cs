using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mcrpv;

/// <summary>
/// Item-iconen: de echte 16×16-textures uit de mods en resourcepacks van de instance.
/// De index wordt één keer per instance opgebouwd (alleen bestandsnamen uit de archieven)
/// en de bitmaps pas bij gebruik gelezen en gecacht. Elk archief moet daarvoor open, dus
/// het opbouwen gebeurt op de achtergrond (<see cref="EnsureLoadedAsync"/>); tot de index
/// klaar is tekent de bracketweergave de gekleurde terugval-tegels.
///</summary>
public static class ItemIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    // De index zelf (textures, modellen en de woorddelen-tabel) wordt op de achtergrond
    // opgebouwd en daarna in één keer vervangen: lezers zien nooit een half gevulde tabel
    // en de UI-thread wacht nergens op.
    private static Index _index = new();

    private static string? _loadedRoot;
    private static string? _loadingRoot;
    private static Task<bool>? _loadTask;

    private sealed class Index
    {
        public string Root = "";

        public readonly Dictionary<string, (string Archive, string Entry)> Textures =
            new(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<string, (string Archive, string Entry)> Models =
            new(StringComparer.OrdinalIgnoreCase);

        // Texture-index op woorddelen: "aluminium_ingot" en "ingot_aluminium" zijn hetzelfde item.
        public readonly Dictionary<string, (string Archive, string Entry)> ByWords =
            new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Aantal gevonden textures (0 als de instance nog niet gescand is).</summary>
    public static int Count => _index.Textures.Count;

    /// <summary>Aantal gevonden item-modellen.</summary>
    public static int ModelCount => _index.Models.Count;

    /// <summary>Leest de textuur- en modelindex van deze instance, één keer per instance.</summary>
    public static void EnsureLoaded(string? instanceRoot)
    {
        if (string.IsNullOrWhiteSpace(instanceRoot) || !Directory.Exists(instanceRoot)) return;
        if (IsLoaded(instanceRoot)) return;

        Publish(BuildIndex(instanceRoot));
    }

    /// <summary>
    /// Hetzelfde als <see cref="EnsureLoaded"/>, maar het indexeren gebeurt op de
    /// achtergrond zodat de eerste bracket-tekening niet seconden blijft hangen. Eén
    /// indexering tegelijk: een tweede vraag om dezelfde instance wacht op dezelfde taak.
    /// Geeft true als de index daarna klaar is.
    /// </summary>
    public static Task<bool> EnsureLoadedAsync(string? instanceRoot)
    {
        if (string.IsNullOrWhiteSpace(instanceRoot) || !Directory.Exists(instanceRoot))
            return Task.FromResult(false);

        if (IsLoaded(instanceRoot)) return Task.FromResult(true);

        if (_loadTask != null && string.Equals(_loadingRoot, instanceRoot, StringComparison.OrdinalIgnoreCase))
            return _loadTask;

        _loadingRoot = instanceRoot;
        _loadTask = LoadAsync(instanceRoot);
        return _loadTask;
    }

    /// <summary>Is de index van deze instance al binnen?</summary>
    public static bool IsLoaded(string? instanceRoot) =>
        !string.IsNullOrWhiteSpace(instanceRoot) &&
        string.Equals(_loadedRoot, instanceRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>Loopt er nu een indexering voor deze instance?</summary>
    public static bool IsLoading(string? instanceRoot) =>
        _loadTask != null &&
        string.Equals(_loadingRoot, instanceRoot, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> LoadAsync(string instanceRoot)
    {
        try
        {
            var index = await Task.Run(() => BuildIndex(instanceRoot));
            Publish(index);
            return true;
        }
        finally
        {
            _loadTask = null;
            _loadingRoot = null;
        }
    }

    private static void Publish(Index index)
    {
        _index = index;
        _loadedRoot = index.Root;
        Cache.Clear();
    }

    private static Index BuildIndex(string instanceRoot)
    {
        var index = new Index { Root = instanceRoot };

        foreach (var gameDir in GameDirs(instanceRoot))
        {
            foreach (var sub in new[] { "mods", "resourcepacks", "texturepacks", "mods-resourcepacks" })
            {
                string dir = Path.Combine(gameDir, sub);
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Files(dir, "*.jar").Concat(Files(dir, "*.zip")))
                    ScanArchive(index, file);
            }

            // Vanilla-items (iron_ingot, stick, …) zitten in de client-jar; die staat bij
            // een instance meestal niet, maar als hij er is pakken we hem meteen mee.
            string versions = Path.Combine(gameDir, "versions");
            if (Directory.Exists(versions))
            {
                foreach (var jar in Files(versions, "*.jar", recursive: true))
                    ScanArchive(index, jar);
            }
        }

        // De vanilla client-jar staat bij de launcher zelf (PrismLauncher/libraries), niet in de
        // instance. Zonder die jar houden items als iron_ingot of stick hun gekleurde tegel.
        foreach (var jar in LauncherJars(instanceRoot))
            ScanArchive(index, jar);

        return index;
    }

    /// <summary>Icoon voor een receptsleutel ("iron_ingot", "thermal:iron_ingot" of "Iron Ingot"), of null.</summary>
    public static ImageSource? Get(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        string normalized = RecipePlanner.NormalizeName(key);
        if (normalized.Length == 0) return null;

        if (Cache.TryGetValue(normalized, out var cached)) return cached;

        string? scope = null;
        int colon = key.IndexOf(':');
        if (colon > 0) scope = key[..colon].Trim().ToLowerInvariant();

        ImageSource? image = null;

        // 1. Texture met dezelfde naam (eerst binnen de mod, dan wereldwijd).
        image = LookupTexture(normalized, scope);

        // 2. Anders via het item-model: dat wijst de echte texture aan.
        if (image == null)
            image = LookupModelTexture(normalized, scope);

        // 3. Anders op woorddelen: "aluminium_ingot" hoort bij "ingot_aluminium".
        if (image == null)
            image = LookupByWords(normalized);

        Cache[normalized] = image;
        return image;
    }

    private static ImageSource? LookupTexture(string name, string? scope)
    {
        foreach (var candidate in Candidates(name))
        {
            if (scope != null && _index.Textures.TryGetValue(scope + ":" + candidate, out var scoped))
            {
                var image = Load(scoped);
                if (image != null) return image;
            }

            if (_index.Textures.TryGetValue(candidate, out var plain))
            {
                var image = Load(plain);
                if (image != null) return image;
            }
        }

        return null;
    }

    private static ImageSource? LookupModelTexture(string name, string? scope)
    {
        foreach (var candidate in Candidates(name))
        {
            (string Archive, string Entry)? model =
                scope != null && _index.Models.TryGetValue(scope + ":" + candidate, out var scoped) ? scoped
                : _index.Models.TryGetValue(candidate, out var plain) ? plain
                : null;

            if (model == null) continue;

            string? texture = ReadModelTexture(model.Value);
            if (texture == null) continue;

            var image = LookupTexture(texture, null);
            if (image != null) return image;
        }

        return null;
    }

    private static ImageSource? LookupByWords(string name)
    {
        foreach (var candidate in Candidates(name))
        {
            if (!_index.ByWords.TryGetValue(Words(candidate), out var location)) continue;

            var image = Load(location);
            if (image != null) return image;
        }

        return null;
    }

    /// <summary>"ingot_aluminium" en "aluminium_ingot" geven dezelfde sleutel.</summary>
    private static string Words(string name)
    {
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(parts, StringComparer.OrdinalIgnoreCase);
        return string.Join("_", parts);
    }

    /// <summary>
    /// Zoekt de textuur van een item-model. Modellen erven hun texture vaak via "parent"
    /// (item/generated, block/cube_all) en verwijzen binnen het model met "#naam" naar een
    /// andere sleutel; beide volgen we door, anders bleef juist voor vanilla-items een lege
    /// tegel over.
    /// </summary>
    private static string? ReadModelTexture((string Archive, string Entry) location) =>
        ReadModelTexture(location.Archive, location.Entry, 0);

    private static string? ReadModelTexture(string archive, string entry, int depth)
    {
        if (depth > 6) return null;

        JsonObject? model;

        try
        {
            using var zip = ZipFile.OpenRead(archive);

            var file = zip.GetEntry(entry);
            if (file == null) return null;

            using var reader = new StreamReader(file.Open());
            model = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        }
        catch
        {
            // Model stuk of geen JSON: gewoon zonder icoon verder.
            return null;
        }

        if (model == null) return null;

        if (model["textures"] is JsonObject textures)
        {
            foreach (var key in new[]
            {
                "layer0", "layer1", "layer2", "layer3", "layer4",
                "texture", "all", "side", "top", "front", "particle"
            })
            {
                string? reference = textures[key]?.ToString();
                if (string.IsNullOrWhiteSpace(reference)) continue;

                // "#all" wijst naar een andere textures-sleutel in hetzelfde model. De
                // verwijzing kan naar niets wijzen; dan is reference null en mag de lus
                // niet opnieuw StartsWith aanroepen (dat gaf een NullReferenceException).
                for (int hop = 0; hop < 4 && reference != null && reference.StartsWith('#'); hop++)
                    reference = textures[reference[1..]]?.ToString();

                if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('#')) continue;

                string texture = TextureName(reference);
                if (texture.Length > 0) return texture;
            }
        }

        // Geen eigen texture: via de parent verder zoeken (item/generated, block/cube_all, …).
        string? parent = model["parent"]?.ToString();
        if (string.IsNullOrWhiteSpace(parent)) return null;

        parent = parent[(parent.LastIndexOf(':') + 1)..];
        parent = parent[(parent.LastIndexOf('/') + 1)..];
        if (parent.Length == 0) return null;

        (string Archive, string Entry)? inherited =
            _index.Models.TryGetValue("minecraft:" + parent, out var vanilla) ? vanilla
            : _index.Models.TryGetValue(parent, out var plain) ? plain
            : null;

        return inherited == null
            ? null
            : ReadModelTexture(inherited.Value.Archive, inherited.Value.Entry, depth + 1);
    }

    /// <summary>"minecraft:items/iron_ingot" → "iron_ingot"; de index bewaart alleen de bestandsnaam.</summary>
    private static string TextureName(string reference)
    {
        string texture = reference[(reference.LastIndexOf(':') + 1)..];
        int slash = texture.LastIndexOf('/');
        return slash >= 0 ? texture[(slash + 1)..] : texture;
    }

    /// <summary>Naamvarianten: "machineblock2" → ook "machineblock" en "machineblock2" zonder underscores.</summary>
    private static IEnumerable<string> Candidates(string name)
    {
        yield return name;

        string withoutDigits = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (withoutDigits.Length > 0 && !withoutDigits.Equals(name, StringComparison.OrdinalIgnoreCase))
            yield return withoutDigits;

        string compact = name.Replace("_", "");
        if (!compact.Equals(name, StringComparison.OrdinalIgnoreCase) && compact.Length > 0)
            yield return compact;
    }

    /// <summary>
    /// De client-jar van de juiste Minecraft-versie staat in de launcher-map
    /// (&lt;launcher&gt;/libraries/com/mojang/minecraft/1.12.2/…): daar zitten alle vanilla-textures.
    /// We pakken alleen de versie die de instance gebruikt, anders zouden textures van een
    /// andere Minecraft-versie de index vervuilen.
    /// </summary>
    private static IEnumerable<string> LauncherJars(string instanceRoot)
    {
        DirectoryInfo? launcher = null;

        for (var current = new DirectoryInfo(instanceRoot); current != null; current = current.Parent)
        {
            if (!current.Name.Equals("instances", StringComparison.OrdinalIgnoreCase)) continue;
            launcher = current.Parent;
            break;
        }

        if (launcher == null) yield break;

        var jars = new List<string>();

        string mojang = Path.Combine(launcher.FullName, "libraries", "com", "mojang", "minecraft");
        foreach (var dir in Files(mojang, "*.jar", recursive: true))
            jars.Add(dir);

        foreach (var dir in Files(Path.Combine(launcher.FullName, "versions"), "*.jar", recursive: true))
            jars.Add(dir);

        string? version = MinecraftVersion(instanceRoot);
        var wanted = version == null
            ? jars
            : jars.Where(jar => Path.GetFileName(jar).Contains(version, StringComparison.OrdinalIgnoreCase)).ToList();

        // Geen jar voor precies deze versie gevonden: dan maar alles wat er staat.
        if (wanted.Count == 0) wanted = jars;

        foreach (var jar in wanted)
            yield return jar;
    }

    /// <summary>Leest "net.minecraft" uit mmc-pack.json: dat is de versie waarvan we de jar willen.</summary>
    private static string? MinecraftVersion(string instanceRoot)
    {
        try
        {
            string file = Path.Combine(instanceRoot, "mmc-pack.json");
            if (!File.Exists(file)) return null;

            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject pack) return null;
            if (pack["components"] is not JsonArray components) return null;

            foreach (var component in components)
            {
                if (component?["uid"]?.ToString() != "net.minecraft") continue;
                return component["version"]?.ToString();
            }
        }
        catch
        {
            // Geen (leesbare) pack-info: dan pakken we gewoon elke gevonden jar.
        }

        return null;
    }

    private static IEnumerable<string> GameDirs(string instanceRoot)
    {
        yield return instanceRoot;

        foreach (var sub in new[] { "minecraft", ".minecraft" })
        {
            string dir = Path.Combine(instanceRoot, sub);
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    private static IEnumerable<string> Files(string dir, string pattern, bool recursive = false)
    {
        try
        {
            return Directory.GetFiles(dir, pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void ScanArchive(Index index, string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName;
                if (!name.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) continue;

                string mod = name.Split('/')[1];

                if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    IndexTexture(index, path, name, mod);
                    continue;
                }

                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    IndexModel(index, path, name, mod);
            }
        }
        catch
        {
            // Eén kapotte jar mag de rest niet tegenhouden.
        }
    }

    private static void IndexTexture(Index index, string archive, string name, string mod)
    {
        int textures = name.IndexOf("/textures/", StringComparison.OrdinalIgnoreCase);
        if (textures < 0) return;

        string rest = name[(textures + "/textures/".Length)..];
        int slash = rest.LastIndexOf('/');
        if (slash <= 0) return;

        string folder = rest[..slash];
        string top = folder.Split('/')[0];
        if (!IsItemFolder(top)) return;

        string key = rest[(slash + 1)..^4];

        index.Textures.TryAdd(key, (archive, name));
        index.Textures.TryAdd(mod + ":" + key, (archive, name));
        index.ByWords.TryAdd(Words(key), (archive, name));
    }

    private static void IndexModel(Index index, string archive, string name, string mod)
    {
        int models = name.IndexOf("/models/", StringComparison.OrdinalIgnoreCase);
        if (models < 0) return;

        string rest = name[(models + "/models/".Length)..];
        string[] parts = rest.Split('/');
        if (parts.Length < 2) return;

        string kind = parts[0];
        if (!kind.Equals("item", StringComparison.OrdinalIgnoreCase) &&
            !kind.Equals("block", StringComparison.OrdinalIgnoreCase)) return;

        string key = rest[(rest.LastIndexOf('/') + 1)..^5];

        index.Models.TryAdd(key, (archive, name));
        index.Models.TryAdd(mod + ":" + key, (archive, name));
    }

    private static bool IsItemFolder(string folder) =>
        folder.Equals("item", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("items", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("block", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("blocks", StringComparison.OrdinalIgnoreCase);

    private static ImageSource? Load((string Archive, string Entry) location)
    {
        try
        {
            using var archive = ZipFile.OpenRead(location.Archive);

            var entry = archive.GetEntry(location.Entry);
            if (entry == null) return null;

            using var stream = entry.Open();
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = buffer;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
