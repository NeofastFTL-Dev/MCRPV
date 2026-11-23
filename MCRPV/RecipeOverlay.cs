using System.Linq;

namespace Mcrpv;

/// <summary>
/// Vult de receptenset aan met bronnen die een JEI-dump niet als recept laat zien:
///   - ertsen: mijnen (Mining) en de Industrial Foregoing Laser Drill;
///   - Tinkers' Construct: patroon -> cast -> onderdeel -> tool, plus de smeltery die
///     erts tot twee ingots omsmelt.
/// De dump blijft leidend: bestaande recepten worden nooit overschreven, er komt
/// hooguit een alternatief bij. Alles gebeurt in het geheugen, dus recipes.json op
/// schijf blijft precies wat de scan opleverde.
/// </summary>
public static class RecipeOverlay
{
    /// <summary>Mijnen: een eindhalte in de boom (je gaat niet een houweel uitrekenen).</summary>
    public const string MiningCategory = "Mining";

    /// <summary>Industrial Foregoing Laser Drill als tweede bron van ertsen.</summary>
    public const string LaserDrillCategory = "Laser Drill";

    private const string SmelteryCategory = "Smeltery";
    private const string CastingCategory = "Casting";
    private const string PartBuilderCategory = "Part Builder";
    private const string StencilCategory = "Stencil Table";
    private const string StationCategory = "Tool Station";
    private const string ForgeCategory = "Tool Forge";

    /// <summary>
    /// Onderdelen van Tinkers' Construct 1.12. In de mod is een onderdeel een item
    /// waarvan het materiaal in de NBT zit, dus dit zijn de namen die JEI toont:
    /// "Pickaxe Head", "Tool Rod", ...
    /// </summary>
    private static readonly string[] TinkersParts =
    {
        "pickaxe_head", "shovel_head", "axe_head", "sword_blade", "wide_guard",
        "hammer_head", "excavator_head", "kama_head", "scythe_head",
        "tool_rod", "tough_tool_rod", "tool_binding", "tough_binding",
        "large_plate", "large_sword_blade"
    };

    /// <summary>Metalen waarvan de smeltery de erts verdubbelt (alleen als de ingot bestaat).</summary>
    private static readonly string[] SmelteryMetals =
    {
        "iron", "gold", "copper", "tin", "bronze", "steel", "nickel", "silver",
        "lead", "aluminum", "platinum", "electrum", "invar", "pigiron",
        "cobalt", "ardite", "manyullyn"
    };

    /// <summary>Tools die je uit drie onderdelen in elkaar zet, met de machine die je nodig hebt.</summary>
    private static readonly (string Tool, string Category, string[] Parts)[] TinkersTools =
    {
        ("pickaxe",   StationCategory, new[] { "pickaxe_head", "tool_rod", "tool_binding" }),
        ("shovel",    StationCategory, new[] { "shovel_head", "tool_rod", "tool_binding" }),
        ("axe",       StationCategory, new[] { "axe_head", "tool_rod", "tool_binding" }),
        ("kama",      StationCategory, new[] { "kama_head", "tool_rod", "tool_binding" }),
        ("sword",     StationCategory, new[] { "sword_blade", "tool_rod", "wide_guard" }),
        ("hammer",    ForgeCategory,   new[] { "hammer_head", "tough_tool_rod", "large_plate" }),
        ("excavator", ForgeCategory,   new[] { "excavator_head", "tough_tool_rod", "large_plate" }),
        ("scythe",    ForgeCategory,   new[] { "scythe_head", "tough_tool_rod", "tough_binding" }),
        ("cleaver",   ForgeCategory,   new[] { "large_sword_blade", "tough_tool_rod", "large_plate" })
    };

    /// <summary>Past alle aanvullingen toe en geeft terug hoeveel recepten er bij kwamen.</summary>
    public static int Apply(Dictionary<string, Recipe> recipes)
    {
        int added = 0;
        added += ApplyOres(recipes);
        added += ApplyTinkers(recipes);
        return added;
    }

    // ========== Ertsen ==========

    /// <summary>
    /// Elk ertsblok is te mijnen. Ontbreekt de laser-lens-variant (JEI-tab "Laser"),
    /// dan komt de Laser Drill er als alternatief bij, zodat elk erts beide routes
    /// laat zien: zelf delven of de Industrial Foregoing Laser Drill.
    /// </summary>
    private static int ApplyOres(Dictionary<string, Recipe> recipes)
    {
        bool hasDrill = recipes.ContainsKey("laser_drill");
        int added = 0;

        foreach (string key in recipes.Keys.ToList())
        {
            if (!key.EndsWith("_ore", StringComparison.OrdinalIgnoreCase)) continue;

            added += AddAlternative(recipes, key, MiningCategory, 1, Array.Empty<Ingredient>());

            if (hasDrill && !HasCategory(recipes[key], "Laser"))
                added += AddAlternative(recipes, key, LaserDrillCategory, 1, Ingredient("laser_drill", 1));
        }

        return added;
    }

