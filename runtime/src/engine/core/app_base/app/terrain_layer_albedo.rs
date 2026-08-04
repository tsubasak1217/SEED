// ============================================================
//  terrain_layer_albedo.rs — 地形チャンクの実効平均アルベド（RT 反射／水面反射／GI 用）
//
//  ## 役割（単一責任）
//  「レイヤ定義（layers.json）＋チャンクの頂点カラー（＝スロット重み）」から、
//  **そのチャンク 1 個の平均アルベド（リニア RGB）**を 1 つ求めることだけを行う。
//  GPU・ECS・メッシュ生成の知識は持たない（呼び出し側 terrain_mesh_build の責務）。
//
//  ## なぜ必要か（水面反射／RT 反射の「地形が灰色」問題）
//  RT 系のヒットシェーディングは、バインドレスでベースカラーテクスチャを実サンプル
//  できないインスタンスを `BindlessInstanceRecord.avg_albedo`（＝マテリアルの
//  平均アルベド）へ縮退させる。ところが地形チャンクのマテリアルは
//  `Material::default()`（テクスチャ無し・base_color_factor=白）で作られており、
//  平均アルベドが**白のベタ塗り**になっていた。地形はレイヤブレンドで色を決めるため
//  単一のベースカラーテクスチャが存在せず、bindless の実サンプル経路には乗れない。
//  結果として、水面に映る画面外の地形・RT 反射の地形・DDGI のバウンス・RT 色付き影が
//  すべて「白〜灰色の板」になっていた。
//
//  そこで **チャンク単位の実効平均色**（レイヤ重みの平均 × 各レイヤの実効色）を
//  CPU で焼き、マテリアルの平均アルベドへ書き込む。灰色 → 土色／草色になる。
//
//  ## 平均色の定義（GPU の合成式と一致させること）
//  terrain_gbuffer_write.wgsl のベースカラー合成は
//      albedo = Σ_slot  slot_weight × ( layer.base_color × [テクスチャがあればその値] )
//  である。よって「テクスチャ値」をテクスチャ全体のアルファ加重平均で置き換えた
//      chunk_avg = Σ_slot  mean(slot_weight) × ( layer.base_color × layer_tex_avg )
//  が、チャンクを 1 色で代表させたときの最も素直な近似になる。
//
//  ## 精度の限界（申し送り）
//  チャンク（既定 16m 角）内の色の分布は失われる。水面へ映る地形は
//  「チャンク単位のモザイク」になり、チャンク境界で色が段差になる。
//  ヒット位置ベースのブレンド（レイヤ重みテクスチャの bindless 登録）は
//  docs/water_interaction_roadmap.md へ申し送りとして残してある。
// ============================================================

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

use crate::engine::terrain::layers::{TERRAIN_BLEND_SLOTS, TerrainLayer, TerrainLayerSet};

/// テクスチャが読めない／未指定のレイヤに使う平均値（白＝`base_color` をそのまま実効色にする）。
///
/// terrain_layer_textures.rs のベースカラー既定ピクセル（白）と同じ縮退であり、
/// 描画側（`base_color.a`＝has-texture フラグが 0 のとき単色）とも一致する。
const LAYER_TEX_AVG_FALLBACK: [f32; 3] = [1.0, 1.0, 1.0];

/// レイヤ定義が空（0 層）のときに返す平均アルベド（中間グレー）。
///
/// 実運用では `TerrainLayerSet::from_json_str` が空定義を既定 4 層へ置き換えるため
/// 到達しないが、`TerrainLayerSet` を直接組み立てれば起こりうる。黒（＝反射が
/// 真っ黒な穴になる）にも白（＝元の症状）にも寄せない中立値を返す。
const EMPTY_LAYER_SET_ALBEDO: [f32; 3] = [0.5, 0.5, 0.5];

/// レイヤテクスチャの平均色キャッシュ（正規化済みアセットパス → リニア RGB）。
///
/// 4K PNG のデコードは数十 ms かかるうえ、地形は数百チャンクを再メッシュするため
/// キャッシュ無しではレイヤ変更のたびにデコードが層数 × チャンク数だけ走る。
/// **失敗もキャッシュする**（毎チャンク失敗パスを再デコードし続けないため）。
/// プロセス全体で共有する（`gpu_resources::override_base_color_tex_avg` と同じ流儀）。
static LAYER_TEX_AVG_CACHE: OnceLock<Mutex<HashMap<String, [f32; 3]>>> = OnceLock::new();

