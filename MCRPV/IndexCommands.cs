using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Mcrpv;

/// <summary>
/// Alles wat de index zonder venster doet: lagen bouwen, folden, rapporteren, en de
/// pack-profielen beheren (PLAN.md fase 2, 3 en 5).
/// </summary>
public static class IndexCommands
{
    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCRPV");

    public static void RunIndex(string[] args)
    {
        string data = DataFolder;
        var report = new StringBuilder();
        var clock = Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(data);

            string mode = (Value(args, "--reindex=") ?? "all").ToLowerInvariant();
            var stored = PackProfile.Load(Path.Combine(data, "pack.json"));

            string? instance = Value(args, "--instance=");
            if (string.IsNullOrWhiteSpace(instance) && stored != null &&
                !string.IsNullOrWhiteSpace(stored.InstancePath) && Directory.Exists(stored.InstancePath))
                instance = stored.InstancePath;

            if (string.IsNullOrWhiteSpace(instance))
                instance = MinecraftBookmarks.FindAll()
                                            .Select(b => MinecraftBookmarks.InstanceFolder(b.Path))
                                            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

            if (string.IsNullOrWhiteSpace(instance))
            {
                report.AppendLine("geen instance gevonden");
            }
            else
            {
                string? game = ModRecipeScanner.GameFolder(instance)
                               ?? throw new DirectoryNotFoundException("Geen mods-map gevonden in deze instance");

                var profile = mode == "pack" && stored != null && !string.IsNullOrWhiteSpace(stored.Pack)
                    ? stored
                    : PackProfile.Detect(instance);

                profile.InstancePath = instance;
                report.AppendLine("pack: " + profile.Caption);
                report.AppendLine("instance: " + instance);

                // 1. Basis: jars → shards → base-bestand. Bij --reindex=pack overslaan.
                var shards = new List<JarShard>();
                BaseIndexResult? basis = null;

                if (mode is "all" or "base")
                {
                    var jars = JarIndex.Build(instance, Path.Combine(data, "jars"));
                    shards = jars.Shards;
                    report.AppendLine($"jars: {jars.Summary} · {clock.Elapsed.TotalSeconds:0.0}s");

                    basis = BaseIndex.Build(data, shards);
                    report.AppendLine($"basis: {Path.GetFileName(basis.Path)} · {basis.Recipes.Count} recepten · " +
                                      $"{(basis.FromCache ? "uit cache" : "gebouwd")} · {basis.Mods} mods · " +
                                      $"{clock.Elapsed.TotalSeconds:0.0}s");
                }
                else
                {
                    basis = string.IsNullOrWhiteSpace(profile.BaseFile)
                        ? null
                        : BaseIndex.Load(Path.Combine(data, profile.BaseFile));

                    if (basis == null)
                    {
                        var jars = JarIndex.Build(instance, Path.Combine(data, "jars"));
                        shards = jars.Shards;
                        basis = BaseIndex.Build(data, shards);
                        report.AppendLine($"basis: opnieuw gebouwd · {basis.Recipes.Count} recepten");
                    }
                    else
                    {
                        report.AppendLine($"basis: {profile.BaseFile} · {basis.Recipes.Count} recepten · uit cache (--reindex=pack)");
                    }
                }

                // 2. Pack-laag: scripts en datapacks als bewerkingen met bron.
                var wanted = (Value(args, "--modes=") ?? "")
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var folders = ModRecipeScanner.ScriptFolders(instance)
                    .Where(f => wanted.Length == 0 ||
                                wanted.Contains(ModRecipeScanner.FolderLabel(f), StringComparer.OrdinalIgnoreCase))
                    .ToList();

                report.AppendLine("scripts: " + (folders.Count == 0
                    ? "(geen scriptmappen gevonden)"
                    : string.Join(", ", folders.Select(ModRecipeScanner.FolderLabel))));

                var patches = PatchLog.Build(game, data, folders);
                report.AppendLine($"pack-laag: {Path.GetFileName(patches.Path)} · {patches.Summary} · " +
                                  $"{(patches.FromCache ? "uit cache" : "gelezen")} · {clock.Elapsed.TotalSeconds:0.0}s");
                foreach (string warning in patches.WarningLines.Take(12)) report.AppendLine("  " + warning);

                // 3. Runtime-dump als basislaag (1.12 code-recepten). --dump leest de dump
                //    opnieuw in; zonder --dump telt de dump-shard van de vorige run mee,
                //    anders verdwijnen die recepten bij elke --reindex=pack.
                var dumpOps = new List<RecipeOp>();
                string? dumpFolder = Value(args, "--dump=");
                bool wantsDump = !string.IsNullOrWhiteSpace(dumpFolder) ||
                                 args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));

