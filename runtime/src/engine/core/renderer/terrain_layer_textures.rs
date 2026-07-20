// ============================================================
//  terrain_layer_textures.rs — 地形レイヤテクスチャ配列の構築（Terrain T2b）
//
//  ## 役割（単一責任）
//  「layers.json のレイヤ定義（最大 TERRAIN_MAX_LAYERS 層）が参照する PNG などを読み、
//   共通解像度へ揃えて 1 本の 2D 配列テクスチャへ束ねる」ことだけを行う。
//  パイプライン・バインドグループ・シェーダの知識は持たない（terrain_gbuffer.rs の責務）。
//
//  ## なぜ texture_2d_array なのか（bindless を採らなかった理由）
//  レイヤ数を 4 → 16 へ増やすと、個別バインディング（texture_2d を 16 個）では
//  バインドグループが肥大し、レイヤ数の変更のたびに BGL とシェーダを書き換える必要がある。
//  素直な代替は bindless（`binding_array<texture_2d<f32>>`）だが、wgpu では
//  **binding_array と uniform を同一バインドグループへ置けない**（実機の BGL 構築で
//  パニックする既往あり）。地形の group3 にはレイヤパラメータ uniform が同居必須で、
//  かつ group0〜3 はすべて埋まっているため uniform を別グループへ逃がす余地が無い。
//  したがって texture_2d_array を選んだ。追加の機能フラグ（TEXTURE_BINDING_ARRAY）も
//  不要で全 GPU で動く。代償の「全レイヤ同一解像度」は本モジュールのリサイズで吸収する。
//
//  ## ミップマップを CPU で作る理由
//  シェーダ側は全サンプルを `textureSampleGrad` で行う（確率的タイリングのシームを
//  消すため明示 grad が必須）。ミップが無いと遠景で猛烈にエイリアスするので、
//  ミップ連鎖は必ず用意する。GPU ミップ生成（ブリット連鎖）は専用パイプラインを
//  要するため、レイヤ枚数が高々 16・構築が起動時 1 回であることを踏まえ
//  CPU 側（image::imageops::resize）で作る方が総コストが小さい。
// ============================================================

use std::collections::HashMap;

use image::RgbaImage;

use crate::engine::terrain::layers::{TerrainLayerSet, TERRAIN_MAX_LAYERS};

// ─── 調整用定数（マジックナンバー禁止）──────────────────────────────────────

/// 全レイヤを揃える共通解像度（1 辺ピクセル）。2 のべき乗であること（ミップ段数計算の前提）。
/// 大きくすると VRAM が層数ぶん効いてくる（512² RGBA × 16 層 × 3 マップ × ミップ ≒ 64MB）。
pub const TERRAIN_LAYER_TEXTURE_SIZE: u32 = 512;

/// リサイズフィルタ。Triangle は速度と品質のバランスが良く、ミップ連鎖のボックス相当。
const RESIZE_FILTER: image::imageops::FilterType = image::imageops::FilterType::Triangle;

/// ベースカラー未指定レイヤの既定ピクセル（白・不透明）。uniform 側の has-texture=0 と
/// 併用され、実際には参照されないが未定義データを GPU へ送らないために敷く。
const DEFAULT_BASE_COLOR_PIXEL: [u8; 4] = [255, 255, 255, 255];
/// 法線マップ未指定レイヤの既定ピクセル（(0.5,0.5,1) = タンジェント空間 +Z）。
const DEFAULT_NORMAL_PIXEL: [u8; 4] = [128, 128, 255, 255];
/// ラフネス未指定レイヤの既定ピクセル（白 = 係数そのまま）。
const DEFAULT_ROUGHNESS_PIXEL: [u8; 4] = [255, 255, 255, 255];

/// RGBA8 の 1 テクセルあたりバイト数。
const BYTES_PER_TEXEL: u32 = 4;

/// ミップ段数（2 のべき乗解像度の完全連鎖）。
pub fn mip_level_count(size: u32) -> u32 {
    // size=512 → ilog2()=9 → 10 段（512,256,…,1）。
    size.max(1).ilog2() + 1
}

// ============================================================
//  TerrainLayerTextureArrays — 3 本の 2D 配列テクスチャ
// ============================================================

