// ============================================================
//  skinned_sprite_component.rs — メッシュ変形スキニング 2D スプライト
//
//  Spine 風の「メッシュを持つスプライト」を 2D キャンバス上に表示する
//  コンポーネント（Phase A1）。既存の SpriteComponent が「矩形 1 枚固定」
//  なのに対し、本コンポーネントは `.sprite_mesh` アセットが定義する
//  **任意の三角形メッシュ**を、ボーンで変形しながら描画する。
//
//  【ボーン＝普通の 2D 子アクター】
//  独立した Skeleton アセットは存在しない。ボーンはこのコンポーネントを
//  持つアクターの**子孫アクター**（CanvasTransform を持つ Actor2D）である。
//  したがってボーンの移動・回転はギズモで直接操作でき、Undo も既存機構が効き、
//  キーフレームアニメも既存の `.anim`（汎用プロパティトラック）で
//  `{actor_path=ボーンへの相対パス, component=CanvasTransform, property=rotation}`
//  を打つだけで再生できる。
//
//  【ボーン解決規則】
//  `.sprite_mesh` の各ボーン名について:
//    ① `bone_overrides` に明示エントリがあればその相対パスで解決する
//    ② 無ければ「同名の子孫アクターを DFS で探す」自動解決
//  どちらでも見つからないボーンはバインドポーズ（無変形）として扱う。
//
//  【描画規約】
//  color / layer / 描画ゾーン / キャンバス Transform の扱いは
//  SpriteComponent と完全に同一である（同じ収集・ソート経路を通る）。
// ============================================================

use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

// ─── デフォルト値関数（マジックナンバー排除） ─────────────────

/// 既定の表示カラー（白・不透明）。
fn default_color() -> [f32; 4] {
    [1.0, 1.0, 1.0, 1.0]
}

// ─── SkinnedSpriteComponentData ──────────────────────────────

/// SkinnedSpriteComponent のシリアライズ用データ。
///
/// 全フィールドに `#[serde(default)]` を付けることで、フィールドを
/// 後から増やしても旧 `.scene` の読み込みが失敗しない。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SkinnedSpriteComponentData {
    /// メッシュアセット（`.sprite_mesh`）のパス（空文字列 = 未設定・非表示）。
    #[serde(default)]
    pub mesh_path: String,
    /// テクスチャファイルパス（空文字列 = テクスチャなし、単色表示）。
    /// 参照方式は SpriteComponent の `texture_path` と同一。
    #[serde(default)]
    pub texture_path: String,
    /// 表示カラー（RGBA 正規化値）。
    #[serde(default = "default_color")]
    pub color: [f32; 4],
    /// 描画優先度レイヤー（SpriteComponent と同じソート規約）。
    #[serde(default)]
    pub layer: i32,
    /// ボーン名 → アクター相対パスの明示対応表。
    /// 空 = すべて自動解決（メッシュ内のボーン名と同名の子孫アクターを探す）。
    #[serde(default)]
    pub bone_overrides: BTreeMap<String, String>,
}

impl Default for SkinnedSpriteComponentData {
    fn default() -> Self {
        Self {
            mesh_path: String::new(),
            texture_path: String::new(),
            color: default_color(),
            layer: 0,
            bone_overrides: BTreeMap::new(),
        }
    }
}

// ─── SkinnedSpriteComponent ──────────────────────────────────

/// メッシュ変形スキニング 2D スプライト（ECS 実体）。
///
/// フィールド構成はシリアライズ用データと同一。ロジックは一切持たない
/// （ECS 理念: コンポーネントはデータ、変形と描画はシステム／収集側の責務）。
#[derive(Clone, Debug)]
pub struct SkinnedSpriteComponent {
    /// メッシュアセット（`.sprite_mesh`）のパス（空文字列 = 未設定・非表示）。
    pub mesh_path: String,
    /// テクスチャファイルパス（空文字列 = テクスチャなし、単色表示）。
    pub texture_path: String,
    /// 表示カラー（RGBA 正規化値）。
    pub color: [f32; 4],
    /// 描画優先度レイヤー（SpriteComponent と同じソート規約）。
    pub layer: i32,
    /// ボーン名 → アクター相対パスの明示対応表（空 = 全自動解決）。
    pub bone_overrides: BTreeMap<String, String>,
}

impl SkinnedSpriteComponent {
    /// シリアライズ用データから復元する（`to_data` の逆）。
    /// シーン読込・Undo/Redo・複製・プレハブ展開の唯一の復元経路。
    pub fn from_data(data: SkinnedSpriteComponentData) -> Self {
        Self {
            mesh_path: data.mesh_path,
            texture_path: data.texture_path,
            color: data.color,
            layer: data.layer,
            bone_overrides: data.bone_overrides,
        }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> SkinnedSpriteComponentData {
        SkinnedSpriteComponentData {
            mesh_path: self.mesh_path.clone(),
            texture_path: self.texture_path.clone(),
            color: self.color,
            layer: self.layer,
            bone_overrides: self.bone_overrides.clone(),
        }
    }

    /// 指定ボーン名に対応するアクター相対パスを返す。
    ///
    /// 明示エントリがあればそれを、無ければ「ボーン名そのもの」を返す
    /// （＝ 同名の子アクターを名前で自動解決する既定挙動）。
    /// 呼び出し側は返り値をまず直下パスとして解決し、失敗したら
    /// 名前による子孫 DFS 探索へフォールバックする。
    pub fn bone_path<'a>(&'a self, bone_name: &'a str) -> &'a str {
        self.bone_overrides
            .get(bone_name)
            .map(|s| s.as_str())
            .filter(|s| !s.is_empty())
            .unwrap_or(bone_name)
    }
}

impl Default for SkinnedSpriteComponent {
    fn default() -> Self {
        Self::from_data(SkinnedSpriteComponentData::default())
    }
}

impl Component for SkinnedSpriteComponent {}

#[cfg(test)]
mod tests {
    use super::*;

    /// 明示オーバーライドが無いボーンは「名前そのもの」を返す（自動解決の既定）。
    #[test]
    fn bone_path_defaults_to_bone_name() {
        let c = SkinnedSpriteComponent::default();
        assert_eq!(c.bone_path("upper_arm"), "upper_arm");
    }

    /// 明示オーバーライドがあればそちらが優先される。
    #[test]
    fn bone_path_uses_override() {
        let mut c = SkinnedSpriteComponent::default();
        c.bone_overrides
            .insert("upper_arm".into(), "rig/arm_L/upper".into());
        assert_eq!(c.bone_path("upper_arm"), "rig/arm_L/upper");
        // 空文字のオーバーライドは「未設定」と同じ扱い
        c.bone_overrides.insert("hand".into(), String::new());
        assert_eq!(c.bone_path("hand"), "hand");
    }

    /// 旧シーン互換: フィールドが 1 つも無い JSON でも既定値で読める。
    #[test]
    fn data_deserializes_from_empty_object() {
        let d: SkinnedSpriteComponentData = serde_json::from_str("{}").expect("読み込み成功");
        assert_eq!(d.color, [1.0, 1.0, 1.0, 1.0]);
        assert_eq!(d.layer, 0);
        assert!(d.mesh_path.is_empty());
        assert!(d.bone_overrides.is_empty());
    }
}
