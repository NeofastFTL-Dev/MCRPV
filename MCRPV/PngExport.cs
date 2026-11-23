using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mcrpv;

/// <summary>
/// Rendert één deel van een tekening naar PNG. De indeling komt uit
/// <see cref="PngSlicing"/>, zodat de knop, de zelftest en de tests dezelfde grenzen
/// gebruiken.
/// </summary>
public static class PngExport
{
    /// <summary>
    /// Tekent <paramref name="visual"/> vanaf rij <c>plan.Parts[part].Top</c> in een
    /// afbeelding van <c>plan.Width</c> bij de hoogte van dat deel.
    /// </summary>
    public static void RenderPart(FrameworkElement visual, PngSlicing.Plan plan, int part,
                                  Brush background, Stream stream)
    {
        if (part < 0 || part >= plan.Count) throw new ArgumentOutOfRangeException(nameof(part));

        PngSlicing.Part slice = plan.Parts[part];

        var canvas = new Canvas
        {
            Width = plan.Width,
            Height = slice.Height,
            Background = background,
            ClipToBounds = true,
            Clip = new RectangleGeometry(new Rect(0, 0, plan.Width, slice.Height))
        };

        // Eerst schalen, dan verschuiven: de verschuiving is in uitvoerpixels, dus
        // delen door de schaal. Zo valt de rand van elk deel op dezelfde plek als in
        // de hele tekening.
        double scale = Math.Max(plan.Scale, 0.0001);
        var transform = new TransformGroup();
        if (plan.Scale < 0.999)
            transform.Children.Add(new ScaleTransform(plan.Scale, plan.Scale));
        transform.Children.Add(new TranslateTransform(0, -slice.Top / scale));
        visual.RenderTransform = transform;

        canvas.Children.Add(visual);
        canvas.Measure(new Size(plan.Width, slice.Height));
        canvas.Arrange(new Rect(0, 0, plan.Width, slice.Height));

        var bitmap = new RenderTargetBitmap(plan.Width, slice.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);

        canvas.Children.Clear();
        visual.RenderTransform = Transform.Identity;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
