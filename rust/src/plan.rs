//! The step plan: base materials first, then every crafting step grouped per machine, in the
//! order the machines are last needed — a port of `RecipePlanner.BuildStepPlan`.
//!
//! The plan walks the same tree the window shows, so it follows the recipes the planner picked
//! (including alternatives and the machine a step ended up in). Truncated branches are not
//! silently listed as base material: they are marked so you can work that item out separately.

use crate::lang;
use crate::layers::{self, Layer};
use crate::model::RecipeSet;
use crate::names::{self, Names};
use crate::tree::Node;
use std::collections::{HashMap, HashSet};

#[derive(Clone, Debug)]
pub struct PlannedStep {
    pub name: String,
    pub quantity: i64,
    pub crafts: i64,
    pub machine: String,
    pub ingredients: Vec<(String, i64)>,
    pub source: String,
    pub layer: Layer,
}

#[derive(Clone, Debug)]
pub struct MachineBlock {
    pub machine: String,
    pub steps: Vec<PlannedStep>,
}

#[derive(Clone, Debug, Default)]
pub struct BaseItem {
    pub name: String,
    pub quantity: i64,
    /// The branch above this item was cut off by a planner limit.
    pub cut: bool,
}

#[derive(Clone, Debug, Default)]
pub struct StepPlan {
    pub root: String,
    pub quantity: i64,
    pub base: Vec<BaseItem>,
    pub machines: Vec<MachineBlock>,
    /// Crafting steps, not machine blocks.
    pub step_count: usize,
    /// Distinct items whose branch was truncated.
    pub cut_count: usize,
}

impl StepPlan {
    pub fn is_empty(&self) -> bool {
        self.machines.is_empty()
    }

    /// `van basis tot eindproduct · 4 craft-stap(pen) · 2 basis-materiaal/-materialen …`
    pub fn summary(&self) -> String {
        let cut = if self.cut_count > 0 {
            lang::format("plan.cutnote", &[&self.cut_count.to_string()])
        } else {
            String::new()
        };

        lang::format(
            "plan.summary",
            &[&self.step_count.to_string(), &self.base.len().to_string(), &cut],
        )
    }
}

pub fn crafts_for(amount: i64, output: i64) -> i64 {
    let output = output.max(1);
    (amount + output - 1) / output
}

