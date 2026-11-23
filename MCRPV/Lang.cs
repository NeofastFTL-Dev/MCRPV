using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mcrpv;

/// <summary>
/// Alle zichtbare tekst van de GUI, per taal in een tabel (Lang.nl.cs / Lang.en.cs).
/// De taal komt uit settings.json en wordt bij het opstarten gezet; een wissel in het
/// voorkeurenpaneel bouwt het hoofdvenster opnieuw op, zodat elke tekst opnieuw uit de
/// tabel komt. CLI-uitvoer (--plan, --index, --selftest) blijft bewust Nederlands.
/// </summary>
public static class Lang
{
    /// <summary>Taal die gebruikt wordt als er niets gekozen is.</summary>
    public const string Default = "nl";

    private static string _code = Default;

    private static readonly Dictionary<string, Dictionary<string, string>> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nl"] = DutchStrings.Values,
        ["en"] = EnglishStrings.Values
    };

    /// <summary>Code van de actieve taal ("nl" of "en").</summary>
    public static string Code => _code;

    /// <summary>Beschikbare talen, in de volgorde van de keuzelijst.</summary>
    public static IReadOnlyList<string> Codes { get; } = new[] { "nl", "en" };

    /// <summary>Naam van de taal zoals je hem zelf leest.</summary>
    public static string NativeName(string code) => code.Equals("en", StringComparison.OrdinalIgnoreCase)
        ? "English"
        : "Nederlands";

    /// <summary>Zet de taal; een onbekende of lege code valt terug op de standaardtaal.</summary>
    public static void Set(string? code) => _code = Normalize(code);

    public static string Normalize(string? code)
    {
        string trimmed = (code ?? string.Empty).Trim();

        foreach (var known in Tables.Keys)
            if (known.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return known;

        return Default;
    }

    /// <summary>
    /// Leest de taal uit settings.json. De app doet dit vóór het eerste venster, zodat
    /// XAML-teksten meteen in de gekozen taal verschijnen.
    /// </summary>
    public static void LoadFromSettings(string dataDirectory)
    {
        try
        {
            string path = Path.Combine(dataDirectory, "settings.json");
            if (!File.Exists(path)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (document.RootElement.TryGetProperty("Language", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                Set(value.GetString());
            }
        }
        catch
        {
            // Geen leesbare settings: de standaardtaal is prima.
        }
    }

    /// <summary>Tekst bij een sleutel; ontbreekt hij, dan blijft de Nederlandse tekst staan.</summary>
    public static string T(string key)
    {
        if (Tables[_code].TryGetValue(key, out var text)) return text;
        if (Tables[Default].TryGetValue(key, out var fallback)) return fallback;

        // Zichtbaar maken dat er een sleutel ontbreekt, in plaats van stil niets te tonen.
        return "‽" + key;
    }

    /// <summary>Tekst met invulvelden ({0}, {1}, …).</summary>
    public static string T(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>Alle sleutels van de standaardtaal; de zelftest controleert of elke taal ze heeft.</summary>
    public static IReadOnlyCollection<string> Keys => Tables[Default].Keys;

    /// <summary>Sleutels die in deze taal ontbreken (voor de zelftest).</summary>
    public static IEnumerable<string> MissingIn(string code) =>
        Tables[Default].Keys.Where(key => !Tables[Normalize(code)].ContainsKey(key));
}


