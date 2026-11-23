using System;
using System.IO;
using System.Text.Json;

namespace Mcrpv;

/// <summary>
/// De twee afkapgrenzen van de planner. Ze staan standaard op 16 beurten per stap en
/// 1 000 basis-materialen per gepland item, maar zijn in de app instelbaar. De gekozen
/// waarden worden in settings.json bewaard, zodat de CLI met dezelfde grenzen rekent.
/// </summary>
public sealed class PlannerLimits
{
    public const int DefaultMaxCraftsPerStep = 16;
    public const int DefaultBaseItemsPerProduct = 1_000;

    public const int MinMaxCraftsPerStep = 1;
    public const int MaxMaxCraftsPerStep = 999;
    public const int MinBaseItemsPerProduct = 1;
    public const int MaxBaseItemsPerProduct = 1_000_000;

    /// <summary>Meer beurten dan dit voor één stap is bulk: de tak wordt afgekapt (⚠).</summary>
    public int MaxCraftsPerStep { get; set; } = DefaultMaxCraftsPerStep;

    /// <summary>Basis-materialen per gepland item; daarna wordt de rest van de tak afgekapt.</summary>
    public int BaseItemsPerProduct { get; set; } = DefaultBaseItemsPerProduct;

    /// <summary>Wat buiten de grenzen valt schuift naar de dichtstbijzijnde rand.</summary>
    public PlannerLimits Clamped() => new()
    {
        MaxCraftsPerStep = Math.Clamp(MaxCraftsPerStep, MinMaxCraftsPerStep, MaxMaxCraftsPerStep),
        BaseItemsPerProduct = Math.Clamp(BaseItemsPerProduct, MinBaseItemsPerProduct, MaxBaseItemsPerProduct)
    };

    /// <summary>
    /// Leest de grenzen uit settings.json. Ontbreekt het bestand of staan de waarden er
    /// niet in, dan gelden de standaardwaarden; onzin wordt naar de rand geschoven.
    /// </summary>
    public static PlannerLimits Load(string dataDirectory)
    {
        var limits = new PlannerLimits();

        try
        {
            string path = Path.Combine(dataDirectory, "settings.json");
            if (!File.Exists(path)) return limits;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("MaxCraftsPerStep", out var crafts) && crafts.TryGetInt32(out int parsedCrafts))
                    limits.MaxCraftsPerStep = parsedCrafts;

                if (root.TryGetProperty("BaseItemsPerProduct", out var bases) && bases.TryGetInt32(out int parsedBases))
                    limits.BaseItemsPerProduct = parsedBases;
            }
        }
        catch
        {
            // Onleesbare of half geschreven settings: gewoon de standaardwaarden gebruiken.
        }

        return limits.Clamped();
    }
}
