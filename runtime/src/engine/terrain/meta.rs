// ============================================================
//  terrain/meta.rs — 地形フォルダの付随メタデータ（terrain_meta.json）
//
//  【責務】
//    .tvox（密度）/ .tscatter（散布）/ .tcover（カバー）のどれにも属さない
//    「地形アセット全体に効く小さな設定」を 1 ファイルへまとめて直列化する。
//    ファイル IO は行わない（純粋な文字列 in/out）。IO はエンジン層の責務。
//
//  【なぜ .tvox へ入れないのか】
//    .tvox はチャンク 1 枚の密度＋スプラットを固定長で並べたバイナリで、
//    ヘッダに項目を足すとバージョンを上げて全チャンクを書き直すことになる。
//    ここで持ちたいのは「チャンク数個ぶんの真偽値」と「スライダー 1 個」だけなので、
//    地形フォルダに JSON を 1 枚置くほうが圧倒的に安い。
//    **ファイルが無い＝すべて既定値**なので、旧データはそのまま開ける（後方互換）。
//
//  【保持する内容】
//    1. `collision_disabled`: 物理コライダーを登録しない**チャンクの一覧**。
//       既定（＝載っていないチャンク）は当たり判定 **有効**。
//       「無効なものだけ」を列挙するのは、全チャンク列挙より小さく、
//       チャンクを増やしたときに既定が自動で有効になるからである。
//    2. `decimate_strength`: その場デシメートの強度（0〜1）。
//       ロード後の自動再適用に使う。0 なら適用しない。
//
//  【オンディスク仕様】
//    { "version": 1,
//      "collision_disabled": [[x,y,z], ...],
//      "decimate_strength": 0.0 }
// ============================================================

use std::collections::HashSet;

use serde::{Deserialize, Serialize};

use super::chunk_coord::ChunkCoord;

/// 地形メタデータファイルの名前（地形フォルダ直下）。
pub const TERRAIN_META_FILE_NAME: &str = "terrain_meta.json";

/// 現行フォーマットバージョン。書き出しは常にこれ。
pub const TERRAIN_META_VERSION: u32 = 1;

/// デシメート強度の値域（UI スライダーと一致させる）。
pub const DECIMATE_STRENGTH_MIN: f32 = 0.0;
/// デシメート強度の上限。
pub const DECIMATE_STRENGTH_MAX: f32 = 1.0;

/// 地形フォルダのメタデータ（オンディスク表現）。
///
/// `serde` の既定値属性により、項目が欠けた古い JSON でも読める。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct TerrainMeta {
    /// フォーマットバージョン。
    #[serde(default = "default_version")]
    pub version: u32,
    /// 当たり判定を **無効**にしたチャンクの一覧（既定はすべて有効）。
    #[serde(default)]
    pub collision_disabled: Vec<[i32; 3]>,
    /// その場デシメートの強度（0〜1）。0 = 適用しない。
    #[serde(default)]
    pub decimate_strength: f32,
}

/// `version` 欠落時の既定（v1 として読む）。
fn default_version() -> u32 {
    TERRAIN_META_VERSION
}

impl Default for TerrainMeta {
    fn default() -> Self {
        Self {
            version: TERRAIN_META_VERSION,
            collision_disabled: Vec::new(),
            decimate_strength: DECIMATE_STRENGTH_MIN,
        }
    }
}

impl TerrainMeta {
    /// ランタイム状態（無効チャンク集合＋強度）からメタデータを組む。
    ///
    /// 無効チャンクは座標順（x, y, z）に並べる。`HashSet` の走査順は実行ごとに
    /// 変わるため、並べないと「何も変えていないのにファイルの中身が毎回変わる」
    /// （＝差分が出てバージョン管理が汚れる）ことになる。
    pub fn from_state(collision_disabled: &HashSet<ChunkCoord>, decimate_strength: f32) -> Self {
        let mut list: Vec<[i32; 3]> = collision_disabled.iter().map(|c| [c.x, c.y, c.z]).collect();
        list.sort_unstable();
        Self {
            version: TERRAIN_META_VERSION,
            collision_disabled: list,
            decimate_strength: clamp_strength(decimate_strength),
        }
    }

    /// 当たり判定を無効にしたチャンクの集合を返す。
    pub fn collision_disabled_set(&self) -> HashSet<ChunkCoord> {
        self.collision_disabled
            .iter()
            .map(|c| ChunkCoord::new(c[0], c[1], c[2]))
            .collect()
    }

    /// 値域へ丸めたデシメート強度を返す（壊れたファイル・手書きの値に備える）。
    pub fn clamped_decimate_strength(&self) -> f32 {
        clamp_strength(self.decimate_strength)
    }
}

/// デシメート強度を値域へ丸める（NaN は 0 とみなす）。
pub fn clamp_strength(v: f32) -> f32 {
    if !v.is_finite() {
        return DECIMATE_STRENGTH_MIN;
    }
    v.clamp(DECIMATE_STRENGTH_MIN, DECIMATE_STRENGTH_MAX)
}

/// メタデータを JSON 文字列へ直列化する（人が読める整形つき）。
pub fn write_meta(meta: &TerrainMeta) -> String {
    // 失敗しうるのは「シリアライズ不能な型」だけで、この構造体では起きない。
    // 万一のときも保存経路を落とさず、既定値の JSON を返す。
    serde_json::to_string_pretty(meta).unwrap_or_else(|_| "{\"version\":1}".to_string())
}

/// JSON 文字列からメタデータを復元する。
///
/// **壊れた JSON でも既定値を返す**（`Err` にしない）。地形メタは補助情報であり、
/// これが読めないからといって地形そのものを開けなくするのは割に合わない。
/// 読めなかったことを呼び出し側が知りたいときのために、第 2 戻り値で示す。
pub fn read_meta(text: &str) -> (TerrainMeta, bool) {
    match serde_json::from_str::<TerrainMeta>(text) {
        Ok(m) => (m, true),
        Err(_) => (TerrainMeta::default(), false),
    }
}
