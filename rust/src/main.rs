//! MCRPV — Minecraft Recipe Tree Viewer, Rust/egui port (stage 1).
//!
//! This stage brings over the data layer (recipes.json model, item names, language tables), the
//! recipe tree planner and a searchable main window. The .NET app stays on `main` until the
//! remaining windows reach parity.
//!
//! `--selftest` runs the checks CI uses; `--data <dir>` points at a folder with recipes.json and
//! names.json instead of the local data folder.

mod lang;
mod model;
mod names;
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

    query: String,
    last_query: String,
    results: Vec<String>,
    quantity: i64,
    selected: Option<String>,
    root: Option<Node>,
    limits: PlannerLimits,
    show_bracket: bool,
    language: String,
}

impl App {
    fn new(
        cc: &eframe::CreationContext<'_>,
        recipes: RecipeSet,
        names: Names,
        index: SearchIndex,
        status: String,
    ) -> Self {
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
            query: String::new(),
            last_query: String::from("\u{0}"),
            results: Vec::new(),
            quantity: 1,
            selected: None,
            root: None,
            limits: PlannerLimits::default(),
            show_bracket: false,
            language: lang::code(),
        }
    }

    fn build_tree(&mut self) {
        if let Some(item) = self.selected.clone() {
            self.root = Some(tree::build(&item, self.quantity.max(1), &self.recipes, &self.limits));
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
