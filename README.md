# MCRPV — Minecraft Recipe Tree Viewer

A WPF desktop app that expands a single item into its **full recursive crafting tree**, built for
modded Minecraft packs. It was written for the mythic recipes of *Project Ozone 3* (1.12.2), where a
late-game item can sit six or seven crafting steps away from anything you can actually mine — and the
whole point is to see that chain at once instead of clicking through JEI.

Nothing in the app is specific to that pack: it reads a plain `recipes.json`, or builds one itself
from a JEIExporter dump or straight out of a Minecraft instance. The UI is in Dutch; the code and this
README are in English.

## What it does

| Feature | Detail |
|---|---|
| Recursive tree | unlimited depth, quantities multiply down the whole tree, cycle detection instead of infinite recursion |
| Two views | an interactive `TreeView` for exploring and a monospace bracket tree for pasting into Discord or notes |
| Filter | highlights matches, bolds them and auto-expands the branches that contain them |
| Base → end plan | base materials plus the crafting steps in order, with real totals |
| Shopping list | adds up the base materials of every JEI bookmark at once |
| JEI bookmarks | pick a bookmark and see its step plan; the bookmark file is watched, so it stays live while Minecraft runs |
| Watch folder | reloads when a new `recipes.json` (or dump) appears |
| Export | clipboard, `.txt` and `.png`, the last rendered from the bracket text |
| Themes | dark (Catppuccin Mocha) and a purple/cream light theme |
| Real item names | names come from the instance's lang files (`mods/*.jar`, resource packs), falling back to title case |
| Index & packs | builds a per-pack recipe index (`pack.json` + jar shards + base + pack layer), switchable profiles |
| Provenance, dead patches, changes | which source supplied a recipe, which patches no longer apply, and what changed between two sets |

## Using it

**Watch folder.** Pick a folder with *Kies map…* and press *Start Watch*: any `.json` that appears
or changes there is copied to `%LOCALAPPDATA%\MCRPV\recipes.json` and becomes the active set, so a
JEIExporter dump can drop in while the app is open.

**Instances and JEI bookmarks.** *🗂 Instance…* lists every instance it can find — `.minecraft`,
Prism, MultiMC, PolyMC, CurseForge, Modrinth and ATLauncher — newest first, with its last-modified
time. For each it looks at `config/jei/bookmarks.ini`, the per-world `saves/<world>/jei/` files and a
`bookmarks.ini` in the instance root. The choice is remembered in
`%LOCALAPPDATA%\MCRPV\settings.json`; with one instance it is used automatically, with several the
picker opens. *📥 Laad bookmarks…* also accepts `bookmarks.json`, a plain list (one item per line,
optionally `x4`) or a JEIExporter dump.

**Three views** on the right: *🌳 Tree*, *📋 Basis → eind* (base materials plus the steps in order,
with totals) and *🛒 Boodschappenlijst*, which adds up the base materials of every loaded bookmark.

**Live bookmarks.** Once a bookmark file is loaded it is watched: JEI rewrites `bookmarks.ini` on every
change, so an in-game bookmark shows up within about a second. Reloads wait for a short quiet period
so a half-written file is never read, the selected bookmark stays selected, and an open shopping list
is re-totalled.

**Recipes from the instance.** Choosing an instance also looks for a JEIExporter dump in `exports/`,
`jei_exports/`, `dumps/`, `config/jeiexporter/exports/`, `config/jei/dumps/` or a `recipes/` folder,
including the split per-category files; it is converted in place. Only a **newer** dump is re-read, so
hand-made additions to the active set are not silently overwritten.

## Repository layout

| Path | What |
|---|---|
| `MCRPV/` | the WPF project (`net8.0-windows`, assembly `MCRPV`) |
| `MCRPV/MainWindow.xaml`, `MCRPV/MainWindow.xaml.cs` | the main window: tree, bracket view, plan, exports, themes |
| `MCRPV/RecipePlanner.cs`, `MCRPV/RecipeLayers.cs`, `MCRPV/RecipeIndex.cs` | plan building, pack layering, the recipe index |
| `MCRPV/ModRecipeScanner.cs`, `MCRPV/ModScanWindow.xaml` | scanning a Minecraft instance / mod jars for recipes |
| `MCRPV/IndexCommands.cs` | the command-line modes (`--index`, `--profiles`, `--herkomst`, …) |
| `MCRPV/SelfTest.cs` | the built-in self-check (`--selftest`) |
| `MCRPV/Lang.nl.cs`, `MCRPV/Lang.en.cs` | every user-facing string |
| `MCRPV/recipes.json` | the demo recipe set, copied next to the exe on every build |
| `build-wine.sh`, `run-wine.sh` | build and launch the published exe under Wine |

## Running it

**A fresh clone contains source only — there is no exe in git.** Build once, then run:

```bash
./build-wine.sh     # -> MCRPV/dist/wine-x64/MCRPV.exe (self-contained, needs Wine on Linux)
./run-wine.sh       # launches that exe in ~/.wine-apps
```

On Windows, open `MCRPV/MCRPV.csproj` in Visual Studio 2022 (**.NET desktop development**) and press F5.

### The one thing that trips people up on Linux

`dotnet run` in a clone fails with:

```
error NETSDK1100: To build a project targeting Windows on this operating system,
set the EnableWindowsTargeting property to true.
```

The project targets `net8.0-windows` (WPF only exists on Windows), so on Linux every build step needs
the Windows targeting pack. `build-wine.sh` passes it for you; by hand it is:

```bash
dotnet build MCRPV/MCRPV.csproj -p:EnableWindowsTargeting=true
# or a real self-contained exe:
dotnet publish MCRPV/MCRPV.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableWindowsTargeting=true -o dist/wine-x64
```

