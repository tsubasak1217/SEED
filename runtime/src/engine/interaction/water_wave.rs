// ============================================================
//  interaction/water_wave.rs — 水面への波エネルギー注入量の決定（Phase I2）
//
//  正典: docs/water_interaction_roadmap.md §1.3 / §2 I2。
//
//  ## 役割（単一責任）
//  「速度が確定したインタラクションソース列」と「解決済み水ボリューム列」を突き合わせ、
//  **各ソースが水面へ立てる波の振幅（m 相当）を決める**ことだけを担う。
//  場テクスチャも wgpu も知らない（GPU への転送は renderer::interaction の責務）。
//
//  ## なぜ CPU で判定するのか
//  瞬発場は俯瞰 2D テクスチャであり、高さ（Y）の情報を持たない。
//  「水面から 10m 上を飛ぶ鳥」と「水面を歩くキャラ」を場の上で区別することは
//  原理的にできないため、**Y の照合は場へ焼く前（CPU 側）に済ませる**必要がある。
//  水ボリュームは高々数十個・ソースも高々 64 個なので、総当たりで十分安い。
//
//  ## 注入量の決め方
//    ・水平速度に比例  … 歩けばさざ波、走れば強い航跡（ロードマップの要求そのもの）
//    ・垂直速度はより強く … 飛び込みは水面を大きく叩く
//    ・静止時は注入なし … 立っているだけの物体が波を出し続けるのは不自然
//  いずれも「振幅の上限」で飽和させる（水面が破綻するほどの摂動を入れない）。
// ============================================================

use crate::engine::water::{ResolvedWaterVolume, WaterQuery};

use super::velocity::MovingInteractionSource;

// ─── 調整定数（マジックナンバー禁止）─────────────────────────

/// 「水面付近にいる」とみなす Y 方向の帯の幅（ソース半径に対する倍率）。
///
/// ソースの半径は「そのオブジェクトが場へ及ぼす影響の広がり」なので、
/// おおむねオブジェクトの太さに相当する。半径ぶんだけ水面から離れていても
/// 「水に浸かっている／叩いている」とみなすのが直感に合う。
/// 1.0 より大きくすると宙に浮いた物体まで波を立て始める。
const WAVE_SURFACE_BAND_SCALE: f32 = 1.0;

/// この水平速度（m/s）未満では波を注入しない（静止時の無反応を保証する）。
///
/// 手動ドラッグやアニメーションの微小な揺れで水面が常時ざわつくのを防ぐ。
/// 人がゆっくり歩く速度（約 1.0m/s）の 1/10 に置く。
const WAVE_MIN_HORIZONTAL_SPEED: f32 = 0.1;

/// この垂直速度（m/s）未満では垂直成分の寄与を無視する。
/// 重力で微小に沈む・浮くといった常時の揺れを拾わないための下限。
const WAVE_MIN_VERTICAL_SPEED: f32 = 0.5;

/// 水平速度 1m/s あたりの波振幅（m·s/m）。
///
/// 歩行（約 1.4m/s）で 0.035m ≒ さざ波、疾走（約 6m/s）で 0.15m ≒ はっきりした航跡。
/// 水面法線の摂動として消費されるため、実寸の波高というより「見た目の強さ」の尺度。
const WAVE_AMPLITUDE_PER_HORIZONTAL_SPEED: f32 = 0.025;

/// 垂直速度 1m/s あたりの波振幅（m·s/m）。
///
/// 水平の 3 倍。落下速度 5m/s の飛び込みで一気に上限へ張り付き、
/// 「歩く＝さざ波／飛び込む＝ドボン」の差が明確に出る。
const WAVE_AMPLITUDE_PER_VERTICAL_SPEED: f32 = 0.075;

/// 1 ソースが注入できる波振幅の上限（m 相当）。
///
/// 波の伝播は明示スキームなので、注入値が大きすぎると数値が暴れる。
/// また水面は頂点変位を行わない（法線摂動のみ）ため、これ以上大きくしても
/// 見た目は破綻するだけで良くならない。
const WAVE_MAX_AMPLITUDE: f32 = 0.25;

// ─── 注入量の決定 ────────────────────────────────────────────

