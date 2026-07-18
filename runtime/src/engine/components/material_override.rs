// ============================================================
//  material_override.rs — マテリアルオーバーライドのデータモデル（Phase R7）
//
//  【役割】
//  ModelComponent 単位で「Model 内蔵マテリアル（glTF スロット）の一部を差し替える」ための
//  データ定義。ECS の理念に沿い、本ファイルは純粋データ（構造体・シリアライズ・
//  署名計算という決定的な純関数）のみを持ち、GPU への焼き込みロジックは
//  core::renderer::gpu_resources::GpuModel::apply_overrides（System 側処理）に置く。
//
//  【スロットについて】
//  `slot` は Model::materials のインデックス（＝ Primitive::material_index）と一致する。
//  1 つの ModelComponent は Vec<MaterialOverride> で複数スロットのオーバーライドを保持する
//  （同一 slot の重複は禁止：常に「その slot に対する唯一の有効なオーバーライド」を 1 件保持する）。
// ============================================================

use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use serde::{Deserialize, Serialize};

// ============================================================
//  MaterialOverride
// ============================================================

/// 1 マテリアルスロット分のオーバーライド。
#[derive(Clone, Serialize, Deserialize)]
pub struct MaterialOverride {
    /// Model::materials のインデックス（= primitive.material_index）
    pub slot: usize,
    pub kind: MaterialOverrideKind,
}

/// オーバーライドの種別。
///
/// - `MatAsset`: `.mat` アセットファイルを丸ごと適用する
/// - `Inline`  : glTF 埋込マテリアルの一部フィールドのみをその場で上書きする
///               （`None` のフィールドは埋込値をそのまま使う）
#[derive(Clone, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum MaterialOverrideKind {
    /// `.mat` アセットのパス（assets:// 仮想パス想定）
    MatAsset {
        #[serde(default)]
        path: String,
    },
    /// インライン差し替え。各フィールドは `None` なら埋込値を維持する。
    Inline {
        #[serde(default)]
        base_color: Option<[f32; 4]>,
        #[serde(default)]
        metallic: Option<f32>,
        #[serde(default)]
        roughness: Option<f32>,
        #[serde(default)]
        emissive: Option<[f32; 3]>,
        /// "opaque" | "mask" | "blend"（material_asset::parse_alpha_mode で変換）
        #[serde(default)]
        alpha_mode: Option<String>,
        #[serde(default)]
        alpha_cutoff: Option<f32>,
        /// 屈折率（IOR, Phase RT-Translucency）。`None` なら埋込値を維持する。
        /// RT-Translucency 有効・Blend のときスクリーンスペース屈折に使う（1.0=屈折なし）。
        #[serde(default)]
        ior: Option<f32>,
        /// 透過率（transmission, 0..1。ガラス表現）。`None` なら埋込値を維持する。
        /// アルファと分離した透け具合。RT-Translucency の合成でフレネル配分に使う。
        #[serde(default)]
        transmission: Option<f32>,
        /// 拡散透過（diffuse_transmission, 0..1。葉・布・紙の逆光透け）。`None` なら埋込値を維持する。
        /// ガラスの鏡面透過（上の transmission）とは別物で、屈折を伴わない内部散乱の逆光透け。
        #[serde(default)]
        diffuse_transmission: Option<f32>,
        /// MR テクスチャ無視トグル。`None` なら埋込値を維持する。
        /// true で metallic/roughness テクスチャの乗算をスキップし factor を実効値にする（既定 false＝乗算）。
        #[serde(default)]
        mr_tex_ignore: Option<bool>,
        /// カリング面 "back" | "front" | "none"（material_asset::parse_cull_face で変換）。
        /// `None` なら埋込マテリアルの値（glTF の double_sided 由来）を維持する。
        #[serde(default)]
        cull_face: Option<String>,
    },
}

// ============================================================
//  署名計算（バッチキー用）
// ============================================================

/// オーバーライド一覧から安定した短い署名文字列を計算する。
///
/// 【用途】per-アクタで異なるオーバーライドを持つ ModelComponent 同士が、同じ
/// `source_path` のまま誤って同一インスタンスバッチへマージされるのを防ぐため、
/// `ModelComponent::batch_key()` がこの署名をバッチキーへ混ぜ込む（方式(a)の最軽量形）。
///
/// - 空 Vec → 空文字列（オーバーライド無しモデルは `batch_key() == source_path` と完全一致させ、
///   旧シーン・既存の性能特性を破壊しないことを保証する）。
/// - 非空 Vec → JSON シリアライズしたバイト列のハッシュを 16 進文字列で返す
///   （内容が変われば必ずキーが変わる決定的な値であればよく、暗号学的強度は不要）。
pub fn overrides_signature(ovr: &[MaterialOverride]) -> String {
    if ovr.is_empty() {
        return String::new();
    }
    // JSON化してハッシュする（フィールド順は serde の構造体定義順で安定）
    let json = serde_json::to_string(ovr).unwrap_or_default();
    let mut hasher = DefaultHasher::new();
    json.hash(&mut hasher);
    format!("{:016x}", hasher.finish())
}
