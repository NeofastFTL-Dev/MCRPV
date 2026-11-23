using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mcrpv;

/// <summary>
/// Overzicht van de voorkeursmachines (★) die je per item instelde. Je kunt ze hier bekijken,
/// één voor één intrekken met ✕ of in één keer alles wissen. Wat je intrekt komt in
/// <see cref="RemovedItems"/>; het hoofdvenster past dat toe en rekent de boom opnieuw.
/// </summary>
public sealed class PreferenceList : Window
{
    private readonly Dictionary<string, string> _machines;
    private readonly List<string> _rows = new();

    private readonly StackPanel _list = new() { Margin = new Thickness(8, 0, 8, 8) };
    private readonly ScrollViewer _scroll = new();
    private readonly TextBlock _status = new();
    private readonly Button _clearAll;
    private readonly ComboBox _language;
    private readonly ControlTemplate _flatButton;
    private bool _cleared;

    private readonly Brush _text;
    private readonly Brush _dim;
    private readonly Brush _accent;
    private readonly Brush _border;
    private readonly Brush _screen;

    /// <summary>Items waarvan je hier de voorkeur introk.</summary>
    public List<string> RemovedItems { get; } = new();

    /// <summary>Taal die je hier koos (null = ongewijzigd); het hoofdvenster bouwt zich dan opnieuw op.</summary>
    public string? NewLanguage { get; private set; }

    public PreferenceList(Window owner, IReadOnlyDictionary<string, string> preferences)
    {
        _machines = preferences
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        _rows.AddRange(_machines.Keys.OrderBy(ItemNames.Display, StringComparer.CurrentCultureIgnoreCase));

        _text = Pick(owner, "ScreenText", Color.FromRgb(0xE6, 0xED, 0xE2));
        _dim = Pick(owner, "ScreenDim", Color.FromRgb(0x8C, 0x97, 0x8F));
        _accent = Pick(owner, "Accent", Color.FromRgb(0x9B, 0x7B, 0xD4));
        _border = Pick(owner, "BevelLight", Color.FromRgb(0x59, 0x61, 0x6B));
        _screen = Pick(owner, "ScreenBg", Color.FromRgb(0x0A, 0x0D, 0x10));

        _flatButton = FlatTemplate();

        Title = Lang.T("pref.title");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 560;
        Height = 460;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1A));
        FontFamily = new FontFamily("Consolas");
        FontSize = 12;

        _clearAll = new Button
        {
            Content = Lang.T("pref.clearall"),
            Foreground = _accent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 10.5,
            Cursor = Cursors.Hand,
            Template = _flatButton,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            ToolTip = Lang.T("pref.clearall.tip")
        };
        _clearAll.Click += (_, _) => ClearAll();
        AutomationProperties.SetAutomationId(_clearAll, "PrefClearAll");
        AutomationProperties.SetName(_clearAll, Lang.T("pref.clearall.name"));

        // Taalkeuze: bij een wissel bouwt het hoofdvenster zich opnieuw op (zie ShowPreferences_Click).
        _language = new ComboBox
        {
            Width = 132,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Lang.T("pref.language.tip")
        };

        foreach (var code in Lang.Codes)
            _language.Items.Add(new LanguageChoice(code, Lang.NativeName(code)));

        _language.DisplayMemberPath = "Label";
        _language.SelectedIndex = Math.Max(0, Lang.Codes.ToList().IndexOf(Lang.Code));
        if (owner.TryFindResource("McComboBox") is Style comboStyle) _language.Style = comboStyle;
        _language.SelectionChanged += (_, _) =>
        {
            if (_language.SelectedItem is not LanguageChoice chosen || chosen.Code == Lang.Code) return;

            NewLanguage = chosen.Code;
            Lang.Set(chosen.Code);
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(BuildHeader());

        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Content = _list;
        _scroll.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetRow(_scroll, 1);
        grid.Children.Add(_scroll);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 6, 12, 8)
        };

        footer.Children.Add(new TextBlock
        {
            Text = Lang.T("pref.language"),
            Foreground = _dim,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        footer.Children.Add(_language);
        footer.Children.Add(new TextBlock
        {
            Text = Lang.T("pref.footer"),
            Foreground = _dim,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        });
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);

        Content = new Border
        {
            Child = grid,
            BorderBrush = _accent,
            BorderThickness = new Thickness(1),
            Background = Background
        };

        BuildList();

        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                args.Handled = true;
                Close();
            }
        };
    }

    /// <summary>Kop met titel, teller en de knop om alles te wissen.</summary>
    private Grid BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(12, 10, 12, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = Lang.T("pref.header"),
            Foreground = _text,
            FontWeight = FontWeights.SemiBold
        };

        _status.Foreground = _dim;
        _status.FontSize = 10.5;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(12, 0, 0, 0);

        Grid.SetColumn(title, 0);
        Grid.SetColumn(_status, 1);
        Grid.SetColumn(_clearAll, 2);

        header.Children.Add(title);
        header.Children.Add(_status);
        header.Children.Add(_clearAll);

        Grid.SetRow(header, 0);
        return header;
    }

    private void BuildList()
    {
        _list.Children.Clear();

        if (_rows.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = Lang.T(_cleared ? "pref.empty.cleared" : "pref.empty"),
                Foreground = _dim,
                Margin = new Thickness(10, 12, 10, 12),
                TextWrapping = TextWrapping.Wrap
            });
        }

        for (int i = 0; i < _rows.Count; i++)
            _list.Children.Add(MakeRow(_rows[i], i));

        _status.Text = Lang.T(_rows.Count == 1 ? "pref.count.one" : "pref.count", _rows.Count);
        _clearAll.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border MakeRow(string item, int index)
    {
        var name = new TextBlock
        {
            Text = ItemNames.Display(item),
            Foreground = _text,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var machine = new TextBlock
        {
            Text = "★ " + _machines[item],
            Foreground = _accent,
            Margin = new Thickness(10, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var remove = new Button
        {
            Content = "✕",
            Foreground = _dim,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 4, 0),
            Cursor = Cursors.Hand,
            Template = _flatButton,
            ToolTip = Lang.T("pref.remove.tip")
        };

        AutomationProperties.SetAutomationId(remove, "PrefRemove" + index);
        AutomationProperties.SetName(remove, Lang.T("pref.remove.name", ItemNames.Display(item)));

        remove.Click += (_, _) => Remove(item);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        Grid.SetColumn(name, 0);
        Grid.SetColumn(machine, 1);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(name);
        grid.Children.Add(machine);
        grid.Children.Add(remove);

        return new Border
        {
            Child = grid,
            Background = Brushes.Transparent,
            BorderBrush = _screen,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 3, 4, 3)
        };
    }

    private void Remove(string item)
    {
        if (!_rows.Remove(item)) return;

        RemovedItems.Add(item);
        BuildList();
    }

    private void ClearAll()
    {
        foreach (var item in _rows) RemovedItems.Add(item);

        _rows.Clear();
        _cleared = true;
        BuildList();
    }

    /// <summary>Eén regel in de taalkeuze: de code en de naam die je zelf leest.</summary>
    private sealed record LanguageChoice(string Code, string Label);

    /// <summary>Knop zonder chroom, zodat ✕ er als tekst uitziet maar wel klikbaar en focusbaar is.</summary>
    private static ControlTemplate FlatTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

        return new ControlTemplate(typeof(Button)) { VisualTree = presenter };
    }

    private static Brush Pick(FrameworkElement host, string key, Color fallback)
    {
        if (host.TryFindResource(key) is Brush brush) return brush;

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
