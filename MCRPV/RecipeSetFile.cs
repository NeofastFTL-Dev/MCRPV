using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mcrpv;

/// <summary>
/// Herkent een bestand dat echt een receptenset is. De watch-folder meldt elk .json dat
/// daar verschijnt; zonder deze controle overschreef een willekeurig bestand (settings.json,
/// een halve dump) de actieve set. Staat hier en niet in het venster, zodat de tests hem
/// op elke machine kunnen nalopen.
/// </summary>
public static class RecipeSetFile
{
    /// <summary>Een bewaarde set van vóór een fold/scan is een reservekopie, geen nieuwe set.</summary>
    private const string BackupPrefix = "recipes.before-";

    /// <summary>True als dit bestand recepten met ingrediënten of alternatieven bevat.</summary>
    public static bool LooksLikeSet(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        if (Path.GetFileName(path).StartsWith(BackupPrefix, System.StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(path));

            // Bestanden als settings.json komen er als recepten zonder ingrediënten uit.
            return loaded is { Count: > 0 } &&
                   loaded.Values.Any(recipe => recipe != null &&
                                               (recipe.Ingredients.Count > 0 || recipe.Alternatives.Count > 0));
        }
        catch
        {
            return false;
        }
    }
}
