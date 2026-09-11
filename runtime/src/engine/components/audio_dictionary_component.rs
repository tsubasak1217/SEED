// ============================================================
//  audio_dictionary_component.rs — 音声辞書コンポーネント
//
//  「音の意味（用途）」と「音声ファイルの実体」を分離するための
//  スロット型 ECS コンポーネント。任意のアクタに付けられる。
//
//  ## 何のためにあるか
//  スクリプトや AudioComponent が `assets://sounds/player_attack_01.wav` の
//  ような **生のパス** を直接持つと、素材を差し替えるたびに参照側を
//  すべて書き換える必要がある。本コンポーネントは
//
//      「Player の attack」→ (パス, 既定音量)
//
//  という **意味 → 実体** の対応表（辞書）をシーン上のデータとして持ち、
//  参照側は `Player/attack` という **キー** だけを持つようにする。
//  素材を差し替えたいときは辞書の 1 行を直すだけで全参照に波及する
//  （データドリブン）。
//
//  ## データ構造
//  辞書は「グループ」の配列で、グループは「行」の配列を持つ。
//
//      groups: [
//        { name: "Player", entries: [
//            { usage: "attack", path: "assets://se/atk.wav", volume: 1.0 },
//            { usage: "jump",   path: "assets://se/jmp.wav", volume: 0.8 },
//        ]},
//      ]
//
//  キーは `グループ名 + AUDIO_DICT_KEY_SEPARATOR + 用途名`（例 `Player/attack`）。
//  グループを 2 階層にしているのは、インスペクタで「Player の音」「Enemy の音」と
//  まとめて見せたい＝人間が扱う単位がグループだからである
//  （フラットな 1 枚の表だと、行が増えたときに探せなくなる）。
//
//  ## ECS 理念（データとロジックの分離）
//  本コンポーネントは **対応表のデータのみ** を持ち、
//  「キーを引く」「複数辞書を横断した索引を作る」「重複キーを警告する」
//  といったロジックは一切持たない。それらは
//  `engine::core::audio::dictionary_index`（純関数群）の責務である。
//
//  ## シリアライズ
//  全フィールドに `#[serde(default)]`（非ゼロ既定値は default 関数）を付け、
//  本コンポーネントを知らない旧 `.scene` でも読み込みが失敗しないようにする。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// キーの区切り文字（`グループ名/用途名`）。
///
/// スラッシュにしているのは、ファイルパスと同じ「階層の区切り」という
/// 直感が働くため。用途名・グループ名にこの文字を含めてはならない
/// （含まれた場合の解釈は `dictionary_index::split_key` が定義する）。
pub const AUDIO_DICT_KEY_SEPARATOR: char = '/';

/// 行の音量の既定値（1.0 = 等倍）。
///
/// 「辞書に登録しただけで、そのまま素の音量で鳴る」状態を既定にする。
pub const DEFAULT_AUDIO_DICT_VOLUME: f32 = 1.0;

/// 1 コンポーネントが保持できるグループの最大数。
///
/// 上限が無いと、インスペクタの行生成と IPC 文字列が際限なく膨らむ。
/// ゲーム 1 本分のカテゴリ数（Player / Enemy / UI / Ambient …）を
/// 十分に上回る値として設定している。
pub const MAX_AUDIO_DICT_GROUPS: usize = 64;

/// 1 グループが保持できる行の最大数。
///
/// 「1 キャラクターが持つ効果音の種類」の上限。これを超えるなら
/// グループを分けるべきという設計上の指針でもある。
pub const MAX_AUDIO_DICT_ENTRIES_PER_GROUP: usize = 256;

// ─── デフォルト値関数 ─────────────────────────────────────────

/// 行の音量の既定値を返す（serde の `default = "..."` 用）。
fn default_entry_volume() -> f32 {
    DEFAULT_AUDIO_DICT_VOLUME
}

// ─── AudioDictEntry（辞書の 1 行）─────────────────────────────

/// 音声辞書の 1 行 ＝「用途名 → 音声ファイル + 既定音量」の対応。
///
/// シリアライズ用と ECS 実体で構造が同じ（純粋な値の集まりで揮発状態を
/// 持たない）ため、`AnimClipRef` と同じく型を共有する。
#[derive(Clone, Serialize, Deserialize, Debug, PartialEq)]
pub struct AudioDictEntry {
    /// 用途名（例 `attack`）。グループ名と合わせてキーになる。空 = 未設定（引けない行）。
    #[serde(default)]
    pub usage: String,
    /// 音声ファイルの `assets://` 仮想パス。空 = 未設定（引けない行）。
    #[serde(default)]
    pub path: String,
    /// 既定音量（1.0 = 等倍）。再生側が音量を明示しなかったときに使われる。
    #[serde(default = "default_entry_volume")]
    pub volume: f32,
}