/// レイヤ 1 枚のベースカラーテクスチャ平均色（リニア RGB）をキャッシュ経由で返す。
///
/// パスは `asset_fs::normalize_asset_path` で正規化してから読む
/// （layers.json のアセットルート相対表記が、カレントディレクトリ基準で
/// 解決されて読めなくなるのを防ぐ。terrain_layer_textures.rs と同じ扱い）。
fn layer_texture_avg(path: &str) -> [f32; 3] {
    let key = crate::engine::asset_fs::normalize_asset_path(path);

    let cache = LAYER_TEX_AVG_CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Ok(map) = cache.lock() {
        if let Some(v) = map.get(&key) {
            return *v;
        }
    }

    // ロード＋アルファ加重平均（sRGB → リニア変換込み）。読めなければ白へ縮退。
    let avg = crate::engine::core::loader::asset_cache::texture_avg_linear(&key)
        .unwrap_or(LAYER_TEX_AVG_FALLBACK);

    if let Ok(mut map) = cache.lock() {
        map.insert(key, avg);
    }
    avg
}

/// レイヤ 1 枚の実効色（リニア RGB）＝ `base_color` × ベースカラーテクスチャ平均。
///
/// テクスチャ未指定のレイヤは `base_color` そのもの（GPU 側の単色レイヤ経路と一致）。
pub fn layer_effective_color(layer: &TerrainLayer) -> [f32; 3] {
    let tex = layer
        .base_color_texture
        .as_deref()
        .filter(|p| !p.is_empty())
        .map(layer_texture_avg)
        .unwrap_or(LAYER_TEX_AVG_FALLBACK);
    [
        layer.base_color[0] * tex[0],
        layer.base_color[1] * tex[1],
        layer.base_color[2] * tex[2],
    ]
}

