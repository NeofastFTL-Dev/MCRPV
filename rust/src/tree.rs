//! Builds the recipe tree for one item, with the same rules as the .NET planner: a node budget,
//! a depth limit, cycle detection, a quantity cap, gathering recipes stopping the chain, and a
//! "sane option" pick that avoids recipes eating their own output or reverse pairs.

use crate::model::RecipeSet;
use std::collections::{HashMap, HashSet};

pub const MAX_TREE_NODES: i64 = 3_000;
pub const MAX_TREE_DEPTH: i32 = 16;
const MAX_QUANTITY: i64 = 9_999;
const CHAIN_LOOKAHEAD: i32 = 4;
const PICK_MEMO_PREFIX: &str = "@pick:";

#[derive(Clone, Debug)]
pub struct PlannerLimits {
    /// More crafts than this for one step is bulk: the branch gets truncated.
    pub max_crafts_per_step: i64,
    /// Base materials per planned item; beyond that the rest of the branch is truncated.
    pub base_items_per_product: i64,
}

impl Default for PlannerLimits {
    fn default() -> Self {
        Self { max_crafts_per_step: 16, base_items_per_product: 1_000 }
    }
}

impl PlannerLimits {
    pub fn clamped(&self) -> Self {
        Self {
            max_crafts_per_step: self.max_crafts_per_step.clamp(1, 999),
            base_items_per_product: self.base_items_per_product.clamp(1, 1_000_000),
        }
    }
}

#[derive(Clone, Debug, Default)]
pub struct Node {
    pub name: String,
    pub quantity: i64,
    pub is_cycle: bool,
    pub is_truncated: bool,
    pub children: Vec<Node>,
    /// Machine / JEI tab of the recipe used for this node.
    pub category: String,
    /// Which of the item's recipes was used (0 = the main one).
    pub option_index: usize,
    pub option_count: usize,
    pub source: String,
}

impl Node {
    pub fn has_choice(&self) -> bool {
        self.option_count > 1
    }
}

/// Natural ways of obtaining an item: farming, sieving, mining. The chain stops there.
const NATURAL_CATEGORIES: &[&str] = &[
    "Crop Produce",
    "Crop Mutation",
    "Tier 6 Crop",
    "Bonsai Trees",
    "Sieve",
    "Auto Sieve",
    "Sluice Box",
    "Fluid Sieving Machine",
    "Mining",
];

/// Recipes that transform an item into something else.
const TRANSFORM_CATEGORIES: &[&str] = &[
    "Transmutation",
    "World Transmutation",
    "Starlight Transmutation",
    "Enstabulation Apparatus",
    "Chiseling",
    "Factorizer - Combine",
    "Factorizer - Split",
    "Alchemy Array",
    "Alchemy Array (Binding)",
    "Lootbag",
    "Coin Engraving",
    "Materialization",
    "Crystallization",
];

/// Recipes where you buy the item; they come last.
const BUY_CATEGORIES: &[&str] = &["Market", "Amadron Tablet"];

pub fn build(item: &str, quantity: i64, recipes: &RecipeSet, limits: &PlannerLimits) -> Node {
    let limits = limits.clamped();
    let products = quantity.max(1);
    let mut budget = MAX_TREE_NODES;
    let mut base_left = products * limits.base_items_per_product;
    let crafts_limit = products * limits.max_crafts_per_step;

    let mut path = HashSet::new();
    let mut chain_costs: HashMap<String, i64> = HashMap::new();

    build_node(
        item,
        quantity,
        recipes,
        &mut budget,
        &mut base_left,
        crafts_limit,
        &mut path,
        0,
        &mut chain_costs,
    )
}

