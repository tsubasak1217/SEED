// ============================================================
//  dictionary_index.rs — 音声辞書のキー索引（純粋ロジック）
//
//  AudioDictionaryComponent（＝データ）を横断して
//  「キー → (パス, 既定音量)」の索引を作るロジック層。
//
//  ## なぜコンポーネントと分けるか（ECS 理念）
//  コンポーネントは対応表のデータのみを持つ。
//  「シーン内に複数ある辞書をどうマージするか」「重複キーをどう扱うか」は
//  シーン全体を見渡す横断ロジックであり、1 コンポーネントの責務ではない。
//  ここを純関数に切り出すことで、World もシーンも無しに `cargo test` で
//  検証できる（本ファイル末尾にテストを同梱している）。
//
//  ## キー解決規則（仕様）
//  - キーは `グループ名/用途名`（例 `Player/attack`）。
//  - シーン内の全 AudioDictionary（アクティブ世界線）を **DFS 順** に走査し、
//    完全一致で **先勝ち**（先に見つかった辞書の行が採用される）。
//  - グループ名・用途名・パスのいずれかが空の行は索引に入れない
//    （「作りかけの行」を誤って引かせないため）。
//  - 同名キーが複数の辞書にあった場合は重複として報告し、
//    呼び出し側が起動時に 1 度だけ警告する。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{
    AudioDictionaryComponent, AUDIO_DICT_KEY_SEPARATOR,
};

// ─── AudioDictResolved ───────────────────────────────────────

/// キーを引いた結果（辞書 1 行の「実体」部分）。
#[derive(Clone, Debug, PartialEq)]
pub struct AudioDictResolved {
    /// 音声ファイルの `assets://` 仮想パス。
    pub path: String,
    /// 既定音量（1.0 = 等倍）。再生側が音量を明示しなかったときに使われる。
    pub volume: f32,
}

// ─── AudioDictionaryIndex ────────────────────────────────────

/// シーン全体の音声辞書を 1 枚に畳んだ索引。
///
/// 再生のたびにアクタツリーを走査すると O(アクタ数) になるため、
/// シーンロード・コンポーネント変更のタイミングで作り直して保持する。
#[derive(Clone, Debug, Default)]
pub struct AudioDictionaryIndex {
    /// キー（`グループ/用途`）→ 実体。
    entries: HashMap<String, AudioDictResolved>,
}

impl AudioDictionaryIndex {
    /// 登録キー数を返す（ログ・テスト用）。
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    /// 索引が空か（キーが 1 つも無いか）を返す。
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// キーを引く。未登録なら None。
    pub fn get(&self, key: &str) -> Option<&AudioDictResolved> {
        self.entries.get(key)
    }

    /// 登録済みキーの一覧を辞書順で返す（診断ログ・テスト用）。
    pub fn sorted_keys(&self) -> Vec<String> {
        let mut keys: Vec<String> = self.entries.keys().cloned().collect();
        keys.sort();
        keys
    }
}

// ─── ビルド結果 ──────────────────────────────────────────────

/// `build_index` の結果。索引本体と、重複していたキーの一覧。
#[derive(Clone, Debug, Default)]
pub struct AudioDictionaryIndexBuild {
    /// できあがった索引（先勝ち）。
    pub index: AudioDictionaryIndex,
    /// 複数の辞書に存在したキー（辞書順・重複なし）。呼び出し側が警告に使う。
    pub duplicate_keys: Vec<String>,
}

// ─── キーの組み立て・分解 ────────────────────────────────────

/// グループ名と用途名からキー文字列を組み立てる。
///
/// 索引・IPC・スクリプト API のすべてがこの 1 か所を通ることで、
/// 区切り文字の表記ゆれ（`.` と `/` の混在など）を構造的に防ぐ。
pub fn make_key(group: &str, usage: &str) -> String {
    let mut key = String::with_capacity(group.len() + usage.len() + 1);
    key.push_str(group);
    key.push(AUDIO_DICT_KEY_SEPARATOR);
    key.push_str(usage);
    key
}

