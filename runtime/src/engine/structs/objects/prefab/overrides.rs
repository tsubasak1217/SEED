// ============================================================
//  overrides.rs — プレハブオーバーライドのデータ構造
//
//  `.scene` の各アクタデータ（ActorData::prefab_overrides）に保存される差分の形。
//  **全フィールドに #[serde(default)] を付ける**（このフィールドを持たない
//  既存 `.scene` が読めなくなると全シーンが壊れるため）。
//  空のときは skip_serializing_if で書き出さない＝旧シーンとバイト互換を保つ。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::structs::objects::actor::{ActorData, ComponentSlotData};

/// スケールが実質 0（逆行列が特異）とみなす閾値。
///
/// プレハブ再展開の行列補正（delta = M_scene * M_file^-1）で、
/// プレハブ本体側のスケールが 0 のときに逆行列が発散するのを防ぐガードに使う。
/// 差分抽出（extract.rs）と再展開（prefab_ops.rs）で **同じ判定**を使うため
/// ここを唯一の定義とする。
pub const SINGULAR_SCALE_EPS: f32 = 1e-7;

// ─── NodeStep ─────────────────────────────────────────────────────────────────

/// プレハブインスタンス内のノード（子アクタ）を 1 段たどるためのパス要素。
///
/// 【設計】
/// インデックスだけだとプレハブ本体の子順序が変わった瞬間に差分が別ノードへ
/// 誤爆する。名前だけだと同名兄弟を区別できない。そこで **両方**を保持し、
/// 「同じ位置に同じ名前があればそれ、無ければ名前で探す」という段階的解決を行う
/// （解決処理は再適用側 `prefab_apply.rs`）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct NodeStep {
    /// 親の children 配列における位置（プレハブ本体基準）
    #[serde(default)]
    pub index: u32,
    /// そのノードの名前（インデックスがずれた場合のフォールバック照合用）
    #[serde(default)]
    pub name:  String,
}

// ─── ComponentKey ─────────────────────────────────────────────────────────────

/// 1 ノード内でコンポーネント 1 個を一意に指すキー。
///
/// 同型コンポーネントの複数持ち（例: ModelComponent × 2）に対応するため、
/// 型タグ＋スロット名だけでなく「同じ (型タグ, スロット名) の中での出現順」を持つ。
#[derive(Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct ComponentKey {
    /// シリアライズ表現上の型タグ（`ComponentData::type_tag()`。例 "ColliderComponent"）
    #[serde(default, rename = "type")]
    pub type_tag: String,
    /// スロット名（ユーザー命名。例 "Body"）
    #[serde(default)]
    pub name:     String,
    /// 同じ (type_tag, name) の組の中での 0 始まりの出現順
    #[serde(default)]
    pub ordinal:  u32,
}

impl ComponentKey {
    /// キーを生成するヘルパー。
    pub fn new(type_tag: impl Into<String>, name: impl Into<String>, ordinal: u32) -> Self {
        Self { type_tag: type_tag.into(), name: name.into(), ordinal }
    }
}

// ─── ComponentOverride ────────────────────────────────────────────────────────

/// コンポーネント 1 個分のオーバーライド（値の上書き／追加の両方に使う）。
///
/// `slot` はスロットデータ丸ごと（名前・コンポーネント本体・enabled フラグ）を
/// 保持する。再適用時はこの内容でスロットを作り直すため、部分マージは行わない。
// ComponentSlotData（ComponentData）は Debug を実装しないため Debug は導出しない。
#[derive(Clone, Serialize, Deserialize)]
pub struct ComponentOverride {
    /// インスタンスルートからの相対ノードパス（空＝インスタンスルート自身）
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub path: Vec<NodeStep>,
    /// 対象コンポーネントの識別キー
    #[serde(default)]
    pub key:  ComponentKey,
    /// 保存されたスロット内容（再適用時はこの内容でスロットを再構築する）
    pub slot: ComponentSlotData,
}

// ─── ChildOverride ────────────────────────────────────────────────────────────

/// シーン側で追加された子アクタ 1 本分のオーバーライド。
///
/// 追加された子はプレハブ本体に対応物が無いため、サブツリー全体をそのまま保持する。
/// 追加子の内部にさらに差分を持つ必要は無い（丸ごと保存されているため）。
// ActorData は Debug を実装しないため Debug は導出しない。
#[derive(Clone, Serialize, Deserialize)]
pub struct ChildOverride {
    /// 追加先の親ノードへのパス（空＝インスタンスルート直下）
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub parent_path: Vec<NodeStep>,
    /// 親の children 配列における挿入位置（範囲外なら末尾へ丸める）
    #[serde(default)]
    pub index:       u32,
    /// 追加された子アクタのサブツリー全体
    pub actor:       ActorData,
}

// ─── PrefabOverrides ──────────────────────────────────────────────────────────

/// 1 つのプレハブインスタンスが持つオーバーライドの集合。
///
/// 記録する差分は 3 種類:
///  1. `modified_components` — プレハブにも存在するコンポーネントの値の上書き
///  2. `added_components`    — プレハブに存在しないコンポーネントの追加
///  3. `added_children`      — シーン側で追加された子アクタ
///
/// **コンポーネント／子アクタの「削除」は記録しない**（段階 1 の割り切り）。
/// 誤検出（何らかの理由で一時的にコンポーネントが取得できなかった場合）が
/// プレハブ由来データの恒久的な消失につながるため、安全側に倒している。
#[derive(Clone, Default, Serialize, Deserialize)]
pub struct PrefabOverrides {
    /// 値の上書き（プレハブにも存在するコンポーネント）
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub modified_components: Vec<ComponentOverride>,
    /// 追加されたコンポーネント（プレハブに存在しない）
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub added_components:    Vec<ComponentOverride>,
    /// 追加された子アクタ
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub added_children:      Vec<ChildOverride>,
}

impl PrefabOverrides {
    /// 差分が 1 つも無いか。true のとき `.scene` へ書き出さない（旧シーンとバイト互換）。
    pub fn is_empty(&self) -> bool {
        self.modified_components.is_empty()
            && self.added_components.is_empty()
            && self.added_children.is_empty()
    }

    /// 記録されている差分の総数（ログ・テスト用。段階 2 の UI 表示でも使う）。
    #[allow(dead_code)]
    pub fn len(&self) -> usize {
        self.modified_components.len() + self.added_components.len() + self.added_children.len()
    }
}

// ─── Default 実装 ─────────────────────────────────────────────────────────────

impl Default for ComponentKey {
    /// serde(default) 用。実際には抽出時に必ず値が入る。
    fn default() -> Self { Self { type_tag: String::new(), name: String::new(), ordinal: 0 } }
}
