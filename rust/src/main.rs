//! MCRPV — Minecraft Recipe Tree Viewer, Rust/egui port (stage 1).
//!
//! This stage brings over the data layer (recipes.json model, item names, language tables), the
//! recipe tree planner and a searchable main window. The .NET app stays on `main` until the
//! remaining windows reach parity.
//!
//! `--selftest` runs the checks CI uses; `--data <dir>` points at a folder with recipes.json and
//! names.json instead of the local data folder.

mod lang;
mod layers;
mod model;
mod names;
mod plan;
mod search;
mod selftest;
mod tree;

use eframe::egui;
use model::RecipeSet;
use names::Names;
use search::SearchIndex;
use std::path::{Path, PathBuf};
use tree::{Node, PlannerLimits};

const ORANGE: egui::Color32 = egui::Color32::from_rgb(255, 136, 0);
const BACK: egui::Color32 = egui::Color32::from_rgb(18, 18, 24);
const MUTED: egui::Color32 = egui::Color32::from_rgb(170, 170, 185);
const WARN: egui::Color32 = egui::Color32::from_rgb(255, 196, 87);

#[derive(Clone, Copy, PartialEq, Eq)]
enum Tab {
    Tree,
    Plan,
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|arg| arg == "--selftest") {
        std::process::exit(selftest::run());
    }

    let data_dir = match arg_value(&args, "--data") {
        Some(path) => PathBuf::from(path),
        None => default_data_dir(),
    };

    lang::set(&settings_language(&data_dir));
    let names = Names::load_file(&data_dir.join("names.json"));
    let (recipes, source) = load_recipes(&data_dir);
    let index = SearchIndex::build(recipes.keys().iter().cloned(), &names);
    let status = format!(
        "{} recepten · {} · {}",
        recipes.len(),
        if names.is_empty() { "geen namen".to_string() } else { format!("{} namen", names.len()) },
        source.display()
    );

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1100.0, 760.0])
            .with_min_inner_size([720.0, 480.0])
            .with_title("MCRPV 🌸"),
        ..Default::default()
    };

    let result = eframe::run_native(
        "MCRPV 🌸",
        options,
        Box::new(move |cc| Ok(Box::new(App::new(cc, recipes, names, index, status)))),
    );
    if let Err(error) = result {
        eprintln!("MCRPV: {error}");
    }
}

fn arg_value(args: &[String], name: &str) -> Option<String> {
    args.iter().position(|arg| arg == name).and_then(|index| args.get(index + 1)).cloned()
}

/// `%LOCALAPPDATA%\MCRPV` on Windows, `$XDG_DATA_HOME/mcrpv` elsewhere, with the old folder moved
/// across the way the .NET app did.
fn default_data_dir() -> PathBuf {
    let base = if cfg!(windows) {
        std::env::var("LOCALAPPDATA").ok().map(PathBuf::from)
    } else {
        std::env::var("XDG_DATA_HOME")
            .ok()
            .filter(|dir| !dir.is_empty())
            .map(PathBuf::from)
            .or_else(|| std::env::var("HOME").ok().map(|home| PathBuf::from(home).join(".local").join("share")))
    };

    let base = base.unwrap_or_else(std::env::temp_dir);
    let dir = base.join(if cfg!(windows) { "MCRPV" } else { "mcrpv" });
    let legacy = base.join(if cfg!(windows) { "PO3RecipeTree" } else { "po3recipetree" });
    if !dir.exists() && legacy.exists() {
        let _ = std::fs::rename(&legacy, &dir);
    }
    let _ = std::fs::create_dir_all(&dir);
    dir
}

fn settings_language(data_dir: &Path) -> String {
    let Ok(text) = std::fs::read_to_string(data_dir.join("settings.json")) else {
        return lang::DEFAULT.to_string();
    };
    let Ok(value) = serde_json::from_str::<serde_json::Value>(&text) else {
        return lang::DEFAULT.to_string();
    };
    value
        .get("Language")
        .and_then(|code| code.as_str())
        .map(|code| lang::normalize(Some(code)))
        .unwrap_or_else(|| lang::DEFAULT.to_string())
}

/// The data folder first; when it is empty, the sample that ships with the repository, so a fresh
/// checkout shows a working tree instead of an empty window.
fn load_recipes(data_dir: &Path) -> (RecipeSet, PathBuf) {
    let local = data_dir.join("recipes.json");
    if local.is_file() {
        if let Ok(set) = RecipeSet::load(&local) {
            if !set.is_empty() {
                return (set, local);
            }
        }
    }

    let bundled = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("MCRPV")
        .join("recipes.json");
    if bundled.is_file() {
        if let Ok(set) = RecipeSet::load(&bundled) {
            return (set, bundled);
        }
    }

    (RecipeSet::default(), local)
}

struct App {
    recipes: RecipeSet,
    names: Names,
    index: SearchIndex,
    status: String,
    layer_line: String,

    query: String,
    last_query: String,
    results: Vec<String>,
    quantity: i64,
    selected: Option<String>,
    root: Option<Node>,
    limits: PlannerLimits,
    show_bracket: bool,
    show_plan_text: bool,
    language: String,
    tab: Tab,
    plan: Option<plan::StepPlan>,
}