/// 地形レイヤの 2D 配列テクスチャ 3 本（ベースカラー / 法線 / ラフネス）とそのビュー。
///
/// `wgpu::Texture` はビューが生きていれば解放されないが、明示的に保持して
/// ライフサイクルを分かりやすくしておく（デバッグ時にラベルから辿れる）。
pub struct TerrainLayerTextureArrays {
    /// ベースカラー配列（sRGB）。
    pub base_color: wgpu::TextureView,
    /// 法線マップ配列（リニア）。
    pub normal: wgpu::TextureView,
    /// ラフネス配列（リニア・R チャンネルのみ使用）。
    pub roughness: wgpu::TextureView,

    /// 実体テクスチャ（解放順序を明示するために保持する）。
    _textures: [wgpu::Texture; 3],
}

impl TerrainLayerTextureArrays {
    /// レイヤ定義から 3 本の配列テクスチャを構築する。
    ///
    /// 定義に無いレイヤ・パス未指定のレイヤ・読み込み失敗のレイヤは、
    /// マップ種別ごとの既定色で埋める（uniform の has-texture フラグ側で単色扱いになる）。
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue, set: &TerrainLayerSet) -> Self {
        // 同一 PNG が複数レイヤ・複数マップで参照されても 1 回しかデコードしないための
        // パス文字列キャッシュ。構築 1 回ぶんのスコープで十分（永続キャッシュは不要）。
        let mut cache: HashMap<String, RgbaImage> = HashMap::new();

        // ─── 各マップのレイヤ画像列を作る ───
        let base_imgs = collect_layer_images(
            set, &mut cache, DEFAULT_BASE_COLOR_PIXEL,
            |l| l.base_color_texture.as_deref(),
        );
        let normal_imgs = collect_layer_images(
            set, &mut cache, DEFAULT_NORMAL_PIXEL,
            |l| l.normal_texture.as_deref(),
        );
        let rough_imgs = collect_layer_images(
            set, &mut cache, DEFAULT_ROUGHNESS_PIXEL,
            |l| l.roughness_texture.as_deref(),
        );

        // ─── GPU へアップロード ───
        //   ベースカラーだけ sRGB（サンプル時に自動でリニア展開される）。
        //   法線・ラフネスは数値データなのでリニアフォーマットでなければならない。
        let base_tex = upload_array_texture(
            device, queue, &base_imgs, wgpu::TextureFormat::Rgba8UnormSrgb,
            "terrain_layer_base_color_array",
        );
        let normal_tex = upload_array_texture(
            device, queue, &normal_imgs, wgpu::TextureFormat::Rgba8Unorm,
            "terrain_layer_normal_array",
        );
        let rough_tex = upload_array_texture(
            device, queue, &rough_imgs, wgpu::TextureFormat::Rgba8Unorm,
            "terrain_layer_roughness_array",
        );

        let view_desc = wgpu::TextureViewDescriptor {
            dimension: Some(wgpu::TextureViewDimension::D2Array),
            ..Default::default()
        };
        let base_color = base_tex.create_view(&view_desc);
        let normal     = normal_tex.create_view(&view_desc);
        let roughness  = rough_tex.create_view(&view_desc);

        Self {
            base_color,
            normal,
            roughness,
            _textures: [base_tex, normal_tex, rough_tex],
        }
    }
}

// ============================================================
//  内部ヘルパー
// ============================================================

/// 全レイヤぶんの画像（共通解像度へリサイズ済み）を集める。
///
/// - `pick`      : `TerrainLayer` からそのマップのパスを取り出すクロージャ。
/// - `default_px`: パス未指定レイヤに敷く既定色。
fn collect_layer_images<F>(
    set:        &TerrainLayerSet,
    cache:      &mut HashMap<String, RgbaImage>,
    default_px: [u8; 4],
    pick:       F,
) -> Vec<RgbaImage>
where
    F: Fn(&crate::engine::terrain::layers::TerrainLayer) -> Option<&str>,
{
    // 配列テクスチャの層数は「実際に定義されたレイヤ数」に合わせる。
    // TERRAIN_MAX_LAYERS ぶんを常に確保すると、既定の 4 層構成でも
    // 512² × RGBA × 16 層 × 3 マップ ≒ 64MB を占有してしまうため
    // （4 層なら 16MB で済む）。0 層は配列テクスチャとして不正なので 1 を下限とする。
    let count = set.layers.len().clamp(1, TERRAIN_MAX_LAYERS);
    let mut out = Vec::with_capacity(count);
    for i in 0..count {
        let path = set.layers.get(i).and_then(&pick);
        out.push(match path {
            // 読み込み失敗時も「未指定と同じ既定色」へ落とす（マップ種別ごとに安全な値）。
            Some(p) => load_and_resize(p, cache, default_px),
            None => solid_image(default_px),
        });
    }
    out
}

