//! `settings.json`, shared with the .NET app: the same keys and casing, and unknown keys are
//! carried through untouched so switching between the two builds never loses a setting.
//!
//! `Picks` is the part the planner uses: a ★ machine preference per item and the recipe you
//! chose by hand (an index into `Recipe.options()`).

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct Settings {
    pub bookmark_source: Option<String>,
    /// Per item the option index you chose (1 = first alternative; 0 means "no choice").
    pub recipe_choices: BTreeMap<String, i64>,
    /// Per item the machine that must always be used, when it can make the item.
    pub machine_preferences: BTreeMap<String, String>,
    /// Last filter used in the machine picker.
    pub machine_filter: String,
    pub scan_scripts: Vec<String>,
    pub max_crafts_per_step: Option<i64>,
    pub base_items_per_product: Option<i64>,
    pub language: Option<String>,
    /// Settings this build does not know about, kept so the .NET app still finds them.
    #[serde(flatten)]
    pub other: Map<String, Value>,
}

impl Settings {
    pub fn path(dir: &Path) -> PathBuf {
        dir.join("settings.json")
    }

    pub fn load(dir: &Path) -> Self {
        let Ok(text) = std::fs::read_to_string(Self::path(dir)) else {
            return Self::default();
        };
        serde_json::from_str(&text).unwrap_or_default()
    }

    /// Writes through a temporary file so a crash mid-write cannot leave half a settings file.
    pub fn save(&self, dir: &Path) -> std::io::Result<()> {
        std::fs::create_dir_all(dir)?;
        let path = Self::path(dir);
        let temp = path.with_extension("json.tmp");
        let text = serde_json::to_string_pretty(self).map_err(std::io::Error::other)?;
        std::fs::write(&temp, text)?;
        std::fs::rename(&temp, &path)
    }

    pub fn language(&self) -> String {
        crate::lang::normalize(self.language.as_deref())
    }

    pub fn set_picks(&mut self, picks: &Picks) {
        self.machine_preferences = picks
            .preferences
            .iter()
            .map(|(item, machine)| (item.clone(), machine.clone()))
            .collect();
        self.recipe_choices = picks
            .choices
            .iter()
            .filter(|(_, index)| **index > 0)
            .map(|(item, index)| (item.clone(), *index))
            .collect();
    }
}

/// The ★ preferences and hand-picked recipes, keyed by lowercased item name.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Picks {
    pub preferences: BTreeMap<String, String>,
    pub choices: BTreeMap<String, i64>,
}

impl Picks {
    pub fn from_settings(settings: &Settings) -> Self {
        let preferences = settings
            .machine_preferences
            .iter()
            .filter(|(item, machine)| !item.trim().is_empty() && !machine.trim().is_empty())
            .map(|(item, machine)| (item.trim().to_lowercase(), machine.trim().to_string()))
            .collect();

        let choices = settings
            .recipe_choices
            .iter()
            .filter(|(item, index)| !item.trim().is_empty() && **index > 0)
            .map(|(item, index)| (item.trim().to_lowercase(), *index))
            .collect();

        Self { preferences, choices }
    }

    pub fn is_empty(&self) -> bool {
        self.preferences.is_empty() && self.choices.is_empty()
    }

    pub fn len(&self) -> usize {
        self.preferences.len()
    }

    pub fn preference(&self, item: &str) -> Option<&str> {
        self.preferences.get(&item.trim().to_lowercase()).map(String::as_str)
    }

    pub fn choice(&self, item: &str) -> Option<usize> {
        self.choices
            .get(&item.trim().to_lowercase())
            .and_then(|index| usize::try_from(*index).ok())
    }

    pub fn set_preference(&mut self, item: &str, machine: &str) {
        if item.trim().is_empty() || machine.trim().is_empty() {
            return;
        }
        self.preferences
            .insert(item.trim().to_lowercase(), machine.trim().to_string());
    }

    pub fn clear_preference(&mut self, item: &str) {
        self.preferences.remove(&item.trim().to_lowercase());
    }

    pub fn clear_all_preferences(&mut self) {
        self.preferences.clear();
    }

    pub fn set_choice(&mut self, item: &str, index: usize) {
        if item.trim().is_empty() || index == 0 {
            self.choices.remove(&item.trim().to_lowercase());
            return;
        }
        self.choices.insert(item.trim().to_lowercase(), index as i64);
    }

    /// Preferences sorted by item, for the overview window.
    pub fn sorted_preferences(&self) -> Vec<(String, String)> {
        self.preferences
            .iter()
            .map(|(item, machine)| (item.clone(), machine.clone()))
            .collect()
    }
}