impl App {
    fn new(
        cc: &eframe::CreationContext<'_>,
        recipes: RecipeSet,
        names: Names,
        index: SearchIndex,
        status: String,
    ) -> Self {
        let layer_line = layers::summary(&layers::counts(&recipes));

        let mut style = (*cc.egui_ctx.style()).clone();
        style.visuals = egui::Visuals::dark();
        style.visuals.panel_fill = BACK;
        style.spacing.item_spacing = egui::vec2(8.0, 6.0);
        cc.egui_ctx.set_style(style);

        Self {
            recipes,
            names,
            index,
            status,
            layer_line,
            query: String::new(),
            last_query: String::from("\u{0}"),
            results: Vec::new(),
            quantity: 1,
            selected: None,
            root: None,
            limits: PlannerLimits::default(),
            show_bracket: false,
            show_plan_text: false,
            language: lang::code(),
            tab: Tab::Tree,
            plan: None,
        }
    }

    fn build_tree(&mut self) {
        if let Some(item) = self.selected.clone() {
            let root = tree::build(&item, self.quantity.max(1), &self.recipes, &self.limits);
            // The plan is built from the same tree, so it follows the recipes and machines the
            // planner picked instead of guessing from the recipe set.
            self.plan = Some(plan::build(&root, &self.recipes));
            self.root = Some(root);
        }
    }

    fn select(&mut self, key: String) {
        self.selected = Some(key);
        self.build_tree();
    }
}

impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // Search results only change when the query does.
        if self.query != self.last_query {
            self.last_query = self.query.clone();
            self.results = self
                .index
                .search(&self.query, 200)
                .into_iter()
                .map(|entry| entry.key.clone())
                .collect();
        }

        egui::TopBottomPanel::top("top").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label(egui::RichText::new(lang::t("main.title")).size(24.0).strong().color(ORANGE));
                ui.label(egui::RichText::new(lang::t("main.subtitle")).color(MUTED));
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    for code in lang::codes() {
                        let active = self.language.eq_ignore_ascii_case(code);
                        let label = lang::native_name(code);
                        if ui.selectable_label(active, label).clicked() {
                            lang::set(code);
                            self.language = lang::code();
                        }
                    }
                });
            });
            ui.separator();

            ui.horizontal(|ui| {
                ui.selectable_value(&mut self.tab, Tab::Tree, "🌳 RECIPE TREE");
                ui.selectable_value(&mut self.tab, Tab::Plan, lang::t("header.plan"));
            });

            ui.horizontal(|ui| {
                ui.label("🧱 ITEM");
                let response = ui.add(
                    egui::TextEdit::singleline(&mut self.query)
                        .hint_text(lang::t("tip.item"))
                        .desired_width(360.0),
                );
                if response.lost_focus() && ui.input(|i| i.key_pressed(egui::Key::Enter)) {
                    if let Some(first) = self.results.first().cloned() {
                        self.select(first);
                    }
                }

                ui.label(lang::t("lbl.quantity"));
                ui.add(egui::DragValue::new(&mut self.quantity).range(1..=9999).speed(1.0));

                if ui.button("✨ BUILD TREE").clicked() {
                    if let Some(first) = self.results.first().cloned() {
                        self.select(first);
                    } else if let Some(selected) = self.selected.clone() {
                        self.selected = Some(selected);
                        self.build_tree();
                    }
                }
            });
        });

        egui::SidePanel::left("results").resizable(true).default_width(300.0).show(ctx, |ui| {
            ui.label(egui::RichText::new(format!("🔍 FILTER · {}", self.results.len())).color(MUTED));
            if self.index.is_empty() {
                ui.weak("geen recepten geladen — zet recipes.json in de datamap");
            }
            egui::ScrollArea::vertical().show(ui, |ui| {
                for key in self.results.clone() {
                    let display = self.names.display(&key);
                    let selected = self.selected.as_deref() == Some(key.as_str());
                    if ui.selectable_label(selected, display).clicked() {
                        self.select(key);
                    }
                }
                if self.results.is_empty() {
                    ui.weak("geen resultaten");
                }
            });
        });

        egui::TopBottomPanel::bottom("status").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.weak(&self.status);
                ui.separator();
                ui.weak(&self.layer_line);
                if let Some(item) = &self.selected {
                    ui.separator();
                    ui.weak(format!("gekozen: {}", self.names.display(item)));
                }
            });
        });

        egui::CentralPanel::default().show(ctx, |ui| {
            let Some(root) = &self.root else {
                ui.centered_and_justified(|ui| {
                    ui.label(egui::RichText::new("Kies een item aan de linkerkant om de boom te bouwen").color(MUTED));
                });
                return;
            };

            match self.tab {
                Tab::Tree => {
                    let (crafts, bases) = tree::totals(root);
                    ui.horizontal(|ui| {
                        ui.label(
                            egui::RichText::new(format!("{} ×{}", self.names.display(&root.name), root.quantity))
                                .size(20.0)
                                .strong()
                                .color(ORANGE),
                        );
                        if !self.recipes.contains(&root.name) {
                            ui.weak("⚠ geen recept");
                        }
                        ui.weak(format!("STAPPEN: {crafts}"));
                        ui.weak(format!("BASIS: {bases}"));
                    });

                    ui.separator();
                    egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| {
                        render_node(ui, root, &self.names);
                    });

                    ui.separator();
                    ui.checkbox(&mut self.show_bracket, lang::t("tab.bracket.tip"));
                    if self.show_bracket {
                        let text = tree::bracket_text(root, &self.names);
                        ui.add(egui::TextEdit::multiline(&mut text.as_str()).desired_rows(12));
                    }
                }
                Tab::Plan => {
                    if let Some(plan) = self.plan.take() {
                        let mut show_text = self.show_plan_text;
                        egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| {
                            render_plan(ui, &plan, &self.names);
                            ui.separator();
                            ui.checkbox(&mut show_text, "platte tekst");
                            if show_text {
                                let text = plan::text(&plan, &self.names);
                                ui.add(egui::TextEdit::multiline(&mut text.as_str()).desired_rows(18));
                            }
                        });
                        self.show_plan_text = show_text;
                        self.plan = Some(plan);
                    }
                }
            }
        });
    }
}