/// パスの画像を読み、共通解像度へリサイズして返す（同一パスはキャッシュから返す）。
///
/// 読み込み・デコードに失敗した場合は `fallback_px` の単色を返す。
/// 共通のマゼンタフォールバック（`asset_fs::read_image`）を使わないのは、
/// **法線マップにマゼンタ (1,0,1) が入るとデコード後の法線が異常なベクトルになり、
/// ライティングが破綻する**ため。マップ種別ごとに中立な既定値へ落とす方が安全側に縮退する。
///
/// パスは `asset_fs::normalize_asset_path` で正規化される（layers.json の
/// アセットルート相対表記がカレントディレクトリ基準で解決されないようにするため）。
fn load_and_resize(
    path:        &str,
    cache:       &mut HashMap<String, RgbaImage>,
    fallback_px: [u8; 4],
) -> RgbaImage {
    // キャッシュキーは正規化後のパス（同じ実体を指す別表記を 1 回のデコードにまとめる）。
    let key = crate::engine::asset_fs::normalize_asset_path(path);
    if let Some(img) = cache.get(&key) {
        return img.clone();
    }

    let src = match crate::engine::asset_fs::read_image_result(path) {
        Ok(img) => img,
        Err(e) => {
            log_texture_failure_once(&key, &e);
            solid_image(fallback_px)
        }
    };

    let resized = image::imageops::resize(
        &src,
        TERRAIN_LAYER_TEXTURE_SIZE,
        TERRAIN_LAYER_TEXTURE_SIZE,
        RESIZE_FILTER,
    );
    cache.insert(key, resized.clone());
    resized
}

/// 同一パスの読み込み失敗を 1 回だけログする。
///
/// レイヤ定義は設定ウィンドウの保存のたびに再構築されるため、素直に毎回出すと
/// 同じ 1 行がログを埋める。パスごとに 1 回に絞って「気付けるが埋もれない」ようにする。
fn log_texture_failure_once(key: &str, err: &std::io::Error) {
    use std::collections::HashSet;
    use std::sync::{Mutex, OnceLock};

    /// 既に報告済みのパス集合（プロセス全体で共有）。
    static REPORTED: OnceLock<Mutex<HashSet<String>>> = OnceLock::new();

    let reported = REPORTED.get_or_init(|| Mutex::new(HashSet::new()));
    // ロック取得に失敗（毒化）した場合はログを諦める。描画は続行させる方が重要。
    if let Ok(mut set) = reported.lock() {
        if set.insert(key.to_string()) {
            eprintln!(
                "[SEED terrain] レイヤテクスチャを読み込めません: path={key:?} err={err}\
                 （既定色で代替します。layers.json のパスは assets フォルダ相対で記述してください）"
            );
        }
    }
}

/// 単色で共通解像度を埋めた画像を作る。
fn solid_image(px: [u8; 4]) -> RgbaImage {
    RgbaImage::from_pixel(
        TERRAIN_LAYER_TEXTURE_SIZE,
        TERRAIN_LAYER_TEXTURE_SIZE,
        image::Rgba(px),
    )
}

