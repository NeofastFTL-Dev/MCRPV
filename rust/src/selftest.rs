//! Headless checks for the stage-1 data layer, planner and search. They run in CI and in the
//! `.NET`-free build, and mirror the parts of `SelfTest.cs` that this stage covers.

use crate::lang;
use crate::layers;
use crate::model::RecipeSet;
use crate::plan;
use crate::names::{self, Names};
use crate::search::SearchIndex;
use crate::tree::{self, PlannerLimits};

/// Language keys the window uses; every one of them must resolve in both languages.
const UI_KEYS: &[&str] = &[
    "main.title",
    "main.subtitle",
    "lbl.quantity",
    "tip.item",
    "tab.bracket.tip",
    "header.plan",
    "plan.base",
    "plan.base.none",
    "plan.steps",
    "plan.steps.norecipe",
    "plan.steps.count",
    "plan.machine",
    "plan.machine.other",
    "plan.product",
    "plan.summary",
    "plan.cut",
    "plan.cut.warn",
    "plan.cut.advice",
    "plan.cutnote",
];

pub fn run() -> i32 {
    let mut checks: Vec<(bool, String)> = Vec::new();
    macro_rules! check {
        ($cond:expr, $name:expr) => {
            checks.push(($cond, $name.to_string()))
        };
    }

    // ---------- names ----------
    check!(names::normalize_name("minecraft:Iron Ingot{display:{Name:\"x\"}}") == "iron_ingot", "naam: NBT strippen");
    check!(names::normalize_name("minecraft:stone 1 0") == "stone", "naam: stack-notatie");
    check!(names::normalize_name("mod:path:0") == "path", "naam: metadata eraf");
    check!(names::normalize_name("Block of Iron") == "block_of_iron", "naam: spaties");
    check!(names::normalize_name("  ").is_empty(), "naam: leeg blijft leeg");
    check!(names::pretty("iron_ingot") == "Iron Ingot", "naam: Title Case");
    check!(names::pretty("tier_6_crop") == "Tier 6 Crop", "naam: cijfers niet aan elkaar");

    let mut map = std::collections::HashMap::new();
    map.insert("iron_ingot".to_string(), "Iron Ingot".to_string());
    let names = Names::load_map(map);
    check!(names.display("IRON_INGOT") == "Iron Ingot", "namen: case-insensitive");
    check!(names.display("unknown_thing") == "Unknown Thing", "namen: terugval op Title Case");

    // ---------- language ----------
    check!(lang::keys().len() >= 290, "taal: 294 sleutels beschikbaar");
    check!(lang::missing_in("nl").is_empty(), "taal: Nederlands compleet");
    check!(lang::missing_in("en").is_empty(), "taal: Engels compleet");
    check!(lang::non_string_entries().is_empty(), "taal: alle waarden zijn tekst");
    check!(lang::t("main.title").contains("MCRPV"), "taal: titel");
    let unknown = lang::t("bestaat.niet");
    check!(unknown.starts_with('‽'), "taal: ontbrekende sleutel is zichtbaar");
    check!(
        lang::normalize(Some("EN")) == "en" && lang::normalize(Some("de")) == lang::DEFAULT,
        "taal: onbekende code valt terug op Nederlands"
    );
    let unresolved: Vec<&str> = UI_KEYS
        .iter()
        .copied()
        .filter(|key| lang::t(key).starts_with('‽'))
        .collect();
    check!(unresolved.is_empty(), "taal: alle venstersleutels bestaan");

    // ---------- recipes.json ----------
    let sample = r#"{
        "iron_pickaxe": {"Ingredients": [{"Item": "iron_ingot", "Count": 3}, {"Item": "stick", "Count": 2}]},
        "iron_ingot": {"Category": "Furnace", "Ingredients": [{"Item": "iron_ore", "Count": 1}]},
        "stick": {"Ingredients": [{"Item": "plank", "Count": 2}]},
        "plank": {"Output": 4, "Ingredients": [{"Item": "log", "Count": 1}]},
        "log": {"Ingredients": []}
    }"#;
    let recipes = RecipeSet::from_json(sample).expect("sample parses");
    check!(recipes.len() == 5, "recepten: vijf items");
    check!(recipes.get("IRON_PICKAXE").is_some(), "recepten: case-insensitive opzoeken");
    check!(recipes.get("iron_pickaxe").unwrap().output == 1, "recepten: Output standaard 1");
    check!(recipes.get("plank").unwrap().output == 4, "recepten: Output uit het bestand");
    check!(RecipeSet::from_json("{niet json").is_err(), "recepten: kapotte json is een fout");

    let with_alternatives = r#"{"x": {"Ingredients": [], "Alternatives": [
        {"Category": "Furnace", "Ingredients": [{"Item": "y"}]}, null]}}"#;
    let alts = RecipeSet::from_json(with_alternatives).unwrap();
    check!(alts.get("x").unwrap().options().len() == 2, "recepten: alternatieven zonder null-gaten");

    // ---------- tree ----------
    let limits = PlannerLimits::default();
    let pickaxe = tree::build("iron_pickaxe", 2, &recipes, &limits);
    check!(pickaxe.quantity == 2, "boom: wortel-aantal");
    check!(pickaxe.children.len() == 2, "boom: twee ingredienten");
    check!(pickaxe.children[0].quantity == 6, "boom: 3 × 2 = 6");
    check!(pickaxe.children[1].quantity == 4, "boom: 2 × 2 = 4");
    check!(pickaxe.children[1].children[0].name == "plank", "boom: keten loopt door");
    check!(pickaxe.children[1].children[0].quantity == 8, "boom: 2 × 4 = 8");
    check!(pickaxe.children[0].children[0].name == "iron_ore", "boom: ovenrecept gebruikt");
    check!(pickaxe.children[0].children[0].children.is_empty(), "boom: basis-item stopt");

    let planks = tree::build("plank", 5, &recipes, &limits);
    check!(planks.children[0].quantity == 2, "boom: 5 stuks bij Output 4 = 2 beurten");

    let cycle_set = RecipeSet::from_json(
        r#"{"a": {"Ingredients": [{"Item": "b"}]}, "b": {"Ingredients": [{"Item": "a"}]}}"#,
    )
    .unwrap();
    let cycle = tree::build("a", 1, &cycle_set, &limits);
    check!(cycle.children[0].children[0].is_cycle, "boom: kring wordt gemarkeerd");

    let gathering = RecipeSet::from_json(
        r#"{"wheat": {"Category": "Crop Produce", "Ingredients": [{"Item": "wheat_seeds"}]}}"#,
    )
    .unwrap();
    let wheat = tree::build("wheat", 3, &gathering, &limits);
    check!(wheat.children.is_empty(), "boom: verzamelen stopt de keten");

    let reverse = RecipeSet::from_json(
        r#"{"x": {"Ingredients": [{"Item": "x"}],
                 "Alternatives": [{"Category": "Furnace", "Ingredients": [{"Item": "y"}]}]},
            "y": {"Ingredients": []}}"#,
    )
    .unwrap();
    let reversed = tree::build("x", 1, &reverse, &limits);
    check!(
        reversed.option_index == 1 && reversed.category == "Furnace",
        "boom: recept dat zijn eigen product opeet wordt overgeslagen"
    );

    let huge = RecipeSet::from_json(
        r#"{"big": {"Ingredients": [{"Item": "tiny", "Count": 9999}]}, "tiny": {"Ingredients": []}}"#,
    )
    .unwrap();
    let capped = tree::build("big", 2, &huge, &limits);
    check!(capped.children[0].is_truncated, "boom: hoeveelheid boven 9999 wordt afgekapt");
    check!(capped.children[0].quantity == 9999, "boom: afkap op 9999");

    let mut chain_json = String::from("{");
    for i in 0..20 {
        chain_json.push_str(&format!("\"c{i}\": {{\"Ingredients\": [{{\"Item\": \"c{}\"}}]}},", i + 1));
    }
    chain_json.push_str("\"c20\": {\"Ingredients\": []}}");
    let chain_set = RecipeSet::from_json(&chain_json).unwrap();
    let deep = tree::build("c0", 1, &chain_set, &limits);
    let mut node = &deep;
    let mut truncated = false;
    while let Some(next) = node.children.first() {
        if next.is_truncated {
            truncated = true;
        }
        node = next;
    }
    check!(truncated, "boom: dieptegrens markeert de afkap");

    let bulk = tree::build("log", 20, &recipes, &PlannerLimits { max_crafts_per_step: 1, base_items_per_product: 1000 });
    check!(!bulk.is_truncated, "boom: basis-item blijft staan");
    let tight = tree::build("iron_pickaxe", 1, &recipes, &PlannerLimits { max_crafts_per_step: 16, base_items_per_product: 1 });
    check!(tight.children[1].is_truncated, "boom: basis-begroting kap't de volgende tak af");

    let (crafts, bases) = tree::totals(&pickaxe);
    check!(crafts == 4 && bases == 8, "boom: totalen geteld (4 stappen, 8 basis)");
    check!(tree::bracket_text(&pickaxe, &names).contains("└─"), "boom: bracket-tekst");

    // ---------- search ----------
    let index = SearchIndex::build(recipes.keys().iter().cloned(), &names);
    check!(index.len() == 5, "zoeken: index over alle recepten");
    check!(
        index.search("iron", 10).first().map(|entry| entry.key.as_str()) == Some("iron_ingot")
            || index.search("iron", 10).iter().any(|entry| entry.key == "iron_pickaxe"),
        "zoeken: vindt iron-items"
    );
    check!(
        index.search("Iron Pickaxe", 10).first().map(|entry| entry.key.as_str()) == Some("iron_pickaxe"),
        "zoeken: op weergavenaam"
    );
    check!(index.search("zzz", 10).is_empty(), "zoeken: geen treffers");
    check!(
        index.search("iron ingot", 10).first().map(|entry| entry.key.as_str()) == Some("iron_ingot"),
        "zoeken: genormaliseerde invoer"
    );

    // ---------- step plan (RecipePlanner.BuildStepPlan) ----------
    let plan_set = RecipeSet::from_json(
        r#"{
            "pickaxe": {"Category": "Crafting", "Ingredients": [{"Item": "iron_ingot", "Count": 2}, {"Item": "stick"}]},
            "iron_ingot": {"Category": "Furnace", "Ingredients": [{"Item": "raw_iron"}]},
            "stick": {"Category": "Crafting", "Ingredients": [{"Item": "plank", "Count": 2}]},
            "plank": {"Category": "Crafting", "Output": 4, "Ingredients": [{"Item": "log"}]},
            "raw_iron": {"Ingredients": []},
            "log": {"Ingredients": []}
        }"#,
    )
    .unwrap();

    let plan_root = tree::build("pickaxe", 1, &plan_set, &limits);
    let step_plan = plan::build(&plan_root, &plan_set);
    check!(step_plan.step_count == 4, "plan: vier craft-stappen");
    check!(step_plan.base.len() == 2, "plan: twee basis-materialen");
    check!(
        step_plan.base.iter().any(|item| item.name == "raw_iron" && item.quantity == 2),
        "plan: basis-telling (2 raw iron)"
    );
    check!(step_plan.machines.len() == 2, "plan: twee machines");
    check!(step_plan.machines[0].machine == "Furnace", "plan: smelten vóór crafting");

    let crafting = step_plan
        .machines
        .iter()
        .find(|block| block.machine == "Crafting")
        .expect("crafting block");
    check!(crafting.steps.len() == 3, "plan: crafting-blok heeft drie stappen");
    check!(crafting.steps.last().unwrap().name == "pickaxe", "plan: eindproduct achteraan");

    let plank_step = crafting.steps.iter().find(|step| step.name == "plank").unwrap();
    check!(
        plank_step.crafts == 1 && plank_step.ingredients[0].1 == 1,
        "plan: 2 planken bij Output 4 = 1 beurt, 1 log"
    );

    let iron_step = step_plan.machines[0]
        .steps
        .iter()
        .find(|step| step.name == "iron_ingot")
        .unwrap();
    check!(
        iron_step.ingredients[0] == ("raw_iron".to_string(), 2),
        "plan: ingredienten × beurten"
    );

    let plan_text = plan::text(&step_plan, &names);
    check!(
        plan_text.contains(&lang::t("plan.base"))
            && plan_text.contains("EINDPRODUCT")
            && !plan_text.contains('‽'),
        "plan: tekst heeft basis en eindproduct"
    );
    check!(
        plan_text.find("Furnace").unwrap() < plan_text.find("Crafting").unwrap(),
        "plan: machinevolgorde in de tekst"
    );

    let cycle_plan = plan::build(&tree::build("a", 1, &cycle_set, &limits), &cycle_set);
    check!(
        !plan::text(&cycle_plan, &names).is_empty(),
        "plan: een kringetje geeft nog een plan"
    );

    let cut_plan = plan::build(&tree::build("c0", 1, &chain_set, &limits), &chain_set);
    check!(
        cut_plan.cut_count > 0 && plan::text(&cut_plan, &names).contains(&lang::t("plan.cut.advice")),
        "plan: afgekapte tak wordt gemeld"
    );

    // ---------- layers (RecipeLayers) ----------
    check!(layers::classify("jar-JSON thermal") == layers::Layer::Base, "laag: jar-JSON is basis");
    check!(layers::classify("") == layers::Layer::Base, "laag: lege bron is basis");
    check!(layers::classify("overlay") == layers::Layer::Base, "laag: overlay is basis");
    check!(layers::classify("runtime:jei") == layers::Layer::Dump, "laag: runtime is dump");
    check!(layers::classify("scripts/mythic/runes.zs") == layers::Layer::Pack, "laag: script is pack");

    let layered = RecipeSet::from_json(
        r#"{
            "a": {"Source": "jar-JSON x", "Ingredients": []},
            "b": {"Source": "runtime:d", "Ingredients": []},
            "c": {"Source": "scripts/x.zs", "Ingredients": []}
        }"#,
    )
    .unwrap();
    let layer_counts = layers::counts(&layered);
    check!(
        layer_counts == layers::LayerCounts { base: 1, dump: 1, pack: 1, user: 0 },
        "laag: verdeling per laag"
    );
    check!(
        layers::summary(&layer_counts).contains('📦') && layers::summary(&layer_counts).contains('🧩'),
        "laag: samenvatting"
    );
    check!(
        lang::format("plan.machine", &["X", "3"]).contains("X") && lang::format("plan.machine", &["X", "3"]).contains("3 stap"),
        "taal: placeholders gevuld"
    );

    // ---------- the sample that ships with the repo ----------
    let bundled = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("MCRPV")
        .join("recipes.json");
    match RecipeSet::load(&bundled) {
        Ok(set) => check!(!set.is_empty(), "bundel: recipes.json leesbaar"),
        Err(_) => check!(false, "bundel: recipes.json leesbaar"),
    }

    let failed = checks.iter().filter(|(ok, _)| !ok).count();
    for (ok, name) in &checks {
        println!("{} {name}", if *ok { "✔" } else { "✖" });
    }
    if failed == 0 {
        println!("✔ alles goed: {} controle(s) geslaagd", checks.len());
        0
    } else {
        println!("✖ {failed} van {} controle(s) mislukt", checks.len());
        1
    }
}
