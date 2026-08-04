// ============================================================
//  interaction/water_physics.rs — 水域ごとの物性 → 波紋シミュレーション係数（Phase I2.1）
//
//  正典: docs/water_interaction_roadmap.md §2 I2.1。
//
//  ## 役割（単一責任）
//  「水域の物性（粘度・波紋の減衰率）」を「インタラクションフィールドの
//  波動方程式が使う係数（伝播係数 k・1 サブステップの減衰係数 damp）」へ
//  **変換すること**、および「どのテクセルがどの水域に覆われているか」を
//  GPU が引ける矩形リストへ**畳み込むこと**だけを担う。
//
//  wgpu も場テクスチャも知らない（GPU への転送は `renderer::interaction` の責務）。
//  純粋関数とプレーンな値型だけで構成してあるので、安定性の根拠を
//  ユニットテストで直接検証できる（本ファイル末尾）。
//
//  ## なぜ「水域ごと」の係数が要るのか
//  波紋の場は **全水域が 1 枚を共有する**カメラ追従テクスチャである
//  （64m / 512px。草の揺れなど非水用途とも共有）。したがって
//  「泥の池ではゆっくり・水の池では普通」を同時に成立させるには、
//  波を進めるシェーダが**テクセルごとに係数を切り替える**しかない。
//
//  ## 方式の選択（案A: 矩形リストの走査／案B: パラメータマップの事前ベイク）
//  **案A（本実装）**を採る。理由:
//    ・水域は高々数十個で、しかも 64m の窓に重なるものだけへ CPU 側で絞れる
//      （窓外の水域は 1 個も GPU へ行かない）。テクセルあたりの走査は
//      「最初に当たった 1 個で打ち切り」なので実測コストは無視できる。
//    ・案B は「もう 1 枚テクスチャ」＋「もう 1 本のディスパッチ」＋
//      「カメラ移動時の再マップ（＝場と同じ問題をもう一度解く）」を要求する。
//      I2 が「バッファを増やさない」ことを設計の核にしてきたのと真逆になる。
//    ・案A なら**水域が 0 個のフレームは走査ループ自体が回らない**ので、
//      草だけのシーン（非水用途）へのコストがきっかり 0 になる。
//
//  ## 水域外のテクセルは「従来定数」
//  どの矩形にも当たらなかったテクセルは uniform の既定係数
//  （`INTERACTION_WAVE_K` / 既定の減衰）をそのまま使う。草の揺れ・非水用途の
//  領域は W5.2 以前と 1 ビットも変わらない。
// ============================================================

use crate::engine::components::water_volume_component::WaterVolumeKind;
use crate::engine::water::ResolvedWaterVolume;

// ─── 粘度の効き方（マジックナンバー禁止）───────────────────────

/// 粘度の下限（0 = さらさらの水）。
pub const VISCOSITY_MIN: f32 = 0.0;

/// 粘度の上限（1 = 最も重い流体＝マグマ相当）。
///
/// 1 を超える値を許しても各スケール係数が下限へ張り付くだけで意味が無く、
/// 「上げ続ければ止まる」誤解を招くので明示的に締める。
pub const VISCOSITY_MAX: f32 = 1.0;

/// 粘度 1 のとき波源スタンプの振幅が何割**減る**か。
///
/// 0.9 ＝ マグマ（粘度 1）では水の 1/10 の高さの波紋しか立たない。
/// 完全に 0 にしないのは「重い流体でも落下物はわずかに波を立てる」ためで、
/// 0 にすると粘度 1 の水域だけ波紋機能が丸ごと消えたように見える。
pub const VISCOSITY_STAMP_ATTENUATION: f32 = 0.9;

/// 粘度 1 のとき波の伝播速度が何割**減る**か。
///
/// 0.8 ＝ マグマ（粘度 1）では波紋の輪が水の 1/5 の速さで広がる。
/// スタンプ（0.9）より控えめなのは、伝播速度を落としすぎると
/// 波紋が「その場で震えるだけ」になって流体に見えなくなるため。
///
/// **この値は 1 未満でなければならない**（1 以上にすると粘度で波速が 0 以下になり、
/// 波が伝播しない／符号が反転する）。テストで固定する。
pub const VISCOSITY_WAVE_SPEED_ATTENUATION: f32 = 0.8;