/// Builds the plan for one tree. `recipes` is only used for the item name fallback and for
/// ingredients of steps whose node did not record a used recipe.
pub fn build(root: &Node, recipes: &RecipeSet) -> StepPlan {
    let mut plan = StepPlan {
        root: root.name.clone(),
        quantity: root.quantity,
        ..StepPlan::default()
    };

    let mut totals: HashMap<String, (String, i64)> = HashMap::new();
    let mut depths: HashMap<String, i32> = HashMap::new();
    let mut craftable: HashSet<String> = HashSet::new();
    let mut cut: HashMap<String, (String, i64)> = HashMap::new();
    let mut used: HashMap<String, (usize, String)> = HashMap::new();

    fn walk(
        node: &Node,
        depth: i32,
        totals: &mut HashMap<String, (String, i64)>,
        depths: &mut HashMap<String, i32>,
        craftable: &mut HashSet<String>,
        cut: &mut HashMap<String, (String, i64)>,
        used: &mut HashMap<String, (usize, String)>,
    ) {
        let key = node.name.to_lowercase();
        let entry = totals.entry(key.clone()).or_insert_with(|| (node.name.clone(), 0));
        entry.1 += node.quantity;

        let known = depths.entry(key.clone()).or_insert(depth);
        if depth > *known {
            *known = depth;
        }

        if node.is_truncated {
            let cut_entry = cut.entry(key.clone()).or_insert_with(|| (node.name.clone(), 0));
            cut_entry.1 += node.quantity;
        }

        if !node.children.is_empty() {
            craftable.insert(key.clone());
            used.insert(key, (node.option_index, node.category.clone()));
        }

        for child in &node.children {
            walk(child, depth + 1, totals, depths, craftable, cut, used);
        }
    }

    walk(root, 0, &mut totals, &mut depths, &mut craftable, &mut cut, &mut used);

    // Name lookups survive a fold that renamed an ingredient (dump names versus recipe keys).
    let mut canon_by_norm: HashMap<String, String> = HashMap::new();
    for (canonical, _) in totals.values() {
        let norm = names::normalize_name(canonical);
        if !norm.is_empty() {
            canon_by_norm.entry(norm).or_insert_with(|| canonical.clone());
        }
    }

    let mut recipe_by_norm: HashMap<String, &crate::model::Recipe> = HashMap::new();
    for key in recipes.keys() {
        let norm = names::normalize_name(key);
        if norm.is_empty() || recipe_by_norm.contains_key(&norm) {
            continue;
        }
        if let Some(recipe) = recipes.get(key) {
            recipe_by_norm.insert(norm, recipe);
        }
    }

    let canonical = |name: &str| -> String {
        let lower = name.to_lowercase();
        if let Some((canonical, _)) = totals.get(&lower) {
            return canonical.clone();
        }
        canon_by_norm
            .get(&names::normalize_name(name))
            .cloned()
            .unwrap_or_else(|| names::normalize_name(name))
    };

    // The recipe a step actually used: the tree recorded the option index the planner picked.
    let recipe_for = |name: &str| -> Option<crate::model::Recipe> {
        let recipe = recipes.get(name)?;
        let options = recipe.options();
        if options.is_empty() {
            return None;
        }
        let (index, _) = used.get(&name.to_lowercase()).cloned().unwrap_or((0, String::new()));
        let index = index.min(options.len() - 1);
        Some(options[index].clone())
    };

    let step_keys: Vec<String> = craftable
        .iter()
        .filter(|key| totals.contains_key(*key))
        .cloned()
        .collect();

    let mut indegree: HashMap<String, usize> = HashMap::new();
    let mut dependents: HashMap<String, Vec<String>> = HashMap::new();

    for key in &step_keys {
        indegree.entry(key.clone()).or_insert(0);
        let Some((name, _)) = totals.get(key) else { continue };
        let Some(recipe) = recipe_for(name) else { continue };

        for ingredient in &recipe.ingredients {
            let ing_key = canonical(&ingredient.item).to_lowercase();
            if !craftable.contains(&ing_key) || !totals.contains_key(&ing_key) || ing_key == *key {
                continue;
            }

            *indegree.entry(key.clone()).or_insert(0) += 1;
            dependents.entry(ing_key).or_default().push(key.clone());
        }
    }

    // Kahn, deepest steps first: you read from base material to end product.
    let mut ready: Vec<String> = indegree.iter().filter(|(_, count)| **count == 0).map(|(key, _)| key.clone()).collect();
    let mut ordered: Vec<String> = Vec::new();

    while !ready.is_empty() {
        ready.sort_by(|a, b| {
            let by_depth = depths.get(b).copied().unwrap_or(0).cmp(&depths.get(a).copied().unwrap_or(0));
            if by_depth != std::cmp::Ordering::Equal {
                return by_depth;
            }
            totals
                .get(a)
                .map(|(name, _)| name.to_lowercase())
                .cmp(&totals.get(b).map(|(name, _)| name.to_lowercase()))
        });

        let next = ready.remove(0);
        if ordered.contains(&next) {
            continue;
        }
        ordered.push(next.clone());

        if let Some(deps) = dependents.get(&next) {
            for dep in deps {
                if let Some(count) = indegree.get_mut(dep) {
                    *count = count.saturating_sub(1);
                    if *count == 0 {
                        ready.push(dep.clone());
                    }
                }
            }
        }
    }

    // Cycles and steps without a usable recipe go last rather than disappearing.
    for key in &step_keys {
        if !ordered.contains(key) {
            ordered.push(key.clone());
        }
    }

    plan.base = totals
        .iter()
        .filter(|(key, _)| !craftable.contains(*key))
        .map(|(_, (name, quantity))| BaseItem {
            name: name.clone(),
            quantity: *quantity,
            cut: cut.keys().any(|key| key == &name.to_lowercase()),
        })
        .collect();
    plan.base.sort_by_key(|item| item.name.to_lowercase());

    let mut machine_order: Vec<String> = Vec::new();
    let mut by_machine: HashMap<String, Vec<PlannedStep>> = HashMap::new();
    let mut last_step: HashMap<String, usize> = HashMap::new();

    for (index, key) in ordered.iter().enumerate() {
        let Some((name, quantity)) = totals.get(key) else { continue };
        let recipe = recipe_for(name);
        let machine = match &recipe {
            Some(recipe) if !recipe.category.trim().is_empty() => recipe.category.trim().to_string(),
            _ => lang::t("plan.machine.other"),
        };

        let output = recipe.as_ref().map(|recipe| recipe.output).unwrap_or(1);
        let crafts = crafts_for(*quantity, output);
        let ingredients = recipe
            .as_ref()
            .map(|recipe| {
                recipe
                    .ingredients
                    .iter()
                    .filter(|ingredient| !ingredient.item.trim().is_empty())
                    .map(|ingredient| (ingredient.item.clone(), ingredient.count * crafts))
                    .collect()
            })
            .unwrap_or_default();

        let (_, category) = used.get(key).cloned().unwrap_or((0, String::new()));
        let source = recipes.get(name).map(|recipe| recipe.source.clone()).unwrap_or_default();
        let _ = category;

        let step = PlannedStep {
            name: name.clone(),
            quantity: *quantity,
            crafts,
            machine: machine.clone(),
            ingredients,
            layer: layers::classify(&source),
            source,
        };

        if !by_machine.contains_key(&machine) {
            machine_order.push(machine.clone());
        }
        by_machine.entry(machine.clone()).or_default().push(step);
        last_step.insert(machine, index);
    }

    // Blocks in the order the machine is last needed: a machine that only supplies materials
    // comes before the one that makes the end product, even if it started later.
    machine_order.sort_by_key(|machine| last_step.get(machine).copied().unwrap_or(usize::MAX));

    plan.machines = machine_order
        .into_iter()
        .map(|machine| MachineBlock {
            steps: by_machine.remove(&machine).unwrap_or_default(),
            machine,
        })
        .collect();

    plan.step_count = plan.machines.iter().map(|block| block.steps.len()).sum();
    plan.cut_count = cut.len();
    plan
}

