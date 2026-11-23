//! The machine picker (`MachinePicker.cs`): every way an item can be made, grouped per machine
//! (JEI tab), with a filter, a ★ to make a machine the preference for this item, and a click to
//! use one recipe once. Plus the overview of the ★ preferences (`PreferenceList.cs`).

use crate::lang;
use eframe::egui;
use crate::model::{Recipe, RecipeSet};
use crate::names::Names;
use crate::settings::Picks;

const ACCENT: egui::Color32 = egui::Color32::from_rgb(176, 146, 222);

#[derive(Default)]
pub struct Picker {
    item: String,
    current: usize,
    filter: String,
    pub open: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Action {
    /// Use this option for the open tree (remembered in `RecipeChoices`).
    Chose(usize),
    /// Make this machine the ★ preference for the item (remembered in `MachinePreferences`).
    Prefer(String),
    ClearPreference,
}

impl Picker {
    pub fn open_for(&mut self, item: &str, current: usize, filter: &str) {
        self.item = item.to_string();
        self.current = current;
        self.filter = filter.to_string();
        self.open = true;
    }

    pub fn is_open(&self) -> bool {
        self.open
    }

    pub fn close(&mut self) {
        self.open = false;
    }

    pub fn item(&self) -> &str {
        &self.item
    }

    pub fn filter(&self) -> &str {
        &self.filter
    }