// ─── 波紋の減衰率（1/s）の許容レンジ ──────────────────────────

/// 波紋の減衰率の下限（1/s）。
///
/// 0 を許すと `damp = exp(0) = 1` ＝ 波紋が永久に消えない（陽解法の特性方程式の
/// 2 根の積がちょうど 1 ＝ 減衰しない中立モードになる）。目視では止まって見えないが、
/// 場が `settle` しても波が残り続けるという別の破綻を招くため、
/// 「時定数 100 秒」相当の極小値で下限を切る。
pub const RIPPLE_DAMPING_RATE_MIN: f32 = 0.01;

/// 波紋の減衰率の上限（1/s）。
///
/// 時定数 10ms 相当。これより速く消す意味は無く（1 サブステップ = 1/60 秒で
/// 既に 5 桁減る）、大きくし続けると `exp` の結果が非正規化数へ落ちる。
pub const RIPPLE_DAMPING_RATE_MAX: f32 = 100.0;

// ─── スケール係数（純粋関数）──────────────────────────────────

/// 粘度を許容レンジ（0..1）へ丸める。
///
/// **NaN は `VISCOSITY_MIN`（= 0 ＝ふつうの水）へ落とす。**
/// `f32::clamp` は NaN を NaN のまま返すため使えない — 使うと NaN が波速へ伝播し、
/// 波動方程式の 1 テクセルが NaN に汚染されてラプラシアン経由で場全体へ広がる。
/// 「壊れた入力は既定の水として扱う」が最も安全側に倒れる。
fn sanitize_viscosity(viscosity: f32) -> f32 {
    if !viscosity.is_finite() {
        return VISCOSITY_MIN;
    }
    viscosity.max(VISCOSITY_MIN).min(VISCOSITY_MAX)
}

/// 粘度から「波源スタンプの振幅スケール」を求める（0 < scale ≤ 1）。
///
/// **粘度 0 で厳密に 1.0** を返す（＝既存シーンの波紋の振幅が 1 ビットも変わらない）。
pub fn viscosity_stamp_scale(viscosity: f32) -> f32 {
    1.0 - sanitize_viscosity(viscosity) * VISCOSITY_STAMP_ATTENUATION
}

/// 粘度から「波の伝播速度スケール」を求める（0 < scale ≤ 1）。
///
/// **粘度 0 で厳密に 1.0** を返す。解析波（`wave_speed`）の低下係数にも
/// 同じ関数を使う＝見た目（模様のスクロール）と波紋（伝播）が同じ物性で動く。
pub fn viscosity_wave_speed_scale(viscosity: f32) -> f32 {
    1.0 - sanitize_viscosity(viscosity) * VISCOSITY_WAVE_SPEED_ATTENUATION
}

/// 波紋の減衰率を許容レンジへ丸める（1/s）。
///
/// NaN は下限へ落ちる（`clamp` は NaN で panic するため使わない）。
pub fn sanitize_ripple_damping_rate(rate: f32) -> f32 {
    if !rate.is_finite() {
        return RIPPLE_DAMPING_RATE_MIN;
    }
    rate.max(RIPPLE_DAMPING_RATE_MIN).min(RIPPLE_DAMPING_RATE_MAX)
}

// ─── WaterPhysicsRegion（GPU へ渡す前の中間表現）──────────────

/// 「この XZ 矩形の中では、波はこの係数で進む」1 件ぶん。
///
/// GPU 側の構造体（`renderer::interaction::WaterPhysicsRegionGpu`）は
/// これをそのまま詰め替えるだけ。**係数は CPU で完成させておく**のが要点で、
/// 安定性のクランプをシェーダへ散らさずここ 1 箇所で検証できる。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WaterPhysicsRegion {
    /// 矩形のワールド XZ 最小（窓でクリップ済み）。
    pub min_xz: [f32; 2],
    /// 矩形のワールド XZ 最大（窓でクリップ済み）。
    pub max_xz: [f32; 2],
    /// この矩形内での波の伝播係数 k = (c·dt_fixed/dx)²（無次元）。
    pub wave_k: f32,
    /// この矩形内での 1 サブステップぶんの減衰係数 exp(-dt_fixed × 減衰率)。
    pub wave_damp: f32,
}

