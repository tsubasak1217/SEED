// ============================================================
// renderer/ddgi — DDGI（プローブ格子方式のリアルタイム レイトレース GI）
//
// ## 何をするか
// ワールドに置いたプローブ格子から inline ray query でレイを飛ばし、八面体マップへ
// 放射輝度（8×8）と可視性（深度・深度², 16×16）を蓄積する。描画側（lighting_eval.wgsl）は
// 周囲 8 プローブを可視性重み（チェビシェフ）付きでトライリニア補間し、アンビエント項の
// 置き換えとして間接光を合流させる。レイ数が画面解像度から独立しているのが本質。
//
// ## RT 対応のみ
// GI は EXPERIMENTAL_RAY_QUERY 対応 GPU でのみ機能する（プローブ更新 compute が rayQuery を
// 使う）。非対応 GPU では compute を一切走らせず、GiParams.enabled=0 として描画側は従来の
// フラットアンビエントへ完全フォールバックする（アトラス等のリソースは小さいので生成はする）。
//
// ## 近似の割り切り（bindless 回避）
//   - ヒット点マテリアル: プリミティブ平均アルベド（ローダで焼く）を TLAS custom_data で引く。
//   - ヒット法線: -レイ方向で近似（頂点フェッチ不可）。
//   - 詳細は ddgi_probe_update.wgsl / docs/rendering_roadmap.md を参照。
//
// ## ファイル構成（単一責任で分割）
//   - octahedral.rs : 八面体 dir↔uv（WGSL と往復一致をテスト）
//   - grid.rs       : プローブ格子の幾何・AABB フィット・番号/座標/ワールド変換
//   - params.rs     : GiParams（GPU uniform, repr(C)）と naga レイアウト照合
//   - resources.rs  : GiResources（アトラス2枚・avg アルベド・compute BindGroup・ディスパッチ）
// ============================================================

pub mod grid;
pub mod octahedral;
pub mod params;
pub mod resources;

pub use grid::GiGrid;
pub use params::{GiParams, GI_MODE_FLAT, GI_MODE_DDGI, GI_MODE_SSGI};
pub use resources::GiResources;

// ─── プローブ格子の既定寸法 ───────────────────────────────────

/// 既定のプローブ格子次元（x, y, z）。16×8×16 = 2048 プローブ。
pub const GI_DEFAULT_DIMS: [u32; 3] = [16, 8, 16];

// ─── 八面体タイルの解像度（ガター込み）─────────────────────────

/// 放射輝度タイルの内側解像度（八面体 1 辺のテクセル数）。
pub const GI_IRRADIANCE_RES: u32 = 8;
/// 可視性タイルの内側解像度。
pub const GI_VISIBILITY_RES: u32 = 16;
/// タイル境界のガター幅（バイリニア補間のための境界複製。片側 1px）。
pub const GI_BORDER: u32 = 1;
/// 放射輝度タイルの全幅（内側 8 ＋ ガター両側 1px = 10）。
pub const GI_IRRADIANCE_TILE: u32 = GI_IRRADIANCE_RES + 2 * GI_BORDER;
/// 可視性タイルの全幅（内側 16 ＋ ガター両側 1px = 18）。
pub const GI_VISIBILITY_TILE: u32 = GI_VISIBILITY_RES + 2 * GI_BORDER;

// ─── レイ／更新の上限 ─────────────────────────────────────────

/// 1 プローブあたりのレイ本数の上限（compute のワークグループ共有メモリ容量を静的に縛る）。
/// rays_per_probe はこの値でクランプされる。
pub const GI_MAX_RAYS: u32 = 64;
/// プローブ更新 compute の 1 ワークグループのスレッド数（= 1 プローブを処理）。
pub const GI_PROBE_WG_THREADS: u32 = 64;

// ─── AABB フィット ────────────────────────────────────────────

/// AABB フィット時の各辺拡張率（size に対する割合。隅プローブを壁から浮かせる）。
pub const GI_AABB_MARGIN: f32 = 0.05;

// ─── アトラスフォーマット ─────────────────────────────────────

/// 放射輝度／可視性アトラスの GPU フォーマット。
///
/// 【設計仕様からの意図的な逸脱】仕様書は可視性を Rg16Float としていたが、compute から
/// storage テクスチャとして書き込むには「コア WebGPU で storage 対応のフォーマット」で
/// なければならない。rg16float はコアの storage 対応リストに含まれない（rgba16float は含む）。
/// よって両アトラスとも Rgba16Float にする。可視性は .rg に (平均深度, 平均深度²) を格納し
/// .ba は未使用（メモリはわずかに増えるが安全側）。放射輝度は .rgb を使い .a は未使用。
pub const GI_ATLAS_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;

// ============================================================
//  WGSL static validation (naga parse + validate)
// ============================================================
//
// The probe-update compute is only built at runtime on RT-capable GPUs, so
// `cargo build` alone never checks it. Parse + validate the concatenation here
// (with RAY_QUERY capability) so WGSL errors are caught in CI/local builds.
#[cfg(test)]
mod wgsl_tests {
    /// Parse + validate the probe-update compute (cluster_common + ddgi_common + update).
    /// Concatenation order must match GiUpdatePipeline::new (pipeline.rs).
    #[test]
    fn probe_update_shader_parses_and_validates() {
        let cluster = include_str!("../shaders/cluster_common.wgsl");
        let common  = include_str!("../shaders/ddgi_common.wgsl");
        let update  = include_str!("../shaders/ddgi_probe_update.wgsl");
        let src = format!("{cluster}\n{common}\n{update}");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("ddgi_probe_update WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::RAY_QUERY,
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("ddgi_probe_update WGSL validate 失敗: {e:?}"));
    }
}
