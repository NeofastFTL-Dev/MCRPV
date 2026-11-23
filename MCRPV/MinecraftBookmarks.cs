using System.IO;
using System.Linq;

namespace Mcrpv;

/// <summary>Gevonden JEI-bookmarksbestand met de instance waar het bij hoort.</summary>
public sealed class MinecraftBookmarkFile
{
    public string Path { get; init; } = "";
    public string Instance { get; init; } = "";
    public DateTime Modified { get; init; }

    public string ModifiedText => Modified == DateTime.MinValue ? "onbekend" : Modified.ToString("yyyy-MM-dd HH:mm");
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    public override string ToString() => $"{Instance} · {ModifiedText}";
}

/// <summary>
/// Zoekt JEI-bookmarks in Minecraft-instances van de gebruikelijke launchers
/// (officiële launcher, Prism/MultiMC/PolyMC, CurseForge, Modrinth, ATLauncher).
/// JEI bewaart ze als bookmarks.ini, soms onder config/jei en soms per wereld
/// onder saves/&lt;wereld&gt;/jei of config/jei/world/…
/// </summary>
public static class MinecraftBookmarks
{
    private static readonly string[] BookmarkExtensions = { ".ini", ".json", ".txt", ".cfg" };

    public static List<string> InstanceRoots()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var roots = new List<string>();

        void AddExisting(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            roots.Add(dir);
        }

        void AddInstances(string? parent)
        {
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return;

            try
            {
                foreach (var child in Directory.GetDirectories(parent))
                {
                    AddExisting(child);
                    AddExisting(Path.Combine(child, ".minecraft"));
                }
            }
            catch { }
        }

        AddExisting(Path.Combine(appData, ".minecraft"));

        AddInstances(Path.Combine(appData, "PrismLauncher", "instances"));
        AddInstances(Path.Combine(appData, "MultiMC", "instances"));
        AddInstances(Path.Combine(appData, "PolyMC", "instances"));
        AddInstances(Path.Combine(appData, "ModrinthApp", "profiles"));
        AddInstances(Path.Combine(appData, "com.modrinth.theseus", "profiles"));
        AddInstances(Path.Combine(appData, "ATLauncher", "instances"));
        AddInstances(Path.Combine(appData, "GDLauncher", "instances"));
        AddInstances(Path.Combine(userProfile, "curseforge", "minecraft", "Instances"));
        AddInstances(Path.Combine(documents, "curseforge", "minecraft", "Instances"));

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Alle bookmark-bestanden, nieuwste eerst.</summary>
    public static List<MinecraftBookmarkFile> FindAll()
    {
        var files = new Dictionary<string, MinecraftBookmarkFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in InstanceRoots())
        {
            foreach (var file in BookmarkFilesIn(root))
            {
                if (files.ContainsKey(file)) continue;

                DateTime modified;
                try { modified = File.GetLastWriteTime(file); }
                catch { modified = DateTime.MinValue; }

                files[file] = new MinecraftBookmarkFile
                {
                    Path = file,
                    Instance = InstanceName(file),
                    Modified = modified
                };
            }
        }

        return files.Values
                    .OrderByDescending(f => f.Modified)
                    .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    private static IEnumerable<string> BookmarkFilesIn(string root)
    {
        var result = new List<string>();

        // De game-bestanden staan óf direct in de instance-map (MultiMC-stijl) óf in
        // een minecraft/- of .minecraft/-submap (Prism en de officiële launcher).
        ScanGameFolder(root, result);

        foreach (var sub in new[] { "minecraft", ".minecraft" })
        {
            string dir = Path.Combine(root, sub);
            if (Directory.Exists(dir)) ScanGameFolder(dir, result);
        }

        return result;
    }

    /// <summary>
    /// Zoekt bookmark-bestanden in één game-map: bookmarks.ini staat direct in de map,
    /// onder config/jei of per wereld onder config/jei/world/… / saves/&lt;wereld&gt;/jei.
    /// </summary>
    private static void ScanGameFolder(string root, List<string> result)
    {
        void Scan(string? dir, SearchOption option)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

            try
            {
                foreach (var file in Directory.GetFiles(dir, "*bookmark*", option))
                {
                    string extension = Path.GetExtension(file);
                    if (BookmarkExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                        result.Add(file);
                }
            }
            catch { }
        }

        Scan(root, SearchOption.TopDirectoryOnly);
        Scan(Path.Combine(root, "jei"), SearchOption.AllDirectories);
        Scan(Path.Combine(root, "config"), SearchOption.AllDirectories);
        Scan(Path.Combine(root, "exports"), SearchOption.AllDirectories);

        // Per-wereld bookmarks: saves/<wereld>/jei/…
        string saves = Path.Combine(root, "saves");
        if (Directory.Exists(saves))
        {
            try
            {
                foreach (var world in Directory.GetDirectories(saves))
                    Scan(Path.Combine(world, "jei"), SearchOption.AllDirectories);
            }
            catch { }
        }
    }

    /// <summary>Naam van de instance of wereld waar het bestand bij hoort.</summary>
    public static string InstanceName(string path)
    {
        var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                               StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("instances", StringComparison.OrdinalIgnoreCase) ||
                parts[i].Equals("profiles", StringComparison.OrdinalIgnoreCase))
                return Nice(parts[i + 1]);
        }

        int marker = Array.FindIndex(parts, p =>
            p.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("jei", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("saves", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("exports", StringComparison.OrdinalIgnoreCase));

        if (marker > 0) return Nice(parts[marker - 1]);
        return "Minecraft";
    }

    /// <summary>
    /// De instance-map waar een bookmark-bestand bij hoort: het deel vóór
    /// config/, saves/, jei/ of exports/. Zo kan de app ook de recipes uit die
    /// instance halen.
    /// </summary>
    public static string? InstanceFolder(string path)
    {
        try
        {
            var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                                   StringSplitOptions.RemoveEmptyEntries);

            int marker = Array.FindIndex(parts, p =>
                p.Equals("config", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("saves", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("jei", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("exports", StringComparison.OrdinalIgnoreCase));

            if (marker > 0)
            {
                string root = string.Join(Path.DirectorySeparatorChar.ToString(), parts.Take(marker));
                if (Directory.Exists(root)) return root;
            }
        }
        catch { }

        return Path.GetDirectoryName(path);
    }

    /// <summary>Herkomst voor in de UI, of null als het geen Minecraft-bestand is.</summary>
    public static string? DescribeSource(string path)
    {
        var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                               StringSplitOptions.RemoveEmptyEntries);

        bool minecraftish = parts.Any(p =>
            p.Equals(".minecraft", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("instances", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("profiles", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("jei", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("saves", StringComparison.OrdinalIgnoreCase));

        return minecraftish ? InstanceName(path) : null;
    }

    private static string Nice(string name)
    {
        if (name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase)) return "Minecraft";
        return name;
    }
}
