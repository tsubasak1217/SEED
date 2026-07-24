// ============================================================
//  terrain_component.rs — 地形チャンクコンポーネント（エディタ管理・内部用）
//
//  【責務】
//    ボクセル地形の 1 チャンクを表すアクターに付与する「識別＋永続化リンク」
//    コンポーネント。チャンクの整数格子座標（ChunkCoord）と、その密度データを
//    保存した .tvox ファイルへの仮想パスを保持する。
//
//  【役割】
//    - シーンロード時に、このコンポーネントを持つアクターを走査して、
//      各チャンクの .tvox を読み戻す手掛かりにする（terrain_ops の
//      rebuild_terrain_after_load）。
//    - このコンポーネントはユーザーが手動で追加するものではなく、
//      地形システム（terrain_ops）が内部的に生成・管理する。
//      そのため C# エディタの「コンポーネント追加」UI には露出しない。
//
//  設計テンプレートは sprite_component.rs（パス文字列の永続化）に倣う。
// ============================================================

use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};

/// 地形チャンクのメッシュアクターが持つ ModelComponent の合成 source_path 接頭辞。
///
/// 地形チャンクのメッシュは .glb 等の実ファイルではなくランタイム生成のため、
/// `terrain://<scene>/chunk_X_Y_Z` という「実在しないが空でないパス」を割り当てる。
/// 空でないことで通常のモデルと同じ描画・RT（レイトレ）キャスター経路に乗る一方、
/// シーンロード時の load_model はこの接頭辞を見てファイル読み込みをスキップする
/// （terrain_ops の rebuild パスが .tvox からメッシュを再構築して埋める）。
pub const TERRAIN_SOURCE_SCHEME: &str = "terrain://";

// ─── TerrainChunkComponentData ───────────────────────────────────────────────

/// TerrainChunkComponent のシリアライズ用データ。
///
/// 旧シーン互換のため全フィールドに `#[serde(default)]` を付ける
/// （欠落しても読み込みが失敗しないようにする）。
#[derive(Clone, Serialize, Deserialize, Default)]
pub struct TerrainChunkComponentData {
    /// チャンク格子座標 X（ChunkCoord.x）
    #[serde(default)]
    pub chunk_x: i32,
    /// チャンク格子座標 Y（ChunkCoord.y）
    #[serde(default)]
    pub chunk_y: i32,
    /// チャンク格子座標 Z（ChunkCoord.z）
    #[serde(default)]
    pub chunk_z: i32,
    /// このチャンクの密度を保存した .tvox の仮想パス
    /// （例: `assets://terrain/<scene>/chunk_X_Y_Z.tvox`。空 = 未保存）
    #[serde(default)]
    pub tvox_path: String,
}

// ─── TerrainChunkComponent ───────────────────────────────────────────────────

/// 地形チャンクコンポーネント（ECS 実体）。
///
/// フィールド構成はシリアライズ用データと同一。メソッドロジックは持たず、
/// 地形の生成・編集・再メッシュ化ロジックは terrain_ops（システム側）が担う
/// （ECS の理念：データ＝コンポーネント／ロジック＝システム）。
#[derive(Clone, Default)]
pub struct TerrainChunkComponent {
    /// チャンク格子座標 X
    pub chunk_x: i32,
    /// チャンク格子座標 Y
    pub chunk_y: i32,
    /// チャンク格子座標 Z
    pub chunk_z: i32,
    /// このチャンクの密度を保存した .tvox の仮想パス
    pub tvox_path: String,
}

impl TerrainChunkComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: TerrainChunkComponentData) -> Self {
        Self {
            chunk_x: data.chunk_x,
            chunk_y: data.chunk_y,
            chunk_z: data.chunk_z,
            tvox_path: data.tvox_path,
        }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> TerrainChunkComponentData {
        TerrainChunkComponentData {
            chunk_x: self.chunk_x,
            chunk_y: self.chunk_y,
            chunk_z: self.chunk_z,
            tvox_path: self.tvox_path.clone(),
        }
    }
}

impl Component for TerrainChunkComponent {}