/// チャンクの実効平均アルベド（リニア RGB）を求める。
///
/// - `colors`:  チャンク全頂点の頂点カラー（＝パレット内スロット重み。総和 1 に正規化済み）。
/// - `palette`: このチャンクのレイヤパレット（スロット → レイヤ番号）。
/// - `layers`:  レイヤ定義一式。
///
/// 頂点が 0 個（空チャンク）の場合はパレット先頭レイヤの実効色を返す。
/// 空チャンクは TLAS インスタンスを持たないため実際には参照されないが、
/// 「常に妥当な色を返す」ことで呼び出し側に分岐を持ち込まないようにする。
///
/// 【重複スロットについて】`compute_layer_colors` はパレットに同じレイヤ番号が
/// 複数回現れた場合、2 回目以降のスロット重みを 0 にしている。したがって
/// ここで単純にスロット重み × レイヤ色を足し込んでも二重計上は起きない。
pub fn chunk_avg_albedo(
    colors: &[[f32; 4]],
    palette: [u32; TERRAIN_BLEND_SLOTS],
    layers: &TerrainLayerSet,
) -> [f32; 3] {
    if layers.layers.is_empty() {
        return EMPTY_LAYER_SET_ALBEDO;
    }

    // ─── スロットごとのレイヤ実効色を先に引く（頂点ループ内でのデコード引きを避ける）───
    let mut slot_color = [[0.0f32; 3]; TERRAIN_BLEND_SLOTS];
    for slot in 0..TERRAIN_BLEND_SLOTS {
        // パレットが範囲外レイヤを指す（定義が減った等）場合は寄与 0 のまま。
        if let Some(layer) = layers.layers.get(palette[slot] as usize) {
            slot_color[slot] = layer_effective_color(layer);
        }
    }

    // ─── 頂点が無い（空メッシュ）ならパレット先頭レイヤの色で代表させる ───
    if colors.is_empty() {
        return slot_color[0];
    }

    // ─── スロット重みの平均を取り、レイヤ実効色と線形結合する ───
    //   f64 で累積するのは、頂点数が数万に達するチャンクで f32 の加算誤差が
    //   目に見える色ずれ（特に暗いレイヤ）になるのを避けるため。
    let mut weight_sum = [0.0f64; TERRAIN_BLEND_SLOTS];
    for c in colors {
        for slot in 0..TERRAIN_BLEND_SLOTS {
            weight_sum[slot] += c[slot] as f64;
        }
    }

    let inv_count = 1.0 / colors.len() as f64;
    let mut acc = [0.0f64; 3];
    for slot in 0..TERRAIN_BLEND_SLOTS {
        let w = weight_sum[slot] * inv_count;
        for ch in 0..3 {
            acc[ch] += w * slot_color[slot][ch] as f64;
        }
    }

    [acc[0] as f32, acc[1] as f32, acc[2] as f32]
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::terrain::layers::TerrainLayer;

    /// 色比較の許容誤差（f64 累積 → f32 変換ぶん）。
    const EPS: f32 = 1.0e-5;

    /// 単色レイヤ（テクスチャ無し）だけのレイヤセットを作る。
    fn solid_layers(colors: &[[f32; 3]]) -> TerrainLayerSet {
        TerrainLayerSet {
            layers: colors
                .iter()
                .enumerate()
                .map(|(i, c)| TerrainLayer {
                    name: format!("l{i}"),
                    base_color: *c,
                    ..TerrainLayer::default()
                })
                .collect(),
        }
    }

    /// テクスチャ無しレイヤの実効色は base_color そのもの（GPU の単色経路と一致）。
    #[test]
    fn effective_color_without_texture_is_base_color() {
        let layer = TerrainLayer {
            base_color: [0.2, 0.4, 0.6],
            ..TerrainLayer::default()
        };
        assert_eq!(layer_effective_color(&layer), [0.2, 0.4, 0.6]);
    }

    /// 読めないテクスチャパスは白へ縮退し、base_color を潰さない
    /// （＝アセット欠損で地形の反射が真っ黒／真っ白にならない）。
    #[test]
    fn effective_color_with_unreadable_texture_falls_back_to_base_color() {
        let layer = TerrainLayer {
            base_color: [0.2, 0.4, 0.6],
            base_color_texture: Some("no/such/terrain_layer_for_test.png".to_string()),
            ..TerrainLayer::default()
        };
        let c = layer_effective_color(&layer);
        assert!((c[0] - 0.2).abs() < EPS && (c[1] - 0.4).abs() < EPS && (c[2] - 0.6).abs() < EPS);
    }

    /// 全頂点がスロット 0 に 100% なら、チャンク平均色はそのレイヤの色になる。
    #[test]
    fn single_layer_chunk_takes_that_layer_color() {
        let layers = solid_layers(&[[0.16, 0.38, 0.12], [0.5, 0.4, 0.3]]);
        let colors = vec![[1.0, 0.0, 0.0, 0.0]; 8];
        let avg = chunk_avg_albedo(&colors, [0, 1, 0, 0], &layers);
        assert!((avg[0] - 0.16).abs() < EPS);
        assert!((avg[1] - 0.38).abs() < EPS);
        assert!((avg[2] - 0.12).abs() < EPS);
    }

    /// 2 レイヤが半々なら、平均色は 2 色の中点になる（線形結合であること）。
    #[test]
    fn half_and_half_blends_linearly() {
        let layers = solid_layers(&[[1.0, 0.0, 0.0], [0.0, 0.0, 1.0]]);
        let colors = vec![[0.5, 0.5, 0.0, 0.0]; 4];
        let avg = chunk_avg_albedo(&colors, [0, 1, 2, 3], &layers);
        assert!((avg[0] - 0.5).abs() < EPS);
        assert!((avg[1] - 0.0).abs() < EPS);
        assert!((avg[2] - 0.5).abs() < EPS);
    }

    /// 頂点ごとに違うレイヤでも、チャンク全体の重み平均で混ざること。
    #[test]
    fn per_vertex_weights_are_averaged_over_the_chunk() {
        let layers = solid_layers(&[[1.0, 0.0, 0.0], [0.0, 1.0, 0.0]]);
        // 3 頂点がレイヤ 0、1 頂点がレイヤ 1 → 期待値 (0.75, 0.25, 0)。
        let colors = vec![
            [1.0, 0.0, 0.0, 0.0],
            [1.0, 0.0, 0.0, 0.0],
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
        ];
        let avg = chunk_avg_albedo(&colors, [0, 1, 0, 0], &layers);
        assert!((avg[0] - 0.75).abs() < EPS, "avg={avg:?}");
        assert!((avg[1] - 0.25).abs() < EPS, "avg={avg:?}");
        assert!((avg[2] - 0.0).abs() < EPS, "avg={avg:?}");
    }

    /// パレットがレイヤ定義の範囲外を指すスロットは寄与 0（黒混入も panic もしない）。
    #[test]
    fn out_of_range_palette_slot_contributes_nothing() {
        let layers = solid_layers(&[[1.0, 1.0, 1.0]]);
        let colors = vec![[0.5, 0.5, 0.0, 0.0]; 2];
        let avg = chunk_avg_albedo(&colors, [0, 99, 0, 0], &layers);
        // スロット 1 は範囲外 → 寄与 0。スロット 0 のみ 0.5 が効く。
        assert!((avg[0] - 0.5).abs() < EPS, "avg={avg:?}");
    }

    /// 空メッシュ（頂点 0）でもパレット先頭レイヤの色を返す（NaN・黒を出さない）。
    #[test]
    fn empty_chunk_returns_first_palette_layer_color() {
        let layers = solid_layers(&[[0.1, 0.2, 0.3], [0.9, 0.9, 0.9]]);
        let avg = chunk_avg_albedo(&[], [1, 0, 0, 0], &layers);
        assert_eq!(avg, [0.9, 0.9, 0.9]);
        assert!(avg.iter().all(|v| v.is_finite()));
    }

    /// レイヤ定義が空でも中立グレーを返す（0 除算・空配列アクセスをしない）。
    #[test]
    fn empty_layer_set_returns_neutral_gray() {
        let layers = TerrainLayerSet { layers: Vec::new() };
        assert_eq!(
            chunk_avg_albedo(&[[1.0, 0.0, 0.0, 0.0]], [0, 0, 0, 0], &layers),
            EMPTY_LAYER_SET_ALBEDO
        );
    }

    /// 【実アセット依存・既定では無視】プロジェクトの `assets/terrain/layers.json` を
    /// 実際に読み、各レイヤの実効色が**白のままでない**ことを確認する。
    ///
    /// 実プロジェクトのレイヤは `base_color=[1,1,1]` ＋ 2K の JPEG テクスチャという
    /// 構成なので、**テクスチャ平均が引けているかどうか**がそのまま本修正の成否になる
    /// （引けなければ実効色は白に張り付き、症状が再発する）。
    /// アセットの実在に依存するため、実 GPU テストと同じ流儀で `#[ignore]` にしてある
    /// （`cargo test -- --ignored` で実行）。
    ///
    /// テストプロセスでは `asset_fs::init` が呼ばれておらずアセットルートが未設定なので、
    /// `CARGO_MANIFEST_DIR/assets` を起点に**絶対パス**へ組み立ててから引く
    /// （`normalize_asset_path` は絶対パスを素通しする）。グローバル状態を書き換えないため、
    /// 他の `#[ignore]` テストへ影響しない。実行時のアセットルート解決そのものは、
    /// 同じ仕組みでレイヤテクスチャを読んでいる `terrain_layer_textures.rs` が担保している。
    #[test]
    #[ignore = "実アセット（assets/terrain/layers.json とテクスチャ）に依存する"]
    fn real_project_layers_resolve_to_non_white_colors() {
        let assets = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("assets");
        let text = std::fs::read_to_string(assets.join("terrain/layers.json"))
            .expect("assets/terrain/layers.json を読めない");
        let set = TerrainLayerSet::from_json_str(&text).expect("layers.json のパースに失敗");

        for layer in &set.layers {
            // アセット相対パスを絶対パスへ差し替えたレイヤで評価する。
            let abs = layer.base_color_texture.as_ref().map(|p| {
                assets
                    .join(p.replace('\\', "/"))
                    .to_string_lossy()
                    .to_string()
            });
            let has_tex = abs.is_some();
            let probe = TerrainLayer {
                base_color_texture: abs,
                ..layer.clone()
            };
            let c = layer_effective_color(&probe);
            eprintln!("layer {:?}: effective={c:?}", layer.name);
            assert!(c.iter().all(|v| v.is_finite() && *v >= 0.0));
            if has_tex {
                assert!(
                    c.iter().any(|v| *v < 0.95),
                    "レイヤ {:?}: テクスチャ平均が引けておらず白に張り付いている: {c:?}",
                    layer.name
                );
            }
        }
    }

    /// 既定レイヤセット（草・土・岩・砂）の平均色が「白ではない」こと。
    /// これが本修正の主眼（白／灰色のベタ塗りからの脱却）の回帰テストである。
    #[test]
    fn default_layer_set_chunk_is_not_white() {
        let layers = TerrainLayerSet::default();
        let colors = vec![[1.0, 0.0, 0.0, 0.0]; 4]; // 全頂点がパレット先頭レイヤ
        let avg = chunk_avg_albedo(&colors, [0, 1, 2, 3], &layers);
        assert!(
            avg.iter().any(|v| *v < 0.9),
            "既定レイヤセットのチャンク平均色が白に張り付いている: {avg:?}"
        );
    }
}