impl WaterPhysicsRegion {
    /// 矩形の XZ 面積（m²）。**小さいものを優先する**並べ替えに使う。
    pub fn area(&self) -> f32 {
        (self.max_xz[0] - self.min_xz[0]).max(0.0)
            * (self.max_xz[1] - self.min_xz[1]).max(0.0)
    }

    /// 点がこの矩形の内側か（境界を含む）。GPU 側の判定と同一規則。
    pub fn contains_xz(&self, x: f32, z: f32) -> bool {
        x >= self.min_xz[0] && x <= self.max_xz[0]
            && z >= self.min_xz[1] && z <= self.max_xz[1]
    }
}

/// 水域 1 個の XZ 覆い矩形（窓クリップ前）を求める。
///
/// - `Ocean`  … XZ 無限。窓全体を覆う（＝窓と同じ矩形）。
/// - `Region` … AABB の XZ をそのまま使う（厳密）。
/// - `Spline` … **川の折れ線の XZ バウンディングボックス**（近似）。
///   折れ線までの距離をテクセルごとに測るのは高コストなうえ、区間ごとに
///   矩形を分けると 1 本の川で矩形リストを使い切ってしまう。
///   曲がった川では実際の水面より広い範囲を覆うが、
///   後述の「面積の小さい矩形が優先」により、川の中に置かれた小さな池は
///   ちゃんと自分の物性で勝つ。
///
/// 折れ線を持たない Spline（制御点不足）は水域として存在しないので `None`。
fn volume_bounds_xz(
    v:             &ResolvedWaterVolume,
    window_min:    [f32; 2],
    window_max:    [f32; 2],
) -> Option<([f32; 2], [f32; 2])> {
    match v.kind {
        WaterVolumeKind::Ocean => Some((window_min, window_max)),
        WaterVolumeKind::Region => Some((
            [v.center[0] - v.half_extents[0], v.center[2] - v.half_extents[2]],
            [v.center[0] + v.half_extents[0], v.center[2] + v.half_extents[2]],
        )),
        WaterVolumeKind::Spline => {
            let river = v.river.as_ref()?;
            let mut min = [f32::INFINITY, f32::INFINITY];
            let mut max = [f32::NEG_INFINITY, f32::NEG_INFINITY];
            for n in &river.nodes {
                min[0] = min[0].min(n.pos[0]);
                min[1] = min[1].min(n.pos[2]);
                max[0] = max[0].max(n.pos[0]);
                max[1] = max[1].max(n.pos[2]);
            }
            if !min[0].is_finite() || !max[0].is_finite() {
                return None;
            }
            // 中心線の bbox にリボンの半幅ぶんを膨らませる（川の縁まで覆う）。
            let hw = river.half_width.abs();
            Some((
                [min[0] - hw, min[1] - hw],
                [max[0] + hw, max[1] + hw],
            ))
        }
    }
}

