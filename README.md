# PO3 Mythic Recipe Tree

A small WPF desktop app that expands a single item into its **full recursive crafting tree**.
It was written for the mythic recipes of *Project Ozone 3* (1.12.2), where a late-game item can sit
six or seven crafting steps away from anything you can actually mine — and the whole point is to see
that chain at once instead of clicking through JEI.

Recipes live in a plain `recipes.json`, so the same app works for any pack whose recipes you can
express as "item → list of ingredients".

## How it works

```
recipes.json ──► Dictionary<string, Recipe> (case-insensitive)
                        │
     item + quantity ───┴──► BuildNode(item, qty, visited)
                                │  qty of an ingredient = Count × qty of its parent
                                ├─ no recipe / empty Ingredients  → leaf (ore, log, cobble, …)
                                └─ item already on this branch    → "♻️ (cycle)", stops there
                        │
        ┌───────────────┴────────────────┐
        │                                │
  TreeView (left)                bracket text (right)
  expand / collapse              ├─ └─ box drawing, copy, .txt, PNG
```

Two views of the same tree are built side by side: an interactive `TreeView` you can filter and
collapse, and a monospace bracket tree that is what you actually paste into Discord or a notes file.
The PNG export renders that same text block with `RenderTargetBitmap`, so no screenshot tool is
needed to share a tree.

## Features

| Feature | Detail |
|---|---|
| Recursive tree | unlimited depth, quantities multiply down the whole tree |
| Cycle detection | a recipe that loops back marks the node `♻️ (cycle)` instead of recursing forever |
| Quantity scaling | type `4` for a stack of four and every ingredient is multiplied accordingly |
| Filter | highlights matches in yellow, bolds them and auto-expands the branches that contain them |
| Expand / collapse all | one click each |
| Copy text | the bracket tree to the clipboard |
| Save `.txt` | `<item>_recipe_tree.txt` |
| Save `.png` | `<item>_recipe_tree.png`, rendered from the bracket text in Consolas |
| Dark / light theme | dark (Catppuccin Mocha palette) on start, purple/cream light theme otherwise |

## Repository layout

| Path | What |
|---|---|
| `PO3RecipeTree/` | the WPF project (`net8.0-windows`, assembly name `PO3 Mythic Recipe Tree`) |
| `PO3RecipeTree/MainWindow.xaml` | layout and both themes' brushes |
| `PO3RecipeTree/MainWindow.xaml.cs` | tree building, bracket rendering, filter, exports |
| `PO3RecipeTree/recipes.json` | the recipe database, copied next to the exe on every build |
| `PO3RecipeTree/dist/wine-x64/` | the published single-file build (not committed) |

## The recipe file

`recipes.json` is a dictionary keyed by item name; keys are compared case-insensitively, so
`Diamond` and `diamond` are the same item.

```json
{
  "iron_pickaxe": {
    "Ingredients": [
      { "Item": "iron_ingot", "Count": 3 },
      { "Item": "stick", "Count": 2 }
    ]
  },
  "stick": {
    "Ingredients": [ { "Item": "plank", "Count": 2 } ]
  }
}
```

Rules:

* An item with no entry, or with an empty `Ingredients` list, is a **base item** — the tree stops there.
  Ores, logs, cobblestone and anything else you cannot craft should simply be left out.
* `Count` is the amount for **one** craft of the parent. The app multiplies it by the quantity of the
  node above, so a `×4` pickaxe asks for 12 ingots and 8 sticks.
* Names are shown exactly as written in the JSON. There is no tag/ore-dictionary support: if a recipe
  accepts "any plank", pick one and use its name.
* If no `recipes.json` sits next to the exe, the app writes a small starter file there so it can always
  start; replace it with your own.

The bundled file holds a handful of demo recipes only. A full PO3 database can be generated with the
companion JEIConverter project from a JEIExporter dump.

## Building on this machine

The app is WPF, so it only targets Windows (`net8.0-windows`). Two toolchains exist on this machine:

* `~/.dotnet/dotnet` — Linux SDKs **8.0.428** and **10.0.404** (`dotnet` on `PATH` resolves to 10.0.404)
* `~/.wine-dotnet` — the same SDKs installed inside a Wine prefix, which is how the Windows build is
  produced here

