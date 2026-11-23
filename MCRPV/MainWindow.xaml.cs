using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Mcrpv;

public partial class MainWindow : Window
{
    private Dictionary<string, Recipe> _recipes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per item welke manier van maken je koos (index in Recipe.Options).</summary>
    private readonly Dictionary<string, int> _recipeChoice = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per item de machine die je als voorkeur instelde ("Alloy Smelter"); die wint van een losse klik.</summary>
    private readonly Dictionary<string, string> _machinePreference = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Laatst gebruikte filter in het machinekeuzepaneel, zodat het de volgende keer klaarstaat.</summary>
    private string _machineFilter = "";
    private Dictionary<string, string> _keyLookup = new(StringComparer.OrdinalIgnoreCase);
    private RecipeNode? _currentRoot;

    /// <summary>Boom waar het bracket-diagram op dit moment van gemaakt is.</summary>
    private RecipeNode? _bracketRoot;

    /// <summary>Boom waar de boomtekst en het stappenplan op dit moment van gemaakt zijn.</summary>
    private RecipeNode? _planRoot;
    /// <summary>true = de set komt uit de fold van het actieve pack, false = recipes.json.</summary>
    private bool _useFold;

    private bool _isDark = true;
    private string? _instanceRoot;

    private readonly ObservableCollection<BookmarkChoice> _bookmarks = new();
    private bool _suppressBookmarkEvent;

    private FileSystemWatcher? _watcher;
    private bool _isWatching;
    private string? _watchFolder;
    private DateTime _lastReload = DateTime.MinValue;
    private readonly object _reloadLock = new();

    private FileSystemWatcher? _bookmarkWatcher;
    private string? _bookmarkWatchFile;
    private DispatcherTimer? _bookmarkReloadTimer;

    public MainWindow()
    {
        // Taal vóór het eerste venster: de XAML-teksten komen meteen uit de tabel.
        Lang.LoadFromSettings(DataDirectory);
        InitializeComponent();
        FitToWorkArea();
        BookmarkCombo.ItemsSource = _bookmarks;
        LoadRecipes();
        RestoreSavedSource();

        // Meteen zichtbaar hoeveel van de set meegeleverd is en hoeveel de instance zelf gaf.
        RecipeSourceText.Text = $"🧪 recipes: {_recipes.Count} items{SourceSplitText()}{PackLayerText()}{SourceText()}";
        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?");
        CheckFreshness();
        ApplyTheme(true);
        // Pas na het laden: onthouden bookmark-bron, anders instances zoeken (kan een dialoog openen).
        Loaded += (_, _) =>
        {
            LoadRecipeChoices();
            LoadPlannerLimits();
            AutoLoadBookmarks();
        };
        Closed += (_, _) =>
        {
            StopWatch();
            StopBookmarkWatch();
        };
    }

    /// <summary>
    /// Op een klein(er) scherm past het venster anders niet en valt de rechterkant
    /// buiten beeld. Daarom krimpen we het venster (en de minimummaten) naar het werkvlak.
    /// </summary>
    private void FitToWorkArea()
    {
        Rect work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0) return;

