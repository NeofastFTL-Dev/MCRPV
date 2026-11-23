using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Mcrpv
{
    /// <summary>
    /// Zelfcontrole zonder venster:
    ///   "MCRPV.exe" --selftest
    /// Rekent de bracket-zoom na (inclusief de waarden waarop de app vroeger vastliep) en
    /// bouwt een stappenplan uit een vaste set testrecepten. Elke fout komt met de
    /// verwachting erbij; exitcode 0 = alles goed, 1 = minstens één controle mislukt.
    /// </summary>
    public static class SelfTest
    {
        private static readonly StringBuilder Report = new();
        private static int _checks;
        private static int _failures;
        private static int _warnings;

        public static int Run()
        {
            _checks = 0;
            _failures = 0;
            _warnings = 0;
            Report.Clear();

            // Zonder dit print cmd de vinkjes en pijltjes als vraagtekens.
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var version = typeof(SelfTest).Assembly.GetName().Version;
            Report.AppendLine($"MCRPV · zelftest · v{version}");
            Report.AppendLine();

            Section("Bracket-zoom");
            CheckBracketZoom();

            Section("PNG-export");
            CheckBracketPng();

            Report.AppendLine();
            Section("Stappenplan");
            CheckStepPlan();

            Report.AppendLine();
            Section("Afkapgrenzen");
            CheckLimits();

            Report.AppendLine();
            Section("Annuleren");
            CheckCancellation();

            Section("Zelf indexeren (zonder JEIExporter)");
            CheckSelfIndexing();

            Section("Index (fase 1)");
            CheckIndex();

            Report.AppendLine();
            Section("Lagen (fase 2–5)");
            CheckLayers();

            Report.AppendLine();
            Section("Echte recepten (recipes.json)");
            CheckRealRecipes();

            Report.AppendLine();
            Section("Talen");
            CheckLanguages();

            Report.AppendLine();
            string summary = _failures == 0
                ? $"✔ alles goed: {_checks} controle(s) geslaagd"
                : $"✖ {_failures} van {_checks} controle(s) mislukt";
            if (_warnings > 0) summary += $" · {_warnings} waarschuwing(en)";
            Report.AppendLine(summary);

            string text = Report.ToString();
            Console.Write(text);

            try
            {
                string data = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCRPV");
                Directory.CreateDirectory(data);
                string path = Path.Combine(data, "selftest-report.txt");
                File.WriteAllText(path, text);
                Console.WriteLine("verslag: " + path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("let op: verslag niet weggeschreven (" + ex.Message + ")");
            }

            return _failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Elke zichtbare tekst staat in alle talen; een sleutel die alleen in het Nederlands
        /// bestaat zou in het Engels stil op Nederlands terugvallen.
        /// </summary>
        private static void CheckLanguages()
        {
            foreach (var code in Lang.Codes)
            {
                var missing = Lang.MissingIn(code).ToList();

                Check($"taal {Lang.NativeName(code)}: alle sleutels aanwezig",
                      () => missing.Count == 0,
                      missing.Count == 0 ? "volledig" : "ontbreekt: " + string.Join(", ", missing.Take(5)));
            }

            Check("taal: onbekende code valt terug op Nederlands",
                  () => Lang.Normalize("xx") == Lang.Default && Lang.Normalize(null) == Lang.Default,
                  Lang.Default);
        }

        // ========== Hulpmiddelen ==========

        private static void Section(string title)
        {
            int dashes = Math.Max(8, 52 - title.Length);
            Report.AppendLine("── " + title + " " + new string('─', dashes));
        }

        private static void Pass(string message)
        {
            _checks++;
            Report.AppendLine("  ✔ " + message);
        }

        private static void Fail(string message, string detail)
        {
            _checks++;
            _failures++;
            Report.AppendLine("  ✖ " + message);
            if (!string.IsNullOrWhiteSpace(detail))
                Report.AppendLine("      " + detail);
        }

        private static void Check(string message, Func<bool> test, string expectation)
        {
            try
            {
                if (test()) Pass(message);
                else Fail(message, "verwacht: " + expectation);
            }
            catch (Exception ex)
            {
                Fail(message, "uitzondering: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string Fmt(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        // ========== Bracket-zoom ==========

        /// <summary>
        /// De PNG-knop exporteert het bracket-diagram. Hier wordt hetzelfde pad bewandeld:
        /// diagram bouwen, in delen knippen, een deel echt renderen en het PNG weer inlezen.
        /// Zo valt een lege of onleesbare export op zonder dat er iemand op de knop klikt.
        /// </summary>
        private static void CheckBracketPng()
        {
            var root = new RecipeNode { Name = "gear", Quantity = 1, Category = "Crafting", OptionCount = 1 };

            for (int branch = 0; branch < 3; branch++)
            {
                var part = new RecipeNode { Name = "part" + branch, Quantity = 2, Category = "Furnace", OptionCount = 1 };
                part.Children.Add(new RecipeNode { Name = "ore" + branch, Quantity = 4 });
                part.Children.Add(new RecipeNode { Name = "dust" + branch, Quantity = 1 });
                root.Children.Add(part);
            }

            var diagram = BracketDiagram.Build(root, new System.Windows.Controls.Border(), "");
            diagram.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            diagram.Arrange(new System.Windows.Rect(diagram.DesiredSize));

            var plan = PngSlicing.PlanFor(diagram.Width, diagram.Height, BracketDiagram.RowPitch);

            Check("diagram levert een of meer PNG-delen",
                  () => plan.Count >= 1 && plan.Width > 0 && plan.Height > 0,
                  $"deel ≥ 1, {plan.Width}×{plan.Height} px");

            Check("PNG-delen blijven binnen de encoder-limiet",
                  () => plan.Width <= PngSlicing.MaxWidth
                        && plan.Parts.All(p => p.Height <= PngSlicing.MaxPartHeight)
                        && plan.Parts.All(p => (long)plan.Width * p.Height <= PngSlicing.MaxPixels),
                  $"≤ {PngSlicing.MaxWidth}×{PngSlicing.MaxPartHeight} px en ≤ {PngSlicing.MaxPixels:N0} px");

            byte[] png = Array.Empty<byte>();

            Check("het diagram wordt als PNG gerenderd", () =>
            {
                using var stream = new MemoryStream();
                PngExport.RenderPart(diagram, plan, 0, System.Windows.Media.Brushes.Black, stream);
                png = stream.ToArray();

                var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                    new MemoryStream(png),
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                var frame = decoder.Frames[0];
                return frame.PixelWidth == plan.Width && frame.PixelHeight == plan.Parts[0].Height;
            }, $"{plan.Width}×{plan.Parts[0].Height} px");

            Check("het PNG bevat de vakjes en lijnen (niet leeg)",
                  () => DrawnPixels(png) > 100,
                  "meer dan 100 getekende pixels");

            Check("voorbeeld-PNG bewaard voor controle", () => SavePngSample(png), "selftest-bracket.png");
        }

        /// <summary>Pixels die niet (bijna) zwart zijn: de vakjes, randen en tekst.</summary>
        private static int DrawnPixels(byte[] png)
        {
            try
            {
                var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                    new MemoryStream(png),
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                var frame = decoder.Frames[0];
                int stride = frame.PixelWidth * 4;
                var pixels = new byte[stride * frame.PixelHeight];
                frame.CopyPixels(pixels, stride, 0);

                int drawn = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] > 24 || pixels[i + 1] > 24 || pixels[i + 2] > 24) drawn++;

                return drawn;
            }
            catch
            {
                return 0;
            }
        }

        private static bool SavePngSample(byte[] png)
        {
            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                           "MCRPV");
                Directory.CreateDirectory(data);
                File.WriteAllBytes(Path.Combine(data, "selftest-bracket.png"), png);
                return png.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void CheckBracketZoom()
        {
            // De zoomwaarden uit crash.log: hier ging het in v1.33 en eerder mis.
            foreach (double zoom in new[] { 1.9487171000000014, 1.6, 4.0 })
            {
                double value = zoom;
                Check($"klik op een knoop bij {Fmt(value * 100)}% zoomt zonder fout",
                      () =>
                      {
                          double next = BracketZoom.Target(value);
                          return next >= BracketZoom.Min && next <= BracketZoom.Max && next >= value - 1e-9;
                      },
                      $"{Fmt(value)} ≤ uitkomst ≤ {Fmt(BracketZoom.Max)}");
            }

            bool rangeOk = true, noZoomOut = true, notTooFar = true, atLeastFull = true;
            string problem = "";

            for (double from = BracketZoom.Min; from <= BracketZoom.Max + 1e-9; from += 0.01)
            {
                double to;
                try
                {
                    to = BracketZoom.Target(from);
                }
                catch (Exception ex)
                {
                    rangeOk = false;
                    problem = $"{Fmt(from)} → uitzondering: {ex.Message}";
                    break;
                }

                if (to < BracketZoom.Min - 1e-9 || to > BracketZoom.Max + 1e-9)
                {
                    rangeOk = false;
                    problem = $"{Fmt(from)} → {Fmt(to)} valt buiten {Fmt(BracketZoom.Min)}–{Fmt(BracketZoom.Max)}";
                }

                if (to < from - 1e-9)
                {
                    noZoomOut = false;
                    problem = $"{Fmt(from)} → {Fmt(to)} zoomt uit";
                }

                if (from >= 1.0 && to > from * BracketZoom.ClickStep + 1e-6)
                {
                    notTooFar = false;
                    problem = $"{Fmt(from)} → {Fmt(to)} is meer dan {Fmt(BracketZoom.ClickStep)}× dichterbij";
                }

                if (from < 1.0 && to < 1.0 - 1e-9)
                {
                    atLeastFull = false;
                    problem = $"{Fmt(from)} → {Fmt(to)} blijft onder 100%";
                }
            }

            Check($"klik-zoom blijft tussen {Fmt(BracketZoom.Min * 100)}% en {Fmt(BracketZoom.Max * 100)}% (391 waarden)",
                  () => rangeOk, problem);
            Check("klik-zoom zoomt nooit verder uit", () => noZoomOut, problem);
            Check($"klik-zoom gaat hoogstens {Fmt(BracketZoom.ClickStep)}× dichterbij", () => notTooFar, problem);
            Check("klik-zoom gaat vanuit ver uitgezoomd eerst naar 100%", () => atLeastFull, problem);

            Check("Clamp houdt 0% op de ondergrens", () => Math.Abs(BracketZoom.Clamp(0) - BracketZoom.Min) < 1e-9, Fmt(BracketZoom.Min));
            Check("Clamp houdt 900% op de bovengrens", () => Math.Abs(BracketZoom.Clamp(9) - BracketZoom.Max) < 1e-9, Fmt(BracketZoom.Max));
            Check("Clamp maakt van een onbekende waarde (NaN) 100%", () => Math.Abs(BracketZoom.Clamp(double.NaN) - 1.0) < 1e-9, "1");
        }

        // ========== Stappenplan ==========

        private static void CheckStepPlan()
        {
            var recipes = TestRecipes();
            RecipeNode root;

            try
            {
                root = Build("iron_pickaxe", 2, recipes);
            }
            catch (Exception ex)
            {
                Fail("boom bouwen voor 2 houwelen", "uitzondering: " + ex.Message);
                return;
            }

            Check("2 houwelen vragen 6 ijzer (3 per stuk)", () => QuantityOf(root, "iron_ingot") == 6,
                  "iron_ingot × 6");
            Check("2 houwelen vragen 4 stokken (2 per stuk)", () => QuantityOf(root, "stick") == 4,
                  "stick × 4");
            Check("4 stokken vragen 2 planken (recept levert 4)", () => QuantityOf(root, "plank") == 2,
                  "plank × 2");
            Check("2 planken vragen 1 log (recept levert 4)", () => QuantityOf(root, "log") == 1,
                  "log × 1");

            // 10 planken = 3 keer craften van 4 → 3 logs
            Check("10 planken vragen 3 logs (naar boven afronden)", () => QuantityOf(Build("plank", 10, recipes), "log") == 3,
                  "log × 3");

            Check("item zonder recept levert een lege boom", () => Build("nothing", 3, recipes).Children.Count == 0,
                  "0 kinderen");

            string plan = "";
            Check("stappenplan bouwt zonder fout", () => { plan = RecipePlanner.BuildStepPlan(root, recipes); return plan.Length > 0; },
                  "niet-lege tekst");

            string flat = plan.ToLowerInvariant().Replace(' ', '_');
            Check("stappenplan telt 4 craft-stappen", () => flat.Contains("4_craft-stap"), "… 4 craft-stap(pen) …");
            Check("stappenplan telt 2 basis-materialen (log en raw_iron)", () => flat.Contains("2_basis-materiaal"),
                  "… 2 basis-materiaal/-materialen …");
            Check("stappenplan noemt de basis-materialen", () => flat.Contains("raw_iron") && flat.Contains("log"),
                  "raw_iron en log in de lijst");
            Check("stappenplan zet Smelten vóór Crafting (basis → eindproduct)",
                  () => plan.IndexOf("Smelting", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        plan.IndexOf("Crafting", StringComparison.OrdinalIgnoreCase) >
                        plan.IndexOf("Smelting", StringComparison.OrdinalIgnoreCase),
                  "Smelting eerder dan Crafting");

            // Kringetje (a → b → a) mag niet blijven doorlopen en moet als cycle gemarkeerd zijn.
            var cycleRecipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = new Recipe { Category = "Crafting", Ingredients = { new Ingredient { Item = "b", Count = 1 } } },
                ["b"] = new Recipe { Category = "Crafting", Ingredients = { new Ingredient { Item = "a", Count = 1 } } },
            };

            var stopwatch = Stopwatch.StartNew();
            bool cycleCaught = false;
            string cyclePlan = "";
            try
            {
                RecipeNode cycleRoot = Build("a", 1, cycleRecipes);
                cycleCaught = HasCycleNode(cycleRoot);
                cyclePlan = RecipePlanner.BuildStepPlan(cycleRoot, cycleRecipes);
            }
            catch (Exception ex)
            {
                Fail("kringetje a → b → a blijft niet hangen", "uitzondering: " + ex.Message);
                return;
            }
            stopwatch.Stop();

            Check("kringetje a → b → a wordt als cycle gemarkeerd", () => cycleCaught, "IsCycle ergens in de boom");
            Check("kringetje a → b → a blijft niet hangen", () => stopwatch.ElapsedMilliseconds < 2000,
                  "korter dan 2 s (duurde " + stopwatch.ElapsedMilliseconds + " ms)");
            Check("stappenplan van een kringetje is niet leeg", () => cyclePlan.Length > 0, "niet-lege tekst");
        }

        // ========== Afkapgrenzen ==========

        /// <summary>
        /// De grenzen uit de balk (beurten per stap, basis-materialen per item) moeten
        /// echt afkappen, en een ruimere grens moet dezelfde keten wél doorrekenen.
        /// </summary>
        private static void CheckLimits()
        {
            Check("standaard: 16 beurten per stap",
                  () => new PlannerLimits().MaxCraftsPerStep == PlannerLimits.DefaultMaxCraftsPerStep,
                  PlannerLimits.DefaultMaxCraftsPerStep.ToString());
            Check("standaard: 1 000 basis-materialen per item",
                  () => new PlannerLimits().BaseItemsPerProduct == PlannerLimits.DefaultBaseItemsPerProduct,
                  PlannerLimits.DefaultBaseItemsPerProduct.ToString());

            var low = new PlannerLimits { MaxCraftsPerStep = 0, BaseItemsPerProduct = -5 }.Clamped();
            Check("Clamp tilt te lage grenzen naar de ondergrens",
                  () => low.MaxCraftsPerStep == PlannerLimits.MinMaxCraftsPerStep &&
                        low.BaseItemsPerProduct == PlannerLimits.MinBaseItemsPerProduct,
                  $"{PlannerLimits.MinMaxCraftsPerStep} en {PlannerLimits.MinBaseItemsPerProduct}");

            var high = new PlannerLimits { MaxCraftsPerStep = 999_999, BaseItemsPerProduct = int.MaxValue }.Clamped();
            Check("Clamp tilt te hoge grenzen naar de bovengrens",
                  () => high.MaxCraftsPerStep == PlannerLimits.MaxMaxCraftsPerStep &&
                        high.BaseItemsPerProduct == PlannerLimits.MaxBaseItemsPerProduct,
                  $"{PlannerLimits.MaxMaxCraftsPerStep} en {PlannerLimits.MaxBaseItemsPerProduct}");

            var recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase)
            {
                ["gear"] = new Recipe { Category = "Crafting", Ingredients = { new Ingredient { Item = "scrap", Count = 64 } } },
                ["scrap"] = new Recipe { Category = "Crafting", Ingredients = { new Ingredient { Item = "ore", Count = 1 } } },
                ["top"] = new Recipe
                {
                    Category = "Crafting",
                    Ingredients = { new Ingredient { Item = "left", Count = 1 }, new Ingredient { Item = "right", Count = 1 } }
                },
            };

            Check("64 scrappen voor één tandwiel is te veel beurten en wordt afgekapt",
                  () => HasTruncatedNode(BuildWithLimits("gear", recipes, new PlannerLimits())),
                  "minstens één afgekapte knoop");

            Check("met een ruimere grens rekent hij dezelfde keten gewoon door",
                  () => !HasTruncatedNode(BuildWithLimits("gear", recipes, new PlannerLimits { MaxCraftsPerStep = 100 })),
                  "geen enkele afgekapte knoop");

            Check("basis-begroting van 1 kapt de tweede tak af",
                  () => HasTruncatedNode(BuildWithLimits("top", recipes, new PlannerLimits { BaseItemsPerProduct = 1 })),
                  "minstens één afgekapte knoop");

            Check("met de standaard-begroting blijven beide takken staan",
                  () => !HasTruncatedNode(BuildWithLimits("top", recipes, new PlannerLimits())),
                  "geen enkele afgekapte knoop");
        }

        private static RecipeNode BuildWithLimits(string item, IReadOnlyDictionary<string, Recipe> recipes, PlannerLimits limits)
        {
            int budget = RecipePlanner.MaxTreeNodes;
            return RecipePlanner.Build(item, 1, recipes, new Dictionary<string, string>(), new Dictionary<string, int>(), ref budget, limits);
        }

        private static bool HasTruncatedNode(RecipeNode node) =>
            node.IsTruncated || node.Children.Any(HasTruncatedNode);

        /// <summary>Kleine vaste receptenset: ijzer smelten, hout naar planken en stokken, een houweel.</summary>
        private static Dictionary<string, Recipe> TestRecipes() =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["raw_iron"] = new Recipe { Category = "Mining" },
                ["log"] = new Recipe(),
                ["plank"] = new Recipe
                {
                    Category = "Crafting", Output = 4,
                    Ingredients = { new Ingredient { Item = "log", Count = 1 } },
                },
                ["stick"] = new Recipe
                {
                    Category = "Crafting", Output = 4,
                    Ingredients = { new Ingredient { Item = "plank", Count = 2 } },
                },
                ["iron_ingot"] = new Recipe
                {
                    Category = "Smelting", Output = 1,
                    Ingredients = { new Ingredient { Item = "raw_iron", Count = 1 } },
                },
                ["iron_pickaxe"] = new Recipe
                {
                    Category = "Crafting", Output = 1,
                    Ingredients =
                    {
                        new Ingredient { Item = "iron_ingot", Count = 3 },
                        new Ingredient { Item = "stick", Count = 2 },
                    },
                },
            };

        // ========== Echte recepten ==========

        private const int RealSampleSize = 8;
        private const int MaxSaneNodes = 2000;
        private const int MaxSaneSteps = 300;
        private const long MaxSaneBaseQuantity = 250_000;

        /// <summary>Bekende PO3-items die de steekproef bij voorkeur pakt.</summary>
        private static readonly string[] WellKnownItems =
        {
            "iron_pickaxe", "diamond", "ender_pearl", "end_seeds", "glowstone", "obsidian", "piston", "iron_ingot",
        };

        /// <summary>
        /// Rekent een handvol echte items uit de set door en meldt een leeg, afgekapt of
        /// verdacht groot plan. Ontbreekt recipes.json, dan slaat de test dit onderdeel over
        /// (een verse installatie heeft nog geen set, dat is geen fout).
        /// </summary>
        // ========== Annuleren ==========

        private static void CheckCancellation()
        {
            string root = Path.Combine(Path.GetTempPath(), "po3-selftest-cancel-" + Guid.NewGuid().ToString("N"));
            try
            {
                string game = Path.Combine(root, "minecraft");
                Directory.CreateDirectory(Path.Combine(game, "mods"));

                using var stopped = new CancellationTokenSource();
                stopped.Cancel();

                var scan = ModRecipeScanner.Scan(game, Array.Empty<string>(),
                                                 Path.Combine(root, "mods.json"), null, null, stopped.Token);
                Check("scan meteen geannuleerd komt terug als gestopt, niet als fout",
                      () => scan.Cancelled && scan.Error == null && scan.Added == 0,
                      "Cancelled = true, Error = null, Added = 0");

                string dump = Path.Combine(root, "dump");
                Directory.CreateDirectory(dump);

                var import = RecipeDumpImporter.Import(dump, null, stopped.Token);
                Check("dump-inlezen meteen geannuleerd levert geen halve set op",
                      () => import.Cancelled && import.Recipes.Count == 0 && import.Files == 0,
                      "Cancelled = true, 0 recepten, 0 bestanden");

                // Halverwege annuleren: ✖ STOP moet bij het volgende bestand ingrijpen.
                string scripts = Path.Combine(root, "scripts");
                Directory.CreateDirectory(scripts);
                string line = string.Concat(Enumerable.Repeat("recipes.remove(<minecraft:stone>);\n", 20));
                for (int i = 0; i < 40; i++)
                    File.WriteAllText(Path.Combine(scripts, $"stop_{i:00}.zs"), line);

                int seen = 0;
                using var midScan = new CancellationTokenSource();
                var halfway = ModRecipeScanner.Scan(game, new[] { scripts },
                                                    Path.Combine(root, "mods2.json"), null,
                                                    _ => { if (Interlocked.Increment(ref seen) >= 4) midScan.Cancel(); },
                                                    midScan.Token);
                Check("scan stopt halverwege bij het volgende script",
                      () => halfway.Cancelled && halfway.Error == null,
                      "Cancelled = true zonder fout, na een paar scripts");

                string dump2 = Path.Combine(root, "dump2");
                Directory.CreateDirectory(dump2);
                for (int i = 0; i < 20; i++)
                    File.WriteAllText(Path.Combine(dump2, $"items_{i:00}.json"), "{}");

                int seenDump = 0;
                using var midDump = new CancellationTokenSource();
                var halfDump = RecipeDumpImporter.Import(dump2,
                    _ => { if (Interlocked.Increment(ref seenDump) >= 4) midDump.Cancel(); },
                    midDump.Token);
                Check("dump-inlezen stopt halverwege bij het volgende bestand",
                      () => halfDump.Cancelled && halfDump.Files < 20,
                      "Cancelled = true en minder dan 20 bestanden verwerkt");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ========== Zelf indexeren ==========

        /// <summary>
        /// Zonder JEIExporter moet de instance zélf genoeg opleveren: shaped recepten én
        /// machine-recepten uit de CraftTweaker-scripts.
        /// </summary>
        private static void CheckSelfIndexing()
        {
            string root = Path.Combine(Path.GetTempPath(), "po3-selftest-index-" + Guid.NewGuid().ToString("N"));
            try
            {
                string game = Path.Combine(root, "minecraft");
                Directory.CreateDirectory(Path.Combine(game, "mods"));

                string scripts = Path.Combine(game, "scripts");
                Directory.CreateDirectory(scripts);

                File.WriteAllText(Path.Combine(scripts, "test.zs"), string.Join("\n", new[]
                {
                    "recipes.addShaped(<minecraft:stick> * 4, [[<minecraft:planks>, null], [null, <minecraft:planks>]]);",
                    "mods.botania.RuneAltar.addRecipe(<minecraft:diamond> * 2, [<minecraft:coal>, <minecraft:coal>], 5000);",
                    "mods.tconstruct.Melting.addRecipe(<liquid:iron> * 144, <minecraft:iron_ore>);",
                    "mods.astralsorcery.Altar.addDiscoveryAltarRecipe(\"po3:shaped/altar\", <minecraft:emerald>, 200, 200, [<minecraft:coal>]);",
                    "mods.actuallyadditions.Crusher.addRecipe(<minecraft:gravel>, <minecraft:cobblestone>, <minecraft:gravel>, 5);",
                    "mods.enderio.Enchanter.addRecipe(<enchantment:minecraft:sharpness>, <minecraft:book>, 10, 7);",
                    "mods.pneumaticcraft.pressurechamber.addRecipe([<minecraft:clay>, <minecraft:sand>], 1.0, [<minecraft:brick>]);",
                    "mods.botania.RuneAltar.addRecipe(<ore:ingotIron>, [<minecraft:coal>], 100);"
                }));

                var result = ModRecipeScanner.Scan(game, new[] { scripts }, Path.Combine(root, "mods.json"));

                Check("scan zonder dump levert recepten uit scripts",
                      () => result.Error == null && result.Recipes.Count >= 2,
                      "geen fout en minstens 2 recepten");

                Check("shaped recept uit een script komt eruit",
                      () => result.Recipes.TryGetValue("stick", out var stick) && stick.Output == 4 &&
                            stick.Ingredients.Any(i => i.Item == "planks"),
                      "stick: 2× planks, 4 per beurt");

                Check("machine-recept (addRecipe) wordt geïndexeerd",
                      () => result.Recipes.TryGetValue("diamond", out var rune) &&
                            rune.Category.Contains("Rune Altar", StringComparison.OrdinalIgnoreCase) &&
                            rune.Ingredients.Any(i => i.Item == "coal"),
                      "diamond uit Rune Altar met kolen als ingrediënt");

                Check("machine-recept met recept-id vóór de uitvoer (Astral) komt eruit",
                      () => result.Recipes.TryGetValue("emerald", out var astral) &&
                            astral.Category.Contains("Altar", StringComparison.OrdinalIgnoreCase) &&
                            astral.Ingredients.Any(i => i.Item == "coal"),
                      "emerald uit het Altar-recept met kolen als ingrediënt");

                Check("byproduct dat hetzelfde item is wordt geen ingrediënt",
                      () => result.Recipes.TryGetValue("gravel", out var gravel) &&
                            gravel.Ingredients.Any(i => i.Item == "cobblestone") &&
                            gravel.Ingredients.All(i => i.Item != "gravel"),
                      "gravel uit cobblestone, zonder gravel als ingrediënt");

                Check("enchantment-stack wordt niet als item gezien",
                      () => !result.Recipes.ContainsKey("sharpness") && !result.Names.ContainsKey("sharpness"),
                      "geen sharpness-knoop");

                Check("lijst-eerste machine-aanroep wordt overgeslagen",
                      () => !result.Recipes.ContainsKey("clay") && !result.Recipes.ContainsKey("brick"),
                      "pressurechamber-recept levert geen knoop op");

                Check("ore-groep als uitvoer wordt geen recept",
                      () => result.Recipes.Keys.All(k => !k.StartsWith("ore:", StringComparison.OrdinalIgnoreCase)),
                      "geen ore:-uitvoer in de set");

                Check("vloeistof-uitvoer (Melting) levert geen verkeerd recept op",
                      () => !result.Recipes.ContainsKey("iron_ore"),
                      "iron_ore blijft zonder recept");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ========== Index (fase 1) ==========

        /// <summary>
        /// Jar-shards op inhoud-hash, de basislaag daaruit en pack-detectie. De acceptatie
        /// is dat de basis exact gelijk is aan wat de bestaande jar-scan oplevert.
        /// </summary>
        private static void CheckIndex()
        {
            string root = Path.Combine(Path.GetTempPath(), "po3-selftest-fase1-" + Guid.NewGuid().ToString("N"));
            try
            {
                string game = Path.Combine(root, "minecraft");
                string mods = Path.Combine(game, "mods");
                Directory.CreateDirectory(mods);

                WriteJar(Path.Combine(mods, "alpha.jar"), "alpha",
                    ("data/alpha/recipes/stick.json",
                     """{"type":"minecraft:crafting_shaped","pattern":["#","#"],"key":{"#":{"item":"minecraft:planks"}},"result":{"item":"minecraft:stick","count":4}}"""));

                WriteJar(Path.Combine(mods, "beta.jar"), "beta",
                    ("data/beta/recipes/gem.json",
                     """{"type":"minecraft:crafting_shapeless","ingredients":[{"item":"minecraft:coal"}],"result":{"item":"minecraft:diamond"}}"""));

                string shards = Path.Combine(root, "jars");
                var first = JarIndex.Build(game, shards);
                Check("elke jar krijgt een shard",
                      () => first.Shards.Count == 2 && first.Read == 2,
                      "2 shards, 2 gelezen");

                var basis1 = BaseIndex.Build(root, first.Shards);
                Check("basis bevat de recepten uit de jars",
                      () => basis1.Recipes.Count == 2 &&
                            basis1.Recipes.TryGetValue("stick", out var stick) && stick.Output == 4 &&
                            basis1.Recipes.ContainsKey("diamond"),
                      "stick (4 per beurt) en diamond in de basis");

                // Acceptatie: dezelfde set als het bestaande jar-pad.
                var reference = ModRecipeScanner.Scan(game, Array.Empty<string>(), Path.Combine(root, "mods.json"));
                Check("basis is gelijk aan de bestaande jar-scan",
                      () => SameSet(basis1.Recipes, reference.Recipes),
                      "zelfde items, categorieën, aantallen en ingrediënten");

                var second = JarIndex.Build(game, shards);
                var basis2 = BaseIndex.Build(root, second.Shards);
                Check("tweede run opent geen enkele jar",
                      () => second.Read == 0 && second.Cached == 2 && basis2.FromCache && basis2.Hash == basis1.Hash,
                      "0 gelezen, 2 uit cache, basis uit cache");

                WriteJar(Path.Combine(mods, "beta.jar"), "beta",
                    ("data/beta/recipes/gem.json",
                     """{"type":"minecraft:crafting_shapeless","ingredients":[{"item":"minecraft:coal"},{"item":"minecraft:coal"}],"result":{"item":"minecraft:diamond","count":2}}"""));

                var third = JarIndex.Build(game, shards);
                var basis3 = BaseIndex.Build(root, third.Shards);
                Check("gewijzigde jar geeft een nieuwe basis",
                      () => third.Read == 1 && basis3.Hash != basis1.Hash &&
                            basis3.Recipes.TryGetValue("diamond", out var gem) && gem.Output == 2 &&
                            gem.Ingredients.Count == 1,
                      "1 jar gelezen, andere hash, 2 diamant uit kolen");

                string mmc = Path.Combine(root, "mmc");
                Directory.CreateDirectory(mmc);
                File.WriteAllText(Path.Combine(mmc, "mmc-pack.json"),
                    """{"components":[{"uid":"net.minecraft","version":"1.12.2"},{"uid":"net.minecraftforge","version":"14.23.5.2860"}]}""");
                File.WriteAllText(Path.Combine(mmc, "instance.cfg"), "name=Project Ozone 3\n");
                var p1 = PackProfile.Detect(mmc);
                Check("pack-detectie via mmc-pack.json",
                      () => p1.Minecraft == "1.12.2" && p1.Loader.Contains("forge") && p1.Pack == "project-ozone-3",
                      "1.12.2 + forge, packnaam uit instance.cfg");

                string cf = Path.Combine(root, "cf");
                Directory.CreateDirectory(cf);
                File.WriteAllText(Path.Combine(cf, "manifest.json"),
                    """{"name":"All the Mods 9","minecraft":{"version":"1.20.1","modLoaders":[{"id":"forge-47.2.0"}]}}""");
                var p2 = PackProfile.Detect(cf);
                Check("pack-detectie via CurseForge-manifest",
                      () => p2.Minecraft == "1.20.1" && p2.Loader == "forge-47.2.0" && p2.Pack == "all-the-mods-9",
                      "1.20.1 + forge, packnaam uit manifest");

                string mr = Path.Combine(root, "mr");
                Directory.CreateDirectory(mr);
                File.WriteAllText(Path.Combine(mr, "modrinth.index.json"),
                    """{"name":"Fabulously Optimized","dependencies":{"minecraft":"1.21","fabric-loader":"0.16.5"}}""");
                var p3 = PackProfile.Detect(mr);
                Check("pack-detectie via Modrinth-index",
                      () => p3.Minecraft == "1.21" && p3.Loader.StartsWith("fabric") && p3.Pack == "fabulously-optimized",
                      "1.21 + fabric, packnaam uit index");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void WriteJar(string path, string modId, params (string Name, string Json)[] files)
        {
            if (File.Exists(path)) File.Delete(path);

            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            var meta = zip.CreateEntry("mcmod.info");
            using (var writer = new StreamWriter(meta.Open()))
                writer.Write("[" + JsonSerializer.Serialize(new { modid = modId, name = modId.ToUpperInvariant(), version = "1.0" }) + "]");

            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(file.Json);
            }
        }

        private static bool SameSet(Dictionary<string, Recipe> left, Dictionary<string, Recipe> right)
        {
            if (left.Count != right.Count) return false;

            foreach (var kv in left)
            {
                if (!right.TryGetValue(kv.Key, out var other)) return false;
                if (!string.Equals(kv.Value.Category, other.Category, StringComparison.OrdinalIgnoreCase)) return false;
                if (kv.Value.Output != other.Output) return false;
                if (kv.Value.Ingredients.Count != other.Ingredients.Count) return false;

                var a = kv.Value.Ingredients.OrderBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
                                            .Select(i => i.Item + "x" + i.Count).ToList();
                var b = other.Ingredients.OrderBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
                                         .Select(i => i.Item + "x" + i.Count).ToList();

                if (!a.SequenceEqual(b)) return false;
            }

            return true;
        }

        // ========== Lagen: patchlog, fold, diff, validatie, profielen ==========

        /// <summary>
        /// De pack-laag als bewerkingen, de fold met prioriteit, de diff, de checks en de
        /// profielen. De acceptatie is dat dezelfde lagen altijd dezelfde set geven en dat
        /// elk recept zijn bron houdt.
        /// </summary>
        private static void CheckLayers()
        {
            string root = Path.Combine(Path.GetTempPath(), "po3-selftest-lagen-" + Guid.NewGuid().ToString("N"));
            try
            {
                string game = Path.Combine(root, "minecraft");
                string data = Path.Combine(root, "data");
                string pack = Path.Combine(game, "datapacks", "testpack", "data", "test", "recipes");
                Directory.CreateDirectory(pack);
                Directory.CreateDirectory(Path.Combine(game, "mods"));
                Directory.CreateDirectory(data);

                File.WriteAllText(Path.Combine(pack, "gear.json"),
                    """{"type":"minecraft:crafting_shaped","pattern":["##","##"],"key":{"#":{"item":"minecraft:iron_ingot"}},"result":{"item":"pack2:gear","count":2}}""");

                var first = PatchLog.Build(game, data, Array.Empty<string>());
                Check("pack-laag leest het datapack als bewerking",
                      () => first.Ops.Count(o => o.Op == "add") == 1 && first.Files == 1 && !first.FromCache,
                      "1 add, 1 bestand, eerste keer gelezen");

                var second = PatchLog.Build(game, data, Array.Empty<string>());
                Check("tweede keer pack-laag uit cache",
                      () => second.FromCache && second.Hash == first.Hash,
                      "zelfde hash, uit cache");

                var shard = new JarShard { Jar = "test.jar", ModId = "testmod" };
                shard.Recipes["stick"] = new Recipe
                {
                    Category = "Crafting",
                    Output = 4,
                    Ingredients = { new Ingredient { Item = "planks", Count = 2 } }
                };

                var basis = Fold.Apply(new[] { shard }, Array.Empty<RecipeOp>(), first.Ops, Array.Empty<RecipeOp>());
                Check("fold zet de basis en de pack-laag bij elkaar",
                      () => basis.Recipes.ContainsKey("stick") && basis.Recipes.ContainsKey("gear"),
                      "stick uit de jar en gear uit het datapack naast elkaar");

                Check("elk recept houdt zijn eigen bron",
                      () => basis.Recipes.TryGetValue("stick", out var stickBron) &&
                            stickBron.Source == "jar-JSON testmod" &&
                            basis.Recipes.TryGetValue("gear", out var gearBron) &&
                            gearBron.Source.Contains("datapacks"),
                      $"stick = '{basis.Recipes.GetValueOrDefault("stick")?.Source}', " +
                      $"gear = '{basis.Recipes.GetValueOrDefault("gear")?.Source}'");

                var replace = new RecipeOp
                {
                    Op = "replace",
                    Out = "stick",
                    Prio = 10,
                    Src = "scripts/pack.zs",
                    Recipe = new Recipe { Category = "Crafting", Output = 8 }
                };
                var user = new RecipeOp
                {
                    Op = "replace",
                    Out = "stick",
                    Prio = 1000,
                    Src = "hand",
                    Recipe = new Recipe { Category = "Crafting", Output = 9 }
                };

                var folded = Fold.Apply(new[] { shard }, Array.Empty<RecipeOp>(), new[] { replace }, new[] { user });
                Check("gebruiker wint van de pack-laag",
                      () => folded.Recipes["stick"].Output == 9 && folded.Recipes["stick"].Source == "hand",
                      "output 9 uit de gebruikerslaag, niet 8 uit het script");

                var swap = new RecipeOp { Op = "replaceIngredient", Item = "iron_ingot", With = "steel_ingot", Prio = 10, Src = "scripts/steel.zs" };
                var swapped = Fold.Apply(new[] { shard }, Array.Empty<RecipeOp>(), first.Ops.Concat(new[] { swap }), Array.Empty<RecipeOp>());
                Check("replaceIngredient vervangt in alle recepten",
                      () => swapped.Recipes["gear"].Ingredients.All(i => i.Item != "iron_ingot") &&
                            swapped.Recipes["gear"].Ingredients.Any(i => i.Item == "steel_ingot"),
                      "ijzer is staal geworden");

                var removeMod = new RecipeOp { Op = "removeByMod", Out = "testmod", Prio = 10, Src = "scripts/weg.zs" };
                var withoutMod = Fold.Apply(new[] { shard }, Array.Empty<RecipeOp>(), first.Ops.Concat(new[] { removeMod }), Array.Empty<RecipeOp>());
                Check("removeByMod haalt alleen die mod weg",
                      () => !withoutMod.Recipes.ContainsKey("stick") && withoutMod.Recipes.ContainsKey("gear"),
                      "stick weg, gear blijft");

                var previous = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase)
                {
                    ["a"] = new Recipe { Category = "Crafting", Output = 1 },
                    ["b"] = new Recipe { Category = "Crafting", Output = 1 }
                };
                var current = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase)
                {
                    ["b"] = new Recipe { Category = "Crafting", Output = 2 },
                    ["c"] = new Recipe { Category = "Crafting", Output = 1, Source = "scripts/x.zs" }
                };
                var diff = DiffReport.Compare(previous, current);
                Check("diff bewaart elke regel voor het filter",
                      () => diff.Entries.Count == 3 && diff.Entries.Any(e => e.Kind == "+" && e.Item == "c") &&
                            diff.Entries.Any(e => e.Kind == "-" && e.Item == "a") &&
                            diff.Entries.Any(e => e.Kind == "~" && e.Item == "b"),
                      "3 entries: + c, ~ b, - a");

                Check("diff telt toegevoegd, weggehaald en gewijzigd",
                      () => diff.Added == 1 && diff.Removed == 1 && diff.Changed == 1 &&
                            diff.Lines.Any(l => l.StartsWith("+ c") && l.Contains("scripts/x.zs")),
                      "+1 · -1 · ~1, met de bron bij het nieuwe recept");

                var broken = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase)
                {
                    ["zichzelf"] = new Recipe { Category = "Crafting", Ingredients = { new Ingredient { Item = "zichzelf" } } },
                    ["links"] = new Recipe { Category = "Crafting", Ingredients = { new Ingredient { Item = "rechts" } } },
                    ["rechts"] = new Recipe { Category = "Crafting", Ingredients = { new Ingredient { Item = "links" } } },
                    ["wees"] = new Recipe { Category = "Crafting", Ingredients = { new Ingredient { Item = "bestaatniet" } } }
                };
                var validation = ValidationResult.Run(broken);
                Check("validatie vindt zelf-ingrediënt, kringetje en los ingrediënt",
                      () => validation.Warnings >= 3 && validation.Lines.Any(l => l.Contains("zichzelf")) &&
                            validation.Lines.Any(l => l.Contains("kringetje")) &&
                            validation.Lines.Any(l => l.Contains("zonder eigen recept")),
                      "drie soorten meldingen, gebundeld per regel");

                string profilesDir = Path.Combine(root, "profielen");
                Profiles.Upsert(profilesDir, new PackProfile { Pack = "pack-een", Instance = "Een", ResultCount = 10 });
                Profiles.Upsert(profilesDir, new PackProfile { Pack = "pack-twee", Instance = "Twee", ResultCount = 20 });
                var book = Profiles.Load(profilesDir);
                var found = Profiles.Find(profilesDir, "Twee");

                Check("profielen onthouden elk pack en het actieve pack",
                      () => book.Packs.Count == 2 && book.Active == "pack-twee" &&
                            found != null && found.Pack == "pack-twee" &&
                            Profiles.Find(profilesDir, "bestaatniet") == null,
                      "2 profielen, actief = laatst geïndexeerde");

                string freshData = Path.Combine(root, "versheid");
                string freshInst = Path.Combine(root, "instance");
                string dumpDir = Path.Combine(freshInst, "minecraft", "config", "jeiexporter", "exports");
                Directory.CreateDirectory(Path.Combine(freshInst, "minecraft", "mods"));
                Directory.CreateDirectory(dumpDir);
                Directory.CreateDirectory(freshData);

                File.WriteAllText(Path.Combine(freshInst, "minecraft", "mods", "a.jar"), "x");
                File.WriteAllText(Path.Combine(dumpDir, "nameMap.json"), "{}");

                string freshFold = Path.Combine(freshData, "folded-testpack.json");
                string freshShard = Path.Combine(freshData, "dump-shard.json");
                File.WriteAllText(freshFold, "{}");
                File.WriteAllText(freshShard, "{}");

                // Fold en import zijn ouder dan de jar en de dump.
                File.SetLastWriteTime(freshFold, DateTime.Now.AddHours(-2));
                File.SetLastWriteTime(freshShard, DateTime.Now.AddHours(-2));
                File.SetLastWriteTime(Path.Combine(dumpDir, "nameMap.json"), DateTime.Now.AddMinutes(-10));
                File.SetLastWriteTime(Path.Combine(freshInst, "minecraft", "mods", "a.jar"), DateTime.Now.AddMinutes(-5));

                new PackProfile { Pack = "testpack", InstancePath = freshInst, Jars = 1, JarStamp = "1:1" }
                    .Save(Path.Combine(freshData, "pack.json"));

                var stale = FreshnessReport.Check(freshData);
                Check("versheidscontrole ziet gewijzigde jars en een nieuwere dump",
                      () => stale.Stale && stale.JarsChanged && stale.DumpNewer && stale.Text.Contains("verouderd"),
                      "jars gewijzigd en dump nieuwer dan de import");

                File.SetLastWriteTime(freshShard, DateTime.Now);
                new PackProfile
                {
                    Pack = "testpack", InstancePath = freshInst, Jars = 1,
                    JarStamp = IndexCommands.JarStamp(Path.Combine(freshInst, "minecraft"))
                }.Save(Path.Combine(freshData, "pack.json"));

                Check("versheidscontrole zwijgt als alles actueel is",
                      () => !FreshnessReport.Check(freshData).Stale,
                      "geen melding bij een verse import en gelijke jars");

                Profiles.SetSource(profilesDir, "pack-een", true);
                Check("keuze per pack onthouden (fold of recipes.json)",
                      () => Profiles.Find(profilesDir, "pack-een")!.UseFold &&
                            !Profiles.Find(profilesDir, "pack-twee")!.UseFold,
                      "pack-een fold, pack-twee recipes");

                string provDir = Path.Combine(root, "herkomst");
                Directory.CreateDirectory(provDir);
                File.WriteAllText(Path.Combine(provDir, "patches-test.jsonl"),
                    JsonSerializer.Serialize(new RecipeOp
                    {
                        Op = "replace", Out = "gear", Prio = 10, Src = "scripts/test.zs", Line = 42,
                        Recipe = new Recipe { Category = "Crafting" }
                    }) + "\n" +
                    JsonSerializer.Serialize(new RecipeOp
                    {
                        Op = "remove", Out = "gear", Prio = 20, Src = "scripts/later.zs", Line = 7
                    }) + "\n");

                var provSet = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gear"] = new Recipe { Category = "Crafting", Source = "scripts/test.zs", PatchId = "scripts/test.zs:42" }
                };
                var prov = ProvenanceReport.Explain(provDir, "gear", provSet);

                Check("herkomst noemt laag, winnende patch en de bewerkingen",
                      () => prov.Layer.Contains("pack-laag") && prov.Ops == 2 &&
                            prov.Lines.Count(l => l.Winner) == 1 && prov.Lines.Any(l => l.Winner && l.Text.Contains("42")),
                      "replace op scripts/test.zs:42 wint, remove blijft zichtbaar");

                new PackProfile { Pack = "testpack" }.Save(Path.Combine(provDir, "pack.json"));
                File.WriteAllText(Path.Combine(provDir, "folded-testpack.json"),
                    JsonSerializer.Serialize(new Dictionary<string, Recipe>
                    {
                        ["gear2"] = new Recipe { Category = "Crafting", Source = "runtime:exports" }
                    }));

                var fallback = ProvenanceReport.Explain(provDir, "gear2", new Dictionary<string, Recipe>());
                var deadSet = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gear"] = new Recipe
                    {
                        Category = "Crafting",
                        Ingredients = { new Ingredient { Item = "only_ingredient" } }
                    }
                };

                var deadOps = new[]
                {
                    new RecipeOp { Op = "remove", Out = "gear", Src = "scripts/a.zs", Line = 1 },
                    new RecipeOp { Op = "remove", Out = "only_ingredient", Src = "scripts/a.zs", Line = 2 },
                    new RecipeOp { Op = "remove", Out = "minecraft:gear", Src = "scripts/b.zs", Line = 3 },
                    new RecipeOp { Op = "remove", Out = "mystery_item", Src = "scripts/b.zs", Line = 4 }
                };

                var dead = DeadPatchReport.Build(deadOps, deadSet);
                Check("dode patches uitgesplitst naar oorzaak en script",
                      () => dead.Total == 4 && dead.Double == 1 && dead.Gap == 1 &&
                            dead.Name == 1 && dead.Never == 1 && dead.Groups.Count == 2,
                      "dubbel, gat, naamvorm en nooit — in 2 groepen");

                Check("herkomst valt terug op de fold zonder bron in de set",
                      () => fallback.FromFold && fallback.Layer.Contains("runtime-dump"),
                      "laag uit de fold, niet 'onbekend'");

                Profiles.Upsert(profilesDir, new PackProfile { Pack = "pack-een", Instance = "Een", ResultCount = 12 });
                Check("herindex wist de onthouden keuze niet",
                      () => Profiles.Find(profilesDir, "pack-een")!.UseFold,
                      "UseFold blijft na Upsert");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void CheckRealRecipes()
        {
            var recipes = LoadRealRecipes(out string source);
            if (recipes == null || recipes.Count == 0)
            {
                Report.AppendLine("  · overgeslagen: geen recipes.json gevonden (nog nooit geïmporteerd of gescand)");
                return;
            }

            var items = SampleItems(recipes);
            Report.AppendLine($"  bron: {source}");
            Report.AppendLine($"  {recipes.Count} items in de set, steekproef van {items.Count}");

            foreach (string item in items)
            {
                try
                {
                    int budget = RecipePlanner.MaxTreeNodes;
                    var root = RecipePlanner.Build(item, 1, recipes,
                                                   new Dictionary<string, string>(), new Dictionary<string, int>(),
                                                   ref budget);
                    string plan = RecipePlanner.BuildStepPlan(root, recipes);
                    var stats = Measure(root);

                    Check($"plan voor {item} wordt gemaakt",
                          () => plan.Contains("STAPPEN", StringComparison.OrdinalIgnoreCase) && plan.Length > 150,
                          "volledig stappenplan");

                    string line = $"{item}: {stats.Steps} stap(pen) · {stats.BaseQuantity:N0} basis-stuk(s) · " +
                                  $"{stats.Nodes} knopen · diepte {stats.MaxDepth}";

                    if (stats.Steps == 0)
                        Warn(line + " → leeg plan (verzamelitem of geen recept)");
                    else if (stats.Truncated > 0)
                        Warn(line + $" → afgekapt op budget/diepte ({stats.Truncated}×)");
                    else if (stats.Nodes > MaxSaneNodes || stats.Steps > MaxSaneSteps || stats.BaseQuantity > MaxSaneBaseQuantity)
                        Warn(line + " → verdacht groot");
                    else
                        Report.AppendLine("  · " + line);
                }
                catch (Exception ex)
                {
                    Fail($"plan voor {item} wordt gemaakt", "uitzondering: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void Warn(string message)
        {
            _warnings++;
            Report.AppendLine("  ⚠ " + message);
        }

        /// <summary>Leest de set die de app gebruikt; anders de recipes.json naast de exe.</summary>
        private static Dictionary<string, Recipe>? LoadRealRecipes(out string source)
        {
            source = "";
            string data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCRPV");
            string path = Path.Combine(data, "recipes.json");

            if (!File.Exists(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, "recipes.json");
                if (!File.Exists(path)) return null;
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Recipe>>(File.ReadAllText(path))
                             ?? new Dictionary<string, Recipe>();
                var recipes = new Dictionary<string, Recipe>(loaded, StringComparer.OrdinalIgnoreCase);
                RecipeOverlay.Apply(recipes);
                source = path;
                return recipes;
            }
            catch (Exception ex)
            {
                Report.AppendLine("  · overgeslagen: recipes.json niet leesbaar (" + ex.Message + ")");
                return null;
            }
        }

        /// <summary>Vaste namen eerst, daarna een gelijkmatige greep uit de set.</summary>
        private static List<string> SampleItems(Dictionary<string, Recipe> recipes)
        {
            var picked = new List<string>();

            foreach (string wanted in WellKnownItems)
            {
                string norm = RecipePlanner.NormalizeName(wanted);
                var hit = recipes.Keys.FirstOrDefault(k =>
                    string.Equals(RecipePlanner.NormalizeName(k), norm, StringComparison.OrdinalIgnoreCase));

                if (hit != null && !picked.Contains(hit, StringComparer.OrdinalIgnoreCase))
                    picked.Add(hit);
            }

            var all = recipes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            int stride = Math.Max(1, all.Count / (RealSampleSize * 2));

            for (int i = 0; i < all.Count && picked.Count < RealSampleSize; i += stride)
                if (!picked.Contains(all[i], StringComparer.OrdinalIgnoreCase))
                    picked.Add(all[i]);

            return picked;
        }

        private sealed class TreeStats
        {
            public int Nodes;
            public int MaxDepth;
            public int Steps;
            public int Truncated;
            public long BaseQuantity;
        }

        private static TreeStats Measure(RecipeNode root)
        {
            var stats = new TreeStats();

            void Walk(RecipeNode node, int depth)
            {
                stats.Nodes++;
                stats.MaxDepth = Math.Max(stats.MaxDepth, depth);
                if (node.IsTruncated) stats.Truncated++;

                if (node.Children.Count > 0) stats.Steps++;
                else stats.BaseQuantity += node.Quantity;

                foreach (var child in node.Children) Walk(child, depth + 1);
            }

            Walk(root, 0);
            return stats;
        }

        private static RecipeNode Build(string item, int quantity, IReadOnlyDictionary<string, Recipe> recipes)
        {
            int budget = RecipePlanner.MaxTreeNodes;
            return RecipePlanner.Build(item, quantity, recipes,
                                       new Dictionary<string, string>(), new Dictionary<string, int>(), ref budget);
        }

        /// <summary>Aantal van een item ergens in de boom (ook dieper dan de eerste laag).</summary>
        private static int QuantityOf(RecipeNode node, string name)
        {
            if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)) return node.Quantity;

            foreach (var child in node.Children)
            {
                int found = QuantityOf(child, name);
                if (found > 0) return found;
            }

            return 0;
        }

        private static bool HasCycleNode(RecipeNode node) =>
            node.IsCycle || node.Children.Any(HasCycleNode);
    }
}
