using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Mcrpv;

/// <summary>
/// Tekent de receptenboom als een echt bracket-diagram: een vakje per item met icoon,
/// naam en aantal, verbonden met ellebooglijnen zoals een tournament bracket.
/// Het eindproduct staat links, de basis-materialen waaieren naar rechts uit.
/// </summary>
public static class BracketDiagram
{
    private const double BoxWidth = 212;
    private const double BoxHeight = 42;
    private const double ColumnGap = 44;
    private const double RowGap = 4;
    private const double IconSize = 24;

    /// <summary>Hoogte van één rij vakjes; de PNG-export knipt op deze grens.</summary>
    public const double RowPitch = BoxHeight + RowGap;

    /// <summary>Meer vakjes wordt onleesbaar (en traag); de rest valt weg.</summary>
    public const int MaxBoxes = 400;

    private sealed class Placed
    {
        public RecipeNode Node = null!;
        public double X;
        public double Y;
        public bool Match;
        public bool Cut;
    }

    public static Canvas Build(RecipeNode root, FrameworkElement host, string filter,
                               Action<RecipeNode>? onNodeClick = null,
                               Action<RecipeNode>? onNodeRightClick = null,
                               Action<RecipeNode, Point>? onNodeFocus = null)
    {
        var canvas = new Canvas { Background = Brushes.Transparent };
        var boxes = new List<Placed>();
        var links = new List<(Placed Parent, Placed Child)>();

        string needle = (filter ?? "").Trim();
        double nextLeaf = 0;
        int budget = MaxBoxes;

        Placed Place(RecipeNode node, int depth)
        {
            var box = new Placed
            {
                Node = node,
                X = depth * (BoxWidth + ColumnGap),
                Match = needle.Length > 0 && node.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            };

            // Budget op: de rest van de boom wordt als afgekapt blad getekend.
            bool cut = node.Children.Count > 0 && --budget <= 0;
            box.Cut = cut;

            if (node.Children.Count == 0 || cut)
            {
                box.Y = nextLeaf;
                nextLeaf += BoxHeight + RowGap;
            }
            else
            {
                var children = new List<Placed>(node.Children.Count);

                foreach (var child in node.Children)
                {
                    var placed = Place(child, depth + 1);
                    children.Add(placed);
                    links.Add((box, placed));
                }

                box.Y = (children[0].Y + children[^1].Y) / 2;
            }

            boxes.Add(box);
            return box;
        }

        var rootBox = Place(root, 0);

        // Lijnen eerst, anders vallen ze over de vakjes heen.
        Brush line = Brush(host, "BevelLight", Color.FromRgb(0x59, 0x61, 0x6B));
        Brush highlight = Brush(host, "Highlight", Color.FromRgb(0xF2, 0xC9, 0x4C));

        foreach (var (parent, child) in links)
        {
            double x1 = parent.X + BoxWidth;
            double y1 = parent.Y + BoxHeight / 2;
            double x2 = child.X;
            double y2 = child.Y + BoxHeight / 2;
            double mid = x1 + ColumnGap / 2;

            var figure = new PathFigure { StartPoint = new Point(x1, y1), IsFilled = false };
            figure.Segments.Add(new LineSegment(new Point(mid, y1), true));
            figure.Segments.Add(new LineSegment(new Point(mid, y2), true));
            figure.Segments.Add(new LineSegment(new Point(x2, y2), true));

            canvas.Children.Add(new Path
            {
                Data = new PathGeometry(new[] { figure }),
                Stroke = child.Match || parent.Match ? highlight : line,
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true
            });
        }

        Brush slot = Brush(host, "SlotFace", Color.FromRgb(0x12, 0x16, 0x1A));
        Brush screen = Brush(host, "ScreenBg", Color.FromRgb(0x0A, 0x0D, 0x10));
        Brush text = Brush(host, "ScreenText", Color.FromRgb(0xE6, 0xED, 0xE2));
        Brush dim = Brush(host, "ScreenDim", Color.FromRgb(0x8C, 0x97, 0x8F));
        Brush bevel = Brush(host, "BevelDark", Color.FromRgb(0x14, 0x18, 0x1C));
        Brush accent = Brush(host, "Accent", Color.FromRgb(0x9B, 0x7B, 0xD4));

        foreach (var box in boxes)
        {
            var frame = new Border
            {
                Width = BoxWidth,
                Height = BoxHeight,
                CornerRadius = new CornerRadius(5),
                Background = slot,
                BorderBrush = box.Match ? highlight : box.Node.Children.Count == 0 ? bevel : accent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 0, 8, 0)
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = IconFor(box.Node.Name, screen);
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            string title = box.Cut ? $"{box.Node.DisplayName} (+ meer)" : box.Node.DisplayName;
            if (box.Node.HasChoice) title = "⇄ " + title;

            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = box.Match ? highlight : text,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (box.Node.MachineText.Length > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = box.Node.MachineText,
                    FontSize = 8.5,
                    Foreground = dim,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            Grid.SetColumn(stack, 1);
            row.Children.Add(stack);

            var quantity = new TextBlock
            {
                Text = box.Node.Quantity > 1 ? $"×{box.Node.Quantity}" : "",
                FontSize = 10,
                Foreground = box.Match ? highlight : dim,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(quantity, 2);
            row.Children.Add(quantity);

            frame.Child = row;

            if (box.Node.HasChoice && (onNodeClick != null || onNodeRightClick != null))
            {
                frame.ToolTip = Tip(box.Node, box.Node.OptionCount > 2
                    ? $"Klik = volgende machine · rechtsklik = machine kiezen ({box.Node.OptionCount} mogelijkheden) — nu: {box.Node.MachineText}"
                    : $"Klik = andere machine — nu: {box.Node.MachineText}");
                frame.Cursor = Cursors.Hand;

                if (onNodeClick != null)
                {
                    // Pas op MouseUp: dan kan de bracket-tab eerst bepalen of dit een klik
                    // was of het begin van een sleep (schuiven met de linkermuisknop).
                    frame.MouseLeftButtonUp += (_, args) =>
                    {
                        onNodeClick(box.Node);
                        args.Handled = true;
                    };
                }

                if (onNodeRightClick != null)
                {
                    frame.MouseRightButtonUp += (_, args) =>
                    {
                        onNodeRightClick(box.Node);
                        args.Handled = true;
                    };
                }
            }
            else if (onNodeFocus != null)
            {
                // Ingrediënt zonder alternatieven: klik erop en het diagram schuift er
                // vloeiend naartoe, zodat je de rest van de keten leesbaar in beeld krijgt.
                frame.Cursor = Cursors.Hand;
                frame.ToolTip = Tip(box.Node, box.Node.MachineText.Length > 0
                    ? $"Klik = hier inzoomen — {box.Node.MachineText}"
                    : "Klik = hier inzoomen");

                var center = new Point(box.X + BoxWidth / 2, box.Y + BoxHeight / 2);
                frame.MouseLeftButtonUp += (_, args) =>
                {
                    onNodeFocus(box.Node, center);
                    args.Handled = true;
                };
            }
            else if (box.Node.MachineText.Length > 0)
            {
                frame.ToolTip = Tip(box.Node, box.Node.MachineText);
            }

            Canvas.SetLeft(frame, box.X);
            Canvas.SetTop(frame, box.Y);
            canvas.Children.Add(frame);
        }

        canvas.Width = boxes.Max(b => b.X) + BoxWidth + 4;
        canvas.Height = Math.Max(nextLeaf + 2, BoxHeight);
        canvas.MinHeight = canvas.Height;
        canvas.MinWidth = canvas.Width;

        // Tooltip met de herkomst van het gebruikte recept erbij (PLAN.md §7).
        static string Tip(RecipeNode node, string text) =>
            node.Source.Length > 0 ? text + "\nuit " + node.Source : text;

        // Waar het eindproduct staat: daarmee zet de UI de scroll meteen op een zinvol
        // punt in plaats van bovenaan, waar alleen de diepste bladeren staan.
        canvas.Tag = rootBox.Y;
        return canvas;
    }

    /// <summary>Icoon uit de instance, of een gekleurde tegel met de beginletters.</summary>
    private static FrameworkElement IconFor(string name, Brush backdrop)
    {
        var frame = new Border
        {
            Width = IconSize,
            Height = IconSize,
            CornerRadius = new CornerRadius(3),
            Background = backdrop,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var source = ItemIcons.Get(name);
        if (source != null)
        {
            var image = new Image { Source = source, Stretch = Stretch.Uniform, Margin = new Thickness(2) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            frame.Child = image;
            return frame;
        }

        frame.Background = FallbackBrush(name);
        frame.Child = new TextBlock
        {
            Text = Initials(name),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        return frame;
    }

    private static string Initials(string name)
    {
        string pretty = ItemNames.Pretty(name);
        var words = pretty.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";

        return words.Length == 1
            ? words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant()
            : (words[0][0].ToString() + words[1][0]).ToUpperInvariant();
    }

    /// <summary>Vaste, gedempte kleur per itemnaam zodat dezelfde items dezelfde tegel krijgen.</summary>
    private static Brush FallbackBrush(string name)
    {
        int hash = 0;
        foreach (char c in name) hash = unchecked(hash * 31 + c);

        double hue = Math.Abs(hash % 360);
        var color = Hsv(hue, 0.45, 0.52);

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Hsv(double hue, double saturation, double value)
    {
        double c = value * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = value - c;

        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static Brush Brush(FrameworkElement host, string key, Color fallback)
    {
        if (host.TryFindResource(key) is Brush brush) return brush;

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
