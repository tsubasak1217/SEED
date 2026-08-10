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

/// このチャンクの AABB に「1 テクセルでも積もらせうる」エミッタがあるか（純関数）。
///
/// 【なぜ独立した関数なのか（性能）】
///   駆動側（`terrain_cover_ops::accumulate_cover`）は、カバー場をまだ持たないチャンクへ
///   空のカバー場を作ってから `accumulate_chunk` を呼んでいた。エミッタが遠くにある
///   チャンクでは 1 テクセルも積もらないのに、チャンク数ぶんの `CoverField`
///   （4 配列 × 1024 テクセル）が確保され、以後ずっとメモリと走査対象に居座る。
///   「触る価値があるか」を場の確保より前に判定できるよう、
///   `accumulate_chunk` の早期棄却と**まったく同じ条件**をここへ切り出した。
///
/// - `chunk_origin`: チャンク最小コーナーのワールド座標（メートル）
/// - `chunk_extent`: チャンク 1 辺のワールド長（メートル）
pub fn chunk_has_active_emitter(
    chunk_origin: [f32; 3],
    chunk_extent: f32,
    emitters: &[CoverEmitSpec],
) -> bool {
    if emitters.is_empty() || !(chunk_extent > 0.0) {
        return false;
    }
    let aabb_max = [
        chunk_origin[0] + chunk_extent,
        chunk_origin[1] + chunk_extent,
        chunk_origin[2] + chunk_extent,
    ];
    emitters
        .iter()
        .any(|e| emitter_touches_chunk(e, chunk_origin, aabb_max))
}

/// 積算ティックのタイマを `dt` 秒進め、発火したら「まとめて積むべき秒数」を返す（純関数）。
///
/// 【なぜティックにするのか】性能。理由と間隔の根拠は
/// `material::DEFAULT_ACCUMULATE_INTERVAL_SEC` の説明を参照。
///
/// 【積算総量が毎フレーム実行時と一致する理由】
///   発火時に返すのは「貯めたタイマの全量」であり、タイマは 0 へ戻す。
///   よって発火のたびに返した秒数の総和は、投入した `dt` の総和から
///   「まだ発火していない端数」を引いた値に **厳密に一致**する。
///   積算式は `量 += 被覆率 × 強度 × dt × 傾斜` の単調加算なので、
///   同じ総秒数を 1 回で入れても N 回に分けて入れても飽和前の合計量は等しい。
///
/// - `timer`: 呼び出し側が保持する未消化の経過時間（秒）。本関数が更新する
/// - `dt`: 今フレームの経過時間（秒）。非有限・負値は 0 として扱う
/// - `interval`: ティック間隔（秒）。`CoverMaterialSet::accumulate_interval_sec()` で丸めた値
///
/// 戻り値が `Some(seconds)` のときだけ積算を走らせる。`None` のフレームは何もしない。
pub fn advance_accumulate_tick(timer: &mut f32, dt: f32, interval: f32) -> Option<f32> {
    let step = if dt.is_finite() && dt > 0.0 { dt } else { 0.0 };
    // タイマが壊れた値になっていたら（外部から差し込まれた NaN 等）0 へ戻して復帰する。
    if !timer.is_finite() {
        *timer = 0.0;
    }
    *timer += step;
    // 間隔が不正なら「毎フレーム積算」へ倒す（丸めは呼び出し側の責務だが二重の防波堤）。
    let interval = if interval.is_finite() && interval > 0.0 { interval } else { 0.0 };
    if *timer < interval || *timer <= 0.0 {
        return None;
    }
    let elapsed = *timer;
    *timer = 0.0;
    Some(elapsed)
}

/// エミッタ 1 個がこのチャンク AABB へ寄与しうるか。
///
/// `chunk_has_active_emitter`（場の確保前の判定）と `accumulate_chunk`（実処理の早期棄却）が
/// **同じ条件**で動くことを型で保証するために 1 か所へ切り出してある。
/// 片方だけ緩いと「場は作られるのに何も積もらない」「場が無いのに積もるはずだった」が起きる。
#[inline]
fn emitter_touches_chunk(e: &CoverEmitSpec, aabb_min: [f32; 3], aabb_max: [f32; 3]) -> bool {
    e.rate > 0.0 && !e.is_outside_aabb(aabb_min, aabb_max)
}

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
        .filter(|e| emitter_touches_chunk(e, aabb_min, aabb_max))
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