impl Default for AudioDictEntry {
    fn default() -> Self {
        Self {
            usage: String::new(),
            path: String::new(),
            volume: default_entry_volume(),
        }
    }
}

// ─── AudioDictGroup（グループ）────────────────────────────────

/// 音声辞書のグループ ＝「まとまりの名前 + 行の配列」。
///
/// インスペクタはこの単位で見出しを出してまとめて表示する。
#[derive(Clone, Serialize, Deserialize, Debug, Default, PartialEq)]
pub struct AudioDictGroup {
    /// グループ名（例 `Player`）。用途名と合わせてキーになる。空 = 未設定（引けないグループ）。
    #[serde(default)]
    pub name: String,
    /// このグループに属する行の配列。
    #[serde(default)]
    pub entries: Vec<AudioDictEntry>,
}

// ─── AudioDictionaryComponentData（シリアライズ用）────────────

/// AudioDictionaryComponent のシリアライズ用データ。
#[derive(Clone, Serialize, Deserialize, Debug, Default, PartialEq)]
pub struct AudioDictionaryComponentData {
    /// グループの配列（表示順 = 配列順）。
    #[serde(default)]
    pub groups: Vec<AudioDictGroup>,
}

// ─── AudioDictionaryComponent（ECS 実体）─────────────────────

/// 音声辞書コンポーネント（ECS 実体）。
///
/// フィールド構成はシリアライズ用データと同一で、揮発状態を持たない
/// （＝ロード・保存でまったく情報を失わない）。
#[derive(Clone, Debug, Default)]
pub struct AudioDictionaryComponent {
    /// グループの配列（表示順 = 配列順）。
    pub groups: Vec<AudioDictGroup>,
}

impl AudioDictionaryComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    ///
    /// 上限（グループ数・行数）を超える入力は、壊れたシーン・壊れた IPC を
    /// そのまま取り込まないよう **切り詰める**（エラーにはしない。
    /// 上限超過は設計ミスであって読み込み不能な破損ではないため）。
    pub fn from_data(data: AudioDictionaryComponentData) -> Self {
        let mut groups = data.groups;
        groups.truncate(MAX_AUDIO_DICT_GROUPS);
        for group in &mut groups {
            group.entries.truncate(MAX_AUDIO_DICT_ENTRIES_PER_GROUP);
        }
        Self { groups }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> AudioDictionaryComponentData {
        AudioDictionaryComponentData {
            groups: self.groups.clone(),
        }
    }
}

impl Component for AudioDictionaryComponent {}

// ─── テスト ──────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 旧 `.scene` 互換: `groups` が無い JSON でも読める（`#[serde(default)]` の検証）。
    #[test]
    fn deserializes_without_groups() {
        let d: AudioDictionaryComponentData = serde_json::from_str("{}").expect("読めるはず");
        assert!(d.groups.is_empty());
    }

    /// 行の `volume` を省略した JSON は既定音量（1.0）で補完される。
    #[test]
    fn entry_volume_defaults_to_one() {
        let json = r#"{"groups":[{"name":"Player","entries":[{"usage":"attack","path":"assets://a.wav"}]}]}"#;
        let d: AudioDictionaryComponentData = serde_json::from_str(json).expect("読めるはず");
        assert_eq!(d.groups[0].entries[0].volume, DEFAULT_AUDIO_DICT_VOLUME);
    }

    /// from_data → to_data のラウンドトリップで情報が落ちない（揮発状態を持たないことの確認）。
    #[test]
    fn from_to_data_roundtrip_keeps_everything() {
        let data = AudioDictionaryComponentData {
            groups: vec![AudioDictGroup {
                name: "Player".to_string(),
                entries: vec![AudioDictEntry {
                    usage: "attack".to_string(),
                    path: "assets://a.wav".to_string(),
                    volume: 0.25,
                }],
            }],
        };
        let back = AudioDictionaryComponent::from_data(data.clone()).to_data();
        assert_eq!(back, data);
    }
}
