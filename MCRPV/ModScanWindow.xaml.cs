using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Mcrpv;

/// <summary>
/// Venster voor het scannen van recepten uit de mods van de instance. Je kiest welke
/// script-mappen meetellen (pack-modes staan in Project Ozone 3 naast elkaar) en ziet
/// daarna wat de scan opleverde voordat je hem overneemt.
/// </summary>
public partial class ModScanWindow : Window
{
    private readonly string _instanceRoot;
    private readonly string _dataDirectory;
    private readonly IDictionary<string, Recipe>? _baseRecipes;
    private readonly List<CheckBox> _checks = new();

    /// <summary>De gescande recepten (null zolang er niets gescand is).</summary>
    public ModScanResult? Result { get; private set; }

    /// <summary>De script-mappen die de gebruiker aan had staan (om te onthouden).</summary>
    public List<string> SelectedScripts { get; private set; } = new();

    public ModScanWindow(string instanceRoot, IReadOnlyList<string> scriptFolders, string dataDirectory,
                         IEnumerable<string>? previousSelection, IDictionary<string, Recipe>? baseRecipes = null)
    {
        InitializeComponent();

        _instanceRoot = instanceRoot;
        _dataDirectory = dataDirectory;
        _baseRecipes = baseRecipes;

        VersionText.Text = "v" + (typeof(ModScanWindow).Assembly.GetName().Version?.ToString(3) ?? "1.21.0");
        InstanceText.Text = $"📁 {instanceRoot}";

        var database = ModDatabase.Load(DatabasePath);
        string databaseText = database.Mods.Count > 0
            ? Lang.T("scan.db", database.Mods.Count, database.BuiltAt.ToString("yyyy-MM-dd HH:mm"))
            : Lang.T("scan.nodb");

        InstanceText.Text += "\n" + databaseText +
                             (database.RecipeJars > 0 ? Lang.T("scan.json", database.RecipeJars) : "");

        var previous = new HashSet<string>(previousSelection ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        bool anythingRemembered = previous.Count > 0;

        // Eerste keer: all_modes plus de pack-mode die deze app als eerste noemt
        // (PO3 Mythic), anders de eerste mode die de instance heeft.
        string defaultMode = new[] { "mythic", "normal", "kappa", "titan" }
            .FirstOrDefault(mode => scriptFolders.Any(f => Path.GetFileName(f).Equals(mode, StringComparison.OrdinalIgnoreCase))) ?? "";

        foreach (var folder in scriptFolders)
        {
            string label = ModRecipeScanner.FolderLabel(folder);
            bool isKube = label.StartsWith("KubeJS", StringComparison.OrdinalIgnoreCase);
            bool isMode = label.Equals(defaultMode, StringComparison.OrdinalIgnoreCase);

            bool isChecked = anythingRemembered
                ? previous.Contains(folder)
                : isKube || IsDefaultMode(label) || isMode;

            var check = new CheckBox
            {
                // Dubbele underscore: anders ziet WPF "all_modes" als toegangstoets
                // en verdwijnt de liggende streep uit het label.
                Content = label.Replace("_", "__"),
                Tag = folder,
                IsChecked = isChecked,
                Margin = new Thickness(0, 3, 0, 3),
                Foreground = (System.Windows.Media.Brush)FindResource("ScreenText"),
                ToolTip = folder + Lang.T(isKube ? "scan.kube.tip" : "scan.ct.tip")
            };

            _checks.Add(check);
            FolderPanel.Children.Add(check);
        }

        if (_checks.Count == 0)
        {
            FolderPanel.Children.Add(new TextBlock
            {
                Text = Lang.T("scan.noscripts"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("ScreenDim")
            });
        }
        else
        {
            FolderPanel.Children.Add(new TextBlock
            {
                Text = Lang.T("scan.tip"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("ScreenDim")
            });
        }
    }

    private string DatabasePath => Path.Combine(_dataDirectory, "mods.json");

    /// <summary>all_modes geldt in elke pack-mode; de rest is per mode.</summary>
    private static bool IsDefaultMode(string label) =>
        label.Contains("all modes", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("all_modes", StringComparison.OrdinalIgnoreCase);

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        SelectedScripts = _checks.Where(c => c.IsChecked == true)
                                 .Select(c => (string)c.Tag)
                                 .ToList();

        ScanButton.IsEnabled = false;
        UseButton.IsEnabled = false;
        ScanResultText.Text = Lang.T("scan.scanning");

        var progress = new Progress<string>(text => ScanResultText.Text = text);

        ModScanResult result;

        try
        {
            // De scan leest honderden jars en scripts: op de achtergrond, maar hier
            // afgewacht. Een losse Task.Run met Dispatcher.Invoke erin leverde zijn werk
            // later af bij een venster dat je allang gesloten had — dat kwam als
            // "Achtergrondfout" terug terwijl je iets heel anders deed.
            result = await Task.Run(() => ModRecipeScanner.Scan(
                _instanceRoot, SelectedScripts, DatabasePath, _baseRecipes,
                text => ((IProgress<string>)progress).Report(text)));
        }
        finally
        {
            // Ook als er iets onverwachts misgaat blijft de knop bruikbaar. De fout zelf
            // gaat naar crash.log; hier inslikken zou hem juist onzichtbaar maken.
            ScanButton.IsEnabled = true;
        }

        // Sloot je het venster tijdens de scan, dan alleen het resultaat bewaren.
        Result = result;
        if (!IsVisible) return;

        UseButton.IsEnabled = result.Error == null && result.Added > 0;

        ScanResultText.Text = result.Error != null
            ? "⚠ " + result.Summary
            : result.Summary + (result.Added == 0
                ? Lang.T("scan.noresult")
                : Lang.T("scan.clickuse"));
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (Result == null || Result.Error != null) return;

        DialogResult = true;
        Close();
    }
}