/// レイヤ画像列を 1 本の 2D 配列テクスチャへアップロードする（ミップ連鎖を CPU 生成）。
fn upload_array_texture(
    device: &wgpu::Device,
    queue:  &wgpu::Queue,
    layers: &[RgbaImage],
    format: wgpu::TextureFormat,
    label:  &str,
) -> wgpu::Texture {
    let mips = mip_level_count(TERRAIN_LAYER_TEXTURE_SIZE);
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d {
            width:                 TERRAIN_LAYER_TEXTURE_SIZE,
            height:                TERRAIN_LAYER_TEXTURE_SIZE,
            depth_or_array_layers: layers.len().max(1) as u32,
        },
        mip_level_count: mips,
        sample_count:    1,
        dimension:       wgpu::TextureDimension::D2,
        format,
        usage:           wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats:    &[],
    });

    for (layer_idx, img) in layers.iter().enumerate() {
        // ─── ミップ 0 から順に「前段を半分へ縮小」して連鎖を作る ───
        let mut level_img = img.clone();
        for level in 0..mips {
            let size = (TERRAIN_LAYER_TEXTURE_SIZE >> level).max(1);
            if level > 0 {
                level_img = image::imageops::resize(&level_img, size, size, RESIZE_FILTER);
            }
            queue.write_texture(
                wgpu::TexelCopyTextureInfo {
                    texture:   &texture,
                    mip_level: level,
                    origin: wgpu::Origin3d { x: 0, y: 0, z: layer_idx as u32 },
                    aspect:    wgpu::TextureAspect::All,
                },
                level_img.as_raw(),
                wgpu::TexelCopyBufferLayout {
                    offset:         0,
                    bytes_per_row:  Some(BYTES_PER_TEXEL * size),
                    rows_per_image: Some(size),
                },
                wgpu::Extent3d { width: size, height: size, depth_or_array_layers: 1 },
            );
        }
    }

    texture
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// ミップ段数の計算が「2 のべき乗解像度の完全連鎖」になっていること。
    #[test]
    fn mip_level_count_is_full_chain() {
        assert_eq!(mip_level_count(1), 1);
        assert_eq!(mip_level_count(2), 2);
        assert_eq!(mip_level_count(256), 9);
        assert_eq!(mip_level_count(512), 10);
        assert_eq!(mip_level_count(1024), 11);
        // 共通解像度が 2 のべき乗であること（最終段が 1×1 になる前提）。
        assert!(TERRAIN_LAYER_TEXTURE_SIZE.is_power_of_two());
        assert_eq!(
            TERRAIN_LAYER_TEXTURE_SIZE >> (mip_level_count(TERRAIN_LAYER_TEXTURE_SIZE) - 1),
            1
        );
    }

    /// 読み込み失敗時のフォールバックが「マップ種別ごとに安全な色」であること。
    ///
    /// 特に法線マップ: マゼンタ (255,0,255) が入るとタンジェント空間法線が
    /// 異常なベクトルになるため、平坦法線 (128,128,255) でなければならない。
    #[test]
    fn missing_texture_falls_back_to_neutral_per_map_kind() {
        let mut cache = HashMap::new();

        // 法線: 平坦（+Z）。マゼンタであってはならない。
        let normal = load_and_resize("no/such/normal_for_test.png", &mut cache, DEFAULT_NORMAL_PIXEL);
        assert_eq!(normal.get_pixel(0, 0).0, DEFAULT_NORMAL_PIXEL);
        assert_ne!(normal.get_pixel(0, 0).0, [255, 0, 255, 255]);
        assert_eq!(normal.width(), TERRAIN_LAYER_TEXTURE_SIZE);

        // ラフネス: 白（係数そのまま）。
        let rough = load_and_resize("no/such/rough_for_test.png", &mut cache, DEFAULT_ROUGHNESS_PIXEL);
        assert_eq!(rough.get_pixel(0, 0).0, DEFAULT_ROUGHNESS_PIXEL);

        // ベースカラー: 白（base_color 係数そのまま）。
        let base = load_and_resize("no/such/base_for_test.png", &mut cache, DEFAULT_BASE_COLOR_PIXEL);
        assert_eq!(base.get_pixel(0, 0).0, DEFAULT_BASE_COLOR_PIXEL);
    }

    /// キャッシュキーが正規化後のパスであること（同一実体の別表記を 1 回にまとめる）。
    #[test]
    fn cache_key_is_normalized_path() {
        let mut cache = HashMap::new();
        let _ = load_and_resize("no/such/tex_for_test.png", &mut cache, DEFAULT_NORMAL_PIXEL);
        assert!(cache.contains_key("assets://no/such/tex_for_test.png"));
        // 区切り違いの別表記でも同じエントリに当たる（デコード 1 回で済む）。
        let _ = load_and_resize(r"no\such\tex_for_test.png", &mut cache, DEFAULT_NORMAL_PIXEL);
        assert_eq!(cache.len(), 1);
    }

    /// 単色画像が共通解像度で作られること（GPU 無しで確認できる範囲の検証）。
    #[test]
    fn solid_image_matches_common_size() {
        let img = solid_image(DEFAULT_NORMAL_PIXEL);
        assert_eq!(img.width(), TERRAIN_LAYER_TEXTURE_SIZE);
        assert_eq!(img.height(), TERRAIN_LAYER_TEXTURE_SIZE);
        assert_eq!(img.get_pixel(0, 0).0, DEFAULT_NORMAL_PIXEL);
    }
}
