//! Item names: the `names.json` cache written by the .NET app (recipe key → display name), with
//! the same Title Case fallback for anything unknown, plus the recipe-key normaliser the planner
//! and the search use.

use regex::Regex;
use serde_json::Value;
use std::collections::HashMap;
use std::path::Path;
use std::sync::OnceLock;

#[derive(Default, Clone)]
pub struct Names {
    /// Lowercased recipe key → display name.
    map: HashMap<String, String>,
}

impl Names {
    pub fn load_map(map: HashMap<String, String>) -> Self {
        let mut names = Self::default();
        names.merge_map(map);
        names
    }

    pub fn merge_map(&mut self, map: HashMap<String, String>) {
        for (key, value) in map {
            if key.trim().is_empty() || value.trim().is_empty() {
                continue;
            }
            self.map.insert(key.to_lowercase(), value);
        }
    }

    /// Reads the `NameCache` file (`{"Version":3,"Names":{…}}`); ignores anything unreadable.
    pub fn load_file(path: &Path) -> Self {
        let Ok(text) = std::fs::read_to_string(path) else {
            return Self::default();
        };
        let Ok(Value::Object(root)) = serde_json::from_str::<serde_json::Value>(&text) else {
            return Self::default();
        };
        let mut names = Self::default();
        if let Some(Value::Object(map)) = root.get("Names") {
            let flat: HashMap<String, String> = map
                .iter()
                .filter_map(|(key, value)| value.as_str().map(|text| (key.clone(), text.to_string())))
                .collect();
            names.merge_map(flat);
        }
        names
    }

    pub fn len(&self) -> usize {
        self.map.len()
    }

    pub fn is_empty(&self) -> bool {
        self.map.is_empty()
    }

    pub fn lookup(&self, key: &str) -> Option<&String> {
        self.map.get(&key.to_lowercase())
    }

    /// Display name for a recipe key: the cache, then the normalised key, then Title Case.
    pub fn display(&self, key: &str) -> String {
        if key.trim().is_empty() {
            return String::new();
        }
        if let Some(nice) = self.lookup(key) {
            return nice.clone();
        }

        let normalized = normalize_name(key);
        if !normalized.is_empty() {
            if let Some(nice) = self.map.get(&normalized) {
                return nice.clone();
            }
        }

        pretty(key)
    }
}

/// `"minecraft:Iron Ingot{...}"` → `"iron_ingot"`, the same cleaning the planner does.
pub fn normalize_name(raw: &str) -> String {
    static STACK: OnceLock<Regex> = OnceLock::new();
    static NOT_ALLOWED: OnceLock<Regex> = OnceLock::new();
    static REPEATED: OnceLock<Regex> = OnceLock::new();

    if raw.trim().is_empty() {
        return String::new();
    }

    let mut text = raw.trim().trim_matches(|c| c == '"' || c == '\'' || c == ',' || c == ';' || c == ' ').to_string();

    if let Some(cut) = text.find(['{', '[', '@']) {
        if cut > 0 {
            text.truncate(cut);
        }
    }

    // JEI sometimes stores stacks as "minecraft:stone 1 0" (id damage count).
    let stack = STACK.get_or_init(|| Regex::new(r"^(\S+)(?:\s+\d+)+$").unwrap());
    if let Some(captures) = stack.captures(&text) {
        text = captures[1].to_string();
    }

    // "minecraft:stone:1" → "minecraft:stone" (only when the extra parts are numbers).
    let parts: Vec<&str> = text.split(':').collect();
    if parts.len() >= 3 && parts[2..].iter().all(|part| part.parse::<i64>().is_ok()) {
        text = format!("{}:{}", parts[0], parts[1]);
    }

    if let Some(index) = text.rfind(':') {
        text = text[index + 1..].to_string();
    }

    let lowered = text
        .to_lowercase()
        .replace([' ', '-', '.'], "_");
    let replaced = NOT_ALLOWED.get_or_init(|| Regex::new(r"[^a-z0-9_]").unwrap()).replace_all(&lowered, "_");
    let collapsed = REPEATED.get_or_init(|| Regex::new(r"_+").unwrap()).replace_all(&replaced, "_");
    collapsed.trim_matches('_').to_string()
}

/// `"iron_ingot"` → `"Iron Ingot"`.
pub fn pretty(key: &str) -> String {
    let text = key.replace('_', " ");
    let words: Vec<String> = text
        .split_whitespace()
        .map(|word| {
            if word.chars().count() <= 2 && word.chars().all(|c| c.is_ascii_digit()) {
                word.to_string()
            } else {
                let mut chars = word.chars();
                match chars.next() {
                    Some(first) => format!("{}{}", first.to_uppercase(), chars.as_str().to_lowercase()),
                    None => String::new(),
                }
            }
        })
        .collect();
    words.join(" ")
}
