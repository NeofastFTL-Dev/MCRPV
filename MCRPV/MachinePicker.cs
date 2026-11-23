using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mcrpv;

/// <summary>
/// Doorzoekbaar keuzepaneel voor de manier waarop een item gemaakt wordt.
/// Alles staat gegroepeerd per machine (JEI-tab), met een filterveld bovenaan, pijltjes +
/// enter om te kiezen en een ster per regel om die machine meteen als voorkeur te zetten.
/// </summary>
public sealed class MachinePicker : Window
{
    private readonly IReadOnlyList<Recipe> _options;
    private readonly StackPanel _list = new() { Margin = new Thickness(6, 0, 6, 6) };
    private readonly TextBox _search = new();
    private readonly ScrollViewer _scroll = new();
    private readonly TextBlock _status = new();
    private readonly List<Border> _rows = new();
    private readonly List<int> _rowOptions = new();

    private readonly string _itemKey;
    private readonly IReadOnlyDictionary<string, Recipe> _recipes;
    private readonly IReadOnlyDictionary<string, string> _preferences;
    private readonly IReadOnlyDictionary<string, int> _choices;

    private readonly TextBlock _detail = new();
    private readonly Dictionary<int, ChainPreview> _previewCache = new();

    /// <summary>True terwijl het onthouden filter in het zoekveld gezet wordt (geen rebuild per teken).</summary>
    private bool _suspendRebuild;