/// The plan as plain text, in the same shape as the .NET window's `StepPlanOutput`.
pub fn text(plan: &StepPlan, names: &Names) -> String {
    let mut out = String::new();
    out.push_str(&format!("🌸 {} × {}\n", names.display(&plan.root).to_uppercase(), plan.quantity));
    out.push_str(&plan.summary());
    out.push('\n');
    out.push_str(&"═".repeat(72));
    out.push_str("\n\n");

    out.push_str(&lang::t("plan.base"));
    out.push('\n');
    if plan.base.is_empty() {
        out.push_str(&lang::t("plan.base.none"));
        out.push('\n');
    } else {
        for item in &plan.base {
            let note = if item.cut { lang::t("plan.cut") } else { String::new() };
            out.push_str(&format!(
                "   {:<26} × {}{}\n",
                names.display(&item.name),
                item.quantity,
                note
            ));
        }
    }

    out.push('\n');
    out.push_str(&lang::t("plan.steps"));
    out.push('\n');

    if plan.machines.is_empty() {
        out.push_str(&lang::t("plan.steps.norecipe"));
        out.push('\n');
    } else {
        out.push_str(&lang::format(
            "plan.steps.count",
            &[&plan.step_count.to_string(), &plan.machines.len().to_string()],
        ));
        out.push('\n');

        for block in &plan.machines {
            out.push('\n');
            out.push_str(&lang::format(
                "plan.machine",
                &[&block.machine, &block.steps.len().to_string()],
            ));
            out.push('\n');
            out.push_str("   ");
            out.push_str(&"─".repeat(64));
            out.push('\n');

            for (index, step) in block.steps.iter().enumerate() {
                let mut line = format!(
                    "   {:>2}. {:<24} × {}",
                    index + 1,
                    names.display(&step.name),
                    step.quantity
                );
                if !step.ingredients.is_empty() {
                    let parts: Vec<String> = step
                        .ingredients
                        .iter()
                        .map(|(item, count)| format!("{} × {}", names.display(item), count))
                        .collect();
                    line.push_str(&format!("   ← {}", parts.join(", ")));
                }
                out.push_str(&line);
                out.push('\n');
            }
        }
    }

    if plan.cut_count > 0 {
        out.push('\n');
        out.push_str(&lang::format("plan.cut.warn", &[&plan.cut_count.to_string()]));
        out.push('\n');
        out.push_str(&lang::t("plan.cut.advice"));
        out.push('\n');
    }

    out.push('\n');
    out.push_str(&lang::format(
        "plan.product",
        &[&names.display(&plan.root), &plan.quantity.to_string()],
    ));
    out.push('\n');
    out
}
