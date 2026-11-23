//! The layer a recipe came from, mirroring `RecipeLayer` in `RecipeLayers.cs` and the source
//! prefixes the .NET fold used: the jar-JSON base (and the generated overlay), the JEIExporter
//! runtime dump, and the pack layer written by datapacks, CraftTweaker and KubeJS scripts.
//!
//! The user layer only exists once `user.jsonl` operations are folded in, which is stage-2 work
//! that needs the script parsers; without them a set has three layers.

use crate::model::RecipeSet;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Layer {
    /// `jar-JSON …`, `overlay`, or a recipe with no source: the pack's own data.
    Base,
    /// `runtime:…`: recipes that only the game itself could produce (1.12 code recipes).
    Dump,
    /// `scripts/…`, `datapacks/…`, `kubejs/…`: a script or datapack changed the recipe.
    Pack,
    /// The player's own operations (`user.jsonl`).
    User,
}

impl Layer {
    pub fn label(self) -> &'static str {
        match self {
            Layer::Base => "basis",
            Layer::Dump => "dump",
            Layer::Pack => "pack",
            Layer::User => "eigen",
        }
    }

    pub fn icon(self) -> &'static str {
        match self {
            Layer::Base => "📦",
            Layer::Dump => "🧱",
            Layer::Pack => "🧩",
            Layer::User => "🖐",
        }
    }
}

/// The same classification `Fold` ends up with: `runtime:` is the dump, the jar base and the
/// overlay are the base, and everything else written by a script or datapack is the pack layer.
pub fn classify(source: &str) -> Layer {
    let source = source.trim();
    let lowered = source.to_ascii_lowercase();

    if lowered.starts_with("runtime:") {
        Layer::Dump
    } else if source.is_empty() || lowered.starts_with("jar-json") || lowered.starts_with("overlay") {
        Layer::Base
    } else {
        Layer::Pack
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct LayerCounts {
    pub base: usize,
    pub dump: usize,
    pub pack: usize,
    pub user: usize,
}

/// How many of the loaded recipes each layer supplied — the `📦 … basis · 🧩 … pack` line.
pub fn counts(recipes: &RecipeSet) -> LayerCounts {
    let mut counts = LayerCounts::default();
    for key in recipes.keys() {
        let Some(recipe) = recipes.get(key) else { continue };
        match classify(&recipe.source) {
            Layer::Base => counts.base += 1,
            Layer::Dump => counts.dump += 1,
            Layer::Pack => counts.pack += 1,
            Layer::User => counts.user += 1,
        }
    }
    counts
}

/// `📦 5 basis · 🧱 0 dump · 🧩 1 pack`, skipping the layers a set does not have.
pub fn summary(counts: &LayerCounts) -> String {
    let mut parts = vec![format!("{} {} {}", Layer::Base.icon(), counts.base, Layer::Base.label())];
    if counts.dump > 0 {
        parts.push(format!("{} {} {}", Layer::Dump.icon(), counts.dump, Layer::Dump.label()));
    }
    if counts.pack > 0 {
        parts.push(format!("{} {} {}", Layer::Pack.icon(), counts.pack, Layer::Pack.label()));
    }
    if counts.user > 0 {
        parts.push(format!("{} {} {}", Layer::User.icon(), counts.user, Layer::User.label()));
    }
    parts.join(" · ")
}