    private readonly Brush _text;
    private readonly Brush _dim;
    private readonly Brush _accent;
    private readonly Brush _border;
    private readonly Brush _screen;
    private readonly Brush _hover = new SolidColorBrush(Color.FromRgb(0x18, 0x1E, 0x28));
    private readonly Brush _selected = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x3A));
    private readonly Brush _warn = new SolidColorBrush(Color.FromRgb(0xD8, 0xA6, 0x57));

    private readonly string? _preference;
    private int _highlight;

    /// <summary>Gekozen recept (index in Recipe.Options), of -1 als het paneel zonder keuze sloot.</summary>
    public int ChosenIndex { get; private set; } = -1;

    /// <summary>Wat er in het zoekveld staat, zodat de app het filter kan onthouden.</summary>
    public string FilterText => _search.Text.Trim();

    /// <summary>Machine die je met de ster als voorkeur instelde, of null.</summary>
    public string? PreferenceMachine { get; private set; }

    /// <summary>True als je de bestaande voorkeur introk.</summary>
    public bool PreferenceCleared { get; private set; }

    public MachinePicker(Window owner, string itemName, IReadOnlyList<Recipe> options, int currentIndex, string? preference,
        string? filter, string itemKey, IReadOnlyDictionary<string, Recipe> recipes,
        IReadOnlyDictionary<string, string> preferences, IReadOnlyDictionary<string, int> choices)
    {
        _options = options;
        _preference = preference;
        _itemKey = itemKey;
        _recipes = recipes;
        _preferences = preferences;
        _choices = choices;

        _text = Pick(owner, "ScreenText", Color.FromRgb(0xE6, 0xED, 0xE2));
        _dim = Pick(owner, "ScreenDim", Color.FromRgb(0x8C, 0x97, 0x8F));
        _accent = Pick(owner, "Accent", Color.FromRgb(0x9B, 0x7B, 0xD4));
        _border = Pick(owner, "BevelLight", Color.FromRgb(0x59, 0x61, 0x6B));
        _screen = Pick(owner, "ScreenBg", Color.FromRgb(0x0A, 0x0D, 0x10));

        Title = Lang.T("pick.title");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 660;
        Height = 540;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1A));
        FontFamily = new FontFamily("Consolas");
        FontSize = 12;

        var root = new Border
        {
            BorderBrush = _accent,
            BorderThickness = new Thickness(1),
            Background = Background
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(BuildHeader(itemName));
        grid.Children.Add(BuildSearch());

        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Content = _list;
        _scroll.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetRow(_scroll, 2);
        grid.Children.Add(_scroll);
        grid.Children.Add(BuildDetail());

        var footer = new TextBlock
        {
            Text = Lang.T("pick.hint"),
            Foreground = _dim,
            FontSize = 10.5,
            Margin = new Thickness(12, 6, 12, 8)
        };
        Grid.SetRow(footer, 4);
        grid.Children.Add(footer);

        // Voorkeur-regel bovenaan: laat zien wat er nu vastligt en biedt wissen aan.
        if (!string.IsNullOrWhiteSpace(_preference))
        {
            var clear = new TextBlock
            {
                Text = Lang.T("pick.preference", _preference),
                Foreground = _accent,
                FontSize = 10.5,
                Margin = new Thickness(12, 0, 12, 6),
                Cursor = Cursors.Hand,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            clear.MouseLeftButtonUp += (_, _) => { PreferenceCleared = true; Close(); };
            clear.ToolTip = Lang.T("pick.clear.tip");
            Grid.SetRow(clear, 1);
            grid.RowDefinitions[1].Height = GridLength.Auto;

            // Eerst uit het grid halen: een element dat al een logische ouder heeft mag
            // niet in een andere panel gezet worden (InvalidOperationException).
            grid.Children.Remove(_search);

            var wrapper = new StackPanel();
            wrapper.Children.Add(_search);
            wrapper.Children.Add(clear);
            Grid.SetRow(wrapper, 1);
            grid.Children.Add(wrapper);
        }

        root.Child = grid;
        Content = root;

        // Het filter van de vorige keer staat meteen in het zoekveld.
        _suspendRebuild = true;
        _search.Text = filter ?? "";
        _suspendRebuild = false;

        Rebuild(currentIndex);

        // Toetsen op vensterniveau afvangen: de TextBox houdt ↑/↓ anders zelf in en dan
        // verschoof de markering niet. PreviewKeyDown komt vóór de TextBox aan de beurt.
        PreviewKeyDown += (_, args) =>
        {
            switch (args.Key)
            {
                case Key.Down:
                    MoveHighlight(1);
                    args.Handled = true;
                    break;
                case Key.Up:
                    MoveHighlight(-1);
                    args.Handled = true;
                    break;
                case Key.Enter:
                    PickHighlighted();
                    args.Handled = true;
                    break;
                case Key.Escape:
                    Close();
                    args.Handled = true;
                    break;
            }
        };

        Loaded += (_, _) =>
        {
            _search.Focus();
            _search.CaretIndex = _search.Text.Length;
            BringHighlightIntoView();
        };
    }

    private UIElement BuildHeader(string itemName)
    {
        var panel = new Grid { Margin = new Thickness(12, 10, 12, 8) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = Lang.T("pick.heading", itemName),
            Foreground = _text,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _status.Foreground = _dim;
        _status.FontSize = 10.5;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(12, 0, 0, 0);

        Grid.SetColumn(title, 0);
        Grid.SetColumn(_status, 1);
        panel.Children.Add(title);
        panel.Children.Add(_status);

        return panel;
    }

    private UIElement BuildSearch()
    {
        _search.Background = _screen;
        _search.Foreground = _text;
        _search.CaretBrush = _accent;
        _search.BorderBrush = _border;
        _search.BorderThickness = new Thickness(1);
        _search.Padding = new Thickness(8, 5, 8, 5);
        _search.Margin = new Thickness(12, 0, 12, 8);
        _search.ToolTip = Lang.T("pick.search.tip");
        AutomationProperties.SetAutomationId(_search, "MachineSearch");
        AutomationProperties.SetName(_search, Lang.T("pick.search.name"));

        _search.TextChanged += (_, _) =>
        {
            if (!_suspendRebuild) Rebuild(-1);
        };

        Grid.SetRow(_search, 1);
        return _search;
    }

    /// <summary>Paneel onder de lijst: de keten van de regel die je vasthoudt.</summary>
    private UIElement BuildDetail()
    {
        _detail.Foreground = _dim;
        _detail.FontSize = 10.5;
        _detail.TextWrapping = TextWrapping.NoWrap;
        AutomationProperties.SetAutomationId(_detail, "MachinePreview");

        var inner = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _detail
        };

        var border = new Border
        {
            Child = inner,
            Background = _screen,
            BorderBrush = _border,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12, 6, 12, 0),
            Padding = new Thickness(8, 5, 8, 5),
            MaxHeight = 136
        };

        Grid.SetRow(border, 3);
        return border;
    }

    /// <summary>
    /// Laat onder de lijst zien wat deze keuze oplevert: de machine, het aantal stappen en
    /// basismaterialen, en de eerste tussenstappen. Zo kun je de keten beoordelen vóór je kiest.
    /// </summary>
    private void ShowPreviewFor(int optionIndex)
    {
        if (optionIndex < 0 || optionIndex >= _options.Count)
        {
            _detail.Text = "";
            return;
        }

        if (!_previewCache.TryGetValue(optionIndex, out var preview))
        {
            preview = RecipePlanner.PreviewChain(_itemKey, _options[optionIndex], _recipes, _preferences, _choices);
            _previewCache[optionIndex] = preview;
        }

        string machine = MachineName(_options[optionIndex]);
        var sb = new StringBuilder();
        sb.Append(machine);

        if (preview.Steps > 0)
        {
            string steps = Lang.T(preview.Steps == 1 ? "pick.steps.one" : "pick.steps", preview.Steps);
            string bases = Lang.T(preview.BaseMaterials == 1 ? "pick.bases.one" : "pick.bases", preview.BaseMaterials);
            sb.Append($"  ·  {steps} · {bases}");
        }

        sb.AppendLine();

        // Recepten die elkaars omgekeerde zijn (blok ↔ ingot, glas ↔ scherf) horen er
        // niet uit te zien als een gewone route: de keten wentelt erop rond.
        string? reverse = RecipePlanner.ReversePairWith(_itemKey, _options[optionIndex], _recipes);
        if (reverse != null)
        {
            sb.AppendLine(Lang.T("pick.reverse", ItemNames.Display(reverse), ItemNames.Display(_itemKey)));
            sb.AppendLine(Lang.T("pick.reverse.note"));
        }

        sb.Append(preview.Lines.Length > 0 ? preview.Lines : Lang.T("preview.noinput"));
        _detail.Text = sb.ToString();
    }

    private void ShowHighlightedPreview()
    {
        if (_highlight < 0 || _highlight >= _rowOptions.Count)
        {
            _detail.Text = "";
            return;
        }

        ShowPreviewFor(_rowOptions[_highlight]);
    }

    /// <summary>Bouwt de lijst opnieuw: per machine een kop, daaronder de recepten.</summary>
    private void Rebuild(int keepHighlightFor)
    {
        _list.Children.Clear();
        _rows.Clear();
        _rowOptions.Clear();

        string filter = _search.Text.Trim();

        var matching = new List<int>();
        for (int i = 0; i < _options.Count; i++)
            if (Matches(i, filter)) matching.Add(i);

        foreach (var group in matching.GroupBy(i => MachineName(_options[i]))
                                      .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Children.Add(MakeGroupHeader(group.Key, group.Count()));

            foreach (int index in group)
                _list.Children.Add(MakeRow(index));
        }

        if (matching.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = Lang.T("pick.none"),
                Foreground = _dim,
                Margin = new Thickness(10, 10, 10, 10)
            });
        }

        _status.Text = filter.Length == 0
            ? Lang.T("pick.count.machines", _options.Count,
                     matching.Select(i => MachineName(_options[i])).Distinct().Count())
            : Lang.T("pick.count.filtered", matching.Count, _options.Count);

        // Hoeveel van de zichtbare recepten een omgekeerd paar vormen (blok ↔ ingot).
        int reverseCount = matching.Count(i => RecipePlanner.ReversePairWith(_itemKey, _options[i], _recipes) != null);
        if (reverseCount > 0) _status.Text += Lang.T("pick.reverse.count", reverseCount);

        // Bij een filter begin je bovenaan bij de eerste treffer; anders bij het recept dat
        // nu gebruikt wordt. Zo is Enter altijd voorspelbaar.
        if (keepHighlightFor >= 0)
        {
            int position = _rowOptions.IndexOf(keepHighlightFor);
            _highlight = position >= 0 ? position : 0;
        }
        else
        {
            _highlight = 0;
        }

        ApplyHighlight();
        ShowHighlightedPreview();
        _scroll.ScrollToTop();
        BringHighlightIntoView();
        _search.Focus();
    }

    private Border MakeGroupHeader(string machine, int count)
    {
        var text = new TextBlock
        {
            Text = $"{machine}  ({count})",
            Foreground = _accent,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 8, 6, 3)
        };

        return new Border
        {
            Child = text,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x22)),
            Margin = new Thickness(0, 4, 0, 2),
            Padding = new Thickness(2, 1, 2, 1)
        };
    }

    private Border MakeRow(int index)
    {
        var option = _options[index];
        string machine = MachineName(option);

        var number = new TextBlock
        {
            Text = (index + 1) + ".",
            Foreground = _dim,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0)
        };

        // Omgekeerde paar? Dan een waarschuwing vóór je deze machine als voorkeur kiest.
        string? reverse = RecipePlanner.ReversePairWith(_itemKey, option, _recipes);

        var name = new TextBlock
        {
            Text = reverse != null ? machine + "  ⚠" : machine,
            Foreground = reverse != null ? _warn : _text,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var ingredients = new TextBlock
        {
            Text = MainWindow.DescribeIngredients(option),
            Foreground = _dim,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 8, 0)
        };

        bool preferred = !string.IsNullOrWhiteSpace(_preference) &&
                         string.Equals(_preference, machine, StringComparison.OrdinalIgnoreCase);

        var star = new TextBlock
        {
            Text = preferred ? "★" : "☆",
            Foreground = preferred ? _accent : _dim,
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = Lang.T(preferred ? "pick.preferred.tip" : "pick.setpref.tip")
        };

        var starBox = new Border { Child = star, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        starBox.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            PreferenceMachine = machine;
            Close();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

        Grid.SetColumn(number, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(ingredients, 2);
        Grid.SetColumn(starBox, 3);
        grid.Children.Add(number);
        grid.Children.Add(name);
        grid.Children.Add(ingredients);
        grid.Children.Add(starBox);

        var row = new Border
        {
            Child = grid,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 3, 4, 3),
            Cursor = Cursors.Hand
        };

        AutomationProperties.SetAutomationId(row, "MachineRow" + index);
        AutomationProperties.SetName(row, (index + 1) + ". " + machine + " — " + MainWindow.DescribeIngredients(option));

        if (reverse != null)
            row.ToolTip = Lang.T("pick.reverse.tip", ItemNames.Display(reverse), ItemNames.Display(_itemKey));

        row.MouseLeftButtonUp += (_, _) =>
        {
            ChosenIndex = index;
            Close();
        };

        row.MouseEnter += (_, _) =>
        {
            int position = _rows.IndexOf(row);
            if (position != _highlight) row.Background = _hover;

            ShowPreviewFor(index);
        };

        row.MouseLeave += (_, _) =>
        {
            int position = _rows.IndexOf(row);
            if (position != _highlight) row.Background = Brushes.Transparent;
        };

        _rows.Add(row);
        _rowOptions.Add(index);

        return row;
    }

    private void ApplyHighlight()
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Background = i == _highlight ? _selected : Brushes.Transparent;
    }

    private void MoveHighlight(int delta)
    {
        if (_rows.Count == 0) return;

        _highlight = Math.Clamp(_highlight + delta, 0, _rows.Count - 1);
        ApplyHighlight();
        ShowHighlightedPreview();
        BringHighlightIntoView();
    }

    private void BringHighlightIntoView()
    {
        if (_highlight < 0 || _highlight >= _rows.Count) return;

        var row = _rows[_highlight];
        Dispatcher.BeginInvoke(new Action(() => row.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PickHighlighted()
    {
        if (_highlight < 0 || _highlight >= _rowOptions.Count) return;

        ChosenIndex = _rowOptions[_highlight];
        Close();
    }

    private bool Matches(int index, string filter)
    {
        if (filter.Length == 0) return true;

        var option = _options[index];

        if (MachineName(option).Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var ingredient in option.Ingredients)
            if (ItemNames.Display(ingredient.Item).Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static string MachineName(Recipe recipe) =>
        recipe.Category.Length > 0 ? recipe.Category : Lang.T("plan.machine.recipe");

    private static Brush Pick(FrameworkElement host, string key, Color fallback)
    {
        if (host.TryFindResource(key) is Brush brush) return brush;

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