#[allow(clippy::too_many_arguments)]
fn build_node(
    item: &str,
    quantity: i64,
    recipes: &RecipeSet,
    budget: &mut i64,
    base_left: &mut i64,
    crafts_limit: i64,
    path: &mut HashSet<String>,
    depth: i32,
    chain_costs: &mut HashMap<String, i64>,
) -> Node {
    let mut node = Node { name: item.to_string(), quantity, ..Node::default() };

    let key = item.to_lowercase();
    if path.contains(&key) {
        node.is_cycle = true;
        return node;
    }

    // Every node costs budget, even one that stops at the depth limit: otherwise a wide chain
    // keeps growing way past the node cap.
    *budget -= 1;
    if *budget < 0 || depth >= MAX_TREE_DEPTH || *base_left <= 0 {
        node.is_truncated = true;
        return node;
    }

    // End of the chain: this item comes from the world, a farm or a machine.
    let recipe = match recipes.get(item) {
        Some(recipe) if !recipe.ingredients.is_empty() => recipe,
        _ => {
            *base_left -= quantity;
            return node;
        }
    };

    let options = recipe.options();
    let mut option_index = 0usize;
    let used = {
        option_index = pick_sane_option(item, &options, option_index, recipes, chain_costs);
        options[option_index]
    };

    node.category = used.category.clone();
    node.source = used.source.clone();
    node.option_index = option_index;
    node.option_count = options.len();

    // Farming, sieving, buying and converting are gathering, not crafting steps.
    if is_gathering(used) {
        *base_left -= quantity;
        return node;
    }

    let per_craft = used.output.max(1);
    let crafts = (quantity + per_craft - 1) / per_craft;
    if crafts > crafts_limit {
        node.is_truncated = true;
        return node;
    }

    path.insert(key);

    for ingredient in &used.ingredients {
        let needed = ingredient.count * crafts;
        if needed > MAX_QUANTITY {
            node.children.push(Node {
                name: ingredient.item.clone(),
                quantity: MAX_QUANTITY,
                is_truncated: true,
                ..Node::default()
            });
            continue;
        }

        node.children.push(build_node(
            &ingredient.item,
            needed,
            recipes,
            budget,
            base_left,
            crafts_limit,
            path,
            depth + 1,
            chain_costs,
        ));
    }

    path.remove(&item.to_lowercase());
    node
}

fn category_tier(recipe: &crate::model::Recipe) -> i32 {
    if BUY_CATEGORIES.iter().any(|c| c.eq_ignore_ascii_case(&recipe.category)) {
        return 3;
    }
    if NATURAL_CATEGORIES.iter().any(|c| c.eq_ignore_ascii_case(&recipe.category)) {
        return 0;
    }
    if TRANSFORM_CATEGORIES.iter().any(|c| c.eq_ignore_ascii_case(&recipe.category)) {
        return 2;
    }
    // "8 Nature Essence → 1 Pumpkin" sits under Crafting but is a conversion.
    if !recipe.ingredients.is_empty()
        && recipe.ingredients.iter().all(|i| i.item.to_lowercase().contains("essence"))
    {
        return 2;
    }
    1
}

fn is_gathering(recipe: &crate::model::Recipe) -> bool {
    category_tier(recipe) != 1
}

fn is_sane_option(item: &str, recipe: &crate::model::Recipe, recipes: &RecipeSet) -> bool {
    if recipe.ingredients.iter().any(|i| i.item.eq_ignore_ascii_case(item)) {
        return false;
    }
    if is_gathering(recipe) {
        return true;
    }
    reverse_pair_with(item, recipe, recipes).is_none()
}

/// An ingredient that forms a reverse pair: its own recipe consumes this item again.
fn reverse_pair_with(item: &str, recipe: &crate::model::Recipe, recipes: &RecipeSet) -> Option<String> {
    for ingredient in &recipe.ingredients {
        if ingredient.item.eq_ignore_ascii_case(item) {
            continue;
        }
        let Some(back) = recipes.get(&ingredient.item) else { continue };
        if back.ingredients.iter().any(|i| i.item.eq_ignore_ascii_case(item)) {
            return Some(ingredient.item.clone());
        }
    }
    None
}