/// 各ソースの `wave_amplitude` を、水ボリュームとの照合結果で埋める。
///
/// - `sources`: `InteractionSourceVelocityTracker::update` の結果（速度確定済み）。
/// - `volumes`: `collect_water_volumes` の結果（ワールド解決済み）。
///
/// 水が 1 つも無いフレームは全ソースが 0（注入なし）になる。
/// **この関数は `wave_amplitude` 以外のフィールドを一切書き換えない。**
pub fn apply_water_wave_injection(
    sources: &mut [MovingInteractionSource],
    volumes: &[ResolvedWaterVolume],
) {
    if volumes.is_empty() {
        // 水が無ければ全ソース「注入なし」。前フレームの値が残らないよう明示的に 0 を書く。
        for s in sources.iter_mut() {
            s.wave_amplitude = 0.0;
        }
        return;
    }
    // 水面高さの問い合わせは正式 API（WaterQuery）経由で行う。
    // 描画都合の内部表現を直接読まないという §1.1 の契約を、書き手側でも守る。
    let query = WaterQuery::new(volumes);
    for s in sources.iter_mut() {
        s.wave_amplitude = wave_amplitude_for(s, &query);
    }
}

/// ソース 1 個が立てる波の振幅を求める（水面付近にいなければ 0）。
fn wave_amplitude_for(src: &MovingInteractionSource, query: &WaterQuery<'_>) -> f32 {
    // ① その XZ に水面があるか（無ければ水域外＝注入なし）。
    let Some(surface_y) = query.surface_height_at(src.world_pos) else {
        return 0.0;
    };
    // ② 水面に十分近いか（Y の帯で判定する。空中・水底深くのソースは弾く）。
    //    半径は collect 側で正の値が保証されているが、0 に潰れた場合でも
    //    「水面ちょうど」だけが通る挙動になり破綻しない。
    let band = src.radius.abs() * WAVE_SURFACE_BAND_SCALE;
    if (src.world_pos[1] - surface_y).abs() > band {
        return 0.0;
    }

    // ③ 速度から振幅を作る（静止時は 0）。
    let h_speed = (src.velocity_xz[0] * src.velocity_xz[0]
                 + src.velocity_xz[1] * src.velocity_xz[1]).sqrt();
    let v_speed = src.velocity_y.abs();
    let h_term = if h_speed >= WAVE_MIN_HORIZONTAL_SPEED {
        h_speed * WAVE_AMPLITUDE_PER_HORIZONTAL_SPEED
    } else {
        0.0
    };
    let v_term = if v_speed >= WAVE_MIN_VERTICAL_SPEED {
        v_speed * WAVE_AMPLITUDE_PER_VERTICAL_SPEED
    } else {
        0.0
    };
    // ④ コンポーネントの強さを掛け、上限で飽和させる。
    ((h_term + v_term) * src.strength.max(0.0)).min(WAVE_MAX_AMPLITUDE)
}

// ─── 水位グラフの流量 → 波源（Phase W2.5 の演出接続）───────────

/// 流量 1m³/s あたりの波振幅（m 相当）。
///
/// 扉ごしに部屋が浸水するときの流量はおおむね 0.5〜5m³/s になる。
/// 0.06 を掛けると 0.03〜0.3m ＝「歩行のさざ波〜飛び込みのドボン」と同じ帯へ入り、
/// 既存の航跡・波紋と自然に混ざる（上限は共通の `WAVE_MAX_AMPLITUDE`）。
const WAVE_AMPLITUDE_PER_FLOW: f32 = 0.06;

/// 流量 1m³/s あたりの波源半径（m）。
///
/// 大量に流れ込むほど広い範囲の水面が波立つ、という直感に合わせる。
/// 下限（`FLOW_WAVE_RADIUS_MIN`）を下回らないので、細い流れでも点にはならない。
const FLOW_WAVE_RADIUS_PER_FLOW: f32 = 0.8;

/// 流量由来の波源の最小半径（m）。閾値ちょうどの流れでもこの広さは波立つ。
const FLOW_WAVE_RADIUS_MIN: f32 = 1.0;

/// 流量由来の波源の最大半径（m）。場（俯瞰テクスチャ）の解像度に対して
/// 大きすぎるスタンプは「面が持ち上がる」だけで波に見えないため上限を切る。
const FLOW_WAVE_RADIUS_MAX: f32 = 8.0;