fn render_node(ui: &mut egui::Ui, node: &Node, names: &Names) {
    let mut label = names.display(&node.name).to_string();
    if node.quantity > 1 {
        label.push_str(&format!(" ×{}", node.quantity));
    }
    if node.is_cycle {
        label.push_str(" ♻️");
    }
    if node.is_truncated {
        label.push_str(" …");
    }

    if node.children.is_empty() {
        ui.horizontal(|ui| {
            ui.label(label);
            if !node.category.is_empty() {
                ui.weak(format!("· {}", node.category));
            }
        });
        return;
    }

    egui::CollapsingHeader::new(label)
        .default_open(true)
        .show(ui, |ui| {
            if !node.category.is_empty() {
                if node.has_choice() {
                    ui.weak(format!("{} · {}/{}", node.category, node.option_index + 1, node.option_count));
                } else {
                    ui.weak(node.category.clone());
                }
            }
            for child in &node.children {
                render_node(ui, child, names);
            }
        });
}

fn render_plan(ui: &mut egui::Ui, plan: &plan::StepPlan, names: &Names) {
    ui.horizontal(|ui| {
        ui.label(
            egui::RichText::new(format!("{} ×{}", names.display(&plan.root), plan.quantity))
                .size(20.0)
                .strong()
                .color(ORANGE),
        );
        ui.weak(format!("{}: {}", lang::t("lbl.steps"), plan.machines.len()));
    });
    ui.weak(plan.summary());
    ui.separator();

    ui.label(egui::RichText::new(lang::t("plan.base")).strong());
    if plan.base.is_empty() {
        ui.weak(lang::t("plan.base.none"));
    } else {
        for item in &plan.base {
            ui.horizontal(|ui| {
                ui.label(names.display(&item.name));
                ui.weak(format!("× {}", item.quantity));
                if item.cut {
                    ui.colored_label(WARN, lang::t("plan.cut"));
                }
            });
        }
    }

    ui.separator();
    ui.label(egui::RichText::new(lang::t("plan.steps")).strong());
    if plan.is_empty() {
        ui.weak(lang::t("plan.steps.norecipe"));
    }

    for block in &plan.machines {
        egui::CollapsingHeader::new(lang::format(
            "plan.machine",
            &[&block.machine, &block.steps.len().to_string()],
        ))
        .default_open(true)
        .show(ui, |ui| {
            for (index, step) in block.steps.iter().enumerate() {
                ui.horizontal(|ui| {
                    ui.weak(format!("{:>2}.", index + 1));
                    ui.label(egui::RichText::new(names.display(&step.name)).strong())
                        .on_hover_text(format!("{}: {}", lang::t("plan.machine"), step.machine));
                    ui.weak(format!("× {}", step.quantity));
                    if step.crafts > 1 {
                        ui.weak(format!("({}×)", step.crafts));
                    }
                    ui.weak(format!("{} {}", step.layer.icon(), step.layer.label()));
                    if !step.source.is_empty() {
                        ui.weak(&step.source);
                    }
                });

                if !step.ingredients.is_empty() {
                    let parts: Vec<String> = step
                        .ingredients
                        .iter()
                        .map(|(item, count)| format!("{} × {}", names.display(item), count))
                        .collect();
                    ui.weak(format!("     ← {}", parts.join(", ")));
                }
            }
        });
    }

    if plan.cut_count > 0 {
        ui.separator();
        ui.colored_label(WARN, lang::format("plan.cut.warn", &[&plan.cut_count.to_string()]));
        ui.weak(lang::t("plan.cut.advice"));
    }

    ui.separator();
    ui.label(
        egui::RichText::new(lang::format(
            "plan.product",
            &[&names.display(&plan.root), &plan.quantity.to_string()],
        ))
        .strong(),
    );
}