                var dump = new RuntimeDumpSource(dumpFolder, data, wantsDump);
                long dumpMs = 0;
                if (dump.Detect(game))
                {
                    var dumpClock = Stopwatch.StartNew();
                    dumpOps.AddRange(dump.Read(game, CancellationToken.None));
                    dumpMs = dumpClock.ElapsedMilliseconds;

                    report.AppendLine($"runtime: {Path.GetFileName(dump.Folder)} · {dumpOps.Count} recept(en) · " +
                                      $"{(dump.FromCache ? "uit cache (dump-shard)" : "ingelezen")} · {dumpMs} ms");
                }
                else
                {
                    report.AppendLine("runtime: geen dump gevonden (gebruik --dump bij de eerste run)");
                }

                // 4. Gebruikerslaag en de fold.
                var userOps = PatchLog.LoadUser(data);
                var foldClock = Stopwatch.StartNew();
                var fold = shards.Count > 0
                    ? Fold.Apply(shards, dumpOps, patches.Ops, userOps)
                    : Fold.Apply(basis!, dumpOps, patches.Ops, userOps);

                report.AppendLine($"fold: {fold.CountsText} → {fold.Result} items · {foldClock.ElapsedMilliseconds} ms " +
                                  $"(patches {fold.Timings.GetValueOrDefault("patches")} ms · nawerk {fold.Timings.GetValueOrDefault("nawerk")} ms)");
                foreach (string message in fold.Messages.Take(6)) report.AppendLine("  " + message);

                // 5. Diff met de vorige fold van dit pack.
                string foldedPath = Path.Combine(data, "folded-" + profile.Pack + ".json");
                var previous = ReadRecipes(foldedPath);
                var diffClock = Stopwatch.StartNew();
                var diff = DiffReport.Compare(previous, fold.Recipes);

                report.AppendLine($"diff: {diff.Summary} (t.o.v. de vorige fold) · {diffClock.ElapsedMilliseconds} ms");
                foreach (string line in diff.Lines.Take(20)) report.AppendLine("  " + line);

                // 6. Validatie en herkomst.
                var validationClock = Stopwatch.StartNew();
                var validation = ValidationResult.Run(fold.Recipes, patches.Ops, fold.DeadPatches);
                report.AppendLine($"validatie: {validation.Summary} · {validationClock.ElapsedMilliseconds} ms");
                foreach (string line in validation.Lines.Take(15)) report.AppendLine("  " + line);

                // 6b. Dode patches als buglijst per oorzaak en per script.
                var dead = DeadPatchReport.Build(fold.DeadOps, fold.Recipes);
                File.WriteAllText(Path.Combine(data, "dead-patches.txt"), dead.Text);
                report.AppendLine($"dode patches: {dead.Summary}");
                foreach (string line in dead.GroupSummary(6)) report.AppendLine("  " + line);

                int withoutSource = fold.Recipes.Count(kv => kv.Value.Source.Length == 0);
                report.AppendLine($"herkomst: {fold.Recipes.Count - withoutSource} recept(en) met bron · {withoutSource} zonder");

                // 7. Wegschrijven: fold-resultaat, pack.json en profielen.
                var writeClock = Stopwatch.StartNew();
                WriteRecipes(foldedPath, fold.Recipes);
                report.AppendLine($"weggeschreven: {Path.GetFileName(foldedPath)} · {writeClock.ElapsedMilliseconds} ms");

