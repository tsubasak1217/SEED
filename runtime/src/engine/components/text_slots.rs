// ============================================================
//  text_slots.rs — TextComponent の「差し込みスロット」データ
//
//  【役割】
//  本文（`TextComponent.content`）に書かれたプレースホルダ記法
//  （`{image}` / `{color}` / `{string}` / `{num}`）1 つにつき 1 件の
//  **差し込み値**を持つ配列の、データ定義だけを担う。
//
//  【なぜ本文とスロットを分けるのか】
//  「どこに何を差し込むか（＝書式・順序）」は本文が正典であり、
//  「何を差し込むか（＝画像パス・色・文字列・数値・バインド先）」は
//  インスペクタやスクリプトが編集する別の関心事である。
//  両者を 1 つの文字列に混ぜると、本文を書き換えるたびに値まで作り直しになり、
//  逆に値へ書式を持たせると本文の見た目がデータ側に散る。
//  そこで **本文 = 形・スロット配列 = 中身** と役割を割り、
//  本文が変わったときは `remap_slots` で「種類が一致する既存値だけ」を引き継ぐ。
//
//  【ECS の位置づけ】
//  ここはデータのみ（ECS 理念）。記法の解析は
//  `core::font::inline::slot_markup`、展開は `core::font::inline::doc` が行う。
//
//  【シリアライズ互換】
//  全フィールドに serde の default を付けること。
//  スロットを 1 件も持たない旧 .scene は空配列として読める。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::core::font::inline::slot_markup::{required_slot_count, SlotSpec};

// ─── 定数（マジックナンバー禁止）───────────────────────────────

/// 色スロットの既定色（不透明な白 = 「色を変えない」と同じ見え方）。
pub const DEFAULT_SLOT_COLOR: [f32; 4] = [1.0, 1.0, 1.0, 1.0];

/// 数値スロットの既定値。
pub const DEFAULT_SLOT_NUM: f32 = 0.0;

/// 色成分数（RGBA）。
pub const SLOT_COLOR_COMPONENTS: usize = 4;

// ─── TextSlotKind ─────────────────────────────────────────────

/// スロットが差し込む値の種類。
///
/// **本文の記法が正典**であり、この値は「再マッピング時に既存値を引き継いで
/// よいか」を判定するためのヒントとして保存する（種類が変わったスロットは
/// 引き継がず既定値へ戻す）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TextSlotKind {
    /// 本文の `{image}` — 1 文字ぶんの画像を差し込む。
    Image,
    /// 本文の `{color}`〜`{/color}` — 区間の文字色を差し替える。
    Color,
    /// 本文の `{string}` — 文字列を差し込む。
    #[default]
    String,
    /// 本文の `{num}` / `{num.3}` — 数値を書式つきで差し込む。
    Num,
}

impl TextSlotKind {
    /// IPC / スクリプト API で使う小文字キー（serde の表現と一致させること）。
    pub fn key(self) -> &'static str {
        match self {
            Self::Image => "image",
            Self::Color => "color",
            Self::String => "string",
            Self::Num => "num",
        }
    }

    /// キー文字列から復元する。未知の値は None（呼び出し側が既存値を保つ）。
    pub fn from_key(s: &str) -> Option<Self> {
        match s {
            "image" => Some(Self::Image),
            "color" => Some(Self::Color),
            "string" => Some(Self::String),
            "num" => Some(Self::Num),
            _ => None,
        }
    }
}

// ─── SlotValue ────────────────────────────────────────────────

/// スロット 1 件の「解決済みの値」。
///
/// 展開器（`inline::doc::build_doc_with_slots`）は**この値を引数で受け取る**
/// 純関数であり、バインドの解決（スクリプト・組込コンポーネントの読み取り）は
/// 一切行わない。解決は `app::text_expand` の `SlotValueProvider` の責務。
#[derive(Clone, Debug, PartialEq)]
pub enum SlotValue {
    /// 文字列として差し込む値。
    Str(String),
    /// 数値として差し込む値（書式は本文の小数指定が決める）。
    Num(f32),
}

// ─── TextSlotData ─────────────────────────────────────────────

