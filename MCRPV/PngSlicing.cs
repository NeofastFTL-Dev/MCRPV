using System;
using System.Collections.Generic;

namespace Mcrpv;

/// <summary>
/// Hoe de PNG-export een tekening in leesbare delen knipt. Bewust zonder WPF, zodat de
/// zelftest en de unit-tests de grenzen kunnen nameten zonder een venster.
///
/// De WIC-encoder weigert afbeeldingen boven ~65.535 px en een grote boom op één plaat
/// is toch onleesbaar; daarom wordt de tekening in delen geknipt van hoogstens
/// <see cref="MaxPartHeight"/> px en hoogstens <see cref="MaxPixels"/> pixels.
/// </summary>
public static class PngSlicing
{
    // De PNG-encoder (WIC) weigert hoger dan ~65.535 px; we blijven ruim daaronder.
    public const int MaxWidth = 16_000;
    public const int MaxPartHeight = 32_000;

    // Budget per deel: het deel staat als RGBA-bitmap in het geheugen (4 bytes per pixel)
    // en de encoder maakt daar nog een kopie van. 8 M px ≈ 32 MB bitmap, dus samen met de
    // encoder ruim onder de 100 MB. (Was 32 M px: bij een brede boom liep dat op tot
    // honderden MB's en dat was de klap die het geheugen liet pieken.)
    public const long MaxPixels = 8_000_000;

    /// <summary>Eén deel: de eerste rij in de hele tekening en de hoogte van dat deel.</summary>
    public readonly record struct Part(int Top, int Height);

    public sealed class Plan
    {
        public Plan(int width, int height, double scale, int partHeight, IReadOnlyList<Part> parts)
        {
            Width = width;
            Height = height;
            Scale = scale;
            PartHeight = partHeight;
            Parts = parts;
        }

        /// <summary>Breedte van elk deel in pixels (alle delen zijn even breed).</summary>
        public int Width { get; }

        /// <summary>Hoogte van de hele tekening in pixels.</summary>
        public int Height { get; }

        /// <summary>Verkleining als de tekening breder is dan <see cref="MaxWidth"/> (1 = ware grootte).</summary>
        public double Scale { get; }

        /// <summary>Hoogte van een heel deel; het laatste deel kan korter zijn.</summary>
        public int PartHeight { get; }

        public IReadOnlyList<Part> Parts { get; }

        public int Count => Parts.Count;
    }

    /// <summary>
    /// Rekent uit in hoeveel delen de tekening past. <paramref name="rowPitch"/> is de
    /// hoogte van één rij vakjes: de grenzen vallen daartussen, zodat geen enkel vakje
    /// doormidden wordt geknipt.
    /// </summary>
    public static Plan PlanFor(double contentWidth, double contentHeight, double rowPitch)
    {
        contentWidth = Math.Max(1, contentWidth);
        contentHeight = Math.Max(1, contentHeight);

        double scale = Math.Min(1.0, (double)MaxWidth / contentWidth);
        int width = Math.Max(1, (int)Math.Ceiling(contentWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(contentHeight * scale));

        double pitch = Math.Max(1.0, rowPitch * scale);

        // Niet alleen de encoderhoogte telt: een breed deel met veel pixels kost anders
        // honderden MB's geheugen bij het renderen. Het budget is een harde bovengrens,
        // dus geen minimumhoogte die er bovenuit komt (bij de maximale breedte wordt een
        // deel dan 500 px hoog in plaats van 600).
        double maxPart = Math.Min(MaxPartHeight, Math.Max(1L, MaxPixels / width));

        int rows = Math.Max(1, (int)(maxPart / pitch));
        int partHeight = (int)Math.Ceiling(rows * pitch);
        partHeight = Math.Min(partHeight, (int)Math.Floor(maxPart));
        partHeight = Math.Min(Math.Max(1, partHeight), height);

        int count = Math.Max(1, (int)Math.Ceiling((double)height / partHeight));
        var parts = new Part[count];

        for (int i = 0; i < count; i++)
        {
            int top = i * partHeight;
            parts[i] = new Part(top, Math.Min(partHeight, height - top));
        }

        return new Plan(width, height, scale, partHeight, parts);
    }
}