/// キー文字列を (グループ名, 用途名) へ分解する。区切り文字が無ければ None。
///
/// 用途名側に区切り文字が含まれていた場合は **最初の区切りで割る**
/// （グループ名は区切りを含めない、という規約を優先する）。
pub fn split_key(key: &str) -> Option<(&str, &str)> {
    key.split_once(AUDIO_DICT_KEY_SEPARATOR)
}

// ─── 単一辞書の引き当て ──────────────────────────────────────

/// 1 つの辞書コンポーネントの中からキーを引く（完全一致・先勝ち）。
///
/// スクリプトの `AudioDictionary` ハンドル（＝特定の辞書を名指しで引く）と、
/// 索引構築の両方がこの関数を経由するので、引き当て規則が 1 か所に閉じる。
/// 空の用途名・空のパスの行は「未完成の行」として無視する。
pub fn lookup_in_component(
    dict: &AudioDictionaryComponent,
    key: &str,
) -> Option<AudioDictResolved> {
    let (group_name, usage) = split_key(key)?;
    if group_name.is_empty() || usage.is_empty() {
        return None;
    }
    for group in &dict.groups {
        if group.name != group_name {
            continue;
        }
        for entry in &group.entries {
            if entry.usage == usage && !entry.path.is_empty() {
                return Some(AudioDictResolved {
                    path: entry.path.clone(),
                    volume: entry.volume,
                });
            }
        }
    }
    None
}

// ─── 索引の構築 ──────────────────────────────────────────────

/// 複数の辞書コンポーネントから索引を作る（先勝ち・重複キーを報告）。
///
/// `dicts` は **走査順がそのまま優先順** になる（DFS 順で渡すこと）。
/// 呼び出し側（App）はアクティブ世界線のアクタツリーを DFS して渡す。
pub fn build_index<'a, I>(dicts: I) -> AudioDictionaryIndexBuild
where
    I: IntoIterator<Item = &'a AudioDictionaryComponent>,
{
    let mut index = AudioDictionaryIndex::default();
    // 重複キーは「同じキーが 2 回以上出た」ことだけ分かればよいので集合で持つ
    let mut duplicates: std::collections::BTreeSet<String> = Default::default();

    for dict in dicts {
        for group in &dict.groups {
            // グループ名が空のグループは丸ごと無視する（キーを作れないため）
            if group.name.is_empty() {
                continue;
            }
            for entry in &group.entries {
                // 用途名・パスのどちらかが空の行は「未完成」として無視する
                if entry.usage.is_empty() || entry.path.is_empty() {
                    continue;
                }
                let key = make_key(&group.name, &entry.usage);
                if index.entries.contains_key(&key) {
                    // 先勝ち: 既にあるものを残し、重複として記録する
                    duplicates.insert(key);
                    continue;
                }
                index.entries.insert(
                    key,
                    AudioDictResolved {
                        path: entry.path.clone(),
                        volume: entry.volume,
                    },
                );
            }
        }
    }

    AudioDictionaryIndexBuild {
        index,
        duplicate_keys: duplicates.into_iter().collect(),
    }
}