        double width = Math.Min(Width, work.Width - 24);
        double height = Math.Min(Height, work.Height - 24);

        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        Width = width;
        Height = height;
    }

    // ========== Recipe loading ==========

    /// <summary>
    /// Beschrijfbare map voor gebruikersdata. Bij een installatie onder Program Files
    /// (of %LOCALAPPDATA%\Programs) mag de app niet naast de exe schrijven, dus de
    /// actieve recipes.json staat in %LOCALAPPDATA%\MCRPV.
    /// </summary>
    private static string DataDirectory
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
                    return dir;
                }
            }
            catch { }

            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    private static string DataFilePath => Path.Combine(DataDirectory, "recipes.json");
    private static string BookmarksFilePath => Path.Combine(DataDirectory, "bookmarks.json");
    private static string NamesFilePath => Path.Combine(DataDirectory, "names.json");

    private void LoadRecipes(string? explicitPath = null)
    {
        string path = explicitPath ?? DataFilePath;

        // Eerste start: neem de meegeleverde recipes.json uit de installatiemap over.
        if (!File.Exists(path))
        {
            string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recipes.json");
            if (File.Exists(bundled) && !string.Equals(bundled, path, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(bundled, path, true); } catch { }
            }
        }

        if (!File.Exists(path))
        {
            var starter = new Dictionary<string, Recipe>
            {
                ["diamond"] = new() { Ingredients = { new() { Item = "diamond_ore", Count = 1 } } },
                ["diamond_ore"] = new() { Ingredients = { new() { Item = "cobblestone", Count = 8 }, new() { Item = "diamond_gem", Count = 1 } } },
                ["iron_ingot"] = new() { Ingredients = { new() { Item = "iron_ore", Count = 1 } } },
                ["iron_pickaxe"] = new() { Ingredients = { new() { Item = "iron_ingot", Count = 3 }, new() { Item = "stick", Count = 2 } } },
                ["stick"] = new() { Ingredients = { new() { Item = "plank", Count = 2 } } },
                ["plank"] = new() { Ingredients = { new() { Item = "log", Count = 1 } } }
            };
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(starter, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                    MessageBox.Show(Lang.T("msg.recipes.create", path, ex.Message),
                                Lang.T("title.error"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        try
        {
            // Klein beetje wachten als het bestand nét geschreven is
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(json);
                    if (loaded != null && loaded.Count > 0)
                    {
                        _recipes = new Dictionary<string, Recipe>(loaded, StringComparer.OrdinalIgnoreCase);

                        // Aanvullen met bronnen die een JEI-dump niet kent (mijnen, laser
                        // drill, Tinkers' Construct). recipes.json op schijf blijft ongewijzigd.
                        RecipeOverlay.Apply(_recipes);

                        RebuildKeyLookup();
                        return;
                    }
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(80);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Lang.T("msg.recipes.load", ex.Message), Lang.T("title.loaderror"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RebuildKeyLookup();
    }

    private void RebuildKeyLookup() => _keyLookup = RecipePlanner.BuildLookup(_recipes.Keys);

    private void ReloadFromWatch(string filePath)
    {
        lock (_reloadLock)
        {
            // Debounce: voorkom dubbele reloads binnen 1.5 sec
            if ((DateTime.Now - _lastReload).TotalMilliseconds < 1500)
                return;
            _lastReload = DateTime.Now;
        }

        Dispatcher.Invoke(() =>
        {
            // Een watch-map kan meer .json-bestanden bevatten dan alleen een dump:
            // namen, instellingen, een reservekopie of een half geschreven bestand.
            // Alleen een echte receptenset mag de actieve recipes.json vervangen.
            if (!RecipeSetFile.LooksLikeSet(filePath))
            {
                WatchStatusText.Text = Lang.T("status.watch.skipped", Path.GetFileName(filePath));
                WatchStatusText.Foreground = Brushes.Orange;
                return;
            }

            try
            {
                LoadRecipes(filePath);

                // Bewaar het geladen bestand als actieve dataset in de data-map van de app
                string local = DataFilePath;
                if (!string.Equals(filePath, local, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Copy(filePath, local, true); } catch { }
                }

                // Bookmark-bestand dat naast de dump staat meteen meenemen
                string? bookmarkFile = FindBookmarksNear(filePath);
                if (bookmarkFile != null)
                    LoadBookmarks(bookmarkFile);

                WatchStatusText.Text = Lang.T("status.watch.reloaded", Path.GetFileName(filePath), _recipes.Count);
                WatchStatusText.Foreground = (Brush)FindResource("WatchActive");

                // Herbouw huidige tree als die open stond
                if (!string.IsNullOrWhiteSpace(ItemInput.Text))
                    BuildTree_Click(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                WatchStatusText.Text = Lang.T("status.watch.failed", ex.Message);
                WatchStatusText.Foreground = Brushes.OrangeRed;
            }
        });
    }

    /// <summary>
    /// True als het bestand een receptenset is: een niet-lege map van item naar recept met
    /// minstens één echt recept erin. Zo niet, dan blijft de actieve dataset staan in plaats
    /// van overschreven te worden met namen, instellingen, een reservekopie of een half
    /// geschreven bestand.
    /// </summary>
    private static string? FindBookmarksNear(string filePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

            return Directory.GetFiles(dir, "*bookmark*.*", SearchOption.TopDirectoryOnly)
                            .OrderBy(f => f.Length)
                            .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    // ========== Herkomst van de receptenset ==========

    /// <summary>De meegeleverde recipes.json naast de exe; die set zit in de installer.</summary>
    private static string BundledRecipesFile => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recipes.json");

    private static readonly System.Globalization.CultureInfo Nl = System.Globalization.CultureInfo.GetCultureInfo("nl-NL");

    private static HashSet<string>? _bundledKeys;
    private static string? _splitStamp;
    private static (int Bundled, int Instance)? _split;

    private static HashSet<string>? BundledKeys()
    {
        if (_bundledKeys != null) return _bundledKeys;

        string path = BundledRecipesFile;
        if (!File.Exists(path)) return null;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(path));
            if (loaded == null) return null;

            _bundledKeys = new HashSet<string>(loaded.Keys, StringComparer.OrdinalIgnoreCase);
            return _bundledKeys;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Hoeveel items van de set op schijf uit de meegeleverde set komen en hoeveel de
    /// instance zelf toevoegde (dump, jars, datapacks, scripts). Null als er nog geen
    /// set of geen meegeleverde set is.
    /// </summary>
    private (int Bundled, int Instance)? RecipeSplit()
    {
        var bundled = BundledKeys();
        if (bundled == null || !File.Exists(DataFilePath)) return null;

        try
        {
            var info = new FileInfo(DataFilePath);
            string stamp = info.LastWriteTimeUtc.Ticks + ":" + info.Length;
            if (_splitStamp == stamp) return _split;

            var stored = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(DataFilePath));
            if (stored == null) return null;

            int fromInstance = stored.Keys.Count(k => !bundled.Contains(k));
            _split = (stored.Count - fromInstance, fromInstance);
            _splitStamp = stamp;
            return _split;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// " · 🗂 po3 · 📦 30 basis · 🧩 3 pack · 🖐 0 eigen": de laagverdeling uit pack.json,
    /// zodat je naast de set ook ziet wat de index heeft opgebouwd (PLAN.md §7).
    /// </summary>
    private static string PackLayerText()
    {
        try
        {
            var profile = PackProfile.Load(Path.Combine(Path.GetDirectoryName(DataFilePath) ?? "", "pack.json"));
            if (profile == null || profile.ResultCount == 0) return "";

            return Lang.T("status.split.pack", profile.Pack,
                           profile.BaseCount.ToString("N0", Nl),
                           profile.PackCount.ToString("N0", Nl),
                           profile.UserCount.ToString("N0", Nl));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>" · 📦 13 839 meegeleverd · 🧩 453 uit de instance" — leeg als het niet te bepalen is.</summary>
    private string RecipeSplitText()
    {
        if (RecipeSplit() is not { } split) return "";

        return Lang.T("status.split.instance",
                       split.Bundled.ToString("N0", Nl), split.Instance.ToString("N0", Nl));
    }

    /// <summary>
    /// Diff met de laatste fold en de pack-switcher (PLAN.md fase 3 en 5). De knoppen in
    /// dat venster gebruiken dezelfde code als --reindex=pack, dus app en CLI lopen gelijk.
    /// </summary>
    private void ShowChanges_Click(object sender, RoutedEventArgs e)
    {
        var window = new ChangesWindow(() => _recipes, () => _useFold, UseFoldedRecipes, UsePlainRecipes,
                                       OpenProvenance)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    /// <summary>
    /// Waarschuwt als de instance-jars of de dump nieuwer zijn dan de laatste fold, zodat je
    /// niet stil met een oude set werkt (PLAN.md fase 4). Verder niets: het is een signaal.
    /// </summary>
    private void CheckFreshness()
    {
        var report = FreshnessReport.Check(DataDirectory);

        StalePanel.Visibility = report.Stale ? Visibility.Visible : Visibility.Collapsed;
        if (!report.Stale) return;

        StaleText.Text = report.Text;
        StaleReindexButton.ToolTip = Lang.T(report.DumpNewer ? "stale.tip.dump" : "stale.tip.pack");
    }

    private async void StaleReindex_Click(object sender, RoutedEventArgs e)
    {
        var report = FreshnessReport.Check(DataDirectory);
        var args = report.DumpNewer
            ? new[] { "--reindex=pack", "--dump" }
            : new[] { "--reindex=pack" };

        StaleReindexButton.IsEnabled = false;
        StaleText.Text = Lang.T(report.DumpNewer ? "stale.busy.dump" : "stale.busy.pack");

        try
        {
            await Task.Run(() => IndexCommands.RunIndex(args));
            if (_useFold) ReloadFold(); else LoadRecipes();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Lang.T("msg.reindex.failed", ex.Message),
                            Lang.T("title.reindex"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            StaleReindexButton.IsEnabled = true;
            CheckFreshness();
        }
    }

    /// <summary>De fold van het actieve pack opnieuw inlezen na een herindex.</summary>
    private void ReloadFold()
    {
        try
        {
            var profile = PackProfile.Load(Path.Combine(DataDirectory, "pack.json"));
            if (profile == null || profile.Pack.Length == 0) return;

            string path = Path.Combine(DataDirectory, "folded-" + profile.Pack + ".json");
            if (!File.Exists(path)) return;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(path));
            if (loaded != null && loaded.Count > 0) UseFoldedRecipes(loaded);
        }
        catch
        {
            // Mislukt herladen: de set die je al had blijft staan.
        }
    }

    /// <summary>
    /// "Waarom dit recept?": laag, bron en patch (bestand:regel) plus elke bewerking die het
    /// item raakte. Bereikbaar met 🔍 HERKOMST, met dubbelklik in de boom en vanuit de diff.
    /// </summary>
    private void ShowProvenance_Click(object sender, RoutedEventArgs e) =>
        OpenProvenance((RecipeTreeView.SelectedItem as RecipeNode)?.Name ?? ItemInput.Text.Trim());

    private void RecipeTreeView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as TreeView)?.SelectedItem is RecipeNode node) OpenProvenance(node.Name);
    }

    private void OpenProvenance(string item)
    {
        if (string.IsNullOrWhiteSpace(item)) return;

        new ProvenanceWindow(item, _recipes) { Owner = this }.ShowDialog();
    }

    /// <summary>De fold als receptenset gebruiken; recipes.json op schijf blijft ongemoeid.</summary>
    private void UseFoldedRecipes(Dictionary<string, Recipe> recipes)
    {
        _recipes = new Dictionary<string, Recipe>(recipes, StringComparer.OrdinalIgnoreCase);
        _useFold = true;
        _splitStamp = null;
        RebuildKeyLookup();
        RememberSource(true);

        RefreshSourceText();
    }

    /// <summary>Terug naar recipes.json van schijf, en dat per pack onthouden.</summary>
    private void UsePlainRecipes()
    {
        _useFold = false;
        LoadRecipes();
        RememberSource(false);

        RefreshSourceText();
    }

    /// <summary>
    /// Herstelt na een herstart de onthouden keuze van het actieve pack. Ontbreekt de fold
    /// (opgeruimd of nooit gemaakt), dan blijft het bij recipes.json; de statusbalk zegt wat.
    /// </summary>
    private void RestoreSavedSource()
    {
        try
        {
            var profile = PackProfile.Load(Path.Combine(DataDirectory, "pack.json"));
            if (profile == null || !profile.UseFold) return;

            string fold = Path.Combine(DataDirectory, "folded-" + profile.Pack + ".json");
            if (!File.Exists(fold)) return;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(fold));
            if (loaded == null || loaded.Count == 0) return;

            _recipes = new Dictionary<string, Recipe>(loaded, StringComparer.OrdinalIgnoreCase);
            _useFold = true;
            _splitStamp = null;
            RebuildKeyLookup();
        }
        catch
        {
            // Kapotte fold: gewoon met recipes.json verder.
        }
    }

    /// <summary>Onthoudt de keuze bij het actieve pack, zodat een herstart dezelfde set geeft.</summary>
    private void RememberSource(bool useFold)
    {
        try
        {
            var profile = PackProfile.Load(Path.Combine(DataDirectory, "pack.json"));
            if (profile == null || profile.Pack.Length == 0) return;

            Profiles.SetSource(DataDirectory, profile.Pack, useFold);
        }
        catch
        {
            // Onthouden is een gemak; de app werkt ook zonder.
        }
    }

    /// <summary>" · 📥 fold" of " · 📄 recipes.json": welke set je nu ziet.</summary>
    private string SourceText() => Lang.T(_useFold ? "status.source.fold" : "status.source.plain");

    /// <summary>De 📦/🧩-splitsing zegt niets over een fold, dus die laten we dan weg.</summary>
    private string SourceSplitText() => _useFold ? "" : RecipeSplitText();

    private void RefreshSourceText()
    {
        RecipeSourceText.Text = $"🧪 recipes: {_recipes.Count} items{SourceSplitText()}{PackLayerText()}{SourceText()}";
    }

    // ========== JEI bookmarks ==========

    private async void LoadBookmarks(string? explicitPath = null, bool autoSelectFirst = false, bool rememberSource = false)
    {
        string path = explicitPath ?? BookmarksFilePath;

        if (!File.Exists(path))
        {
            BookmarkStatusText.Text = Lang.T("status.bookmarks.none");
            return;
        }

        try
        {
            var parsed = RecipePlanner.ParseBookmarks(File.ReadAllText(path));
            if (parsed.Count == 0)
            {
                BookmarkStatusText.Text = Lang.T("status.bookmarks.notfound", Path.GetFileName(path));
                return;
            }

            if (rememberSource)
                SaveBookmarkSource(path);

            string? source = rememberSource ? MinecraftBookmarks.DescribeSource(path) : null;

            // Echte itemnamen uit de lang-bestanden van de instance (eenmalig, daarna cache).
            // Dat leest alle jars door: await, zodat de eerste bookmark-load de UI niet
            // bevriest. Het venster blijft bedienbaar zolang de index loopt.
            string? instanceFolder = MinecraftBookmarks.InstanceFolder(path);
            if (!string.IsNullOrWhiteSpace(instanceFolder))
            {
                // Ook nodig voor de item-iconen in het bracket-diagram.
                _instanceRoot = instanceFolder;
                await EnsureItemNamesAsync(instanceFolder);
            }

            RefreshBookmarkChoices(parsed, autoSelectFirst, source);

            // Live meelezen: JEI schrijft dit bestand terwijl Minecraft draait.
            StartBookmarkWatch(path);

            // Meteen ook de recipes uit dezelfde instance halen (geen losse converter-stap).
            TryImportRecipesFromInstance(path);
        }
        catch (Exception ex)
        {
            BookmarkStatusText.Text = Lang.T("status.bookmarks.error", ex.Message);
        }
    }

    /// <summary>
    /// Haalt recipes.json automatisch uit de instance van het geladen bookmark-bestand.
    /// Staat er een JEIExporter-dump in (exports/, jei_exports/, config/jei/dumps/), dan
    /// converteert de app die zelf — de losse JEIConverter is alleen nog voor handmatig gebruik.
    /// </summary>
    private async void TryImportRecipesFromInstance(string bookmarkPath)
    {
        string? instanceRoot = MinecraftBookmarks.InstanceFolder(bookmarkPath);
        if (string.IsNullOrWhiteSpace(instanceRoot))
        {
            RecipeSourceText.Text = Lang.T("status.recipes.noinstance");
            return;
        }

        // "minecraft" als naam zegt niets; InstanceName kent de launcher-mappen.
        string instanceName = MinecraftBookmarks.InstanceName(bookmarkPath);

        // Mooie namen: goedkoop als ze al geladen zijn, anders uit de jars van deze instance.
        await EnsureItemNamesAsync(instanceRoot);

        // De id-tabel is pas bruikbaar als de import hem aan de receptsleutels koppelt;
        // ontbreekt die koppeling, dan is één herimport nodig.
        bool namesMissing = ItemNames.Count == 0 && ItemNames.IdCount > 0;
        string? dumpFolder = RecipeDumpImporter.FindDumpFolder(instanceRoot);

        // Geen JEIExporter nodig: zonder dump leest de app de recepten rechtstreeks uit
        // de jars, datapacks en scripts van de instance.
        if (dumpFolder == null)
        {
            if (await TryScanModsAutomaticallyAsync(instanceRoot, instanceName))
                return;

            // ✖ STOP: de melding van de scan blijft staan; niet overschrijven.
            if (_scanStopped) return;

            RecipeSourceText.Text = $"🧪 recipes: {_recipes.Count} items{RecipeSplitText()}" +
                                    Lang.T("status.recipes.nonew", instanceName);
            return;
        }

        DateTime dumpStamp = RecipeDumpImporter.DumpStamp(dumpFolder);
        DateTime localStamp = File.Exists(DataFilePath) ? File.GetLastWriteTime(DataFilePath) : DateTime.MinValue;

        // Niet onnodig herconverteren: alleen als de dump nieuwer is of we nog met de
        // meegeleverde startset werken.
        if (!namesMissing && dumpStamp != DateTime.MinValue && dumpStamp <= localStamp && _recipes.Count > 8)
        {
            RecipeSourceText.Text = $"🧪 recipes: {_recipes.Count} items{SourceSplitText()}" +
                                    Lang.T("status.recipes.dumpalready", dumpStamp.ToString("yyyy-MM-dd HH:mm")) +
                                    SourceText();
            return;
        }

        try
        {
            // Converteren kost seconden bij een grote dump; op de achtergrond blijven de
            // knoppen bruikbaar en toont de balk waar hij is.
            var progress = new Progress<string>(step =>
                RecipeSourceText.Text = Lang.T("status.recipes.reading.step", instanceName, step));

            SetScanBusy(true);
            RecipeSourceText.Text = Lang.T("status.recipes.reading", instanceName);

            var cancel = StartCancelableWork();

            DumpImport imported;
            try
            {
                imported = await Task.Run(() => RecipeDumpImporter.Import(
                    dumpFolder, step => ((IProgress<string>)progress).Report(step), cancel));
            }
            finally
            {
                EndCancelableWork();
                SetScanBusy(false);
            }

            if (imported.Cancelled)
            {
                RecipeSourceText.Text = Lang.T("status.recipes.dumpstopped", instanceName, _recipes.Count);
                return;
            }

            if (imported.Recipes.Count == 0)
            {
                // Lege of half geschreven dump: val terug op de mods van de instance.
                if (await TryScanModsAutomaticallyAsync(instanceRoot, instanceName))
                    return;

                if (_scanStopped) return;

                RecipeSourceText.Text = Lang.T("status.recipes.nousable", instanceName);
                return;
            }

            File.WriteAllText(DataFilePath,
                              JsonSerializer.Serialize(imported.Recipes, new JsonSerializerOptions { WriteIndented = true }));

            LoadRecipes(DataFilePath);

            // Echte namen bij de receptsleutels bewaren, zodat de UI ze meteen toont.
            ItemNames.Merge(imported.Names);
            SaveNamesFile();

            // Openstaande boom en stappenplan opnieuw opbouwen met de nieuwe dataset.
            if (!string.IsNullOrWhiteSpace(ItemInput.Text))
                BuildTree_Click(this, new RoutedEventArgs());

            // De bookmarklijst wist nog niet dat deze items nu recepten hebben.
            RefreshKnownBookmarks();

            // De dump is de basis; de instance zelf levert de scripts en machine-recepten
            // erbij, zodat er geen JEIExporter nodig is voor een complete set.
            await TryScanModsAutomaticallyAsync(instanceRoot, instanceName, afterDumpImport: true);
            if (_scanStopped) return;

            RecipeSourceText.Text = Lang.T("status.recipes.imported", _recipes.Count, RecipeSplitText(),
                                           Path.GetFileName(dumpFolder).TrimEnd(Path.DirectorySeparatorChar),
                                           instanceName, imported.Files, DateTime.Now.ToString("HH:mm"));
        }
        catch (Exception ex)
        {
            RecipeSourceText.Text = Lang.T("status.recipes.importfailed", instanceName, ex.Message);
        }
    }

    /// <summary>
    /// Werkt de bookmarklijst bij na een import: items die eerst "zit niet in
    /// recipes.json" zeiden, hebben nu wél een recept.
    /// </summary>
    private void RefreshKnownBookmarks()
    {
        if (_bookmarks.Count == 0) return;

        foreach (var choice in _bookmarks)
        {
            string key = string.IsNullOrWhiteSpace(choice.Key) ? choice.Item : choice.Key;
            choice.Known = _recipes.ContainsKey(key);
        }

        // BookmarkChoice is geen INotifyPropertyChanged: ItemsSource opnieuw zetten
        // ververst de labels (het "⚠" verdwijnt).
        var selected = BookmarkCombo.SelectedItem;
        _suppressBookmarkEvent = true;
        BookmarkCombo.ItemsSource = null;
        BookmarkCombo.ItemsSource = _bookmarks;
        BookmarkCombo.SelectedItem = selected;
        _suppressBookmarkEvent = false;

        int known = _bookmarks.Count(b => b.Known);
        BookmarkStatusText.Text = Lang.T("status.bookmarks.count", _bookmarks.Count, known);
    }

    /// <summary>Onthouden bron → eerder gekozen bestand → automatisch zoeken in instances.</summary>
    private void AutoLoadBookmarks()
    {
        string? saved = ReadBookmarkSource();
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved))
        {
            LoadBookmarks(saved, autoSelectFirst: true);
            return;
        }

        if (File.Exists(BookmarksFilePath))
        {
            LoadBookmarks(BookmarksFilePath, autoSelectFirst: true);
            return;
        }

        ScanMinecraft(autoSelectFirst: true);
    }

    /// <summary>
    /// Zoekt JEI-bookmarks in je instances. Bij één kandidaat wordt die direct gebruikt;
    /// bij meerdere kiest de gebruiker zelf (met laatste wijzigingstijd per instance).
    /// </summary>
    private void ScanMinecraft(bool autoSelectFirst)
    {
        List<MinecraftBookmarkFile> found;

        try
        {
            found = MinecraftBookmarks.FindAll();
        }
        catch (Exception ex)
        {
            BookmarkStatusText.Text = Lang.T("status.bookmarks.scanfailed", ex.Message);
            return;
        }

        if (found.Count == 0)
        {
            BookmarkStatusText.Text = Lang.T("status.bookmarks.nonefound");
            return;
        }

        if (found.Count == 1)
        {
            LoadBookmarks(found[0].Path, autoSelectFirst, rememberSource: true);
            return;
        }

        BookmarkStatusText.Text = Lang.T("status.bookmarks.instances", found.Count);

        if (PromptForInstance(autoSelectFirst))
            return;
    }

    /// <summary>Opent de instance-kiezer en laadt de keuze. Geeft false terug bij annuleren.</summary>
    private bool PromptForInstance(bool autoSelectFirst)
    {
        var dialog = new InstancePickerWindow { Owner = this };

        if (dialog.ShowDialog() != true || dialog.Selected is not { } chosen)
            return false;

        LoadBookmarks(chosen.Path, autoSelectFirst, rememberSource: true);
        return true;
    }

    private void ChooseInstance_Click(object sender, RoutedEventArgs e) => PromptForInstance(autoSelectFirst: true);

    /// <summary>
    /// Recepten uit de mods van de instance halen (zonder JEIExporter): mod-jars,
    /// CraftTweaker- en KubeJS-scripts. De gekozen pack-mode blijft onthouden.
    /// </summary>
    private void ScanMods_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_instanceRoot))
        {
            // De instance van het geladen bookmarks-bestand is de juiste om te scannen.
            string? source = ReadBookmarkSource();
            if (!string.IsNullOrWhiteSpace(source))
                _instanceRoot = MinecraftBookmarks.InstanceFolder(source);
        }

        if (string.IsNullOrWhiteSpace(_instanceRoot))
            PromptForInstance(autoSelectFirst: true);

        if (string.IsNullOrWhiteSpace(_instanceRoot))
        {
            RecipeSourceText.Text = Lang.T("status.mods.noinstance");
            return;
        }

        var folders = ModRecipeScanner.ScriptFolders(_instanceRoot);
        var dialog = new ModScanWindow(_instanceRoot, folders, DataDirectory, ReadSettings().ScanScripts, _recipes)
        {
            Owner = this
        };

        dialog.ShowDialog();

        if (dialog.SelectedScripts.Count > 0)
            WriteSettings(s => s.ScanScripts = dialog.SelectedScripts);

        if (dialog.Result is { } result && result.Error == null && result.Added > 0)
            ApplyModScan(result);
    }

    /// <summary>Toont de mods-database: per mod de versie en het aantal gevonden recepten.</summary>
    private void Mods_Click(object sender, RoutedEventArgs e)
    {
        string databasePath = Path.Combine(DataDirectory, "mods.json");
        var database = ModDatabase.Load(databasePath);

        if (database.Mods.Count == 0)
        {
            RecipeSourceText.Text = Lang.T("status.mods.nodb");
            return;
        }

        new ModsWindow(database) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Standalone zonder JEIExporter: haalt de recepten uit de jars, datapacks en scripts
    /// van de instance. Draait alleen als de instance sinds de vorige scan is veranderd
    /// (mods.json bewaart wanneer die was), zodat een start niet telkens scant.
    /// </summary>
    private async Task<bool> TryScanModsAutomaticallyAsync(string instanceRoot, string instanceName, bool afterDumpImport = false)
    {
        if (_scanRunning) return false;

        // Vanaf hier is er één scan tegelijk, ook terwijl de instance-check nog loopt:
        // een tweede bookmark-wijziging mag er geen tweede scan naast zetten.
        _scanRunning = true;
        _scanStopped = false;

        // Een dump-import vervangt de set: scripts en machine-recepten moeten er dan weer
        // bij, ook als de instance zelf niet is gewijzigd. De check zelf loopt over alle
        // jars, scripts en datapacks van de instance (tienduizenden bestanden) en hoort dus
        // niet op de UI-thread: een bookmark toevoegen in-game liet de app anders hangen.
        if (!afterDumpImport && _recipes.Count > 8 &&
            !await Task.Run(() => InstanceChangedSinceScan(instanceRoot)))
        {
            _scanRunning = false;
            return false;
        }

        var folders = ModRecipeScanner.ScriptFolders(instanceRoot);
        string databasePath = Path.Combine(DataDirectory, "mods.json");

        // De scanner draait op de achtergrond; deze kopie houdt hem weg van _recipes
        // als de UI intussen iets anders laadt.
        var baseRecipes = new Dictionary<string, Recipe>(_recipes, StringComparer.OrdinalIgnoreCase);

        var progress = new Progress<string>(step =>
            RecipeSourceText.Text = Lang.T("status.scan.running", instanceName, step));

        SetScanBusy(true);
        RecipeSourceText.Text = Lang.T("status.scan.start", instanceName);

        var cancel = StartCancelableWork();

        try
        {
            var result = await Task.Run(() => ModRecipeScanner.Scan(
                instanceRoot, folders, databasePath, baseRecipes,
                step => ((IProgress<string>)progress).Report(step), cancel));

            if (result.Cancelled)
            {
                // De aanroeper zet anders meteen zijn eigen "geen nieuwe recepten"-regel
                // over deze melding heen.
                _scanStopped = true;
                RecipeSourceText.Text = Lang.T("status.scan.stopped", instanceName, _recipes.Count);
                return false;
            }

            if (result.Error != null)
            {
                RecipeSourceText.Text = Lang.T("status.scan.failed", instanceName, result.Error);
                return false;
            }

            if (result.Added <= 0)
            {
                RecipeSourceText.Text = $"🧪 recipes: {_recipes.Count} items{RecipeSplitText()}" +
                                        Lang.T("status.recipes.nonew", instanceName);
                return false;
            }

            ApplyModScan(result);
            RecipeSourceText.Text += " · zonder JEIExporter";
            return true;
        }
        catch (Exception ex)
        {
            RecipeSourceText.Text = Lang.T("status.scan.failed", instanceName, ex.Message);
            return false;
        }
        finally
        {
            EndCancelableWork();
            _scanRunning = false;
            SetScanBusy(false);
        }
    }

    /// <summary>
    /// Zet de voortgangsbalk aan of uit en houdt de knoppen die een tweede scan
    /// kunnen starten even stil.
    /// </summary>
    private void SetScanBusy(bool busy)
    {
        ScanProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = busy;
        ScanCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanCancelButton.IsEnabled = busy;
        ScanModsButton.IsEnabled = !busy;
        BookmarkCombo.IsEnabled = !busy;
    }

    /// <summary>Maakt de lopende scan/dump-import annuleerbaar en toont ✖ STOP.</summary>
    private CancellationToken StartCancelableWork()
    {
        _scanCancel?.Dispose();
        _scanCancel = new CancellationTokenSource();
        return _scanCancel.Token;
    }

    private void EndCancelableWork()
    {
        _scanCancel?.Dispose();
        _scanCancel = null;
    }

    /// <summary>
    /// ✖ STOP naast de voortgangsbalk: de scanner stopt bij de volgende mod, het script
    /// of het volgende dump-bestand. Wat er al in recipes.json stond blijft staan.
    /// </summary>
    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        // De scan kan tussen de klik en nu net klaar zijn: EndCancelableWork heeft de
        // token source dan al opgeruimd, en Cancel() op een disposed source gooit.
        var cancel = _scanCancel;
        if (cancel == null || cancel.IsCancellationRequested) return;

        try
        {
            cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Al klaar met scannen: er valt niets meer te stoppen.
            return;
        }

        ScanCancelButton.IsEnabled = false;
        RecipeSourceText.Text += " · stoppen…";
    }

    /// <summary>
    /// Is de instance veranderd sinds de laatste scan? De scan bewaart zijn tijd in
    /// mods.json, dus dat dekt jars, datapacks én scripts in één keer.
    /// </summary>
    private bool InstanceChangedSinceScan(string instanceRoot)
    {
        DateTime scanned = ModDatabase.Load(Path.Combine(DataDirectory, "mods.json")).BuiltAt;
        return scanned == default || ModsStampCached(instanceRoot) > scanned.ToUniversalTime();
    }

    /// <summary>
    /// <see cref="ModsStamp"/> met een korte houdbaarheid. Elke bookmark-wijziging in-game
    /// komt bij deze check langs, en telkens de hele instance afwandelen laat de app
    /// haperen. Binnen <see cref="ModsStampMaxAge"/> geldt dezelfde vingerafdruk; een echte
    /// wijziging wordt bij de volgende bookmark, herstart of scan alsnog opgemerkt.
    /// Alleen de instance-check (één tegelijk, op de achtergrond) raakt deze velden aan.
    /// </summary>
    private DateTime ModsStampCached(string instanceRoot)
    {
        DateTime now = DateTime.UtcNow;

        if (_modsStampRoot != null &&
            string.Equals(_modsStampRoot, instanceRoot, StringComparison.OrdinalIgnoreCase) &&
            now - _modsStampAt < ModsStampMaxAge)
            return _modsStampValue;

        _modsStampValue = ModsStamp(instanceRoot);
        _modsStampAt = now;
        _modsStampRoot = instanceRoot;
        return _modsStampValue;
    }

    /// <summary>
    /// Nieuwste wijziging in de instance, in UTC. Van de bestanden zelf: een bestaand
    /// script bewerken verandert de tijd van de map niet, dus maptijden missen dat.
    /// </summary>
    private static DateTime ModsStamp(string instanceRoot)
    {
        string? game = ModRecipeScanner.GameFolder(instanceRoot);
        if (string.IsNullOrWhiteSpace(game)) return DateTime.MinValue;

        DateTime newest = DateTime.MinValue;

        foreach (string folder in new[] { "mods", "datapacks", "scripts", "kubejs", "config/openloader" })
        {
            string path = Path.Combine(game, folder);
            if (!Directory.Exists(path)) continue;

            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    DateTime stamp = File.GetLastWriteTimeUtc(file);
                    if (stamp > newest) newest = stamp;
                }
            }
            catch
            {
                // Onleesbare map overslaan; de rest van de instance telt gewoon mee.
            }
        }

        return newest;
    }

    /// <summary>Neemt de gescande recepten over: oude set als reserve bewaren, dan herladen.</summary>
    private void ApplyModScan(ModScanResult result)
    {
        if (result.Error != null)
        {
            RecipeSourceText.Text = Lang.T("status.mods.scanfailed", result.Error);
            return;
        }

        try
        {
            Directory.CreateDirectory(DataDirectory);

            if (File.Exists(DataFilePath))
                File.Copy(DataFilePath, Path.Combine(DataDirectory, "recipes.before-scan.json"), true);

            File.WriteAllText(DataFilePath,
                              JsonSerializer.Serialize(result.Recipes, new JsonSerializerOptions { WriteIndented = true }));

            LoadRecipes(DataFilePath);

            if (result.Names.Count > 0)
            {
                ItemNames.Merge(result.Names);
                SaveNamesFile();
            }

            if (!string.IsNullOrWhiteSpace(ItemInput.Text))
                BuildTree_Click(this, new RoutedEventArgs());

            RefreshKnownBookmarks();

            RecipeSourceText.Text = Lang.T("status.mods.takeover", result.Summary, RecipeSplitText());
        }
        catch (Exception ex)
        {
            RecipeSourceText.Text = Lang.T("status.mods.takeoverfailed", ex.Message);
        }
    }

    // ========== Instellingen (onthouden waar de bookmarks vandaan komen) ==========

    /// <summary>
    /// Zorgt dat er mooie itemnamen zijn: uit de lang-bestanden van de instance of uit
    /// de eerder weggeschreven names.json. Geeft true als ze nu pas geladen zijn.
    /// </summary>
    private static async Task<bool> EnsureItemNamesAsync(string? instanceRoot)
    {
        if (ItemNames.Count > 0 || ItemNames.IdCount > 0) return false;
        if (TryLoadNamesFile()) return false;
        if (string.IsNullOrWhiteSpace(instanceRoot)) return false;

        // Vult alleen de id-tabel; de koppeling aan receptsleutels gebeurt bij de import.
        // Eén indexering tegelijk: een tweede bookmark-load wacht op dezelfde taak.
        Task<bool> task;
        if (_namesLoadTask != null && string.Equals(_namesLoadRoot, instanceRoot, StringComparison.OrdinalIgnoreCase))
        {
            task = _namesLoadTask;
        }
        else
        {
            _namesLoadRoot = instanceRoot;
            task = _namesLoadTask = LoadNamesAsync(instanceRoot);
        }

        try
        {
            return await task;
        }
        catch
        {
            // Mislukt: de volgende poging begint opnieuw en de fout gaat gewoon omhoog
            // naar de aanroeper (die hem in de statusbalk meldt).
            if (ReferenceEquals(_namesLoadTask, task)) _namesLoadTask = null;
            throw;
        }
    }

    private static async Task<bool> LoadNamesAsync(string instanceRoot) =>
        await ItemNames.LoadFromInstanceAsync(instanceRoot) > 0;

    private static string? _namesLoadRoot;
    private static Task<bool>? _namesLoadTask;

    /// <summary>Opgeslagen namen-cache; de versie voorkomt dat een oude cache blijft hangen.</summary>
    private sealed class NameCache
    {
        public int Version { get; set; }
        public Dictionary<string, string> Names { get; set; } = new();
    }

    private const int NamesVersion = 3;

    private static bool TryLoadNamesFile()
    {
        try
        {
            if (!File.Exists(NamesFilePath)) return false;

            var cache = JsonSerializer.Deserialize<NameCache>(File.ReadAllText(NamesFilePath));
            if (cache == null || cache.Version != NamesVersion || cache.Names.Count == 0) return false;

            ItemNames.LoadMap(cache.Names);
            return ItemNames.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveNamesFile()
    {
        try
        {
            var cache = new NameCache
            {
                Version = NamesVersion,
                Names = ItemNames.Map.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            };

            File.WriteAllText(NamesFilePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Alleen een cache: zonder names.json werkt de app ook (Titel Case-terugval).
        }
    }

    private static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

    private sealed class AppSettings
    {
        public string? BookmarkSource { get; set; }

        /// <summary>Per item welke manier van maken je koos (index in Recipe.Options).</summary>
        public Dictionary<string, int> RecipeChoices { get; set; } = new();

        /// <summary>Per item de machine die altijd gebruikt moet worden, als die het item kan maken.</summary>
        public Dictionary<string, string> MachinePreferences { get; set; } = new();

        /// <summary>Laatst gebruikte filter in het machinekeuzepaneel.</summary>
        public string MachineFilter { get; set; } = "";

        /// <summary>Script-mappen die bij de laatste mods-scan aan stonden (pack-mode).</summary>
        public List<string> ScanScripts { get; set; } = new();

        /// <summary>Afkapgrens van de planner: beurten per stap (leeg = standaard).</summary>
        public int? MaxCraftsPerStep { get; set; }

        /// <summary>Afkapgrens van de planner: basis-materialen per gepland item (leeg = standaard).</summary>
        public int? BaseItemsPerProduct { get; set; }

        /// <summary>Taal van de GUI ("nl" of "en"); leeg betekent Nederlands.</summary>
        public string? Language { get; set; }
    }

    private static AppSettings ReadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath)) ?? new AppSettings();
        }
        catch { }

        return new AppSettings();
    }

    private static void WriteSettings(Action<AppSettings> change)
    {
        try
        {
            var settings = ReadSettings();
            change(settings);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string? ReadBookmarkSource() => ReadSettings().BookmarkSource;

    private static void SaveBookmarkSource(string path) => WriteSettings(s => s.BookmarkSource = path);

    /// <summary>Eerder gekozen machines terugzetten, zodat een boom hetzelfde blijft.</summary>
    private void LoadRecipeChoices()
    {
        _recipeChoice.Clear();
        _machinePreference.Clear();

        var settings = ReadSettings();

        foreach (var kv in settings.RecipeChoices)
            if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0)
                _recipeChoice[kv.Key] = kv.Value;

        foreach (var kv in settings.MachinePreferences)
            if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                _machinePreference[kv.Key] = kv.Value;

        _machineFilter = settings.MachineFilter ?? "";
    }

    private void SaveRecipeChoices() => WriteSettings(s =>
    {
        s.RecipeChoices = _recipeChoice.Where(kv => kv.Value > 0)
                                       .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        s.MachinePreferences = _machinePreference.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                                                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        s.MachineFilter = _machineFilter;
    });

    /// <summary>Leest de afkapgrenzen uit settings.json en zet ze in de balk.</summary>
    private void LoadPlannerLimits()
    {
        _limits = PlannerLimits.Load(DataDirectory);

        _suppressLimitEvent = true;
        CraftLimitInput.Text = _limits.MaxCraftsPerStep.ToString();
        BaseLimitInput.Text = _limits.BaseItemsPerProduct.ToString();
        _suppressLimitEvent = false;
    }

    /// <summary>
    /// Typen in de twee grensvelden: waarde begrenzen en bewaren, daarna de open boom
    /// opnieuw rekenen zodat je meteen ziet wat een ruimere of strakkere grens doet.
    /// </summary>
    private void PlannerLimit_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressLimitEvent) return;

        // Tijdens InitializeComponent zet XAML de Text-waarde van het eerste veld
        // voordat het tweede bestaat; dan valt er nog niets te bewaren of te bouwen.
        if (CraftLimitInput is null || BaseLimitInput is null) return;

        _limits = new PlannerLimits
        {
            MaxCraftsPerStep = ParseLimit(CraftLimitInput.Text, _limits.MaxCraftsPerStep),
            BaseItemsPerProduct = ParseLimit(BaseLimitInput.Text, _limits.BaseItemsPerProduct)
        }.Clamped();

        WriteSettings(s =>
        {
            s.MaxCraftsPerStep = _limits.MaxCraftsPerStep;
            s.BaseItemsPerProduct = _limits.BaseItemsPerProduct;
        });

        if (string.IsNullOrWhiteSpace(ItemInput?.Text)) return;

        // Niet bij elke toets een boom van duizenden knopen herbouwen: wacht tot je klaar bent.
        _limitTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _limitTimer.Stop();
        _limitTimer.Tick -= OnLimitTick;
        _limitTimer.Tick += OnLimitTick;
        _limitTimer.Start();
    }

    private void OnLimitTick(object? sender, EventArgs e)
    {
        _limitTimer?.Stop();

        // Laat het veld zien wat er echt geldt (bijv. 0 werd 1).
        _suppressLimitEvent = true;
        CraftLimitInput.Text = _limits.MaxCraftsPerStep.ToString();
        BaseLimitInput.Text = _limits.BaseItemsPerProduct.ToString();
        _suppressLimitEvent = false;

        BuildTree_Click(this, new RoutedEventArgs());
    }

    /// <summary>Leest een grensveld; leeg of onleesbaar houdt de huidige waarde.</summary>
    private static int ParseLimit(string text, int fallback) =>
        int.TryParse(text.Trim(), out int value) ? value : fallback;

    private void RefreshBookmarkChoices(List<Bookmark> bookmarks, bool autoSelectFirst, string? sourceLabel = null)
    {
        _suppressBookmarkEvent = true;

        _bookmarks.Clear();
        int known = 0;

        foreach (var bookmark in bookmarks)
        {
            string key = RecipePlanner.ResolveKey(bookmark.Item, _keyLookup, _recipes.Keys);
            bool isKnown = _recipes.ContainsKey(key);

            if (isKnown) known++;

            _bookmarks.Add(new BookmarkChoice
            {
                Item = bookmark.Item,
                Count = bookmark.Count,
                Key = key,
                Known = isKnown
            });
        }

        BookmarkCombo.SelectedIndex = -1;
        _suppressBookmarkEvent = false;

        BookmarkStatusText.Text = Lang.T("status.bookmarks.count", _bookmarks.Count, known) +
                                 (sourceLabel is null ? "" : Lang.T("status.bookmarks.from", sourceLabel));

        // Staat de boodschappenlijst open? Dan meteen bijwerken met de nieuwe bookmarks.
        if (ShoppingScrollViewer.Visibility == Visibility.Visible)
            RefreshShoppingList(showView: false);

        if (autoSelectFirst && _bookmarks.Count > 0)
            BookmarkCombo.SelectedIndex = 0;
    }

    private void BookmarkCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBookmarkEvent) return;
        if (BookmarkCombo.SelectedItem is not BookmarkChoice choice) return;

        ApplyBookmark(choice);
    }

    private void ApplyBookmark(BookmarkChoice choice)
    {
        ItemInput.Text = choice.Key;
        QuantityInput.Text = choice.Count.ToString();
        BuildTree_Click(this, new RoutedEventArgs());

        // Bookmark bekeken → meteen het overzicht van basis tot eindproduct
        ShowPlanView();

        BookmarkStatusText.Text = choice.Known
            ? $"{ItemNames.Display(choice.Item)} ×{choice.Count}"
            : $"{ItemNames.Display(choice.Item)} ×{choice.Count}" + Lang.T("bookmark.unknown");
    }

    private void LoadBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Lang.T("dialog.bookmarks.open"),
            Filter = "Bookmarks (*.ini;*.json;*.txt;*.cfg)|*.ini;*.json;*.txt;*.cfg|Alle bestanden (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
            LoadBookmarks(dialog.FileName, autoSelectFirst: true, rememberSource: true);
    }

    private void ReloadBookmarks_Click(object sender, RoutedEventArgs e) => AutoLoadBookmarks();

    // ========== Live bookmarks (actieve instance) ==========

    /// <summary>
    /// Houdt het geladen bookmark-bestand in de gaten, zodat de lijst bijwerkt terwijl
    /// Minecraft draait. JEI herschrijft bookmarks.ini bij elke wijziging; we wachten
    /// tot het schrijven even stil is voordat we herladen, anders lezen we een half bestand.
    /// </summary>
    private void StartBookmarkWatch(string path)
    {
        StopBookmarkWatch();

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

            _bookmarkWatchFile = path;

            _bookmarkWatcher = new FileSystemWatcher(dir)
            {
                Filter = Path.GetFileName(path),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                               NotifyFilters.CreationTime | NotifyFilters.Size,
                IncludeSubdirectories = false
            };

            _bookmarkWatcher.Changed += OnBookmarkFileEvent;
            _bookmarkWatcher.Created += OnBookmarkFileEvent;
            _bookmarkWatcher.Renamed += OnBookmarkFileEvent;
            _bookmarkWatcher.Deleted += OnBookmarkFileEvent;
            _bookmarkWatcher.EnableRaisingEvents = true;

            _bookmarkReloadTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _bookmarkReloadTimer.Tick -= OnBookmarkReloadTick;
            _bookmarkReloadTimer.Tick += OnBookmarkReloadTick;
        }
        catch
        {
            StopBookmarkWatch();
        }
    }

    private void StopBookmarkWatch()
    {
        _bookmarkReloadTimer?.Stop();

        if (_bookmarkWatcher != null)
        {
            _bookmarkWatcher.EnableRaisingEvents = false;
            _bookmarkWatcher.Changed -= OnBookmarkFileEvent;
            _bookmarkWatcher.Created -= OnBookmarkFileEvent;
            _bookmarkWatcher.Renamed -= OnBookmarkFileEvent;
            _bookmarkWatcher.Deleted -= OnBookmarkFileEvent;
            _bookmarkWatcher.Dispose();
            _bookmarkWatcher = null;
        }

        _bookmarkWatchFile = null;
    }

    private void OnBookmarkFileEvent(object sender, FileSystemEventArgs e)
    {
        if (_bookmarkWatchFile is not { } watched) return;
        if (!string.Equals(e.FullPath, watched, StringComparison.OrdinalIgnoreCase)) return;

        // FileSystemWatcher meldt op een achtergrondthread; de timer moet op de UI-thread.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _bookmarkReloadTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _bookmarkReloadTimer.Stop();
            _bookmarkReloadTimer.Start();
        }));
    }

    private void OnBookmarkReloadTick(object? sender, EventArgs e)
    {
        _bookmarkReloadTimer?.Stop();
        if (_bookmarkWatchFile is not { } file) return;

        if (!File.Exists(file))
        {
            BookmarkStatusText.Text = Lang.T("status.bookmarks.missing", Path.GetFileName(file));
            return;
        }

        // Onthoud welke bookmark open stond, zodat de keuze niet verspringt bij een update.
        string? selectedItem = (BookmarkCombo.SelectedItem as BookmarkChoice)?.Item;

        LoadBookmarks(file);

        if (!string.IsNullOrWhiteSpace(selectedItem))
        {
            int index = _bookmarks.ToList().FindIndex(b =>
                string.Equals(b.Item, selectedItem, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                _suppressBookmarkEvent = true;
                BookmarkCombo.SelectedIndex = index;
                _suppressBookmarkEvent = false;
            }
        }

        BookmarkStatusText.Text += Lang.T("status.live", DateTime.Now.ToString("HH:mm:ss"));
    }

    // ========== Watch folder ==========

    private void BrowseWatchFolder_Click(object sender, RoutedEventArgs e)
    {
        // Simpele folder picker via OpenFileDialog-truc (geen extra NuGet nodig)
        var dialog = new OpenFileDialog
        {
            Title = Lang.T("dialog.watch.open"),
            Filter = "JSON (*.json)|*.json|Alle bestanden (*.*)|*.*",
            CheckFileExists = false,
            FileName = "recipes.json"
        };

        if (dialog.ShowDialog() == true)
        {
            string folder = Path.GetDirectoryName(dialog.FileName) ?? "";
            if (Directory.Exists(folder))
            {
                _watchFolder = folder;
                WatchPathBox.Text = folder;

                // Als watch al actief was → herstart met nieuwe map
                if (_isWatching)
                {
                    StopWatch();
                    StartWatch();
                }
            }
        }
    }

    private void ToggleWatch_Click(object sender, RoutedEventArgs e)
    {
        if (_isWatching)
            StopWatch();
        else
            StartWatch();
    }

    private void StartWatch()
    {
        if (string.IsNullOrWhiteSpace(_watchFolder) || !Directory.Exists(_watchFolder))
        {
            MessageBox.Show(Lang.T("msg.watch.nofolder"), "Watch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(_watchFolder)
            {
                Filter = "*.json",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnWatchEvent;
            _watcher.Created += OnWatchEvent;
            _watcher.Renamed += OnWatchRenamed;

            _isWatching = true;
            WatchToggleButton.Content = "⏹ Stop Watch";
            WatchStatusText.Text = Lang.T("status.watching");
            WatchStatusText.Foreground = (Brush)FindResource("WatchActive");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Lang.T("msg.watch.startfailed", ex.Message), Lang.T("title.error"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
            StopWatch();
        }
    }

    private void StopWatch()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnWatchEvent;
            _watcher.Created -= OnWatchEvent;
            _watcher.Renamed -= OnWatchRenamed;
            _watcher.Dispose();
            _watcher = null;
        }

        _isWatching = false;
        WatchToggleButton.Content = "▶ Start Watch";
        WatchStatusText.Text = Lang.T("status.stopped");
        WatchStatusText.Foreground = (Brush)FindResource("WatchInactive");
    }

    private void OnWatchEvent(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ReloadFromWatch(e.FullPath);
    }

    private void OnWatchRenamed(object sender, RenamedEventArgs e)
    {
        if (e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ReloadFromWatch(e.FullPath);
    }

    // ========== Tree building ==========

    private void BuildTree_Click(object sender, RoutedEventArgs e)
    {
        string target = ItemInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(target)) return;

        if (!int.TryParse(QuantityInput.Text, out int qty) || qty < 1)
            qty = 1;

        _nodeBudget = RecipePlanner.MaxTreeNodes;
        StopBracketFly();
        _currentRoot = BuildNode(target, qty);
        RecipeTreeView.ItemsSource = new ObservableCollection<RecipeNode> { _currentRoot };

        RefreshPlanAndText(force: true);
        ApplyFilter();
    }

    /// <summary>
    /// Vult de boomtekst (voor COPY/.TXT, het bracket-diagram leest de boom zelf) en het
    /// stappenplan opnieuw. Loopt een van beide achter op de getoonde boom, dan wordt hij
    /// hier alsnog bijgewerkt: tekst en plan horen altijd bij hetzelfde item als de tabs.
    /// </summary>
    private void RefreshPlanAndText(bool force = false)
    {
        if (!force && ReferenceEquals(_planRoot, _currentRoot)) return;

        _planRoot = _currentRoot;

        if (_currentRoot == null)
        {
            BracketOutput.Text = string.Empty;
            StepPlanOutput.Text = string.Empty;
            return;
        }

        BracketOutput.Text = BuildPrettyBracketTree(_currentRoot);
        StepPlanOutput.Text = RecipePlanner.BuildStepPlan(_currentRoot, _recipes);
    }

    // De boom wordt door de planner opgebouwd, zodat de CLI (--plan) en de UI precies
    // dezelfde receptkeuzes maken.
    private int _nodeBudget;

    /// <summary>Afkapgrenzen van de planner; in de balk instelbaar en bewaard in settings.json.</summary>
    private PlannerLimits _limits = new();

    /// <summary>True zolang de automatische scan op de achtergrond loopt (voorkomt een tweede).</summary>
    private bool _scanRunning;

    /// <summary>Annulering van de lopende scan of dump-import (✖ STOP).</summary>
    private CancellationTokenSource? _scanCancel;

    /// <summary>True als de laatste automatische scan met ✖ STOP is afgebroken.</summary>
    private bool _scanStopped;

    /// <summary>Hoe lang de gemeten vingerafdruk van de instance hergebruikt wordt.</summary>
    private static readonly TimeSpan ModsStampMaxAge = TimeSpan.FromSeconds(15);

    /// <summary>Laatst gemeten vingerafdruk van de instance (zie <see cref="ModsStampCached"/>).</summary>
    private string? _modsStampRoot;
    private DateTime _modsStampValue;
    private DateTime _modsStampAt;
    private bool _suppressLimitEvent;
    private DispatcherTimer? _limitTimer;

    private RecipeNode BuildNode(string item, int quantity) =>
        RecipePlanner.Build(item, quantity, _recipes, _machinePreference, _recipeChoice, ref _nodeBudget, _limits);

    private string BuildPrettyBracketTree(RecipeNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{node.DisplayName} ×{node.Quantity}");
        BuildBracketRecursive(node, sb, "  ");
        return sb.ToString();
    }

    private void BuildBracketRecursive(RecipeNode node, StringBuilder sb, string indent)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            bool isLast = i == node.Children.Count - 1;

            sb.Append(indent);
            sb.Append(isLast ? "└─ " : "├─ ");
            sb.Append($"{child.DisplayName}");
            if (child.Quantity > 1) sb.Append($" ×{child.Quantity}");
            sb.AppendLine();

            string childIndent = indent + (isLast ? "   " : "│  ");
            BuildBracketRecursive(child, sb, childIndent);
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string filter = FilterBox.Text.Trim().ToLowerInvariant();

        // Ook zonder boom het diagram verversen: anders blijft een oude tekening staan
        // als het item (nog) geen recept heeft.
        if (_currentRoot != null) HighlightNodes(RecipeTreeView, filter);

        RefreshBracket();
    }

    /// <summary>
    /// Start het indexeren van de item-textures op de achtergrond. Het diagram tekent nu
    /// met de terugval-tegels en wordt opnieuw getekend zodra de index klaar is, zodat de
    /// eerste tekening niet seconden op het doorlezen van alle jars hoeft te wachten.
    /// </summary>
    private async void IndexIconsForBracket()
    {
        string? root = _instanceRoot;
        if (ItemIcons.IsLoaded(root) || ItemIcons.IsLoading(root)) return;

        bool loaded = await ItemIcons.EnsureLoadedAsync(root);

        // Alleen hertekenen als dezelfde boom en instance nog aan de beurt zijn.
        if (loaded && _currentRoot != null &&
            string.Equals(_instanceRoot, root, StringComparison.OrdinalIgnoreCase))
            RefreshBracket();
    }

    /// <summary>
    /// Bouwt het bracket-diagram opnieuw. De boomtekst (BracketOutput) blijft bestaan:
    /// COPY/.TXT/PNG gebruiken die, het diagram is wat je in de TREE-tab ziet.
    /// </summary>
    private void RefreshBracket()
    {
        if (_currentRoot == null)
        {
            BracketCanvas.Children.Clear();
            _bracketRoot = null;
            BracketEmptyText.Visibility = Visibility.Visible;
            FitBracket();
            return;
        }

        BracketEmptyText.Visibility = Visibility.Collapsed;

        // De eerste keer de textures van de instance indexeren, daarna uit de cache.
        IndexIconsForBracket();

        var diagram = BracketDiagram.Build(_currentRoot, this, FilterBox.Text, OnBracketNodeClick, ShowMachineMenu, OnBracketNodeFocus);

        BracketCanvas.Children.Clear();
        BracketCanvas.Width = diagram.Width;
        BracketCanvas.Height = diagram.Height;
        BracketCanvas.Children.Add(diagram);

        _bracketRoot = _currentRoot;

        // Nieuwe boom: meteen passend maken, zodat je altijd de hele boom ziet.
        FitBracket();
    }

    /// <summary>
    /// Klik op een vakje in de bracket: heeft dat item meer dan één recept, dan schakelt
    /// dit naar de volgende machine (bijv. steel ingot uit een Alloy Smelter in plaats van
    /// de crafting table) en wordt de boom opnieuw opgebouwd.
    /// </summary>
    private void OnBracketNodeClick(RecipeNode node)
    {
        if (node == null || string.IsNullOrWhiteSpace(node.Name)) return;
        if (!_recipes.TryGetValue(node.Name, out var recipe) || recipe.Options.Count < 2) return;

        SetRecipeChoice(node.Name, (node.OptionIndex + 1) % recipe.Options.Count);
    }

    /// <summary>
    /// Rechtsklik op een vakje: kies uit alle manieren waarop het item gemaakt kan worden.
    /// Dat gebeurt in een eigen paneel met een zoekveld en groepen per machine (JEI-tab),
    /// zodat ook tientallen alternatieven snel te vinden zijn. De ster per regel zet die
    /// machine meteen als voorkeur voor dit item.
    /// </summary>
    private void ShowMachineMenu(RecipeNode node)
    {
        if (node == null || string.IsNullOrWhiteSpace(node.Name)) return;
        if (!_recipes.TryGetValue(node.Name, out var recipe) || recipe.Options.Count < 2) return;

        _machinePreference.TryGetValue(node.Name, out var preference);

        var picker = new MachinePicker(this, ItemNames.Display(node.Name), recipe.Options, node.OptionIndex, preference,
                                       _machineFilter, node.Name, _recipes, _machinePreference, _recipeChoice);

        // Bij de muis openen, maar binnen het scherm blijven.
        var mouse = System.Windows.Input.Mouse.GetPosition(this);
        var area = SystemParameters.WorkArea;

        picker.Left = Math.Clamp(Left + mouse.X - 60, area.Left + 8, Math.Max(area.Left + 8, area.Right - picker.Width - 8));
        picker.Top = Math.Clamp(Top + mouse.Y - 40, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - picker.Height - 8));

        picker.ShowDialog();

        // Onthoud het filter voor de volgende keer, ook als je het paneel met esc sloot.
        string filter = picker.FilterText;
        if (!string.Equals(filter, _machineFilter, StringComparison.Ordinal))
        {
            _machineFilter = filter;
            SaveRecipeChoices();
        }

        if (picker.PreferenceCleared) SetMachinePreference(node.Name, null);
        else if (picker.PreferenceMachine != null) SetMachinePreference(node.Name, picker.PreferenceMachine);
        else if (picker.ChosenIndex >= 0) SetRecipeChoice(node.Name, picker.ChosenIndex);
    }

    internal static string DescribeIngredients(Recipe recipe)
    {
        var parts = recipe.Ingredients.Take(4)
                            .Select(i => $"{ItemNames.Display(i.Item)} ×{i.Count}")
                            .ToList();

        if (parts.Count == 0) return Lang.T("preview.empty");

        string text = string.Join(", ", parts);
        if (recipe.Ingredients.Count > parts.Count) text += ", …";
        return text;
    }

    /// <summary>
    /// Overzicht van alle ingestelde voorkeursmachines; daar kun je ze bekijken en intrekken.
    /// Wat je intrekt wordt bewaard en de boom wordt opnieuw gerekend.
    /// </summary>
    private void ShowPreferences_Click(object sender, RoutedEventArgs e)
    {
        var panel = new PreferenceList(this, _machinePreference);
        panel.ShowDialog();

        // Taalwissel: bewaren en dit venster opnieuw opbouwen; alle teksten worden bij
        // het laden uit de taaltabel gehaald.
        if (panel.NewLanguage is { } language)
        {
            WriteSettings(s => s.Language = language);
            ReopenForLanguage();
            return;
        }

        if (panel.RemovedItems.Count == 0) return;

        foreach (var item in panel.RemovedItems) _machinePreference.Remove(item);

        SaveRecipeChoices();

        if (!string.IsNullOrWhiteSpace(ItemInput.Text)) BuildTree_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Bouwt het hoofdvenster opnieuw op, zodat elke tekst in de nieuwe taal uit de tabel
    /// komt. Het nieuwe venster wordt eerst MainWindow, zodat het sluiten van dit venster
    /// de app niet afsluit.
    /// </summary>
    private void ReopenForLanguage()
    {
        var fresh = new MainWindow();

        if (Application.Current != null) Application.Current.MainWindow = fresh;

        fresh.Show();
        Close();
    }

    /// <summary>Onthoudt welke manier van maken je voor dit item koos en bouwt de boom opnieuw.</summary>
    private void SetRecipeChoice(string item, int index)
    {
        if (!_recipes.TryGetValue(item, out var recipe) || index < 0 || index >= recipe.Options.Count) return;

        // Kies je hier een andere machine dan de voorkeur, dan wint deze klik en
        // vervalt de voorkeur voor dit item (anders zou de voorkeur de klik negeren).
        if (_machinePreference.TryGetValue(item, out var preferredMachine) &&
            !string.Equals(recipe.Options[index].Category, preferredMachine, StringComparison.OrdinalIgnoreCase))
            _machinePreference.Remove(item);

        if (index == 0) _recipeChoice.Remove(item);
        else _recipeChoice[item] = index;

        SaveRecipeChoices();
        BuildTree_Click(this, new RoutedEventArgs());
    }

    /// <summary>Zet (of wist) de voorkeursmachine voor één item; de hele boom wordt opnieuw opgebouwd.</summary>
    private void SetMachinePreference(string item, string? machine)
    {
        if (!_recipes.TryGetValue(item, out _)) return;

        if (string.IsNullOrWhiteSpace(machine)) _machinePreference.Remove(item);
        else _machinePreference[item] = machine;

        SaveRecipeChoices();
        BuildTree_Click(this, new RoutedEventArgs());
    }

    /// <summary>Eerste recept in de lijst dat in deze machine/tab zit, of -1 als die machine het item niet maakt.</summary>
    private static int IndexOfMachine(IReadOnlyList<Recipe> options, string machine)
    {
        for (int i = 0; i < options.Count; i++)
            if (string.Equals(options[i].Category, machine, StringComparison.OrdinalIgnoreCase)) return i;

        return -1;
    }

    // ========== Bracket-zoom ==========

    private double _bracketZoom = 1;
    private const double MinBracketZoom = BracketZoom.Min;
    private const double MaxBracketZoom = BracketZoom.Max;

    /// <summary>Zoomt zo ver uit dat de hele boom in het paneel past.</summary>
    private void FitBracket()
    {
        double width = BracketCanvas.Width;
        double height = BracketCanvas.Height;

        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0 ||
            BracketCanvas.Children.Count == 0)
        {
            SetBracketZoom(1, recenter: false);
            return;
        }

        double viewWidth = BracketScrollViewer.ViewportWidth - 24;
        double viewHeight = BracketScrollViewer.ViewportHeight - 24;

        // Paneel nog niet gemeten (tab nog niet open): gewoon 100%.
        if (viewWidth <= 0 || viewHeight <= 0)
        {
            SetBracketZoom(1, recenter: false);
            return;
        }

        double fit = Math.Min(viewWidth / width, viewHeight / height);
        SetBracketZoom(Math.Min(fit, 1.0), recenter: false);

        BracketScrollViewer.ScrollToHorizontalOffset(0);
        BracketScrollViewer.ScrollToVerticalOffset(0);
    }

    private void SetBracketZoom(double zoom, bool recenter = true)
    {
        // Handmatig zoomen stopt een lopende vloeiende beweging.
        if (_flying) StopBracketFly();

        double centerX = 0, centerY = 0;

        if (recenter && _bracketZoom > 0)
        {
            centerX = (BracketScrollViewer.HorizontalOffset + BracketScrollViewer.ViewportWidth / 2) / _bracketZoom;
            centerY = (BracketScrollViewer.VerticalOffset + BracketScrollViewer.ViewportHeight / 2) / _bracketZoom;
        }

        _bracketZoom = Math.Clamp(zoom, MinBracketZoom, MaxBracketZoom);

        BracketCanvas.LayoutTransform = Math.Abs(_bracketZoom - 1) < 0.001
            ? Transform.Identity
            : new ScaleTransform(_bracketZoom, _bracketZoom);

        ZoomLabel.Text = $"{_bracketZoom * 100:0}%";

        if (!recenter) return;

        // Dezelfde plek in de boom vasthouden na het zoomen.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            BracketScrollViewer.ScrollToHorizontalOffset(centerX * _bracketZoom - BracketScrollViewer.ViewportWidth / 2);
            BracketScrollViewer.ScrollToVerticalOffset(centerY * _bracketZoom - BracketScrollViewer.ViewportHeight / 2);
        }), DispatcherPriority.Background);
    }

    // ========== Bracket: vloeiend naar een vakje ==========

    private const double FlyDurationMs = 320;
    private System.Windows.Threading.DispatcherTimer? _flyTimer;
    private bool _flying;
    private DateTime _flyStart;
    private double _flyFromZoom, _flyToZoom, _flyFromX, _flyFromY, _flyToX, _flyToY;

    /// <summary>
    /// Klik op een ingrediënt: zoom in en schuif in één vloeiende beweging naar dat vakje.
    /// Van heel ver uitgezoomd gaat het eerst naar 100%, daarna telkens 1,4× dichterbij
    /// (tot 150%), zodat de klik nooit uitzoomt.
    /// </summary>
    private void OnBracketNodeFocus(RecipeNode node, Point center)
    {
        if (BracketCanvas.Children.Count == 0) return;

        double fromZoom = _bracketZoom;
        // Nooit verder uitzoomen dan nu, vanuit ver uitgezoomd eerst naar 100% en daarna
        // hoogstens 1,4× dichterbij. Zie BracketZoom.Target: de oude Math.Clamp met fromZoom
        // als ondergrens gooide "'4' cannot be greater than 1.5." zodra de bracket al boven
        // 150% stond (zie crash.log).
        double toZoom = BracketZoom.Target(fromZoom);

        _flyFromZoom = fromZoom;
        _flyToZoom = toZoom;
        _flyFromX = (BracketScrollViewer.HorizontalOffset + BracketScrollViewer.ViewportWidth / 2) / Math.Max(fromZoom, 0.0001);
        _flyFromY = (BracketScrollViewer.VerticalOffset + BracketScrollViewer.ViewportHeight / 2) / Math.Max(fromZoom, 0.0001);
        _flyToX = center.X;
        _flyToY = center.Y;
        _flyStart = DateTime.UtcNow;
        _flying = true;

        _flyTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _flyTimer.Tick -= FlyTick;
        _flyTimer.Tick += FlyTick;
        _flyTimer.Start();
    }

    private void FlyTick(object? sender, EventArgs e)
    {
        double t = (DateTime.UtcNow - _flyStart).TotalMilliseconds / FlyDurationMs;

        if (t >= 1)
        {
            StopBracketFly();
            ApplyBracketView(_flyToZoom, _flyToX, _flyToY);
            return;
        }

        // Ease-out: snel op gang, zachtjes afremmen op het doel.
        double eased = 1 - Math.Pow(1 - t, 3);
        double zoom = _flyFromZoom * Math.Pow(_flyToZoom / _flyFromZoom, eased);

        ApplyBracketView(zoom,
                         _flyFromX + (_flyToX - _flyFromX) * eased,
                         _flyFromY + (_flyToY - _flyFromY) * eased);
    }

    /// <summary>Zet zoom en schuift het paneel zo dat (x, y) in het midden van het venster staat.</summary>
    private void ApplyBracketView(double zoom, double x, double y)
    {
        _bracketZoom = Math.Clamp(zoom, MinBracketZoom, MaxBracketZoom);

        BracketCanvas.LayoutTransform = Math.Abs(_bracketZoom - 1) < 0.001
            ? Transform.Identity
            : new ScaleTransform(_bracketZoom, _bracketZoom);

        ZoomLabel.Text = $"{_bracketZoom * 100:0}%";

        BracketScrollViewer.ScrollToHorizontalOffset(Math.Max(0, x * _bracketZoom - BracketScrollViewer.ViewportWidth / 2));
        BracketScrollViewer.ScrollToVerticalOffset(Math.Max(0, y * _bracketZoom - BracketScrollViewer.ViewportHeight / 2));
    }

    private void StopBracketFly()
    {
        _flyTimer?.Stop();
        _flying = false;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetBracketZoom(_bracketZoom * 1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetBracketZoom(_bracketZoom / 1.25);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => FitBracket();

    private void BracketScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // In de bracket-tab doet het wiel zelf het zoomen; met een toets erbij schuif je.
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        e.Handled = true;

        if ((modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            BracketScrollViewer.ScrollToHorizontalOffset(BracketScrollViewer.HorizontalOffset - e.Delta * 0.5);
            return;
        }

        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift)
        {
            BracketScrollViewer.ScrollToVerticalOffset(BracketScrollViewer.VerticalOffset - e.Delta * 0.5);
            return;
        }

        SetBracketZoom(e.Delta > 0 ? _bracketZoom * 1.1 : _bracketZoom / 1.1);
    }

    // ========== Bracket: slepen om te schuiven ==========

    private const double DragThreshold = 4;

    private bool _dragPressed;
    private bool _dragging;
    private Point _dragStart;
    private Point _dragOrigin;

    /// <summary>
    /// Linkermuisknop indrukken onthoudt alleen het startpunt. De klik van een vakje gaat
    /// pas af bij MouseUp, zodat slepen voorrang krijgt op inzoomen.
    /// </summary>
    private void BracketScrollViewer_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragPressed = true;
        _dragging = false;
        _dragStart = e.GetPosition(BracketScrollViewer);
        _dragOrigin = new Point(BracketScrollViewer.HorizontalOffset, BracketScrollViewer.VerticalOffset);
    }

    private void BracketScrollViewer_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragPressed || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

        var now = e.GetPosition(BracketScrollViewer);

        // Eerste paar pixels zijn nog een klik; pas daarna wordt het slepen.
        if (!_dragging)
        {
            if (Math.Abs(now.X - _dragStart.X) < DragThreshold &&
                Math.Abs(now.Y - _dragStart.Y) < DragThreshold)
                return;

            _dragging = true;
            StopBracketFly();                        // slepen wint van een lopende animatie
            BracketScrollViewer.CaptureMouse();
            BracketScrollViewer.Cursor = System.Windows.Input.Cursors.SizeAll;
        }

        // De kaart volgt de muis: naar rechts slepen schuift de inhoud mee naar rechts.
        BracketScrollViewer.ScrollToHorizontalOffset(Math.Max(0, _dragOrigin.X - (now.X - _dragStart.X)));
        BracketScrollViewer.ScrollToVerticalOffset(Math.Max(0, _dragOrigin.Y - (now.Y - _dragStart.Y)));

        e.Handled = true;
    }

    private void BracketScrollViewer_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragPressed = false;

        if (_dragging)
        {
            _dragging = false;
            BracketScrollViewer.ReleaseMouseCapture();
            BracketScrollViewer.Cursor = null;

            // Dit was een sleep, geen klik: het vakje onder de cursor mag niets doen.
            e.Handled = true;
        }
    }

    private void HighlightNodes(ItemsControl parent, string filter)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi &&
                item is RecipeNode node)
            {
                bool match = string.IsNullOrEmpty(filter) ||
                             node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

                tvi.Foreground = match && !string.IsNullOrEmpty(filter)
                    ? (Brush)FindResource("Highlight")
                    : (Brush)FindResource("ScreenText");

                tvi.FontWeight = match && !string.IsNullOrEmpty(filter) ? FontWeights.Bold : FontWeights.Normal;

                if (match && !string.IsNullOrEmpty(filter))
                    tvi.IsExpanded = true;

                HighlightNodes(tvi, filter);
            }
        }
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetExpanded(RecipeTreeView, true);
    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetExpanded(RecipeTreeView, false);

    private void SetExpanded(ItemsControl parent, bool expand)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
            {
                tvi.IsExpanded = expand;
                SetExpanded(tvi, expand);
            }
        }
    }

    // ========== Weergave: tree of basis → eind ==========

    private void ShowTree_Click(object sender, RoutedEventArgs e) => ShowTreeView();
    private void ShowPlan_Click(object sender, RoutedEventArgs e) => ShowPlanView();
    private void ShowBracket_Click(object sender, RoutedEventArgs e) => ShowBracketView();
    private void BuildShoppingList_Click(object sender, RoutedEventArgs e) => RefreshShoppingList(showView: true);

    // Elke tab vervangt de hele werkplek, niet alleen het rechterscherm.
    private void ShowTreeView()
    {
        ShowWorkspace(TreeWorkspacePanel);
        HighlightViewButton(TreeViewButton);
    }

    private void ShowPlanView()
    {
        ShowWorkspace(PlanWorkspacePanel);
        HighlightViewButton(PlanViewButton);
    }

    private void ShowShoppingView()
    {
        ShowWorkspace(ShoppingWorkspacePanel);
        HighlightViewButton(ShoppingViewButton);
    }

    private void ShowBracketView()
    {
        ShowWorkspace(BracketWorkspacePanel);
        HighlightViewButton(BracketTabButton);

        // Liep het diagram achter op de boom (ander item gekozen op TREE of PLAN), dan
        // hier alsnog tekenen — anders zie je de vorige boom in deze tab staan.
        if (!ReferenceEquals(_bracketRoot, _currentRoot)) RefreshBracket();

        // Het paneel is pas gemeten als het zichtbaar is: daarna passend maken.
        Dispatcher.BeginInvoke(new Action(FitBracket), DispatcherPriority.Loaded);
    }

    private void ShowWorkspace(UIElement active)
    {
        // Boomtekst en stappenplan horen bij de boom die je nu ziet; mochten ze nog van
        // een vorig item zijn, dan worden ze hier bijgewerkt voordat de tab verschijnt.
        RefreshPlanAndText();

        TreeWorkspacePanel.Visibility = active == TreeWorkspacePanel ? Visibility.Visible : Visibility.Collapsed;
        BracketWorkspacePanel.Visibility = active == BracketWorkspacePanel ? Visibility.Visible : Visibility.Collapsed;
        PlanWorkspacePanel.Visibility = active == PlanWorkspacePanel ? Visibility.Visible : Visibility.Collapsed;
        ShoppingWorkspacePanel.Visibility = active == ShoppingWorkspacePanel ? Visibility.Visible : Visibility.Collapsed;

        // BRACKET is een eigen tab: daar blijft alleen het diagram over.
        var bracketOnly = active == BracketWorkspacePanel ? Visibility.Collapsed : Visibility.Visible;
        CraftHeaderPanel.Visibility = bracketOnly;
        CraftInputRow.Visibility = bracketOnly;
        CraftFilterRow.Visibility = bracketOnly;
        BookmarkPanel.Visibility = bracketOnly;
        WatchPanel.Visibility = bracketOnly;
    }

    private void HighlightViewButton(Button active)
    {
        foreach (var button in new[] { TreeViewButton, BracketTabButton, PlanViewButton, ShoppingViewButton })
        {
            button.Background = (Brush)FindResource(button == active ? "Accent" : "ButtonBg");
            button.Foreground = (Brush)FindResource(button == active ? "OnAccent" : "ButtonText");
            button.FontWeight = button == active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>Bouwt de boodschappenlijst van álle bookmarks samen.</summary>
    private void RefreshShoppingList(bool showView)
    {
        var roots = new List<RecipeNode>();

        // Eén budget voor de hele lijst: alle bookmarks samen blijven behapbaar.
        _nodeBudget = RecipePlanner.MaxTreeNodes;

        foreach (var choice in _bookmarks)
        {
            string key = string.IsNullOrWhiteSpace(choice.Key) ? choice.Item : choice.Key;
            roots.Add(BuildNode(key, choice.Count));
        }

        ShoppingOutput.Text = roots.Count == 0
            ? Lang.T("shopping.empty")
            : RecipePlanner.BuildShoppingList(roots, _recipes);

        if (showView) ShowShoppingView();
    }

    // ========== Export ==========

    /// <summary>Tekst van de weergave die nu zichtbaar is (tree, stappenplan of boodschappenlijst).</summary>
    private string CurrentViewText =>
        ShoppingWorkspacePanel.Visibility == Visibility.Visible ? ShoppingOutput.Text
        : PlanWorkspacePanel.Visibility == Visibility.Visible ? StepPlanOutput.Text
        : BracketOutput.Text;

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        string text = CurrentViewText;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // Het klembord kan door een ander programma bezet zijn.
            MessageBox.Show(Lang.T("msg.copy.failed", ex.Message),
                            Lang.T("title.clipboard"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(Lang.T("msg.copied"), "Done", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveTxt_Click(object sender, RoutedEventArgs e)
    {
        string text = CurrentViewText;
        if (string.IsNullOrWhiteSpace(text)) return;

        bool shopping = ShoppingWorkspacePanel.Visibility == Visibility.Visible;
        bool plan = !shopping && PlanWorkspacePanel.Visibility == Visibility.Visible;

        string suffix = shopping ? "boodschappenlijst" : plan ? "basis_tot_eind" : "recipe_tree";
        string baseName = shopping ? "bookmarks" : ItemInput.Text.Trim();

        var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt",
            FileName = $"{baseName}_{suffix}.txt",
            Title = Lang.T(shopping ? "dialog.save.title.shopping" : "dialog.save.title")
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.WriteAllText(dialog.FileName, text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Lang.T("msg.save.failed", ex.Message),
                                Lang.T("title.savefailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(Lang.T("msg.saved", dialog.FileName), "Exported ✨", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SavePng_Click(object sender, RoutedEventArgs e)
    {
        // Het diagram dat je in de app ziet (vakjes met iconen en ellebooglijnen), niet
        // de tekstversie die COPY/.TXT gebruiken: dat is de afbeelding die je deelt.
        // Zonder boom valt er niets te exporteren.
        var root = _bracketRoot ?? _currentRoot;
        if (root == null) return;

        var diagram = BracketDiagram.Build(root, this, FilterBox.Text);
        diagram.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        diagram.Arrange(new Rect(diagram.DesiredSize));

        // Een grote boom past niet op één plaat: dezelfde slicing als eerst, maar op
        // hele rijen van het diagram zodat geen enkel vakje doormidden gaat.
        var plan = PngSlicing.PlanFor(diagram.Width, diagram.Height, BracketDiagram.RowPitch);

        var dialog = new SaveFileDialog
        {
            Filter = "PNG Image (*.png)|*.png",
            FileName = $"{ItemInput.Text.Trim()}_recipe_tree.png",
            Title = "Save Recipe Tree as PNG"
        };

        if (dialog.ShowDialog() != true) return;

        string folder = Path.GetDirectoryName(dialog.FileName) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(dialog.FileName);
        var background = (Brush)FindResource("ScreenBg");
        var written = new List<string>();
        var temporaries = new List<string>();

        try
        {
            for (int part = 0; part < plan.Count; part++)
            {
                string path = plan.Count == 1
                    ? dialog.FileName
                    : Path.Combine(folder, $"{baseName}_part{part + 1}.png");

                // Eerst naar een tijdelijk bestand schrijven en pas hernoemen als het
                // complete PNG klaar is: een afgebroken export laat zo nooit een leeg
                // of half bestand achter.
                string tempPath = path + ".tmp";
                temporaries.Add(tempPath);

                using (var stream = File.Create(tempPath))
                    PngExport.RenderPart(diagram, plan, part, background, stream);

                File.Move(tempPath, path, overwrite: true);
                written.Add(path);
            }

            string note = plan.Count > 1
                ? Lang.T("png.note.parts", plan.Count, plan.Width, plan.PartHeight, folder)
                : plan.Scale < 0.999
                    ? Lang.T("png.note.scaled", PngSlicing.MaxWidth, plan.Width, plan.Height)
                    : "";

            MessageBox.Show($"PNG saved to:\n{written[0]}{note}", "Image Exported 🖼️", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            foreach (var temp in temporaries)
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }

            MessageBox.Show(Lang.T("msg.png.failed", ex.Message), Lang.T("title.exportfailed"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ========== Titelbalk ==========

    // De standaard titelbalk is wit en steekt fel af tegen het donkere paneel;
    // Windows 10 1809+ kan hem donker tekenen via DWM.
    private const int DwmDarkModeAttribute = 20;
    private const int DwmDarkModeAttributeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int dark = _isDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmDarkModeAttribute, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmDarkModeAttributeLegacy, ref dark, sizeof(int));
        }
        catch
        {
            // Oudere Windows-versies kennen dit attribuut niet: titelbalk blijft standaard.
        }
    }

    // ========== Theme ==========

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        ApplyTheme(_isDark);
        ThemeButton.Content = _isDark ? "☀️ Light Mode" : "🌙 Dark Mode";
    }

    /// <summary>
    /// Past het kleurenpalet aan. De schermvlakken (terminal, invoervelden) blijven in
    /// beide thema's donker: daar staat de boom- en plantekst op en die moet leesbaar
    /// blijven. Alleen de panelen, bevels en labels wisselen mee.
    /// </summary>
    private void ApplyTheme(bool dark)
    {
        // Schermkleuren: bewust in beide thema's gelijk.
        Set("SlotFace", 0x12, 0x16, 0x1A);
        Set("ScreenBg", 0x0A, 0x0D, 0x10);
        Set("ScreenText", 0xE6, 0xED, 0xE2);
        Set("ScreenDim", 0x8C, 0x97, 0x8F);
        Set("SelectionBg", 0x22, 0x40, 0x2A);

        if (dark)
        {
            Set("WindowBg", 0x0C, 0x10, 0x14);
            Set("PanelFace", 0x2B, 0x31, 0x38);
            Set("PanelFaceAlt", 0x22, 0x27, 0x2D);
            Resources[SystemColors.ControlBrushKey] = new SolidColorBrush(Color.FromRgb(0x22, 0x27, 0x2D));
            Set("BevelLight", 0x59, 0x61, 0x6B);
            Set("BevelDark", 0x14, 0x18, 0x1C);
            Set("Edge", 0x05, 0x08, 0x0B);
            Set("TextPrimary", 0xE4, 0xE9, 0xDD);
            Set("TextSecondary", 0x98, 0xA2, 0xA8);
            Set("Accent", 0x9B, 0x7B, 0xD4);
            Set("OnAccent", 0x14, 0x10, 0x1C);
            Set("ButtonBg", 0x46, 0x50, 0x5B);
            Set("ButtonText", 0xF0, 0xF4, 0xF7);
            Set("ButtonFace", 0x3B, 0x44, 0x4D);
            Set("SuccessBg", 0x4E, 0x8A, 0x46);
            Set("SuccessText", 0xF2, 0xFF, 0xEC);
            Set("Highlight", 0xF2, 0xC9, 0x4C);
            Set("WatchActive", 0x86, 0xC4, 0x6A);
            Set("WatchInactive", 0x77, 0x81, 0x8B);
        }
        else
        {
            // Klassiek Minecraft-paneel: lichtgrijs met witte bevel en donkere schaduw.
            Set("WindowBg", 0xD7, 0xDA, 0xDF);
            Set("PanelFace", 0xC6, 0xC6, 0xC6);
            Set("PanelFaceAlt", 0xB2, 0xB2, 0xB2);
            Resources[SystemColors.ControlBrushKey] = new SolidColorBrush(Color.FromRgb(0xB2, 0xB2, 0xB2));
            Set("BevelLight", 0xFF, 0xFF, 0xFF);
            Set("BevelDark", 0x55, 0x55, 0x55);
            Set("Edge", 0x2A, 0x2A, 0x2A);
            Set("TextPrimary", 0x33, 0x38, 0x3C);
            Set("TextSecondary", 0x53, 0x59, 0x5E);
            Set("Accent", 0x7B, 0x4F, 0xA8);
            Set("OnAccent", 0xFF, 0xFF, 0xFF);
            Set("ButtonBg", 0x5E, 0x6B, 0x75);
            Set("ButtonText", 0xFF, 0xFF, 0xFF);
            Set("ButtonFace", 0x5E, 0x6B, 0x75);
            Set("SuccessBg", 0x3F, 0x7A, 0x38);
            Set("SuccessText", 0xFF, 0xFF, 0xFF);
            Set("Highlight", 0xB4, 0x69, 0x0E);
            Set("WatchActive", 0x3F, 0x7A, 0x38);
            Set("WatchInactive", 0x6B, 0x73, 0x7A);
        }

        ApplyTitleBarTheme();

        // Het diagram tekent met de themakleuren, dus na een themawissel opnieuw opbouwen.
        RefreshBracket();
    }

    private void Set(string key, byte r, byte g, byte b) =>
        Resources[key] = new SolidColorBrush(Color.FromRgb(r, g, b));
}
