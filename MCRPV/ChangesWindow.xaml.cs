using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Mcrpv;

/// <summary>
/// Diff van de set in de app met de fold van het actieve pack, met filter en
/// doorklik naar de herkomst van een regel; plus de packkeuze. De knoppen gebruiken
/// dezelfde code als de CLI (--reindex=pack), zodat app en command line gelijk lopen.
/// </summary>
public partial class ChangesWindow : Window
{
    private readonly Func<Dictionary<string, Recipe>> _recipes;
    private readonly Func<bool> _usesFold;
    private readonly Action<Dictionary<string, Recipe>> _useFold;
    private readonly Action _usePlain;
    private readonly Action<string> _showProvenance;

    private List<DiffRow> _rows = new();

    /// <summary>De soortkeuze in de XAML vuurt SelectionChanged vóór de rest bestaat.</summary>
    private bool _ready;

    public ChangesWindow(Func<Dictionary<string, Recipe>> recipes,
                         Func<bool> usesFold,
                         Action<Dictionary<string, Recipe>> useFold,
                         Action usePlain,
                         Action<string> showProvenance)
    {
        InitializeComponent();

        _recipes = recipes;
        _usesFold = usesFold;
        _useFold = useFold;
        _usePlain = usePlain;
        _showProvenance = showProvenance;

        _ready = true;
        Loaded += (_, _) => Refresh();
    }

    private static string DataFolder => IndexCommands.DataFolder;

