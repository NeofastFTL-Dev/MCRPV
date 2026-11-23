using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Mcrpv;

/// <summary>Eén regel in het mods-overzicht.</summary>
public sealed class ModRow
{
    public string Name { get; init; } = "";
    public string ModId { get; init; } = "";
    public string Version { get; init; } = "";
    public string Jar { get; init; } = "";
    public int Recipes { get; init; }
    public int Scripts { get; init; }

    public string RecipesText => Recipes > 0 ? Recipes.ToString() : "";
    public string ScriptsText => Scripts > 0 ? Scripts.ToString() : "";

    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        ModId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Jar.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Version.Contains(query, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

/// <summary>
/// Laat de mods-database van de instance zien: welke mods erin zitten, met welke versie,
/// en hoeveel recepten/scripts de scan per mod heeft gevonden.
/// </summary>
public partial class ModsWindow : Window
{
    private readonly List<ModRow> _rows;

    public ModsWindow(ModDatabase database)
    {
        InitializeComponent();

        VersionText.Text = "v" + (typeof(ModsWindow).Assembly.GetName().Version?.ToString(3) ?? "1.23.0");

        _rows = database.Mods
                        .OrderByDescending(m => m.JsonRecipes + m.Scripts)
                        .ThenBy(m => m.Caption, StringComparer.OrdinalIgnoreCase)
                        .Select(m => new ModRow
                        {
                            Name = m.Caption,
                            ModId = m.ModId,
                            Version = m.Version,
                            Jar = m.Jar,
                            Recipes = m.JsonRecipes,
                            Scripts = m.Scripts
                        })
                        .ToList();

        int withRecipes = _rows.Count(r => r.Recipes > 0);
        int withScripts = _rows.Count(r => r.Scripts > 0);

        string stamp = database.BuiltAt == DateTime.MinValue
            ? ""
            : Lang.T("mods.updated", database.BuiltAt.ToString("yyyy-MM-dd HH:mm"));

        StatusText.Text = Lang.T("mods.status", _rows.Count, database.JarRecipes, withRecipes, withScripts, stamp);

        ModList.ItemsSource = _rows;
        CountText.Text = $"{_rows.Count} mods";
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string query = ModSearch.Text.Trim();

        var filtered = query.Length == 0
            ? _rows
            : _rows.Where(r => r.Matches(query)).ToList();

        ModList.ItemsSource = filtered;
        CountText.Text = query.Length == 0
            ? Lang.T("mods.count", _rows.Count)
            : Lang.T("mods.count.filtered", filtered.Count, _rows.Count);
    }
}
