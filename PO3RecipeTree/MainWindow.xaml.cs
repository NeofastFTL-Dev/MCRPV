using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace RecipeTreeApp;

public partial class MainWindow : Window
{
    private Dictionary<string, Recipe> _recipes = new(StringComparer.OrdinalIgnoreCase);
    private RecipeNode? _currentRoot;
    private bool _isDark = true;

    public MainWindow()
    {
        InitializeComponent();
        LoadRecipes();
        ApplyTheme(true);
    }

    private void LoadRecipes()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recipes.json");
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
            File.WriteAllText(path, JsonSerializer.Serialize(starter, new JsonSerializerOptions { WriteIndented = true }));
        }

        string json = File.ReadAllText(path);
        _recipes = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(json)
                   ?? new Dictionary<string, Recipe>();
    }

    private void BuildTree_Click(object sender, RoutedEventArgs e)
    {
        string target = ItemInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(target)) return;

        if (!int.TryParse(QuantityInput.Text, out int qty) || qty < 1)
            qty = 1;

        _currentRoot = BuildNode(target, qty, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        RecipeTreeView.ItemsSource = new ObservableCollection<RecipeNode> { _currentRoot };

        BracketOutput.Text = BuildPrettyBracketTree(_currentRoot);
        ApplyFilter();
    }

    private RecipeNode BuildNode(string item, int quantity, HashSet<string> visited)
    {
        var node = new RecipeNode { Name = item, Quantity = quantity };

        if (visited.Contains(item))
        {
            node.Name += " ♻️ (cycle)";
            return node;
        }

        if (!_recipes.TryGetValue(item, out var recipe) || recipe.Ingredients.Count == 0)
            return node;

        visited.Add(item);
        foreach (var ing in recipe.Ingredients)
        {
            node.Children.Add(BuildNode(ing.Item, ing.Count * quantity, new HashSet<string>(visited)));
        }
        visited.Remove(item);
        return node;
    }

    private string BuildPrettyBracketTree(RecipeNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{node.Name} ×{node.Quantity}");
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
            sb.Append($"{child.Name}");
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
        if (_currentRoot == null) return;
        HighlightNodes(RecipeTreeView, filter);
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
                    : (Brush)FindResource("TextPrimary");

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

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BracketOutput.Text)) return;
        Clipboard.SetText(BracketOutput.Text);
        MessageBox.Show("Tree copied to clipboard! 💕", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveTxt_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BracketOutput.Text)) return;

        var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt",
            FileName = $"{ItemInput.Text.Trim()}_recipe_tree.txt",
            Title = "Save Recipe Tree"
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, BracketOutput.Text);
            MessageBox.Show($"Saved to:\n{dialog.FileName}", "Exported ✨", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SavePng_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BracketOutput.Text)) return;

        var visual = new TextBlock
        {
            Text = BracketOutput.Text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            Foreground = (Brush)FindResource("TextPrimary"),
            Background = (Brush)FindResource("PanelBg"),
            Padding = new Thickness(20),
            TextWrapping = TextWrapping.NoWrap
        };

        visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        visual.Arrange(new Rect(visual.DesiredSize));

        int width = (int)Math.Ceiling(visual.DesiredSize.Width);
        int height = (int)Math.Ceiling(visual.DesiredSize.Height);

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        var dialog = new SaveFileDialog
        {
            Filter = "PNG Image (*.png)|*.png",
            FileName = $"{ItemInput.Text.Trim()}_recipe_tree.png",
            Title = "Save Recipe Tree as PNG"
        };

        if (dialog.ShowDialog() == true)
        {
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            MessageBox.Show($"PNG saved to:\n{dialog.FileName}", "Image Exported 🖼️", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        ApplyTheme(_isDark);
        ThemeButton.Content = _isDark ? "☀️ Light Mode" : "🌙 Dark Mode";
    }

    private void ApplyTheme(bool dark)
    {
        if (dark)
        {
            Resources["WindowBg"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
            Resources["PanelBg"] = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
            Resources["TextPrimary"] = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4));
            Resources["TextSecondary"] = new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8));
            Resources["Accent"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xA6, 0xF7));
            Resources["ButtonBg"] = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA));
            Resources["SuccessBg"] = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1));
            Resources["Highlight"] = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF));
        }
        else
        {
            Resources["WindowBg"] = new SolidColorBrush(Color.FromRgb(0xEF, 0xF1, 0xF5));
            Resources["PanelBg"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            Resources["TextPrimary"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x4F, 0x69));
            Resources["TextSecondary"] = new SolidColorBrush(Color.FromRgb(0x6C, 0x6F, 0x85));
            Resources["Accent"] = new SolidColorBrush(Color.FromRgb(0x88, 0x37, 0xA3));
            Resources["ButtonBg"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x66, 0xF5));
            Resources["SuccessBg"] = new SolidColorBrush(Color.FromRgb(0x40, 0xA0, 0x2B));
            Resources["Highlight"] = new SolidColorBrush(Color.FromRgb(0xDF, 0x8E, 0x1D));
        }
    }
}

public class Recipe
{
    public List<Ingredient> Ingredients { get; set; } = new();
}

public class Ingredient
{
    public string Item { get; set; } = "";
    public int Count { get; set; } = 1;
}

public class RecipeNode
{
    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public ObservableCollection<RecipeNode> Children { get; set; } = new();

    public string DisplayName => Name;
    public string QuantityText => Quantity > 1 ? $"×{Quantity}" : "";
}