    /// <summary>Het actieve pack en zijn fold; de fold is null als er nog geen is.</summary>
    private static (PackProfile? Pack, Dictionary<string, Recipe>? Fold) ReadFold()
    {
        var pack = PackProfile.Load(Path.Combine(DataFolder, "pack.json"));
        if (pack == null || pack.Pack.Length == 0) return (pack, null);

        try
        {
            string path = Path.Combine(DataFolder, "folded-" + pack.Pack + ".json");
            return (pack, File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(path))
                : null);
        }
        catch
        {
            return (pack, null);
        }
    }

    private string SourceLabel() => _usesFold() ? "📥 fold" : "📄 recipes.json";

    /// <summary>Tekent de diff opnieuw; <paramref name="note"/> vertelt wat er net gebeurde.</summary>
    private void Refresh(string? note = null)
    {
        var (pack, fold) = ReadFold();
        bool hasFold = fold is { Count: > 0 };

        ReindexButton.IsEnabled = pack != null && pack.InstancePath.Length > 0;
        LoadFoldButton.IsEnabled = SaveFoldButton.IsEnabled = hasFold;

        PackCaption.Text = pack?.Caption ?? Lang.T("changes.caption.nopack");
        _rows = new List<DiffRow>();

        if (!hasFold)
        {
            SummaryText.Text = pack != null
                ? Lang.T("changes.now", SourceLabel(), pack.Pack)
                : Lang.T("changes.nopack");
        }
        else
        {
            var current = _recipes();
            var diff = DiffReport.Compare(current, fold!);   // hasFold: de fold is gelezen

            SummaryText.Text = Lang.T("changes.summary", SourceLabel(),
                                      current.Count.ToString("N0"), fold!.Count.ToString("N0"),
                                      diff.Empty ? Lang.T("changes.nodiff") : Lang.T("changes.diff", diff.Summary));

            _rows = diff.Entries
                        .Select(e => new DiffRow(e.Label(), e.Item))
                        .ToList();
        }

        if (note != null) SummaryText.Text += " · " + note;

        ApplyFilter();
        LoadPacks();
    }

    /// <summary>Filtert op soort (+/−/~) en op item, mod of scriptbestand.</summary>
    private void ApplyFilter()
    {
        if (!_ready) return;

        string kind = KindBox.SelectedIndex switch { 1 => "+", 2 => "-", 3 => "~", _ => "" };
        string needle = FilterBox.Text.Trim();

        var shown = _rows.Where(r => (kind.Length == 0 || r.Kind == kind) &&
                                     (needle.Length == 0 ||
                                      r.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                         .ToList();

        DiffList.ItemsSource = shown;
        CountText.Text = Lang.T("changes.shown", shown.Count.ToString("N0"), _rows.Count.ToString("N0"));
    }

    /// <summary>Eén regel in de lijst; <see cref="Item"/> is het recept waar hij over gaat.</summary>
    private sealed record DiffRow(string Label, string Item)
    {
        public string Kind => Label.Length > 0 ? Label[..1] : "";
    }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void KindBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        ApplyFilter();

    private void DiffList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DiffList.SelectedItem is DiffRow row) _showProvenance(row.Item);
    }

    /// <summary>
    /// De keuzelijst bewaart de profielobjecten zelf. Een verse Profiles.Load kan de
    /// volgorde veranderen; op index terugzoeken zou dan het verkeerde pack opleveren.
    /// </summary>
    private void LoadPacks()
    {
        var book = Profiles.Load(DataFolder);
        var choices = book.Packs.Select(p => new PackChoice(p, book.Active)).ToList();

        PackCombo.ItemsSource = choices;
        PackCombo.SelectedItem = choices.FirstOrDefault(c => c.Pack.Pack == book.Active) ?? choices.FirstOrDefault();

        PackInfoText.Text = choices.Count == 0
            ? Lang.T("changes.noprofiles")
            : Lang.T("changes.profiles", choices.Count, book.Active);
    }

    /// <summary>Eén regel in de keuzelijst; houdt het profiel vast in plaats van een index.</summary>
    private sealed record PackChoice(PackProfile Pack, string Active)
    {
        public string Label =>
            Lang.T("changes.packitem", Pack.Pack, Pack.Minecraft, Pack.ResultCount.ToString("N0")) +
            (Pack.Pack == Active ? Lang.T("changes.active") : "") +
            (Pack.UseFold ? Lang.T("changes.fold") : "");
    }

    private void PackCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PackCombo.SelectedItem is not PackChoice choice) return;

        var pack = choice.Pack;
        PackInfoText.Text = Lang.T("changes.packinfo", pack.Pack, pack.Minecraft, pack.Jars,
                                   pack.BaseCount.ToString("N0"), pack.PackCount.ToString("N0"),
                                   pack.UserCount.ToString("N0"), pack.ResultCount.ToString("N0"),
                                   $"{pack.Built:yyyy-MM-dd HH:mm} · " +
                                   (pack.UseFold ? Lang.T("changes.setfold") : Lang.T("changes.setplain")));
    }

    private async void Reindex_Click(object sender, RoutedEventArgs e)
    {
        // De knoppen zijn de enige plek waar dit gestart kan worden; uit is uit.
        ReindexButton.IsEnabled = LoadFoldButton.IsEnabled = SaveFoldButton.IsEnabled = UsePlainButton.IsEnabled = false;
        SummaryText.Text = Lang.T("changes.busy.reindex");

        try
        {
            await Task.Run(() => IndexCommands.RunIndex(new[] { "--reindex=pack" }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Lang.T("changes.reindex.failed", ex.Message),
                            Lang.T("title.packlayer"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Refresh();
        }
    }

    private void LoadFold_Click(object sender, RoutedEventArgs e)
    {
        var (pack, fold) = ReadFold();
        if (fold == null || fold.Count == 0) return;

        _useFold(fold);
        Refresh(Lang.T("changes.loaded", pack?.Pack));
    }

    /// <summary>De dode patches opnieuw bepalen en als buglijst openen.</summary>
    private async void DeadPatches_Click(object sender, RoutedEventArgs e)
    {
        DeadPatchesButton.IsEnabled = false;
        SummaryText.Text = Lang.T("changes.busy.dead");

        try
        {
            await Task.Run(() => IndexCommands.RunDeadPatches());
        }
        catch
        {
            // Het indexrapport noemt de fout; het venster toont dan de vorige lijst.
        }
        finally
        {
            DeadPatchesButton.IsEnabled = true;
            Refresh();
        }

        new DeadPatchesWindow { Owner = this }.ShowDialog();
    }

    private void UsePlain_Click(object sender, RoutedEventArgs e)
    {
        _usePlain();
        Refresh(Lang.T("changes.plainloaded"));
    }

    private void SaveFold_Click(object sender, RoutedEventArgs e)
    {
        var (pack, fold) = ReadFold();
        if (fold == null || fold.Count == 0) return;

        string target = Path.Combine(DataFolder, "recipes.json");
        var answer = MessageBox.Show(this,
            Lang.T("changes.savefold.ask", pack?.Pack, fold.Count.ToString("N0")),
            Lang.T("title.savejson"), MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        try
        {
            if (File.Exists(target))
                File.Copy(target, Path.Combine(DataFolder, "recipes.before-fold.json"), true);

            File.WriteAllText(target,
                              JsonSerializer.Serialize(fold, new JsonSerializerOptions { WriteIndented = true }));

            // Nu staat de fold ook op schijf: laad recipes.json, zodat de app niet meer
            // van het fold-bestand afhangt en de diff daarna leeg is.
            _usePlain();
            Refresh(Lang.T("changes.savedfold"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Lang.T("changes.save.failed", ex.Message),
                            Lang.T("title.recipesjson"), MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }
    }

    private void UsePack_Click(object sender, RoutedEventArgs e)
    {
        if (PackCombo.SelectedItem is not PackChoice choice) return;

        var pack = choice.Pack;
        pack.Save(Path.Combine(DataFolder, "pack.json"));
        Profiles.Upsert(DataFolder, pack);

        // Meteen de set van dat pack, met de keuze die daar eerder voor gemaakt is.
        var (_, fold) = ReadFold();
        if (pack.UseFold && fold is { Count: > 0 }) _useFold(fold);
        else _usePlain();

        MessageBox.Show(this,
            Lang.T("changes.activated", pack.Pack, pack.ResultCount.ToString("N0"),
                   Lang.T(pack.UseFold ? "changes.setfold" : "changes.setplain")),
            Lang.T("title.activepack"), MessageBoxButton.OK, MessageBoxImage.Information);

        Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
}
