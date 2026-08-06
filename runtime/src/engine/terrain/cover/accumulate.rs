// ============================================================
//  terrain/cover/accumulate.rs — カバー場の積算（純粋関数）
//
//  【責務】
//    「カバー場 ＋ 地表情報 ＋ エミッタ列 ＋ 経過時間」から
//    新しいカバー場を作る 1 本の関数だけを持つ。
//    Edit のシミュレートボタンも Play 中の毎フレーム積算も、
//    **まったく同じこの関数**を呼ぶ（挙動が二重定義にならない）。
//
//  【自然減衰は無い】
//    本フェーズでは融解・埋め戻し・風化は扱わない（スコープ外）。
//    積もる方向にしか動かないため、シミュレート時間が長いほど
//    単調に量が増えて必ず飽和する（＝発散しない）。
// ============================================================

use super::emit::CoverEmitSpec;
use super::field::{
    texel_center_uv, slope_scale, CoverField, CoverSurface, COVER_FIELD_RESOLUTION,
};

/// 1 チャンク分のカバー場を `dt` 秒ぶん進める。
///
/// - `chunk_origin`: チャンク最小コーナーのワールド座標（メートル）
/// - `chunk_extent`: チャンク 1 辺のワールド長（メートル）
/// - `emitters`: ワールド解決済みのエミッタ列
/// - `dt`: 経過時間（秒）
///
/// 戻り値は「カバー場が実際に変化したか」。false のときチャンクは
/// ダーティにならず、頂点の焼き直しも保存も走らない
/// （＝エミッタが 1 つも無いフレームは完全に無コストで、絵も 1 ピクセルも変わらない）。
pub fn accumulate_chunk(
    field: &mut CoverField,
    surface: &CoverSurface,
    chunk_origin: [f32; 3],
    chunk_extent: f32,
    emitters: &[CoverEmitSpec],
    dt: f32,
) -> bool {
    // ─── 早期棄却: 時間が進まない／エミッタが無い ───
    if !dt.is_finite() || dt <= 0.0 || emitters.is_empty() || !(chunk_extent > 0.0) {
        return false;
    }

    // ─── 早期棄却: このチャンクの AABB にかかるエミッタだけを残す ───
    //   Region / TextureMask が遠くにあるだけのチャンクでは、
    //   32×32 のループを 1 周も回さずに抜ける。
    let aabb_min = chunk_origin;
    let aabb_max = [
        chunk_origin[0] + chunk_extent,
        chunk_origin[1] + chunk_extent,
        chunk_origin[2] + chunk_extent,
    ];
    let active: Vec<&CoverEmitSpec> = emitters
        .iter()
        .filter(|e| e.rate > 0.0 && !e.is_outside_aabb(aabb_min, aabb_max))
        .collect();
    if active.is_empty() {
        return false;
    }

    let mut changed = false;
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            // ─── 面が無いテクセル（空中・チャンクが全て個体）は積もらない ───
            if !surface.has_surface(ix, iz) {
                continue;
            }
            // ─── 傾斜ルール: 急斜面ほど積もりにくい ───
            let slope = slope_scale(surface.up_at(ix, iz));
            if slope <= 0.0 {
                continue;
            }

            // ─── テクセル中心の地表ワールド座標 ───
            //   Y は「面の高さ」を使う。これにより Region の Y 方向の範囲判定が
            //   「谷にだけ雪を積もらせる」という直感どおりに効く。
            let (u, v) = texel_center_uv(ix, iz);
            let world = [
                chunk_origin[0] + u * chunk_extent,
                surface.surface_y_at(ix, iz),
                chunk_origin[2] + v * chunk_extent,
            ];

            // ─── 各エミッタの寄与を順に積む ───
            //   複数エミッタが同じテクセルへ違う素材を降らせた場合は、
            //   配列の後ろのエミッタほど後に適用される（＝置き換え規則で後勝ち）。
            for e in &active {
                let coverage = e.coverage_at(world);
                if coverage <= 0.0 {
                    continue;
                }
                let delta = coverage * e.rate * dt * slope;
                if delta <= 0.0 {
                    continue;
                }
                let before_amount = field.amount_at(ix, iz);
                let before_material = field.material_at(ix, iz);
                field.deposit(ix, iz, e.material_index, delta);
                if field.amount_at(ix, iz) != before_amount
                    || field.material_at(ix, iz) != before_material
                {
                    changed = true;
                }
            }
        }
    }
    changed
}
