using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Mcrpv
{
    public partial class App : Application
    {
        /// <summary>
        /// Onverwachte fouten worden vastgelegd in crash.log en aan de gebruiker getoond.
        /// Zonder deze vangers sloot de app zich zonder enig spoor, waardoor een crash
        /// niet te reproduceren of te repareren was.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            MigrateLegacyDataFolder();

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // Zelfcontrole zonder venster (bracket-zoom en stappenplan nalopen):
            //   "MCRPV.exe" --selftest
            if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.Exit(RunSelfTest());
            }

            // Index bouwen voor een pack: pack.json + jar-shards + basis + pack-laag + fold
            // (zonder venster). --reindex=pack doet alleen de pack-laag en de fold, --diff is
            // daar een synoniem van, --apply schrijft het resultaat naar recipes.json.
            bool wantsIndex = e.Args.Any(a => a.Equals("--index", StringComparison.OrdinalIgnoreCase));
            bool wantsDiff = e.Args.Any(a => a.Equals("--diff", StringComparison.OrdinalIgnoreCase));

            if (wantsIndex || wantsDiff ||
                !string.IsNullOrWhiteSpace(Value(e.Args, "--reindex=")))
            {
                var indexArgs = wantsDiff && !wantsIndex &&
                                string.IsNullOrWhiteSpace(Value(e.Args, "--reindex="))
                    ? e.Args.Concat(new[] { "--reindex=pack" }).ToArray()
                    : e.Args;

                IndexCommands.RunIndex(indexArgs);
                Environment.Exit(0);
            }

            // Profielen van alle geïndexeerde packs (fase 5):
            //   "MCRPV.exe" --profiles
            if (e.Args.Any(a => a.Equals("--profiles", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.Exit(IndexCommands.RunProfiles());
            }

            // Dode patches als buglijst:
            //   "MCRPV.exe" --dead-patches
            if (e.Args.Any(a => a.Equals("--dead-patches", StringComparison.OrdinalIgnoreCase)))
            {
                IndexCommands.RunDeadPatches();
                Environment.Exit(0);
            }

            // Herkomst van één recept, als tekst (zelfde uitleg als het herkomstvenster):
            //   "MCRPV.exe" --herkomst=nether_star
            string? provenance = Value(e.Args, "--herkomst=") ?? Value(e.Args, "--provenance=");
            if (provenance != null)
            {
                Environment.Exit(IndexCommands.RunProvenance(provenance));
            }

            string? usePack = Value(e.Args, "--use-pack=");
            if (!string.IsNullOrWhiteSpace(usePack))
            {
                IndexCommands.RunUsePack(usePack);
                Environment.Exit(0);
            }

            // Pack-laag exporteren of eigen correcties importeren:
            //   --export-patch=pack.po3patch   --import-patch=user.jsonl
            string? exportPatch = Value(e.Args, "--export-patch=");
            string? importPatch = Value(e.Args, "--import-patch=");
            if (!string.IsNullOrWhiteSpace(exportPatch) || !string.IsNullOrWhiteSpace(importPatch))
            {
                IndexCommands.RunPatchIo(exportPatch, importPatch);
                Environment.Exit(0);
            }

            // Zonder venster scannen (handig om te zien wat de mods opleveren):
            //   "MCRPV.exe" --scan --instance=<pad> --modes=all_modes,mythic
            if (e.Args.Any(a => a.Equals("--scan", StringComparison.OrdinalIgnoreCase)))
            {
                RunScan(e.Args);
                Environment.Exit(0);
            }

            // Alleen de JEI-dump omzetten (zonder venster), bijvoorbeeld na een nieuwe export:
            //   "MCRPV.exe" --convert --instance=<pad>
            if (e.Args.Any(a => a.Equals("--convert", StringComparison.OrdinalIgnoreCase)))
            {
                RunConvert(e.Args);
                Environment.Exit(0);
            }

            // Stappenplan zonder venster (handig om een keten na te rekenen):
            //   "MCRPV.exe" --plan=end_seeds --qty=1
            string? planItem = Value(e.Args, "--plan=");
            if (!string.IsNullOrWhiteSpace(planItem))
            {
                Environment.Exit(RunPlan(planItem, e.Args));
            }
        }

        /// <summary>
        /// Zelftest zonder venster. Een fout in de test zelf mag geen modale foutdialoog
        /// openen — dat laat een onbeheerde run eindeloos hangen — dus hij wordt hier
        /// opgevangen en als exitcode 2 gemeld.
        /// </summary>
        private static int RunSelfTest()
        {
            try
            {
                return SelfTest.Run();
            }
            catch (Exception ex)
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCRPV");

                try
                {
                    Directory.CreateDirectory(data);
                    File.WriteAllText(Path.Combine(data, "selftest-report.txt"),
                                      "✖ zelftest kon niet draaien: " + ex + Environment.NewLine);
                }
                catch
                {
                    // Rapporteren mag nooit de exitcode verbergen.
                }

                Console.WriteLine("zelftest mislukt: " + ex.Message);
                return 2;
            }
        }

        /// <summary>Scant de instance en schrijft een rapport + de gevonden recepten weg.</summary>
        private static void RunScan(string[] args)
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCRPV");
            var report = new StringBuilder();

            try
            {
                Directory.CreateDirectory(data);

                string? instance = Value(args, "--instance=");
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
                    var wanted = (Value(args, "--modes=") ?? "all_modes")
                                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    var folders = ModRecipeScanner.ScriptFolders(instance)
                        .Where(f => wanted.Length == 0 ||
                                    wanted.Contains(ModRecipeScanner.FolderLabel(f), StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    report.AppendLine("instance: " + instance);
                    report.AppendLine("scripts: " + string.Join(", ", folders.Select(ModRecipeScanner.FolderLabel)));

                    // Net als in de app blijft de bestaande set de basis waar de scripts
                    // en mod-recepten bovenop komen.
                    IDictionary<string, Recipe>? baseRecipes = null;
                    string basePath = Path.Combine(data, "recipes.json");
                    if (File.Exists(basePath))
                    {
                        try { baseRecipes = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(basePath)); }
                        catch { }
                    }

                    var result = ModRecipeScanner.Scan(instance, folders, Path.Combine(data, "mods.json"), baseRecipes);
                    report.AppendLine(result.Summary);
                    if (result.Diagnostics != null) report.AppendLine(result.Diagnostics);
                    report.AppendLine();

                    foreach (var kv in result.Recipes.Take(60))
                    {
                        report.AppendLine($"{kv.Key} [{kv.Value.Category}] <- " +
                                          string.Join(", ", kv.Value.Ingredients.Select(i => $"{i.Item}x{i.Count}")));
                    }

                    File.WriteAllText(Path.Combine(data, "recipes-scanned.json"),
                                      JsonSerializer.Serialize(result.Recipes, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("fout: " + ex);
            }

            File.WriteAllText(Path.Combine(data, "scan-report.txt"), report.ToString());
        }

        /// <summary>
        /// Rekent het stappenplan voor één item uit en print het. Geeft 1 terug als
        /// recipes.json ontbreekt of onleesbaar is, zodat een script dat kan zien.
        /// </summary>
        private static int RunPlan(string item, string[] args)
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCRPV");
            string recipesPath = Path.Combine(data, "recipes.json");

            if (!File.Exists(recipesPath))
            {
                Console.WriteLine("geen recipes.json in " + data + " — draai eerst --convert of --index");
                return 1;
            }

            Dictionary<string, Recipe>? loadedRecipes;

            try
            {
                loadedRecipes = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(recipesPath));
            }
            catch (Exception ex)
            {
                // Een half geschreven of handmatig bewerkte recipes.json hoort een regel op
                // de console te geven, geen foutdialoog.
                Console.WriteLine("recipes.json in " + data + " is onleesbaar (" + ex.Message + ")" +
                                  " — draai --convert opnieuw");
                return 1;
            }

            if (loadedRecipes == null || loadedRecipes.Count == 0)
            {
                Console.WriteLine("recipes.json in " + data + " bevat geen recepten — draai --convert of --index");
                return 1;
            }

            var recipes = new Dictionary<string, Recipe>(loadedRecipes, StringComparer.OrdinalIgnoreCase);
            RecipeOverlay.Apply(recipes);

            string namesPath = Path.Combine(data, "names.json");
            if (File.Exists(namesPath))
            {
                try
                {
                    var names = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(namesPath));
                    if (names != null) ItemNames.LoadMap(names);
                }
                catch { }
            }

            int qty = 1;
            if (int.TryParse(Value(args, "--qty="), out int parsed) && parsed > 0) qty = parsed;

            string key = RecipePlanner.ResolveKey(item, RecipePlanner.BuildLookup(recipes.Keys), recipes.Keys.ToList());
            if (!recipes.ContainsKey(key))
                Console.WriteLine("let op: " + key + " heeft geen recept in recipes.json");

            // Dezelfde grenzen als in de app: die staan in settings.json.
            var limits = PlannerLimits.Load(data);
            int budget = RecipePlanner.MaxTreeNodes;
            var root = RecipePlanner.Build(key, qty, recipes,
                                           new Dictionary<string, string>(), new Dictionary<string, int>(),
                                           ref budget, limits);

            Console.WriteLine(RecipePlanner.BuildStepPlan(root, recipes));
            return 0;
        }

        /// <summary>Converteert de JEI-dump naar recipes.json + names.json.</summary>
        private static void RunConvert(string[] args)
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCRPV");

            string? instance = Value(args, "--instance=");
            if (string.IsNullOrWhiteSpace(instance))
                instance = MinecraftBookmarks.FindAll()
                                            .Select(b => MinecraftBookmarks.InstanceFolder(b.Path))
                                            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

            string? dump = string.IsNullOrWhiteSpace(instance) ? null : RecipeDumpImporter.FindDumpFolder(instance);
            if (dump == null)
            {
                Console.WriteLine("geen JEI-dump gevonden in " + (instance ?? "(onbekende instance)"));
                return;
            }

            var imported = RecipeDumpImporter.Import(dump);
            Directory.CreateDirectory(data);

            File.WriteAllText(Path.Combine(data, "recipes.json"),
                              JsonSerializer.Serialize(imported.Recipes, new JsonSerializerOptions { WriteIndented = true }));

            string namesPath = Path.Combine(data, "names.json");
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(namesPath))
            {
                try
                {
                    var old = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(namesPath));
                    if (old != null) foreach (var kv in old) names[kv.Key] = kv.Value;
                }
                catch { }
            }
            foreach (var kv in imported.Names) names[kv.Key] = kv.Value;
            File.WriteAllText(namesPath, JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true }));

            int multi = imported.Recipes.Values.Count(r => r.Output > 1);
            Console.WriteLine($"{imported.Recipes.Count} items · {imported.Files} dump-bestand(en) · {multi} recepten met meerdere stuks per beurt");
            Console.WriteLine("dump: " + dump);
        }

        private static string? Value(string[] args, string prefix) =>
            args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Een fout in de UI-thread mag de app niet meer meesleuren: melden en doorgaan.
            e.Handled = true;
            Report("UI-fout", e.Exception, keepRunning: true);
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Report("Fatale fout", e.ExceptionObject as Exception, keepRunning: false);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            Report("Achtergrondfout", e.Exception, keepRunning: true);
        }

        /// <summary>
        /// De app bewaarde zijn index, de actieve recipes.json en het zelfcontrole-verslag in
        /// %LOCALAPPDATA%\PO3RecipeTree. Die map wordt bij de eerste start na de naamswijziging
        /// verhuisd, zodat de index en het gekozen receptenset niet opnieuw opgebouwd hoeven te worden.
        /// </summary>
        private static void MigrateLegacyDataFolder()
        {
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return;
                }

                string legacy = Path.Combine(root, "PO3RecipeTree");
                string current = Path.Combine(root, "MCRPV");
                if (Directory.Exists(legacy) && !Directory.Exists(current))
                {
                    Directory.Move(legacy, current);
                }
            }
            catch
            {
                // Best effort: zonder verhuizing maakt de app de map gewoon opnieuw aan.
            }
        }

        private static string CrashLogPath
        {
            get
            {
                try
                {
                    string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        string dir = Path.Combine(root, "MCRPV");
                        Directory.CreateDirectory(dir);
                        return Path.Combine(dir, "crash.log");
                    }
                }
                catch { }

                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            }
        }

        private static void Report(string kind, Exception? ex, bool keepRunning)
        {
            string path = CrashLogPath;
            string details = ex?.ToString() ?? Lang.T("crash.unknown");

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {kind} | v{typeof(App).Assembly.GetName().Version}");
                sb.AppendLine(details);
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString());
            }
            catch { }

            string message = $"{kind}: {ex?.Message ?? Lang.T("crash.unknown")}\n\n" +
                             Lang.T("crash.details", path) +
                             Lang.T(keepRunning ? "crash.keeprunning" : "crash.closing");

            try
            {
                MessageBox.Show(message, Lang.T("crash.title"), MessageBoxButton.OK,
                                keepRunning ? MessageBoxImage.Warning : MessageBoxImage.Error);
            }
            catch { }
        }
    }
}