/// 解決済み水ボリューム列から、場の窓に重なる「物性矩形」リストを作る。
///
/// - `volumes`       : `collect_water_volumes` の結果（ワールド解決済み）。
/// - `window_origin` : 場の窓のワールド XZ 最小（テクセルスナップ済み）。
/// - `window_extent` : 場の窓の一辺（m）。
/// - `wave_k_base`   : 粘度 0（＝波速そのまま）のときの伝播係数。
/// - `fixed_dt`      : 波を進める固定タイムステップ（秒）。減衰係数の算出に使う。
/// - `max_regions`   : GPU バッファの容量（これを超えたぶんは捨てる）。
///
/// ## 並び順の規約（**小さい矩形が先**）
/// GPU 側は「先頭から走査し、**最初に当たった矩形で打ち切る**」。
/// したがって面積の昇順に並べておくと、大きな水域（大洋・川の bbox）に
/// 覆われた小さな池が自分の物性で勝つ。容量オーバーで捨てるのも
/// 「最も大きい＝最も大雑把な」矩形からになるので、切り捨ての害も最小になる。
///
/// ## 窓外の水域は 1 個も入らない
/// 窓と交差しない水域は完全に除外する。GPU の走査ループは窓に映る水域の数
/// （典型 0〜2 個）しか回らない。
pub fn collect_water_physics_regions(
    volumes:       &[ResolvedWaterVolume],
    window_origin: [f32; 2],
    window_extent: f32,
    wave_k_base:   f32,
    fixed_dt:      f32,
    max_regions:   usize,
) -> Vec<WaterPhysicsRegion> {
    let window_min = window_origin;
    let window_max = [window_origin[0] + window_extent, window_origin[1] + window_extent];

    let mut out: Vec<WaterPhysicsRegion> = Vec::new();
    for v in volumes {
        let Some((mut min, mut max)) = volume_bounds_xz(v, window_min, window_max) else {
            continue;
        };
        // 窓でクリップする（窓外のテクセルは走査しないので持つ意味が無い）。
        min[0] = min[0].max(window_min[0]);
        min[1] = min[1].max(window_min[1]);
        max[0] = max[0].min(window_max[0]);
        max[1] = max[1].min(window_max[1]);
        // 交差しない（＝クリップ後に潰れた）水域は捨てる。
        if !(min[0] < max[0] && min[1] < max[1]) {
            continue;
        }

        // ── 物性 → 係数 ──
        //   波速は粘度で**下がる方向にしか動かない**ので、k は基準値以下に留まる
        //   ＝ CFL 安定条件（k ≤ 1/2）が粘度で破れることは原理的にありえない。
        let speed_scale = viscosity_wave_speed_scale(v.visual.viscosity);
        let wave_k      = wave_k_base * speed_scale * speed_scale;
        let rate        = sanitize_ripple_damping_rate(v.visual.ripple_damping);
        let wave_damp   = (-fixed_dt * rate).exp();

        out.push(WaterPhysicsRegion { min_xz: min, max_xz: max, wave_k, wave_damp });
    }

    // 面積の昇順（小さい＝具体的な水域が優先）。NaN は入らない（クリップ済み矩形）。
    out.sort_by(|a, b| a.area().partial_cmp(&b.area()).unwrap_or(std::cmp::Ordering::Equal));
    out.truncate(max_regions);
    out
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::water::WaterVisualParams;

    /// 判定に関与しない見た目パラメータ（物性だけ差し替えて使う）。
    fn visual(viscosity: f32, ripple_damping: f32) -> WaterVisualParams {
        WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 0.0,
            surface_opacity: 0.0, foam_color: [0.0; 3], foam_width: 0.0, foam_intensity: 0.0,
            wave_amplitude: 0.0, wave_scale: 0.0, wave_speed: 0.0, wave_direction_deg: 0.0,
            wave_noise_strength: 0.0, wave_noise_scale: 1.0,
            fresnel_power: 0.0, fresnel_strength: 0.0,
            reflection_intensity: 0.0, reflection_roughness: 0.0,
            refraction_distortion: 0.0,
            ripple_strength: 0.0, ripple_foam_threshold: 0.0,
            viscosity, ripple_damping,
            caustics_intensity: 0.0, caustics_scale: 1.0, caustics_depth_fade: 1.0,
            shadow_refraction_strength: 0.0,
            shore_wave_strength: 0.0, shore_wave_length: 1.0,
            shore_wave_period: 1.0, shore_wave_foam: 0.0,
        }
    }

    /// 指定の中心・半径・物性を持つ Region。
    fn region(center: [f32; 3], half: [f32; 3], viscosity: f32, damping: f32)
        -> ResolvedWaterVolume
    {
        ResolvedWaterVolume {
            kind: WaterVolumeKind::Region, surface_y: 0.0,
            center, half_extents: half, ocean_extent: 0.0,
            visual: visual(viscosity, damping), actor_dfs_id: 0, river: None,
        }
    }

    /// テスト用の基準値（`renderer::interaction` の実定数と同じ値）。
    const K_BASE:   f32 = 0.25;
    const FIXED_DT: f32 = 1.0 / 60.0;

    /// **粘度 0 のスケールは厳密に 1.0**（＝既存シーンがビット単位で変わらない根拠）。
    #[test]
    fn zero_viscosity_scales_are_exactly_one() {
        assert_eq!(viscosity_stamp_scale(0.0), 1.0);
        assert_eq!(viscosity_wave_speed_scale(0.0), 1.0);
        // 負値を渡してもクランプで 0 相当＝1.0（既定より「軽い水」は作れない）。
        assert_eq!(viscosity_wave_speed_scale(-5.0), 1.0);
    }

    /// 粘度を上げるとスケールは単調に下がり、上限でも正の値に留まること。
    #[test]
    fn viscosity_scales_decrease_monotonically_and_stay_positive() {
        let mut prev_stamp = f32::INFINITY;
        let mut prev_speed = f32::INFINITY;
        for i in 0..=10 {
            let v = i as f32 / 10.0;
            let s = viscosity_stamp_scale(v);
            let c = viscosity_wave_speed_scale(v);
            assert!(s < prev_stamp && c < prev_speed, "粘度 {v} で単調でない");
            assert!(s > 0.0 && c > 0.0, "粘度 {v} でスケールが 0 以下（波が消える／反転する）");
            prev_stamp = s;
            prev_speed = c;
        }
        // 上限を超えても飽和するだけ（粘度 2 は粘度 1 と同じ）。
        assert_eq!(viscosity_wave_speed_scale(2.0), viscosity_wave_speed_scale(1.0));
    }

    /// **壊れた粘度（NaN / 無限大）は「ふつうの水」へ落ちること。**
    ///
    /// `f32::clamp` は NaN を素通しするため、素朴に書くと NaN が波速 → 伝播係数へ
    /// 伝播し、1 テクセルの NaN がラプラシアン経由で場全体へ広がる
    ///（＝水面が丸ごと消える／真っ黒になる）。実装時に実際に踏んだ罠なので契約で固定する。
    #[test]
    fn non_finite_viscosity_falls_back_to_plain_water() {
        for bad in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
            assert_eq!(viscosity_stamp_scale(bad), 1.0, "{bad} でスケールが 1.0 でない");
            assert_eq!(viscosity_wave_speed_scale(bad), 1.0, "{bad} でスケールが 1.0 でない");
        }
    }

    /// 減衰の設計前提: 波速の低下係数は 1 未満（1 以上だと波速が 0 以下になる）。
    #[test]
    fn attenuation_constants_are_within_design_range() {
        assert!(VISCOSITY_WAVE_SPEED_ATTENUATION < 1.0);
        assert!(VISCOSITY_STAMP_ATTENUATION < 1.0);
        assert!(VISCOSITY_WAVE_SPEED_ATTENUATION > 0.0);
        assert!(VISCOSITY_STAMP_ATTENUATION > 0.0);
    }

    /// **どんな粘度・減衰率でも陽解法が発散しないこと**（本フェーズ最重要の不変条件）。
    ///
    /// 波動方程式の陽解法のモード解析では、特性方程式
    /// `g² − damp·(2 − kλ)·g + damp = 0` の 2 根の積が `damp` になる。したがって
    ///   ① `k ≤ 1/2`（CFL）かつ ② `0 < damp < 1`
    /// が保たれる限り、全モードが必ず減衰する。粘度は波速を**下げる方向にしか
    /// 動かない**ので ① は基準値のまま安全側、② はクランプで担保される。
    #[test]
    fn any_viscosity_and_damping_keeps_scheme_stable() {
        const CFL_LIMIT: f32 = 0.5;
        // 極端な入力（範囲外・NaN・無限大）も含めて総当たりする。
        let viscosities = [-1.0, 0.0, 0.25, 0.5, 0.75, 1.0, 2.0, f32::NAN];
        let rates       = [-10.0, 0.0, 0.001, 0.6667, 5.0, 1.0e9, f32::NAN, f32::INFINITY];
        for &visc in &viscosities {
            for &rate in &rates {
                let v = region([0.0; 3], [10.0, 1.0, 10.0], visc, rate);
                let regions = collect_water_physics_regions(
                    &[v], [-32.0, -32.0], 64.0, K_BASE, FIXED_DT, 32);
                assert_eq!(regions.len(), 1);
                let r = regions[0];
                assert!(r.wave_k.is_finite() && r.wave_k > 0.0 && r.wave_k <= CFL_LIMIT,
                    "粘度 {visc} / 減衰率 {rate} で k={} が CFL 限界外", r.wave_k);
                assert!(r.wave_k <= K_BASE + 1e-6,
                    "粘度で k が基準値を上回った（波速は下がる方向のみのはず）: {}", r.wave_k);
                assert!(r.wave_damp.is_finite() && r.wave_damp > 0.0 && r.wave_damp < 1.0,
                    "粘度 {visc} / 減衰率 {rate} で damp={} が (0,1) の外", r.wave_damp);
            }
        }
    }

    /// 既定値（粘度 0・減衰率 = 1/1.5）は現行エンジン定数と等価な係数になること
    /// ＝**既存シーンの見た目が変わらない**契約。
    #[test]
    fn default_physics_matches_current_engine_constants() {
        let v = region([0.0; 3], [10.0, 1.0, 10.0], 0.0, 1.0 / 1.5);
        let regions = collect_water_physics_regions(
            &[v], [-32.0, -32.0], 64.0, K_BASE, FIXED_DT, 32);
        // 伝播係数は基準値と**ビット単位で同一**（スケールが厳密に 1.0 のため）。
        assert_eq!(regions[0].wave_k, K_BASE);
        // 減衰係数は現行の exp(-dt/τ)（τ=1.5s）と一致する
        //（率と時定数の相互変換で最下位ビットが揺れうるため許容誤差つき）。
        let legacy = (-FIXED_DT / 1.5_f32).exp();
        assert!((regions[0].wave_damp - legacy).abs() < 1.0e-7,
            "既定の減衰係数 {} が現行値 {legacy} と食い違う", regions[0].wave_damp);
    }

    /// 窓に重ならない水域は 1 個も入らないこと（＝GPU の走査コストが増えない）。
    #[test]
    fn volumes_outside_window_are_dropped() {
        let far = region([1000.0, 0.0, 1000.0], [5.0, 1.0, 5.0], 0.5, 1.0);
        let regions = collect_water_physics_regions(
            &[far], [-32.0, -32.0], 64.0, K_BASE, FIXED_DT, 32);
        assert!(regions.is_empty(), "窓外の水域が矩形リストへ入っている");
    }

    /// 矩形は窓でクリップされること（窓の外へはみ出した部分を持たない）。
    #[test]
    fn regions_are_clipped_to_the_window() {
        let big = region([0.0; 3], [1000.0, 1.0, 1000.0], 0.5, 1.0);
        let regions = collect_water_physics_regions(
            &[big], [-32.0, -32.0], 64.0, K_BASE, FIXED_DT, 32);
        assert_eq!(regions[0].min_xz, [-32.0, -32.0]);
        assert_eq!(regions[0].max_xz, [32.0, 32.0]);
    }

    /// Ocean は窓全体を覆うこと（XZ 無限を窓の矩形で表現する）。
    #[test]
    fn ocean_covers_the_whole_window() {
        let ocean = ResolvedWaterVolume {
            kind: WaterVolumeKind::Ocean, surface_y: 0.0,
            center: [0.0; 3], half_extents: [0.0; 3], ocean_extent: 2000.0,
            visual: visual(0.5, 1.0), actor_dfs_id: 0, river: None,
        };
        let regions = collect_water_physics_regions(
            &[ocean], [100.0, -50.0], 64.0, K_BASE, FIXED_DT, 32);
        assert_eq!(regions[0].min_xz, [100.0, -50.0]);
        assert_eq!(regions[0].max_xz, [164.0, 14.0]);
    }

    /// **小さい矩形が先に来ること**（大洋の中に置いた池が自分の物性で勝つ根拠）。
    #[test]
    fn smaller_regions_come_first() {
        let ocean = ResolvedWaterVolume {
            kind: WaterVolumeKind::Ocean, surface_y: 0.0,
            center: [0.0; 3], half_extents: [0.0; 3], ocean_extent: 2000.0,
            visual: visual(0.0, 1.0), actor_dfs_id: 0, river: None,
        };
        // 粘度 1 のマグマ池（小さい）を大洋（大きい）より **後ろ**に置いて渡す。
        let pond = region([0.0; 3], [4.0, 1.0, 4.0], 1.0, 1.0);
        let regions = collect_water_physics_regions(
            &[ocean, pond], [-32.0, -32.0], 64.0, K_BASE, FIXED_DT, 32);
        assert_eq!(regions.len(), 2);
        assert!(regions[0].area() < regions[1].area(), "小さい矩形が先頭に来ていない");
        // 先頭（池）は粘度 1 相当の遅い波、2 番目（大洋）は基準の波。
        assert!(regions[0].wave_k < regions[1].wave_k);
        assert_eq!(regions[1].wave_k, K_BASE, "粘度 0 の大洋は基準係数のまま");
        // 池の中心は池の矩形に入り、池の外・窓の中は大洋の矩形にだけ入る。
        assert!(regions[0].contains_xz(0.0, 0.0));
        assert!(!regions[0].contains_xz(20.0, 0.0));
        assert!(regions[1].contains_xz(20.0, 0.0));
    }

    /// 容量を超えたぶんは「大きい矩形から」捨てられること。
    #[test]
    fn overflow_drops_the_largest_regions() {
        let volumes: Vec<ResolvedWaterVolume> = (1..=5)
            .map(|i| region([0.0; 3], [i as f32, 1.0, i as f32], 0.0, 1.0))
            .collect();
        let regions = collect_water_physics_regions(
            &volumes, [-32.0, -32.0], 64.0, K_BASE, FIXED_DT, 2);
        assert_eq!(regions.len(), 2);
        // 残ったのは半径 1 と 2 の矩形（面積 4 と 16）。
        assert!((regions[0].area() - 4.0).abs() < 1e-4);
        assert!((regions[1].area() - 16.0).abs() < 1e-4);
    }

    /// 水域が 0 個なら矩形も 0 個（草だけのシーンは走査ループが回らない）。
    #[test]
    fn no_water_produces_no_regions() {
        let regions = collect_water_physics_regions(
            &[], [-32.0, -32.0], 64.0, K_BASE, FIXED_DT, 32);
        assert!(regions.is_empty());
    }

    /// 川（Spline）は折れ線 bbox ＋ 半幅で覆われること。
    #[test]
    fn river_is_covered_by_its_polyline_bounds() {
        use crate::engine::water::RiverPath;
        let path = RiverPath::build(
            &[[-10.0, 0.0, 0.0], [10.0, 0.0, 0.0]], 4.0, 1.0, 2.0, 2.0)
            .expect("2 点で川が成立すること");
        let v = ResolvedWaterVolume {
            kind: WaterVolumeKind::Spline, surface_y: 0.0,
            center: [0.0; 3], half_extents: [0.0; 3], ocean_extent: 0.0,
            visual: visual(1.0, 1.0), actor_dfs_id: 0, river: Some(path),
        };
        let regions = collect_water_physics_regions(
            &[v], [-32.0, -32.0], 64.0, K_BASE, FIXED_DT, 32);
        assert_eq!(regions.len(), 1, "川が矩形へ落ちていない");
        // 幅 4m ＝ 半幅 2m ぶん Z 方向へ膨らむ。
        assert!(regions[0].contains_xz(0.0, 1.5), "川の内側が覆われていない");
        assert!(!regions[0].contains_xz(0.0, 10.0), "川から遠い所まで覆っている");
    }

    /// 制御点が足りない川は水域として存在しない（矩形も作らない）。
    #[test]
    fn river_without_path_produces_no_region() {
        let v = ResolvedWaterVolume {
            kind: WaterVolumeKind::Spline, surface_y: 0.0,
            center: [0.0; 3], half_extents: [0.0; 3], ocean_extent: 0.0,
            visual: visual(1.0, 1.0), actor_dfs_id: 0, river: None,
        };
        let regions = collect_water_physics_regions(
            &[v], [-32.0, -32.0], 64.0, K_BASE, FIXED_DT, 32);
        assert!(regions.is_empty());
    }

    /// 減衰率のクランプ（0・負値・NaN・巨大値）が許容レンジへ収まること。
    #[test]
    fn ripple_damping_rate_is_sanitized() {
        assert_eq!(sanitize_ripple_damping_rate(0.0), RIPPLE_DAMPING_RATE_MIN);
        assert_eq!(sanitize_ripple_damping_rate(-1.0), RIPPLE_DAMPING_RATE_MIN);
        assert_eq!(sanitize_ripple_damping_rate(f32::NAN), RIPPLE_DAMPING_RATE_MIN);
        assert_eq!(sanitize_ripple_damping_rate(f32::INFINITY), RIPPLE_DAMPING_RATE_MIN,
            "非有限値は下限へ落とす（exp が 0 になるのを避ける）");
        assert_eq!(sanitize_ripple_damping_rate(1.0e9), RIPPLE_DAMPING_RATE_MAX);
        // レンジ内はそのまま通る。
        assert_eq!(sanitize_ripple_damping_rate(1.5), 1.5);
    }
}