// ─── テスト ──────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::{AudioDictEntry, AudioDictGroup};

    /// テスト用の辞書を組み立てる補助関数。
    /// `groups` は (グループ名, [(用途名, パス, 音量)]) の配列。
    fn dict(groups: &[(&str, &[(&str, &str, f32)])]) -> AudioDictionaryComponent {
        AudioDictionaryComponent {
            groups: groups
                .iter()
                .map(|(name, entries)| AudioDictGroup {
                    name: (*name).to_string(),
                    entries: entries
                        .iter()
                        .map(|(usage, path, volume)| AudioDictEntry {
                            usage: (*usage).to_string(),
                            path: (*path).to_string(),
                            volume: *volume,
                        })
                        .collect(),
                })
                .collect(),
        }
    }

    #[test]
    fn make_and_split_key_roundtrip() {
        let key = make_key("Player", "attack");
        assert_eq!(key, "Player/attack");
        assert_eq!(split_key(&key), Some(("Player", "attack")));
        // 区切りが無いキーは分解できない
        assert_eq!(split_key("attack"), None);
    }

    #[test]
    fn lookup_in_component_finds_exact_match() {
        let d = dict(&[(
            "Player",
            &[("attack", "assets://se/atk.wav", 0.8), ("jump", "assets://se/jmp.wav", 1.0)],
        )]);
        let hit = lookup_in_component(&d, "Player/jump").expect("引けるはず");
        assert_eq!(hit.path, "assets://se/jmp.wav");
        assert_eq!(hit.volume, 1.0);
        // グループ違い・用途違い・区切りなしはすべて None
        assert!(lookup_in_component(&d, "Enemy/jump").is_none());
        assert!(lookup_in_component(&d, "Player/dash").is_none());
        assert!(lookup_in_component(&d, "jump").is_none());
    }

    #[test]
    fn lookup_skips_entries_without_path() {
        // パスが空の行は「未完成」として引けない
        let d = dict(&[("Player", &[("attack", "", 1.0)])]);
        assert!(lookup_in_component(&d, "Player/attack").is_none());
    }

    #[test]
    fn build_index_merges_multiple_dictionaries() {
        let a = dict(&[("Player", &[("attack", "assets://a.wav", 1.0)])]);
        let b = dict(&[("Enemy", &[("roar", "assets://b.wav", 0.5)])]);
        let built = build_index([&a, &b]);
        assert_eq!(built.index.len(), 2);
        assert_eq!(built.index.get("Player/attack").unwrap().path, "assets://a.wav");
        assert_eq!(built.index.get("Enemy/roar").unwrap().volume, 0.5);
        assert!(built.duplicate_keys.is_empty());
    }

    #[test]
    fn build_index_first_wins_and_reports_duplicates() {
        let first = dict(&[("Player", &[("attack", "assets://first.wav", 1.0)])]);
        let second = dict(&[("Player", &[("attack", "assets://second.wav", 0.2)])]);
        let built = build_index([&first, &second]);
        // 先に渡した辞書が勝つ
        assert_eq!(
            built.index.get("Player/attack").unwrap().path,
            "assets://first.wav"
        );
        assert_eq!(built.duplicate_keys, vec!["Player/attack".to_string()]);
    }

    #[test]
    fn build_index_skips_incomplete_rows() {
        let d = dict(&[
            ("", &[("attack", "assets://a.wav", 1.0)]),          // グループ名が空
            ("Player", &[("", "assets://b.wav", 1.0)]),          // 用途名が空
            ("Player", &[("jump", "", 1.0)]),                    // パスが空
            ("Player", &[("dash", "assets://c.wav", 1.0)]),      // 正常行
        ]);
        let built = build_index([&d]);
        assert_eq!(built.index.sorted_keys(), vec!["Player/dash".to_string()]);
    }

    #[test]
    fn from_data_truncates_over_limits() {
        use crate::engine::components::{
            AudioDictionaryComponentData, MAX_AUDIO_DICT_ENTRIES_PER_GROUP, MAX_AUDIO_DICT_GROUPS,
        };
        // 上限を超えるグループ・行は切り詰められる（壊れたデータを丸ごと取り込まない）
        let data = AudioDictionaryComponentData {
            groups: (0..MAX_AUDIO_DICT_GROUPS + 5)
                .map(|g| AudioDictGroup {
                    name: format!("G{g}"),
                    entries: (0..MAX_AUDIO_DICT_ENTRIES_PER_GROUP + 3)
                        .map(|e| AudioDictEntry {
                            usage: format!("U{e}"),
                            path: "assets://x.wav".to_string(),
                            volume: 1.0,
                        })
                        .collect(),
                })
                .collect(),
        };
        let comp = AudioDictionaryComponent::from_data(data);
        assert_eq!(comp.groups.len(), MAX_AUDIO_DICT_GROUPS);
        assert_eq!(comp.groups[0].entries.len(), MAX_AUDIO_DICT_ENTRIES_PER_GROUP);
    }
}