Running it also needs Wine plus the core fonts in the prefix (`winetricks -q corefonts` once), because
WPF aborts while shaping text without them. `./run-wine.sh` sets the prefix (`~/.wine-apps`).

### The Wine build route

`build-wine.sh` does what the shipping exe does on this machine: the Linux SDK restores the packages
(NuGet's signed-package verification cannot pass inside Wine), the Windows SDK in the prefix restores
again from that cache, then publishes Release / win-x64 / self-contained / single file.
`./build-wine.sh --dry-run` prints the three commands without running them.

## Command line

| Argument | What it does |
|---|---|
| `--selftest` | runs 85 checks (tree, zoom, plan, index, provenance, languages) without a window and exits with the failure count |
| `--index` | builds the recipe index for a pack; `--diff` / `--reindex=pack` refreshes only the pack layer and the fold, `--apply` writes the result to `recipes.json` |
| `--scan --instance=<dir> --modes=...` | scans an instance for recipes |
| `--convert --instance=<dir>` | converts a JEIExporter dump found in the instance |
| `--plan=<item> --qty=<n>` | prints the base → end plan for one item |
| `--profiles` | lists the profiles of every indexed pack |
| `--use-pack=<name>` | makes an indexed pack the active recipe set |
| `--herkomst=<item>` / `--provenance=<item>` | explains where one recipe came from |
| `--dead-patches` | lists patches that no longer apply |

## Where the data lives

Everything the app writes goes to `%LOCALAPPDATA%\MCRPV`:

| File | What |
|---|---|
| `recipes.json` | the active recipe set (copied in from the instance, an index build or the exe folder) |
| `crash.log` | any unhandled exception, with a timestamp and the version |
| `selftest-report.txt` | the last `--selftest` report |
| index files | the built pack index and profiles |

Before the rename this folder was `%LOCALAPPDATA%\PO3RecipeTree`; it is moved across on the first
start, so the index and the active recipe set are kept.

`recipes.json` is a dictionary keyed by item name, compared case-insensitively:

```json
{
  "iron_pickaxe": {
    "Ingredients": [
      { "Item": "iron_ingot", "Count": 3 },
      { "Item": "stick", "Count": 2 }
    ]
  }
}
```

An item with no entry (or an empty `Ingredients` list) is a base item and the tree stops there.
`Count` is the amount for one craft of the parent; the app multiplies it by the quantity of the node
above.

## Troubleshooting

* **Nothing happens after `./run-wine.sh`** — build first: a clone has no `dist/`. If it still shows
  no window, check `%LOCALAPPDATA%\MCRPV\crash.log` (under Wine:
  `~/.wine-apps/drive_c/users/<you>/AppData/Local/MCRPV/crash.log`).
* **`NETSDK1100`** — add `-p:EnableWindowsTargeting=true`, or use `./build-wine.sh`.
* **The tree stops too early** — that ingredient has no entry in the active `recipes.json`, so it is
  treated as a base item.
* **The PNG is cut off** — the export uses a fixed monospace layout; very wide trees are rendered at
  full size, so zoom out when viewing.

## Rust port (branch `rust-port`)

The app is moving to Rust + egui so it runs natively on Linux, Windows and macOS without Wine. The
.NET app on `main` stays exactly as it is until the port reaches parity — nothing working is deleted
before that.

**Stage 1 (this branch).**

* Data layer: the `recipes.json` model with a case-insensitive index (`rust/src/model.rs`), real item
  names from `names.json` with a Title Case fallback (`rust/src/names.rs`), and both language tables
  lifted verbatim out of `Lang.nl.cs` / `Lang.en.cs` into `rust/lang/{nl,en}.json` (294 keys each).
* The planner (`rust/src/tree.rs`) with the same rules as `RecipePlanner.cs`: 3000-node budget, depth
  16, 9999 quantity cap, gathering recipes ending the chain, recipes that eat their own output or
  form a reverse pair skipped, and the base-material budget.
* An egui main window: filter with live results, quantity, the recursive recipe tree with step/base
  totals, the bracket text, and an NL/EN switch.
* A headless self-test: `--selftest` (48 checks, what CI runs).

```bash
cd rust
cargo run --release                        # data from ~/.local/share/mcrpv
cargo run --release -- --data ../MCRPV     # the sample recipes.json in this repo
cargo run --release -- --selftest
```

The data folder is the one the .NET app used — `%LOCALAPPDATA%\MCRPV` on Windows,
`$XDG_DATA_HOME/mcrpv` (or `~/.local/share/mcrpv`) elsewhere — and an existing `PO3RecipeTree` folder
is moved across on first start.

**Stage 2** covers everything still on the .NET side: the step-plan and layer views
(`RecipeLayers`, `RecipePlanner`, `RecipeOverlay`), the machine picker with ★ preferences, the
shopping list over JEI bookmarks, instance and bookmark discovery with the watch folder, the
per-pack mod index (`pack.json` + jar shards) with provenance, the dead-patch and changes views, and
the exports (clipboard, `.txt`, PNG bracket diagram) plus the light/dark themes. Only after that does
`main` switch over.

## Credits

* UI: **WPF** on .NET 8 (MIT), with no NuGet dependencies at all — the whole app is the `MCRPV/`
  project and the .NET base class library.
* Recipe inputs belong to **Project Ozone 3** and the individual mod authors; the app only renders
  whatever is in the recipe set.
* Recipe dumps come from **JEIExporter** for **Just Enough Items** (JEI) on Minecraft 1.12.2, or from
  the instance's own jars.
* The 256 px application icon was drawn for this project.

## License

MIT - see [LICENSE](LICENSE).