/// 水位グラフの流量イベント 1 件を、インタラクションフィールドの波源へ変換する。
///
/// 開口の位置で水面を叩く「その場に留まる波源」を作る（速度場には寄与しない＝
/// `velocity_xz` は 0）。草をなびかせる速度場と違い、流れ込みが与えたいのは
/// **水面の波エネルギーだけ**だからである。
///
/// 呼び出し側（frame_renderer）は `apply_water_wave_injection` の**後**で
/// この結果を配列へ足すこと（先に足すとあの関数に `wave_amplitude` を潰される）。
pub fn flow_event_wave_source(
    ev: &crate::engine::water::WaterFlowEvent,
) -> MovingInteractionSource {
    let flow = ev.flow.abs();
    MovingInteractionSource {
        world_pos:   ev.world_pos,
        // 流れ込みは「その場で水面を叩く」現象なので速度場へは書かない。
        velocity_xz: [0.0, 0.0],
        velocity_y:  0.0,
        radius:      (flow * FLOW_WAVE_RADIUS_PER_FLOW)
            .clamp(FLOW_WAVE_RADIUS_MIN, FLOW_WAVE_RADIUS_MAX),
        // 強さ（場への書き込み係数）は常に最大。「流れているのに薄い」を避ける。
        strength:    1.0,
        // 振幅は流量に比例させ、既存の波と同じ上限で飽和させる。
        wave_amplitude: (flow * WAVE_AMPLITUDE_PER_FLOW).min(WAVE_MAX_AMPLITUDE),
    }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::water_volume_component::WaterVolumeKind;
    use crate::engine::water::WaterVisualParams;

    /// 判定に関与しない見た目パラメータ（すべてゼロ）。
    fn dummy_visual() -> WaterVisualParams {
        WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 0.0,
            surface_opacity: 0.0, foam_color: [0.0; 3], foam_width: 0.0, foam_intensity: 0.0,
            wave_amplitude: 0.0, wave_scale: 0.0, wave_speed: 0.0, wave_direction_deg: 0.0,
            // 波形ランダマイズ（Phase W6.4）。既定相当の値を入れておく。
            wave_noise_strength: 0.35, wave_noise_scale: 1.0,
            fresnel_power: 0.0,
            fresnel_strength: 0.0, reflection_color: [0.0; 3], refraction_distortion: 0.0,
            ripple_strength: 0.0, ripple_foam_threshold: 0.0,
            // 水中コースティクス（Phase W5.3）。既定相当の値を入れておく。
            caustics_intensity: 0.6, caustics_scale: 1.0, caustics_depth_fade: 6.0,
            // 影の屈折ゆらぎもテスト用ダミーでは 0（＝影をずらさない）。
            shadow_refraction_strength: 0.0,
            // 岸波（W1.5）はテスト用のダミーなので全て 0（＝岸波を出さない）。
            shore_wave_strength: 0.0, shore_wave_length: 0.0,
            shore_wave_period: 0.0, shore_wave_foam: 0.0,
        }
    }

    /// 中心原点・半径 10 の Region（水面 Y = 0）。
    fn pond() -> ResolvedWaterVolume {
        ResolvedWaterVolume {
            kind:         WaterVolumeKind::Region,
            surface_y:    0.0,
            center:       [0.0, 0.0, 0.0],
            half_extents: [10.0, 5.0, 10.0],
            ocean_extent: 0.0,
            visual:       dummy_visual(),
            actor_dfs_id: 0, river: None,
        }
    }

    /// テスト用ソース（半径 1・強さ 1）。
    fn source(pos: [f32; 3], vel_xz: [f32; 2], vel_y: f32) -> MovingInteractionSource {
        MovingInteractionSource {
            world_pos: pos, velocity_xz: vel_xz, velocity_y: vel_y,
            radius: 1.0, strength: 1.0, wave_amplitude: 0.0,
        }
    }

    /// 水面上を走るソースは波を注入する。
    #[test]
    fn moving_source_on_surface_injects() {
        let mut s = vec![source([0.0, 0.0, 0.0], [5.0, 0.0], 0.0)];
        apply_water_wave_injection(&mut s, &[pond()]);
        assert!(s[0].wave_amplitude > 0.0);
    }

    /// 静止しているソースは注入しない（立っているだけで波が出ない）。
    #[test]
    fn stationary_source_injects_nothing() {
        let mut s = vec![source([0.0, 0.0, 0.0], [0.0, 0.0], 0.0)];
        apply_water_wave_injection(&mut s, &[pond()]);
        assert_eq!(s[0].wave_amplitude, 0.0);
    }

    /// 微小な揺れ（しきい値未満）でも注入しない。
    #[test]
    fn sub_threshold_motion_injects_nothing() {
        let mut s = vec![source(
            [0.0, 0.0, 0.0],
            [WAVE_MIN_HORIZONTAL_SPEED * 0.5, 0.0],
            WAVE_MIN_VERTICAL_SPEED * 0.5,
        )];
        apply_water_wave_injection(&mut s, &[pond()]);
        assert_eq!(s[0].wave_amplitude, 0.0);
    }

    /// 水面から半径以上離れた（空中の）ソースは注入しない。
    #[test]
    fn source_far_above_surface_injects_nothing() {
        let mut s = vec![source([0.0, 5.0, 0.0], [5.0, 0.0], 0.0)];
        apply_water_wave_injection(&mut s, &[pond()]);
        assert_eq!(s[0].wave_amplitude, 0.0);
    }

    /// 水域の XZ 外を走るソースは注入しない。
    #[test]
    fn source_outside_water_xz_injects_nothing() {
        let mut s = vec![source([50.0, 0.0, 0.0], [5.0, 0.0], 0.0)];
        apply_water_wave_injection(&mut s, &[pond()]);
        assert_eq!(s[0].wave_amplitude, 0.0);
    }

    /// 同じ速さなら垂直（飛び込み）の方が水平（歩行）より強い。
    #[test]
    fn vertical_motion_injects_more_than_horizontal() {
        let speed = 3.0;
        let mut h = vec![source([0.0, 0.0, 0.0], [speed, 0.0], 0.0)];
        let mut v = vec![source([0.0, 0.0, 0.0], [0.0, 0.0], -speed)];
        apply_water_wave_injection(&mut h, &[pond()]);
        apply_water_wave_injection(&mut v, &[pond()]);
        assert!(v[0].wave_amplitude > h[0].wave_amplitude);
    }

    /// 振幅は上限で飽和する（明示スキームの数値暴走を防ぐ前提条件）。
    #[test]
    fn amplitude_saturates_at_max() {
        let mut s = vec![source([0.0, 0.0, 0.0], [20.0, 20.0], -20.0)];
        apply_water_wave_injection(&mut s, &[pond()]);
        assert_eq!(s[0].wave_amplitude, WAVE_MAX_AMPLITUDE);
    }

    /// 水が 1 つも無ければ、前フレームの注入量は 0 で上書きされる。
    #[test]
    fn no_water_clears_amplitude() {
        let mut s = vec![source([0.0, 0.0, 0.0], [5.0, 0.0], 0.0)];
        s[0].wave_amplitude = 1.0;
        apply_water_wave_injection(&mut s, &[]);
        assert_eq!(s[0].wave_amplitude, 0.0);
    }

    /// Ocean は XZ 無限なので、どこにいても水面高さで判定される。
    #[test]
    fn ocean_injects_anywhere_on_its_surface() {
        let ocean = ResolvedWaterVolume {
            kind: WaterVolumeKind::Ocean, surface_y: 3.0,
            center: [0.0; 3], half_extents: [0.0; 3], ocean_extent: 100.0,
            visual: dummy_visual(), actor_dfs_id: 0, river: None,
        };
        let mut s = vec![source([9999.0, 3.0, -9999.0], [5.0, 0.0], 0.0)];
        apply_water_wave_injection(&mut s, &[ocean]);
        assert!(s[0].wave_amplitude > 0.0);
    }

    /// 強さ 0 のソースは注入しない（コンポーネントの強さがそのまま効く）。
    #[test]
    fn zero_strength_injects_nothing() {
        let mut s = vec![source([0.0, 0.0, 0.0], [5.0, 0.0], 0.0)];
        s[0].strength = 0.0;
        apply_water_wave_injection(&mut s, &[pond()]);
        assert_eq!(s[0].wave_amplitude, 0.0);
    }

    // ── 水位グラフの流量 → 波源（Phase W2.5）──────────────────

    /// 流量イベントは開口位置の波源になり、流量が大きいほど強く広くなること。
    #[test]
    fn flow_event_becomes_wave_source_scaled_by_flow() {
        use crate::engine::water::WaterFlowEvent;
        let weak   = flow_event_wave_source(&WaterFlowEvent {
            world_pos: [1.0, 2.0, 3.0], flow: 0.1 });
        let strong = flow_event_wave_source(&WaterFlowEvent {
            world_pos: [1.0, 2.0, 3.0], flow: 3.0 });
        assert_eq!(weak.world_pos, [1.0, 2.0, 3.0], "開口位置に置かれること");
        assert!(strong.wave_amplitude > weak.wave_amplitude, "流量が大きいほど強い");
        assert!(strong.radius > weak.radius, "流量が大きいほど広い");
        // 流れ込みは速度場へは寄与しない（草をなびかせない）。
        assert_eq!(weak.velocity_xz, [0.0, 0.0]);
        assert_eq!(weak.velocity_y, 0.0);
    }

    /// 巨大流量でも振幅・半径が既存の上限で飽和すること（場の数値暴走の防止）。
    #[test]
    fn flow_event_saturates_at_limits() {
        use crate::engine::water::WaterFlowEvent;
        let huge = flow_event_wave_source(&WaterFlowEvent {
            world_pos: [0.0; 3], flow: 10_000.0 });
        assert_eq!(huge.wave_amplitude, WAVE_MAX_AMPLITUDE);
        assert_eq!(huge.radius, FLOW_WAVE_RADIUS_MAX);
    }
}