### Windows / Visual Studio

Open `PO3RecipeTree/PO3RecipeTree.csproj` (or the folder) in Visual Studio 2022 with the
**.NET desktop development** workload, then press F5.

### Cross-build on Linux (the route used for `dist/wine-x64`)

The repository carries the script that does the whole sequence below:

```bash
./build-wine.sh              # -> PO3RecipeTree/dist/wine-x64/PO3 Mythic Recipe Tree.exe
./build-wine.sh --dry-run    # print the three commands instead
```

By hand, restore with the Linux SDK first — restore has to fetch the Windows targeting/desktop packs, and the
Wine-side NuGet cannot complete HTTPS requests on this machine.

```bash
cd PO3RecipeTree
~/.dotnet/dotnet restore PO3RecipeTree.csproj -r win-x64 -p:EnableWindowsTargeting=true
```

Then publish with the Wine SDK, which has the real Windows targeting packs:

```bash
WINEPREFIX=~/.wine-dotnet wine 'C:\dotnet\dotnet.exe' publish PO3RecipeTree.csproj \
  -c Release -r win-x64 --self-contained true --no-restore \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -o dist/wine-x64
```

That produces `dist/wine-x64/PO3 Mythic Recipe Tree.exe` (about 160 MB, everything bundled, no .NET
install needed on the target) plus `recipes.json`, which must stay in the same folder as the exe.
The publish step takes a few minutes under Wine; the `--no-restore` flag is deliberate, the RID-specific
assets come from the Linux restore above.

### Running it under Wine

```bash
./run-wine.sh                # launches the published exe in ~/.wine-apps
```

which runs the equivalent of:

```bash
WINEPREFIX=~/.wine-apps wine "PO3RecipeTree/dist/wine-x64/PO3 Mythic Recipe Tree.exe"
```

WPF needs core fonts in the prefix or it aborts while shaping text at startup; install them once
with `WINEPREFIX=~/.wine-apps winetricks -q corefonts`.

A 256 px icon lives at `~/.local/share/icons/hicolor/256x256/apps/po3-mythic-recipe-tree.png` and is
used by the Linux desktop entry. Note that `ApplicationIcon` in the `.csproj` is empty, so the exe
itself still ships without an embedded icon; setting it to an `.ico` would fix the Windows taskbar.

## Screenshots

Screenshots belong in `docs/screenshots/`; none are committed yet. Three captures cover the app:

| File to add | What to capture |
|---|---|
| `docs/screenshots/main-window-dark.png` | default dark window, `diamond` ×1 built, tree on the left and bracket tree on the right |
| `docs/screenshots/filter.png` | filter box with `ingot` typed in, matches highlighted and auto-expanded |
| `docs/screenshots/png-export.png` | the saved PNG of an `iron_pickaxe` tree |

```html
<!-- Uncomment once the files exist:
![Main window](docs/screenshots/main-window-dark.png)
![Filter highlighting](docs/screenshots/filter.png)
![Exported PNG](docs/screenshots/png-export.png)
-->
```

## Troubleshooting

* **Nothing happens when you press *Build Tree*** — the name must match a key in `recipes.json`
  exactly (apart from case). `diamond` works, `Diamond Ore` does not.
* **The tree stops too early** — the ingredient has no entry in `recipes.json`, so it is treated as a
  base item. Add it if it is craftable.
* **The PNG is cut off** — the export uses a monospace layout at 14 px; extremely wide trees are
  rendered at their full size, so zoom out when viewing rather than expecting wrapping.

## Credits

* UI: **WPF** on .NET 8 (MIT), no third-party NuGet packages — the whole app is `MainWindow.xaml`
  plus one code-behind file.
* Recipe data: recipe inputs belong to **Project Ozone 3** and the individual mod authors; this app
  only renders whatever is in `recipes.json`.
* Recipe dumps for the full database come from **JEIExporter** for JEI (1.12.2).
* Emoji in labels and messages come from **Segoe UI Emoji**.
* The 256 px application icon was drawn for this project.

## License

MIT - see [LICENSE](LICENSE).
