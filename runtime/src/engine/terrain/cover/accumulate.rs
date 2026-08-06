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
//    融解・風化は扱わない（スコープ外）。積もる方向にしか動かないため、
//    シミュレート時間が長いほど単調に量が増えて必ず飽和する（＝発散しない）。
//
//  【轍の埋め戻し（I3.2）】
//    「自然に消える」ことはしないが、**新しく積もった分だけ轍が浅くなる**。
//    降雪が足跡を埋めるのはこの経路であり、エミッタが止まれば轍は永久に残る。
//    埋め戻し速度は「今このテクセルへ積もった量（＝エミッタ強度に比例）」＋
//    「素材の既定速度 `refill_rate`（量/秒）」の合計とする。
// ============================================================

use super::emit::CoverEmitSpec;
use super::field::{
    texel_center_uv, slope_scale, CoverField, CoverSurface, COVER_BASE_Y_ABSENT,
    COVER_FIELD_RESOLUTION,
};
use super::material::CoverMaterialSet;

/// 1 チャンク分のカバー場を `dt` 秒ぶん進める。
///
/// - `chunk_origin`: チャンク最小コーナーのワールド座標（メートル）
/// - `chunk_extent`: チャンク 1 辺のワールド長（メートル）
/// - `emitters`: ワールド解決済みのエミッタ列
/// - `materials`: カバー素材定義（轍の埋め戻し速度 `refill_rate` を引くために要る）
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
    materials: &CoverMaterialSet,
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
                // 面が消えたテクセルの基準 Y は「未知」へ戻す
                // （地形を掘り直した後に古い高さが残ると Y 照合が誤爆する）。
                field.set_base_y(ix, iz, COVER_BASE_Y_ABSENT);
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
            let surface_y = surface.surface_y_at(ix, iz);
            let world = [
                chunk_origin[0] + u * chunk_extent,
                surface_y,
                chunk_origin[2] + v * chunk_extent,
            ];

            // ─── 面の基準 Y を同期する（I3.2 の Y 照合の土台）───
            //   「量を持つテクセルの基準 Y は必ず有限」という不変条件を、
            //   積む前に更新しておくことで満たす。
            field.set_base_y(ix, iz, surface_y);

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

                // ─── 轍の埋め戻し（I3.2）───
                //   降った分（delta）はそのまま轍を浅くする。加えて素材が
                //   「自分で均される」性質（`refill_rate`：雪はさらさらと崩れる）を
                //   持つなら、その速度ぶんも被覆率に比例して埋める。
                //   エミッタが止まればどちらの項も 0 になり、轍は永久に残る。
                let material_refill = materials
                    .get(e.material_index as usize)
                    .map(|m| m.refill_rate)
                    .unwrap_or(0.0);
                let refill = delta + material_refill * coverage * dt;
                if field.refill_trample(ix, iz, refill) {
                    changed = true;
                }
            }
        }
    }
    changed
}
