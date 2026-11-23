using System.IO;
using System.Linq;

namespace Mcrpv;

/// <summary>
/// "Is de set nog actueel?": vergelijkt de laatste fold met de jars van de instance en met
/// een nieuwere JEI-dump. Alleen signaleren en uitleggen; opnieuw indexeren doet de knop.
/// Zonder fold of zonder pack is er niets om aan te merken: dan is dit rapport leeg.
/// </summary>
public sealed class FreshnessReport
{
    public bool JarsChanged { get; init; }
    public bool DumpNewer { get; init; }

    /// <summary>Ware als de fold ouder is dan de instance of de dump.</summary>
    public bool Stale => JarsChanged || DumpNewer;

    public DateTime FoldTime { get; init; }
    public DateTime DumpTime { get; init; }

    /// <summary>Eén regel voor de waarschuwingsbalk; leeg als alles actueel is.</summary>
    public string Text { get; init; } = "";

    public static FreshnessReport Check(string dataDirectory)
    {
        var profile = PackProfile.Load(Path.Combine(dataDirectory, "pack.json"));
        if (profile == null || profile.InstancePath.Length == 0) return new FreshnessReport();

        string foldFile = Path.Combine(dataDirectory, "folded-" + profile.Pack + ".json");
        if (!File.Exists(foldFile)) return new FreshnessReport();

        var foldTime = File.GetLastWriteTime(foldFile);
        var parts = new List<string>();
        bool jarsChanged = false;

        // 1. Zijn de jars sinds de index veranderd? De vingerafdruk staat in het profiel.
        string? game = ModRecipeScanner.GameFolder(profile.InstancePath);
        if (game != null && profile.JarStamp.Length > 0)
        {
            string stamp = IndexCommands.JarStamp(game);
            if (stamp.Length > 0 && stamp != profile.JarStamp)
            {
                jarsChanged = true;
                parts.Add(Lang.T("fresh.jars", JarCount(profile.JarStamp), JarCount(stamp)));
            }
        }

        // 2. Ligt er een dump die nieuwer is dan de import (dump-shard) of de fold?
        DateTime dumpTime = default;
        bool dumpNewer = false;

        string? dumpFolder = RecipeDumpImporter.FindDumpFolder(profile.InstancePath);
        if (dumpFolder != null)
        {
            dumpTime = NewestFile(dumpFolder);

            string shard = Path.Combine(dataDirectory, "dump-shard.json");
            var imported = File.Exists(shard) ? File.GetLastWriteTime(shard) : foldTime;

            if (dumpTime > imported)
            {
                dumpNewer = true;
                parts.Add(Lang.T("fresh.dump",
                                 dumpTime.ToString("dd-MM HH:mm"), imported.ToString("dd-MM HH:mm")));
            }
        }

        if (parts.Count == 0) return new FreshnessReport { FoldTime = foldTime, DumpTime = dumpTime };

        return new FreshnessReport
        {
            JarsChanged = jarsChanged,
            DumpNewer = dumpNewer,
            FoldTime = foldTime,
            DumpTime = dumpTime,
            Text = Lang.T("stale.text", string.Join(Lang.T("stale.and"), parts)) +
                   Lang.T("stale.tail", foldTime.ToString("dd-MM HH:mm"))
        };
    }

    /// <summary>"281:6383…" → 281; onbekend blijft onbekend.</summary>
    private static string JarCount(string stamp) => stamp.Split(':')[0];

    private static DateTime NewestFile(string folder)
    {
        try
        {
            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                            .Select(File.GetLastWriteTime)
                            .DefaultIfEmpty(default)
                            .Max();
        }
        catch
        {
            return default;
        }
    }
}