    // ========== Tinkers' Construct ==========

    /// <summary>
    /// De Tinkers-keten zoals de mod hem heeft: een patroon uit de Stencil Table,
    /// een cast van goud, het onderdeel uit de cast (of uit steen in de Part Builder)
    /// en de tool uit drie onderdelen. Alleen actief als de instance echt Tinkers
    /// heeft (seared brick bestaat).
    /// </summary>
    private static int ApplyTinkers(Dictionary<string, Recipe> recipes)
    {
        if (!recipes.ContainsKey("seared_brick")) return 0;

        int added = 0;

        // Grout smelt in de smeltery tot seared brick (in de dump ontbreekt die route).
        added += AddAlternative(recipes, "seared_brick", SmelteryCategory, 1, Ingredient("grout", 1));

        // Smeltery-ertsverdubbeling: 1 erts -> 2 ingots.
        foreach (string metal in SmelteryMetals)
        {
            string ingot = metal + "_ingot";
            string ore = metal + "_ore";
            if (!recipes.ContainsKey(ingot) || !recipes.ContainsKey(ore)) continue;

            added += AddAlternative(recipes, ingot, SmelteryCategory, 2, Ingredient(ore, 1));
        }

        bool hasPattern = recipes.ContainsKey("blank_pattern");
        bool hasGold = recipes.ContainsKey("gold_ingot");
        bool hasStone = recipes.ContainsKey("cobblestone");

        foreach (string part in TinkersParts)
        {
            // Patroon: een blank pattern in de Stencil Table.
            if (hasPattern)
                added += AddAlternative(recipes, part + "_pattern", StencilCategory, 1,
                                        Ingredient("blank_pattern", 1));

            // Cast: het patroon met een goudstaaf in de Casting Table.
            if (hasPattern && hasGold)
                added += AddAlternative(recipes, part + "_cast", CastingCategory, 1,
                                        Ingredient(part + "_pattern", 1), Ingredient("gold_ingot", 1));

            // Onderdeel uit de cast (metaal) of uit steen in de Part Builder. Staat er al
            // een echt castrecept (cast + gesmolten metaal), dan laten we dat staan.
            if (!recipes.TryGetValue(part, out var partRecipe) || !HasCategory(partRecipe, CastingCategory))
                added += AddAlternative(recipes, part, CastingCategory, 1, Ingredient(part + "_cast", 1));

            if (hasPattern && hasStone)
                added += AddAlternative(recipes, part, PartBuilderCategory, 1,
                                        Ingredient(part + "_pattern", 1), Ingredient("cobblestone", 1));
        }

        // Tools uit drie onderdelen.
        foreach (var (tool, category, parts) in TinkersTools)
        {
            var ingredients = parts.Select(p => Ingredient(p, 1)).ToArray();
            added += AddAlternative(recipes, tool, category, 1, ingredients);
        }

        return added;
    }

    // ========== Helpers ==========

    private static Ingredient Ingredient(string item, int count) => new() { Item = item, Count = count };

    private static bool HasCategory(Recipe recipe, string category) =>
        recipe.Options.Any(o => string.Equals(o.Category, category, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Voegt een recept toe als alternatief, of maakt het item aan als het nog niet
    /// bestaat. Een identiek recept (zelfde machine, zelfde ingredienten) blijft staan:
    /// dan verandert er niets.
    /// </summary>
    private static int AddAlternative(Dictionary<string, Recipe> recipes, string item, string category,
                                      int output, params Ingredient[] ingredients)
    {
        if (!recipes.TryGetValue(item, out var recipe))
        {
            recipes[item] = new Recipe
            {
                Category = category,
                Output = output,
                Ingredients = new List<Ingredient>(ingredients)
            };
            return 1;
        }

        foreach (var option in recipe.Options)
        {
            if (!string.Equals(option.Category, category, StringComparison.OrdinalIgnoreCase)) continue;
            if (option.Output == output && SameIngredients(option.Ingredients, ingredients)) return 0;
        }

        recipe.Alternatives.Add(new Recipe
        {
            Category = category,
            Output = output,
            Ingredients = new List<Ingredient>(ingredients)
        });
        return 1;
    }

    private static bool SameIngredients(IReadOnlyList<Ingredient> left, IReadOnlyList<Ingredient> right)
    {
        if (left.Count != right.Count) return false;

        var a = left.Select(i => i.Item.ToLowerInvariant() + "x" + i.Count).OrderBy(s => s).ToArray();
        var b = right.Select(i => i.Item.ToLowerInvariant() + "x" + i.Count).OrderBy(s => s).ToArray();

        return a.SequenceEqual(b);
    }
}