/// 差し込みスロット 1 件。
///
/// 4 種類ぶんのフィールドを 1 つの構造体に同居させているのは、
/// 本文の記法を書き換えたときにスロットの**種類だけが変わる**ケースが多く、
/// 種類ごとに別配列を持つと再マッピングが 4 系統に増えて壊れやすくなるため。
/// 未使用のフィールドは既定値のまま残り、種類を戻せば値も戻る。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct TextSlotData {
    /// このスロットが最後に持っていた種類（再マッピングのヒント）。
    #[serde(default)]
    pub kind: TextSlotKind,
    /// 画像スロット用: 画像の assets:// パス、またはアイコンセットのアイコン名。
    ///
    /// スキーム区切り（コロン + スラッシュ 2 つ）を含めば画像パスの直接指定、
    /// 含まなければアイコン名として `TextComponent.icon_set` の表から引く
    /// （既存の `[img:]` / `[icon:]` とまったく同じ解決規則）。
    #[serde(default)]
    pub path: String,
    /// 色スロット用: 区間へ適用する RGBA（0..1）。
    #[serde(default = "default_slot_color")]
    pub rgba: [f32; SLOT_COLOR_COMPONENTS],
    /// バインド先（"アクタ名|スロット名|変数名"。空文字 = バインドなし）。
    ///
    /// 書式は `binding::resolve` と共通（アクタ改名の追従も同じ規則で行う）。
    /// 解決に失敗したら `text` / `num` のフォールバック値を使う。
    #[serde(default)]
    pub bind: String,
    /// 文字列スロット用のフォールバック文字列（バインド未設定・解決失敗時）。
    #[serde(default)]
    pub text: String,
    /// 数値スロット用のフォールバック数値（バインド未設定・解決失敗時）。
    #[serde(default = "default_slot_num")]
    pub num: f32,
}

/// 色の serde 既定値。
fn default_slot_color() -> [f32; SLOT_COLOR_COMPONENTS] {
    DEFAULT_SLOT_COLOR
}

/// 数値の serde 既定値。
fn default_slot_num() -> f32 {
    DEFAULT_SLOT_NUM
}

impl Default for TextSlotData {
    fn default() -> Self {
        Self {
            kind: TextSlotKind::default(),
            path: String::new(),
            rgba: DEFAULT_SLOT_COLOR,
            bind: String::new(),
            text: String::new(),
            num: DEFAULT_SLOT_NUM,
        }
    }
}

impl TextSlotData {
    /// 指定した種類の既定スロットを作る。
    pub fn new_of_kind(kind: TextSlotKind) -> Self {
        Self { kind, ..Self::default() }
    }

    /// バインドを無視した「自前の値」を返す（＝フォールバック値）。
    ///
    /// 種類が Image / Color のときは差し込む文字列を持たないため、
    /// 便宜上 `Str(text)` を返す（展開器はこれを使わない）。
    pub fn fallback_value(&self) -> SlotValue {
        match self.kind {
            TextSlotKind::Num => SlotValue::Num(self.num),
            _ => SlotValue::Str(self.text.clone()),
        }
    }
}

// ─── 再マッピング ─────────────────────────────────────────────

