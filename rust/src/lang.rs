//! Every visible GUI string, per language, from the same tables the .NET build used.
//!
//! The JSON files are extracted verbatim from `Lang.nl.cs` / `Lang.en.cs` (294 keys each), so a
//! missing key still shows up as `‽key` instead of silently rendering an empty label, and the
//! self-test can compare the two tables.

use serde_json::Value;
use std::collections::BTreeMap;
use std::sync::{Mutex, OnceLock};

const NL_JSON: &str = include_str!("../lang/nl.json");
const EN_JSON: &str = include_str!("../lang/en.json");

/// Language used when nothing is chosen, and the fallback for missing keys.
pub const DEFAULT: &str = "nl";

fn parse(text: &str) -> BTreeMap<String, String> {
    serde_json::from_str::<BTreeMap<String, String>>(text).unwrap_or_default()
}

fn tables() -> &'static [(&'static str, BTreeMap<String, String>); 2] {
    static TABLES: OnceLock<[(&'static str, BTreeMap<String, String>); 2]> = OnceLock::new();
    TABLES.get_or_init(|| [("nl", parse(NL_JSON)), ("en", parse(EN_JSON))])
}

static CODE: Mutex<Option<String>> = Mutex::new(None);

pub fn codes() -> [&'static str; 2] {
    ["nl", "en"]
}

pub fn native_name(code: &str) -> &'static str {
    if code.eq_ignore_ascii_case("en") {
        "English"
    } else {
        "Nederlands"
    }
}

pub fn normalize(code: Option<&str>) -> String {
    let trimmed = code.unwrap_or("").trim();
    for (known, _) in tables().iter() {
        if known.eq_ignore_ascii_case(trimmed) {
            return known.to_string();
        }
    }
    DEFAULT.to_string()
}

pub fn set(code: &str) {
    if let Ok(mut guard) = CODE.lock() {
        *guard = Some(normalize(Some(code)));
    }
}

pub fn code() -> String {
    CODE.lock()
        .ok()
        .and_then(|guard| guard.clone())
        .unwrap_or_else(|| DEFAULT.to_string())
}

fn table(code: &str) -> &'static BTreeMap<String, String> {
    let wanted = normalize(Some(code));
    tables()
        .iter()
        .find(|(name, _)| *name == wanted)
        .map(|(_, table)| table)
        .unwrap_or(&tables()[0].1)
}

/// The text for a key, falling back to Dutch and then to a visible marker.
pub fn t(key: &str) -> String {
    if let Some(text) = table(&code()).get(key) {
        return text.clone();
    }
    if let Some(text) = table(DEFAULT).get(key) {
        return text.clone();
    }
    format!("‽{key}")
}

/// All keys of the default language.
pub fn keys() -> Vec<String> {
    table(DEFAULT).keys().cloned().collect()
}

/// Keys the given language is missing (the self-test checks this is empty).
pub fn missing_in(code: &str) -> Vec<String> {
    let wanted = normalize(Some(code));
    table(DEFAULT)
        .keys()
        .filter(|key| !table(&wanted).contains_key(*key))
        .cloned()
        .collect()
}

/// Keys that are not simple strings in the tables (a generator slip, caught by the self-test).
pub fn non_string_entries() -> Vec<String> {
    let mut bad = Vec::new();
    for text in [NL_JSON, EN_JSON] {
        let Ok(Value::Object(map)) = serde_json::from_str::<Value>(text) else {
            bad.push("table is not an object".to_string());
            continue;
        };
        for (key, value) in map {
            if !value.is_string() {
                bad.push(key);
            }
        }
    }
    bad
}