                string? exportPath = Value(args, "--export-patch=");
                if (!string.IsNullOrWhiteSpace(exportPath)) PatchLog.Export(patches.Path, exportPath);

                if (args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase)))
                {
                    string target = Path.Combine(data, "recipes.json");
                    if (File.Exists(target))
                        File.Copy(target, Path.Combine(data, "recipes.before-fold.json"), true);

                    WriteRecipes(target, fold.Recipes);
                    report.AppendLine("toegepast: recipes.json vervangen door de fold (oude set bewaard als recipes.before-fold.json)");
                }

                string stamp = JarStamp(game);

                profile.BaseFile = basis != null ? Path.GetFileName(basis.Path) : profile.BaseFile;
                profile.PatchFile = Path.GetFileName(patches.Path);
                profile.Jars = shards.Count > 0 ? shards.Count : profile.Jars;
                profile.BaseCount = basis?.Recipes.Count ?? fold.BaseCount;
                profile.PackCount = patches.Ops.Count(o => o.Op is "add" or "replace" or "remove" or "replaceIngredient");
                profile.UserCount = userOps.Count;
                profile.ResultCount = fold.Result;
                profile.Parser = BaseIndex.Parser;
                profile.JarStamp = stamp;
                profile.Built = DateTime.Now;
                profile.Save(Path.Combine(data, "pack.json"));

                var before = Profiles.Upsert(data, profile);
                if (before != null && before.JarStamp.Length > 0 && before.JarStamp != stamp)
                    report.AppendLine("pack-update: de jars zijn veranderd sinds de vorige index " +
                                      $"({before.Jars} → {profile.Jars} jars); de basis is bijgewerkt");

                report.AppendLine($"profiel: {profile.Pack} · {profile.ResultCount} items · actief");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine("fout: " + ex);
        }

        File.WriteAllText(Path.Combine(data, "index-report.txt"), report.ToString());
        Console.WriteLine(report.ToString());
    }

    /// <summary>
    /// Profielen van alle geïndexeerde packs. Geeft 1 terug als er niets te lezen valt,
    /// zodat een script het verschil ziet tussen "leeg" en "gelukt".
    /// </summary>
    public static int RunProfiles()
    {
        string data = DataFolder;

        if (!Directory.Exists(data))
        {
            Console.WriteLine("geen gegevensmap: " + data + " — draai eerst --index --instance=<pad>");
            return 1;
        }

        var book = Profiles.Load(data);
        var report = new StringBuilder();

        if (book.Packs.Count == 0)
        {
            report.AppendLine("nog geen profielen — draai eerst --index --instance=<pad>");
        }
        else
        {
            report.AppendLine($"profielen: {book.Packs.Count} · actief: {book.Active}");

            foreach (var pack in book.Packs)
            {
                bool active = pack.Pack.Equals(book.Active, StringComparison.OrdinalIgnoreCase);
                report.AppendLine($"{(active ? "▶" : " ")} {pack.Pack} · {pack.Minecraft} · {pack.Jars} jars · " +
                                  $"{pack.BaseCount} basis + {pack.PackCount} pack + {pack.UserCount} eigen → " +
                                  $"{pack.ResultCount} items · {pack.Built:yyyy-MM-dd HH:mm}");
            }
        }

        try
        {
            File.WriteAllText(Path.Combine(data, "profiles-report.txt"), report.ToString());
        }
        catch (Exception ex)
        {
            // Het rapport is een gemak; de lijst op de console blijft werken.
            Console.WriteLine("kon profiles-report.txt niet schrijven: " + ex.Message);
        }

        Console.WriteLine(report.ToString());
        return 0;
    }

    public static void RunUsePack(string slug)
    {
        string data = DataFolder;
        var pack = Profiles.Find(data, slug);

        if (pack == null)
        {
            Console.WriteLine($"onbekend pack: {slug} — zie --profiles");
            return;
        }

        pack.Save(Path.Combine(data, "pack.json"));

        var book = Profiles.Load(data);
        book.Active = pack.Pack;
        Profiles.Save(data, book);

        Console.WriteLine($"actief: {pack.Pack} · {pack.Minecraft} · {pack.ResultCount} items · basis {pack.BaseFile}");
    }

    public static void RunPatchIo(string? exportPath, string? importPath)
    {
        string data = DataFolder;

        if (!string.IsNullOrWhiteSpace(exportPath))
        {
            var profile = PackProfile.Load(Path.Combine(data, "pack.json"));
            string source = Path.Combine(data, profile?.PatchFile ?? "");

            if (profile == null || !File.Exists(source))
            {
                Console.WriteLine("geen pack-laag om te exporteren — draai eerst --index");
            }
            else
            {
                PatchLog.Export(source, exportPath);
                Console.WriteLine($"geëxporteerd: {Path.GetFullPath(exportPath)} ({File.ReadAllLines(exportPath).Length} regels)");
            }
        }

        if (!string.IsNullOrWhiteSpace(importPath))
        {
            int added = PatchLog.ImportUser(data, importPath);
            Console.WriteLine($"geïmporteerd: {added} regel(s) in user.jsonl — draai --reindex=pack om ze toe te passen");
        }
    }

    /// <summary>Pad naar het rapport met de dode patches (buglijst per script).</summary>
    public static string DeadPatchesFile(string dataDirectory) => Path.Combine(dataDirectory, "dead-patches.txt");

    /// <summary>
    /// De dode patches opnieuw bepalen (cached herindex) en de lijst printen.
    ///   "MCRPV.exe" --dead-patches
    /// </summary>
    public static void RunDeadPatches()
    {
        RunIndex(new[] { "--reindex=pack" });

        string path = DeadPatchesFile(DataFolder);
        if (File.Exists(path)) Console.WriteLine(File.ReadAllText(path));
    }

    /// <summary>
    /// "Waarom dit recept?": dezelfde uitleg als het herkomstvenster, maar als tekst.
    ///   "MCRPV.exe" --herkomst=nether_star
    /// Geeft 1 terug als de gegevensmap of recipes.json ontbreekt/onleesbaar is.
    /// </summary>
    public static int RunProvenance(string item)
    {
        string data = DataFolder;
        string recipesPath = Path.Combine(data, "recipes.json");

        if (!Directory.Exists(data))
        {
            Console.WriteLine("geen gegevensmap: " + data + " — draai eerst --index of --convert");
            return 1;
        }

        var recipes = ReadRecipes(recipesPath);

        if (File.Exists(recipesPath) && (recipes == null || recipes.Count == 0))
        {
            Console.WriteLine("recipes.json in " + data + " is leeg of onleesbaar — draai eerst --convert");
            return 1;
        }

        // Zonder recipes.json kan de fold nog steeds antwoord geven op de vraag.
        recipes ??= new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);

        var report = ProvenanceReport.Explain(data, item, recipes);

        Console.WriteLine($"herkomst: {report.Item}");
        Console.WriteLine($"laag: {report.Layer}");
        Console.WriteLine($"details: {report.Details}");
        if (report.Source.Length > 0)
            Console.WriteLine($"bron: {report.Source}{(report.FromFold ? " (uit de fold)" : "")}");

        if (report.Lines.Count == 0) Console.WriteLine("geen bewerkingen");
        foreach (var line in report.Lines) Console.WriteLine("  " + line.Text);
        return 0;
    }

    private static Dictionary<string, Recipe>? ReadRecipes(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteRecipes(string path, Dictionary<string, Recipe> recipes) =>
        File.WriteAllText(path, JsonSerializer.Serialize(recipes, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Vingerafdruk van de jars (aantal + nieuwste schrijftijd) om pack-updates te zien.</summary>
    public static string JarStamp(string gameFolder)
    {
        try
        {
            var jars = Directory.GetFiles(Path.Combine(gameFolder, "mods"), "*.jar", SearchOption.AllDirectories);
            long newest = jars.Length == 0 ? 0 : jars.Max(j => File.GetLastWriteTimeUtc(j).Ticks);

            return $"{jars.Length}:{newest}";
        }
        catch
        {
            return "";
        }
    }

    private static string? Value(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
}
