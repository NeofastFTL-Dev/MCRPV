//! The recipes.json model: one entry per output item, with alternative recipes from other JEI
//! tabs. Keys are matched case-insensitively, exactly like the .NET dictionary did.

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::Path;

fn one() -> i64 {
    1
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct Ingredient {
    pub item: String,
    #[serde(default = "one")]
    pub count: i64,
}

impl Default for Ingredient {
    fn default() -> Self {
        Self { item: String::new(), count: 1 }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct Recipe {
    /// JEI tab / machine this recipe belongs to ("Crafting", "Alloy Smelter", …).
    pub category: String,
    /// How many items one craft yields.
    #[serde(default = "one")]
    pub output: i64,
    pub ingredients: Vec<Ingredient>,
    /// Other ways to make the same item; a hand-edited file may contain nulls.
    pub alternatives: Vec<Option<Recipe>>,
    /// Where it came from ("jar-JSON immersiveengineering", …).
    pub source: String,
}

impl Default for Recipe {
    fn default() -> Self {
        Self {
            category: String::new(),
            output: 1,
            ingredients: Vec::new(),
            alternatives: Vec::new(),
            source: String::new(),
        }
    }
}

impl Recipe {
    /// The main recipe first, then the alternatives without the null holes.
    pub fn options(&self) -> Vec<&Recipe> {
        let mut options = vec![self];
        options.extend(self.alternatives.iter().flatten());
        options
    }
}

#[derive(Default, Clone, Debug)]
pub struct RecipeSet {
    map: HashMap<String, Recipe>,
    keys: Vec<String>,
}

impl RecipeSet {
    pub fn from_json(text: &str) -> Result<Self, String> {
        let parsed: HashMap<String, Recipe> = serde_json::from_str(text).map_err(|e| e.to_string())?;
        let mut set = Self::default();
        for (key, recipe) in parsed {
            set.insert(key, recipe);
        }
        set.keys.sort();
        Ok(set)
    }

    pub fn load(path: &Path) -> Result<Self, String> {
        let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
        Self::from_json(&text)
    }

    pub fn insert(&mut self, key: String, recipe: Recipe) {
        if key.trim().is_empty() {
            return;
        }
        if !self.map.contains_key(&key.to_lowercase()) {
            self.keys.push(key.clone());
        }
        self.map.insert(key.to_lowercase(), recipe);
    }

    pub fn get(&self, key: &str) -> Option<&Recipe> {
        self.map.get(&key.to_lowercase())
    }

    pub fn contains(&self, key: &str) -> bool {
        self.map.contains_key(&key.to_lowercase())
    }

    pub fn len(&self) -> usize {
        self.map.len()
    }

    pub fn is_empty(&self) -> bool {
        self.map.is_empty()
    }

    pub fn keys(&self) -> &[String] {
        &self.keys
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_sample_file() {
        let text = r#"{
            "diamond": {"Ingredients": [{"Item": "diamond_ore", "Count": 1}]},
            "iron_pickaxe": {"Ingredients": [{"Item": "iron_ingot", "Count": 3},
                                             {"Item": "stick", "Count": 2}]}
        }"#;
        let set = RecipeSet::from_json(text).unwrap();
        assert_eq!(set.len(), 2);
        let pickaxe = set.get("IRON_PICKAXE").expect("case-insensitive lookup");
        assert_eq!(pickaxe.output, 1, "Output defaults to 1");
        assert_eq!(pickaxe.ingredients[1].count, 2);
    }

    #[test]
    fn keeps_alternatives_and_skips_nulls() {
        let text = r#"{"x": {"Ingredients": [], "Alternatives": [
            {"Category": "Furnace", "Ingredients": [{"Item": "y"}]}, null]}}"#;
        let set = RecipeSet::from_json(text).unwrap();
        let recipe = set.get("x").unwrap();
        assert_eq!(recipe.options().len(), 2);
        assert_eq!(recipe.options()[1].category, "Furnace");
    }

    #[test]
    fn name_cleaning_matches_the_planner() {
        assert_eq!(crate::names::normalize_name("minecraft:Iron Ingot{display:1}"), "iron_ingot");
        assert_eq!(crate::names::normalize_name("minecraft:stone 1 0"), "stone");
        assert_eq!(crate::names::normalize_name("mod:path:0"), "path");
        assert_eq!(crate::names::normalize_name("Block of Iron"), "block_of_iron");
    }
}
