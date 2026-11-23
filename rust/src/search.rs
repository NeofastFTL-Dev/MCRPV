//! The searchable index of recipe keys: display name plus key, with a cheap scoring so an exact
//! match beats a prefix, which beats a substring anywhere.

use crate::names::{normalize_name, Names};

pub struct Entry {
    pub key: String,
    pub display: String,
    /// Lowercased display name, kept for matching.
    lower_display: String,
    lower_key: String,
}

pub struct SearchIndex {
    entries: Vec<Entry>,
}

impl SearchIndex {
    pub fn build(keys: impl IntoIterator<Item = String>, names: &Names) -> Self {
        let mut entries: Vec<Entry> = keys
            .into_iter()
            .map(|key| {
                let display = names.display(&key);
                Entry {
                    lower_display: display.to_lowercase(),
                    lower_key: key.to_lowercase(),
                    key,
                    display,
                }
            })
            .collect();
        entries.sort_by(|a, b| a.lower_display.cmp(&b.lower_display));
        Self { entries }
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Up to `limit` matches, best first. An empty query returns the start of the list.
    pub fn search(&self, query: &str, limit: usize) -> Vec<&Entry> {
        let query = query.trim().to_lowercase();
        if query.is_empty() {
            return self.entries.iter().take(limit).collect();
        }

        let normalized = normalize_name(&query);

        let mut scored: Vec<(i32, &Entry)> = Vec::new();
        for entry in &self.entries {
            let score = if entry.lower_key == query || entry.lower_display == query {
                0
            } else if entry.lower_key.starts_with(&query) || entry.lower_display.starts_with(&query) {
                1
            } else if !normalized.is_empty()
                && (entry.lower_key == normalized || entry.lower_key.contains(&normalized))
            {
                2
            } else if entry.lower_display.contains(&query) {
                3
            } else if !normalized.is_empty() && normalize_name(&entry.display).contains(&normalized) {
                4
            } else {
                continue;
            };
            scored.push((score, entry));
        }

        scored.sort_by(|a, b| a.0.cmp(&b.0).then_with(|| a.1.lower_display.cmp(&b.1.lower_display)));
        scored.into_iter().take(limit).map(|(_, entry)| entry).collect()
    }
}