    /// Draws the picker; returns the action the window produced, if any.
    pub fn ui(
        &mut self,
        ctx: &egui::Context,
        recipes: &RecipeSet,
        names: &Names,
        picks: &Picks,
    ) -> Option<Action> {
        if !self.open || self.item.is_empty() {
            return None;
        }

        let options: Vec<Recipe> = recipes
            .get(&self.item)
            .map(|recipe| recipe.options().into_iter().cloned().collect())
            .unwrap_or_default();

        let mut action: Option<Action> = None;
        let mut open = true;

        egui::Window::new(lang::t("pick.title"))
            .open(&mut open)
            .resizable(true)
            .default_width(680.0)
            .show(ctx, |ui| {
                ui.label(
                    egui::RichText::new(lang::format("pick.heading", &[&names.display(&self.item)]))
                        .strong(),
                );

                if let Some(preference) = picks.preference(&self.item) {
                    let label = lang::format("pick.preference", &[preference]);
                    if ui
                        .link(egui::RichText::new(label).color(ACCENT))
                        .on_hover_text(lang::t("pick.clear.tip"))
                        .clicked()
                    {
                        action = Some(Action::ClearPreference);
                    }
                }

                ui.horizontal(|ui| {
                    ui.label("🔍");
                    ui.add(
                        egui::TextEdit::singleline(&mut self.filter)
                            .hint_text(lang::t("pick.search.tip"))
                            .desired_width(280.0),
                    );
                    ui.weak(lang::format(
                        "pick.count.machines",
                        &[&options.len().to_string(), &machine_count(&options).to_string()],
                    ));
                });
                ui.separator();

                let filter = self.filter.trim().to_lowercase();
                let mut groups: Vec<(String, Vec<usize>)> = Vec::new();

                for (index, option) in options.iter().enumerate() {
                    let machine = machine_name(option);
                    if !filter.is_empty()
                        && !machine.to_lowercase().contains(&filter)
                        && !option.ingredients.iter().any(|ingredient| {
                            names.display(&ingredient.item).to_lowercase().contains(&filter)
                        })
                    {
                        continue;
                    }

                    match groups
                        .iter_mut()
                        .find(|(known, _)| known.eq_ignore_ascii_case(&machine))
                    {
                        Some((_, indices)) => indices.push(index),
                        None => groups.push((machine, vec![index])),
                    }
                }

                groups.sort_by_key(|(machine, _)| machine.to_lowercase());

                egui::ScrollArea::vertical().max_height(380.0).show(ui, |ui| {
                    if groups.is_empty() {
                        ui.weak(lang::t("pick.none"));
                    }

                    for (machine, indices) in &groups {
                        ui.add_space(4.0);
                        ui.label(
                            egui::RichText::new(format!("{}  ({})", machine, indices.len()))
                                .color(ACCENT)
                                .strong(),
                        );

                        for index in indices {
                            let option = &options[*index];
                            let preferred = picks
                                .preference(&self.item)
                                .map(|known| known.eq_ignore_ascii_case(machine))
                                .unwrap_or(false);

                            ui.horizontal(|ui| {
                                if ui
                                    .small_button(if preferred { "★" } else { "☆" })
                                    .on_hover_text(lang::t(if preferred {
                                        "pick.preferred.tip"
                                    } else {
                                        "pick.setpref.tip"
                                    }))
                                    .clicked()
                                {
                                    action = Some(Action::Prefer(machine.clone()));
                                }

                                let marker = if *index == self.current { "▸" } else { " " };
                                let text = format!(
                                    "{} {:>2}. {}   {}",
                                    marker,
                                    index + 1,
                                    if machine.is_empty() { "—" } else { machine },
                                    describe_ingredients(option, names)
                                );
                                if ui.selectable_label(*index == self.current, text).clicked() {
                                    action = Some(Action::Chose(*index));
                                }
                            });
                        }
                    }
                });
            });

        if !open {
            self.open = false;
        }
        action
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum PrefAction {
    Remove(String),
    ClearAll,
}

/// The ★ overview: every preference with the machine and a ✕ to drop it.
pub fn preferences_window(
    ctx: &egui::Context,
    picks: &Picks,
    names: &Names,
    open: &mut bool,
) -> Option<PrefAction> {
    if !*open {
        return None;
    }

    let mut action: Option<PrefAction> = None;
    let entries = picks.sorted_preferences();

    egui::Window::new(lang::t("pref.title"))
        .open(open)
        .resizable(true)
        .default_width(520.0)
        .show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label(
                    egui::RichText::new(lang::t("pref.header")).color(ACCENT).strong(),
                );
                ui.weak(lang::format(
                    if entries.len() == 1 { "pref.count.one" } else { "pref.count" },
                    &[&entries.len().to_string()],
                ));
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if !entries.is_empty() && ui.button(lang::t("pref.clearall")).clicked() {
                        action = Some(PrefAction::ClearAll);
                    }
                });
            });
            ui.separator();

            egui::ScrollArea::vertical().max_height(360.0).show(ui, |ui| {
                if picks.is_empty() {
                    ui.weak(lang::t("pref.empty"));
                }

                for (item, machine) in &entries {
                    ui.horizontal(|ui| {
                        if ui
                            .small_button("✕")
                            .on_hover_text(lang::t("pref.remove.tip"))
                            .clicked()
                        {
                            action = Some(PrefAction::Remove(item.clone()));
                        }
                        ui.label(names.display(item));
                        ui.weak(format!("★ {machine}"));
                    });
                }
            });
        });

    action
}

pub fn machine_name(recipe: &Recipe) -> String {
    let category = recipe.category.trim();
    if category.is_empty() {
        lang::t("plan.machine.recipe")
    } else {
        category.to_string()
    }
}

fn machine_count(options: &[Recipe]) -> usize {
    let mut machines: Vec<String> = Vec::new();
    for option in options {
        let machine = machine_name(option);
        if !machines.iter().any(|known| known.eq_ignore_ascii_case(&machine)) {
            machines.push(machine);
        }
    }
    machines.len()
}

/// `Iron Ingot ×3, Stick ×2, …` — the first four ingredients, like `MainWindow.DescribeIngredients`.
fn describe_ingredients(recipe: &Recipe, names: &Names) -> String {
    let parts: Vec<String> = recipe
        .ingredients
        .iter()
        .take(4)
        .map(|ingredient| format!("{} ×{}", names.display(&ingredient.item), ingredient.count))
        .collect();

    if parts.is_empty() {
        return lang::t("preview.noinput");
    }

    let mut text = parts.join(", ");
    if recipe.ingredients.len() > 4 {
        text.push_str(", …");
    }
    text
}