fn pick_sane_option(
    item: &str,
    options: &[&crate::model::Recipe],
    start: usize,
    recipes: &RecipeSet,
    memo: &mut HashMap<String, i64>,
) -> usize {
    if options.len() < 2 {
        return start;
    }

    let memo_key = format!("{PICK_MEMO_PREFIX}{}", item.to_lowercase());
    if let Some(remembered) = memo.get(&memo_key) {
        if *remembered >= 0 && (*remembered as usize) < options.len() {
            return *remembered as usize;
        }
    }

    let mut best: Option<usize> = None;
    let mut best_tier = i64::MAX;
    let mut best_cost = i64::MAX;
    let mut best_output = 0i64;

    for (index, option) in options.iter().enumerate() {
        if !is_sane_option(item, option, recipes) {
            continue;
        }
        let tier = category_tier(option) as i64;
        let output = option.output.max(1);
        let cost = if tier > best_tier {
            i64::MAX
        } else {
            option_chain_cost(item, option, recipes, memo)
        };

        let better = tier < best_tier
            || (tier == best_tier && (cost < best_cost || (cost == best_cost && output > best_output)));

        if better {
            best = Some(index);
            best_tier = tier;
            best_cost = cost;
            best_output = output;
        }
    }

    let chosen = best.unwrap_or(start);
    memo.insert(memo_key, chosen as i64);
    chosen
}

fn option_chain_cost(
    item: &str,
    option: &crate::model::Recipe,
    recipes: &RecipeSet,
    memo: &mut HashMap<String, i64>,
) -> i64 {
    if option.ingredients.is_empty() {
        return 0;
    }

    let mut cost = 1;
    let mut path: HashSet<String> = HashSet::new();
    path.insert(item.to_lowercase());

    for ingredient in &option.ingredients {
        cost += 1 + estimate_chain_cost(&ingredient.item, recipes, memo, &mut path, CHAIN_LOOKAHEAD);
    }
    cost
}

fn estimate_chain_cost(
    item: &str,
    recipes: &RecipeSet,
    memo: &mut HashMap<String, i64>,
    path: &mut HashSet<String>,
    depth: i32,
) -> i64 {
    let key = item.to_lowercase();
    if depth <= 0 || path.contains(&key) {
        return 0;
    }
    if let Some(known) = memo.get(&key) {
        return *known;
    }
    let Some(recipe) = recipes.get(item) else { return 0 };
    if recipe.ingredients.is_empty() {
        return 0;
    }

    let mut best = i64::MAX;
    path.insert(key.clone());

    for option in recipe.options() {
        if !is_sane_option(item, option, recipes) {
            continue;
        }
        let mut cost = 1;
        for ingredient in &option.ingredients {
            cost += 1 + estimate_chain_cost(&ingredient.item, recipes, memo, path, depth - 1);
        }
        if cost < best {
            best = cost;
        }
    }

    path.remove(&key);

    if best == i64::MAX {
        best = 1;
    }
    memo.insert(key, best);
    best
}

/// Total crafts and base materials, for the status line.
pub fn totals(node: &Node) -> (i64, i64) {
    fn walk(node: &Node, crafts: &mut i64, bases: &mut i64) {
        let is_base = node.children.is_empty();
        if is_base {
            *bases += node.quantity;
        } else {
            *crafts += 1;
        }
        for child in &node.children {
            walk(child, crafts, bases);
        }
    }
    let mut crafts = 0;
    let mut bases = 0;
    walk(node, &mut crafts, &mut bases);
    (crafts, bases)
}

/// The tree as indented text, mirroring the bracket text the .NET window produced.
pub fn bracket_text(node: &Node, names: &crate::names::Names) -> String {
    fn walk(node: &Node, names: &crate::names::Names, out: &mut String, indent: &str) {
        for (index, child) in node.children.iter().enumerate() {
            let last = index + 1 == node.children.len();
            out.push_str(indent);
            out.push_str(if last { "└─ " } else { "├─ " });
            out.push_str(&names.display(&child.name));
            if child.quantity > 1 {
                out.push_str(&format!(" ×{}", child.quantity));
            }
            if child.is_cycle {
                out.push_str(" ♻️");
            }
            if child.is_truncated {
                out.push_str(" …");
            }
            out.push('\n');

            let child_indent = format!("{indent}{}", if last { "   " } else { "│  " });
            walk(child, names, out, &child_indent);
        }
    }

    let mut out = format!("{} ×{}\n", names.display(&node.name), node.quantity);
    walk(node, names, &mut out, "  ");
    out
}
