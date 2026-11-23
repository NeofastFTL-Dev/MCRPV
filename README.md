# MCRPV — Minecraft Recipe Tree Viewer (Linux)

The native Linux build of MCRPV: one item expanded into its **full recursive crafting tree**, for
modded Minecraft packs. Written in Rust with egui, so it runs on Linux without Wine, a .NET runtime
or a Windows prefix.

This is the `linux` branch. The original WPF/.NET app (and its Wine fallback) is untouched on
`main`, together with the full history of how it got here.

## What works on this branch

| Feature | Detail |
|---|---|
| Recipe tree | unlimited depth, quantities multiply down the tree, cycles detected, 3000-node / depth-16 planner budget |
| Base → end plan | base materials plus crafting steps grouped per machine, in craft order, with cut-branch warnings |
| Machine picker | every recipe for an item per JEI tab, ☆/★ per-item machine preference, remembered manual choices |
| Recipe layers | per step: base (📦), runtime dump (🧱) or pack (🧩), from the recipe source |
| Search | filter on item id or display name, click or Enter to build |
| Bracket text | the tree as indented text for pasting into Discord or notes |
| Languages | Dutch and English, from the same `Lang.*.cs` tables |
| Item names | `names.json` names with a Title Case fallback |
| Settings | shares `settings.json` with the .NET app, including ★ preferences, recipe choices and the picker filter |

Still on the .NET side and not on this branch yet: JEI bookmarks and the shopping list, instance
discovery, the mod/pack index with provenance and the dead-patch/changes views, and the PNG export.
`main` keeps those until they are ported.

## Build and run

```bash
sudo apt-get install libgl-dev libxkbcommon-dev libwayland-dev   # Debian/Ubuntu
cd rust
cargo run --release
```

`cargo run --release -- --data ../rust` uses the bundled sample `rust/recipes.json`; without
`--data` the app reads `%LOCALAPPDATA%\MCRPV` on Windows and `$XDG_DATA_HOME/mcrpv`
(`~/.local/share/mcrpv`) elsewhere, migrating an old `PO3RecipeTree` folder on first start.

Headless checks:

```bash
cd rust
cargo test --locked
cargo run --release -- --selftest   # 86 checks: names, both language tables, parsing, planner,
                                    # step plan, layers, search, machine preferences, settings
```

## CI

`.github/workflows/build.yml` builds, tests, self-tests and clippies the crate on every push to
`linux` (and on pull requests), checks that the result is a Linux ELF binary, and uploads it as the
`mcrpv-linux-x86_64` artifact.

## Repository layout

| Path | What |
|---|---|
| `rust/src/main.rs` | egui window: filter, tree, step plan, machine picker, ★ overview |
| `rust/src/tree.rs` | planner (`RecipePlanner` rules: budget, depth, quantity cap, gathering, sane options, ★/choices) |
| `rust/src/plan.rs` | step plan (`BuildStepPlan`) |
| `rust/src/layers.rs` | recipe layers (`RecipeLayers`) |
| `rust/src/picker.rs` | machine picker and ★ preference list |
| `rust/src/settings.rs` | `settings.json`, shared with the .NET app |
| `rust/src/model.rs`, `names.rs`, `search.rs`, `lang.rs` | data layer |
| `rust/recipes.json` | the bundled sample recipe set |
| `rust/lang/{nl,en}.json` | the language tables |

## License

MIT — see [LICENSE](LICENSE). Recipe inputs belong to Project Ozone 3 and the individual mod
authors; the app only renders whatever is in the recipe set.