/// 本文から得た記法列（`specs`）に合わせてスロット配列を組み直す（純関数）。
///
/// 規則:
/// - 出来上がる配列の長さは `slot_markup::required_slot_count`
///   （= max(記法の出現数, 明示番号の最大 + 1)）。
/// - 添字 i に記法が付いている場合、既存 existing[i] の種類が一致すれば
///   **値をそのまま引き継ぐ**。一致しなければその種類の既定値へ戻す。
/// - 添字 i にどの記法も付いていない（明示番号で飛ばされた穴）場合は、
///   既存値があればそのまま残す（本文を編集して番号を戻したときに復活する）。
///
/// 「種類が変わったら既定値へ戻す」のは、たとえば色スロットだった位置が
/// 画像スロットになったときに、意味の無い RGBA を引き継いで混乱させないため。
pub fn remap_slots(existing: &[TextSlotData], specs: &[SlotSpec]) -> Vec<TextSlotData> {
    let len = required_slot_count(specs);
    let mut out: Vec<TextSlotData> = Vec::with_capacity(len);
    for i in 0..len {
        // 同じ添字を複数の記法が指す場合は**最初の記法**の種類を採用する
        // （本文の登場順が正典。後続は同じスロットを別書式で読むだけ）。
        let kind = specs.iter().find(|s| s.index == i).map(|s| s.kind);
        let prev = existing.get(i);
        out.push(match (kind, prev) {
            // 記法があり、既存の種類も一致 → 値を丸ごと引き継ぐ。
            (Some(k), Some(p)) if p.kind == k => p.clone(),
            // 記法があるが種類が違う（または既存なし）→ その種類の既定値。
            (Some(k), _) => TextSlotData::new_of_kind(k),
            // 記法が無い穴 → 既存値をそのまま残す（無ければ既定値）。
            (None, Some(p)) => p.clone(),
            (None, None) => TextSlotData::default(),
        });
    }
    out
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::font::inline::slot_markup::slot_specs;

    /// 欠落フィールドのある JSON でも既定値で読める（旧 .scene 互換）。
    #[test]
    fn legacy_json_loads_with_defaults() {
        let d: TextSlotData = serde_json::from_str("{}").expect("空 JSON が読める");
        assert_eq!(d.kind, TextSlotKind::String);
        assert_eq!(d.rgba, DEFAULT_SLOT_COLOR);
        assert_eq!(d.num, DEFAULT_SLOT_NUM);
        assert!(d.path.is_empty() && d.bind.is_empty() && d.text.is_empty());
    }

    /// serde の往復で値が壊れない。
    #[test]
    fn json_roundtrips() {
        let src = TextSlotData {
            kind: TextSlotKind::Num,
            path: "assets://ui/coin.png".into(),
            rgba: [0.5, 0.25, 0.125, 1.0],
            bind: "Player|Status|hp".into(),
            text: "なし".into(),
            num: 12.5,
        };
        let json = serde_json::to_string(&src).unwrap();
        let back: TextSlotData = serde_json::from_str(&json).unwrap();
        assert_eq!(back, src);
    }

    /// 種類のワイヤ表現が serde の表現と一致する（IPC とシーンで綴りがズレない）。
    #[test]
    fn kind_key_matches_serde() {
        for k in [
            TextSlotKind::Image,
            TextSlotKind::Color,
            TextSlotKind::String,
            TextSlotKind::Num,
        ] {
            let json = serde_json::to_string(&k).unwrap();
            assert_eq!(json, format!("\"{}\"", k.key()));
            assert_eq!(TextSlotKind::from_key(k.key()), Some(k));
        }
        assert_eq!(TextSlotKind::from_key("nope"), None);
    }

    /// 記法が増えたら配列が伸び、既存値は種類一致のぶんだけ残る。
    #[test]
    fn remap_grows_and_keeps_matching_kinds() {
        let existing = vec![TextSlotData {
            kind: TextSlotKind::Num,
            num: 42.0,
            ..Default::default()
        }];
        let specs = slot_specs("{num} 円 / {string}");
        let out = remap_slots(&existing, &specs);
        assert_eq!(out.len(), 2);
        assert_eq!(out[0].num, 42.0, "種類が一致するので値が残る");
        assert_eq!(out[1].kind, TextSlotKind::String);
    }

    /// 記法が減ったら配列も縮む。
    #[test]
    fn remap_shrinks() {
        let existing = vec![
            TextSlotData::new_of_kind(TextSlotKind::Num),
            TextSlotData::new_of_kind(TextSlotKind::String),
        ];
        let out = remap_slots(&existing, &slot_specs("{num}"));
        assert_eq!(out.len(), 1);
    }

    /// 種類が変わった位置は既定値へ戻る（意味の無い値を引き継がない）。
    #[test]
    fn remap_resets_on_kind_change() {
        let existing = vec![TextSlotData {
            kind: TextSlotKind::Num,
            num: 42.0,
            text: "残らない".into(),
            ..Default::default()
        }];
        let out = remap_slots(&existing, &slot_specs("{string}"));
        assert_eq!(out.len(), 1);
        assert_eq!(out[0].kind, TextSlotKind::String);
        assert_eq!(out[0].num, DEFAULT_SLOT_NUM);
        assert_eq!(out[0].text, "");
    }

    /// 明示番号で空いた穴は既存値を保つ（番号を戻したら復活する）。
    #[test]
    fn remap_keeps_untouched_holes() {
        let mk = |n: f32| TextSlotData {
            kind: TextSlotKind::Num,
            num: n,
            ..Default::default()
        };
        let existing = vec![mk(1.0), mk(2.0), mk(3.0)];
        // 添字 2 だけを参照する本文（長さは明示番号 2 + 1 = 3）。
        let out = remap_slots(&existing, &slot_specs("{num:2}"));
        assert_eq!(out.len(), 3);
        assert_eq!(out[0].num, 1.0, "穴（記法なし）は既存値のまま");
        assert_eq!(out[1].num, 2.0);
        assert_eq!(out[2].num, 3.0, "種類一致で引き継がれる");
    }

    /// 記法が 1 つも無ければ空配列になる。
    #[test]
    fn remap_to_empty_when_no_markup() {
        let existing = vec![TextSlotData::new_of_kind(TextSlotKind::Num)];
        assert!(remap_slots(&existing, &slot_specs("ただの文字列")).is_empty());
    }
}
