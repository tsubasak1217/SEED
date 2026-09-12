// ============================================================
//  shadow_settings.rs — シャドウマップ品質設定（データ駆動・プロセスグローバル）
//
//  》含む処理「
//  - ShadowQuality:            シャドウマップ経路の品質パラメータ一式（唯一の正典）
//  - DEFAULT_*:                既定値（project_settings.json に shadow ブロックが無いときの値）
//  - ShadowQuality::sanitize():入力値の検証・クランプ — 純関数
//  - parse_shadow_quality():   project_settings.json 文字列 → ShadowQuality — 純関数
//  - set_shadow_quality() / shadow_quality(): プロセス全体の現在値
//
//  【なぜプロセスグローバルなのか】
//  同じ値を読む場所が **互いに離れた 3 系統** にまたがるため。
//    1. ShadowResources::new       … 深度テクスチャの解像度（起動時 1 回）
//    2. ShadowDepthPipelines::new  … ラスタライザの slope-scaled 深度バイアス（起動時 1 回）
//    3. ShadowResources::prepare_frame … カスケード分割・法線オフセット・PCF 半径（毎フレーム）
//  1 と 2 は GPU 資源生成の奥（DrawContext::new の内側）にあり、App のフィールドを
//  引数で配り回すと DrawPipelines まで貫通する長い配線になる。既存の LOD 設定
//  （renderer::lod_settings）と同じ「値の置き場を 1 か所に決める」流儀に揃える。
//  1 プロセス = 1 プロジェクトなので、起動時に 1 回流し込めば足りる。
//
//  【解像度だけは起動時固定】
//  resolution は深度テクスチャと BindGroup の実体に直結するため、実行中に変えると
//  group 4 の複合 BindGroup（lighting.rs）まで作り直す必要がある。エディタから
//  変更した場合は「次回起動から反映」とする（UI 側にもその旨を表示する）。
//  それ以外のパラメータは毎フレーム読まれるため、将来 IPC でライブ変更できる。
// ============================================================

use std::sync::RwLock;

// ─── 既定値（shadow ブロックが無いときの値）────────────────────
//
// ★ 既定は「従来（Phase R2 当時）の値」ではなく **改善後の値** である。
//   従来の既定（split_lambda=0.5・距離無制限・PCF 3x3 固定）は、遠景まで届く
//   カメラ（far=1000 等）で カスケード 0 のテクセルが 10cm 級まで肥大し、
//   シャドウアクネ（縞）と輪郭のジャギーを生む主因だった。設定ファイルを
//   持たない既存プロジェクトこそ改善が必要なので、既定値側を改善する。

/// CSM 1 カスケードあたりの既定解像度（正方）。従来と同値（据え置き）。
pub const DEFAULT_SHADOW_RESOLUTION: u32 = 2048;

/// 選択できるシャドウマップ解像度（エディタのコンボボックスと一致させること）。
pub const SHADOW_RESOLUTION_CHOICES: [u32; 3] = [1024, 2048, 4096];

/// 影を描画する最大距離 [ワールド単位] の既定値。
///
/// CSM の全カスケードはカメラの near..min(far, この距離) を分割する。
/// **この値がシャドウマップの実効解像度を決める最大の要因**である。
/// 例（fov_y=25°, aspect=16:9, 3 カスケード, split_lambda=0.8, 2048px）:
///   - distance=1000（従来＝カメラ far そのまま）→ カスケード 0 のテクセル ≒ 11cm
///   - distance=150                               → カスケード 0 のテクセル ≒ 7mm
/// 150 は「屋外シーンで人・建物のディテール影が要る範囲」を基準にした値。
/// これより遠くは影が付かなくなる（＝光が遮られない）ため、遠景の巨大な影が
/// 要るシーンでは引き上げること。
pub const DEFAULT_SHADOW_DISTANCE: f32 = 150.0;

/// practical split（log/uniform 混合）の既定ブレンド係数。0=均等分割、1=対数分割。
///
/// 0.8 は対数寄り＝近距離のカスケードを小さく取り、手前のテクセル密度を優先する。
/// 従来値 0.5 では カスケード 0 がカメラ far の 1/6 程度まで広がり、近景の
/// テクセルが粗くなっていた。1.0 に寄せすぎると カスケード 0 が数 m まで縮み、
/// カスケード 2 に負担が集中して遠景が破綻するため 0.8 で止める。
pub const DEFAULT_SPLIT_LAMBDA: f32 = 0.8;

/// 法線オフセット量（そのカスケードの**ワールド テクセル幅**の倍数）。
///
/// シャドウ空間へ投影する前にワールド位置を幾何法線方向へずらす量。
/// 「1 テクセル動いたときに生じる深度誤差」を位置側で吸収する手法なので、
/// 単位をテクセル幅にしておけば カスケード・解像度が変わっても自動で追従する。
/// 1.5 は 1 テクセル（＝アクネの原因となる量子化幅）＋ バイリニア PCF が
/// 参照する半テクセルぶんの余裕。大きくするとピーターパン（影の浮き）になる。
pub const DEFAULT_NORMAL_OFFSET_TEXELS: f32 = 1.5;

/// 定数深度バイアス（そのカスケードの**ワールド テクセル幅**の倍数）。
///
/// 法線オフセットで吸収しきれない残差（法線が真上を向く面＝オフセットが効かない面の
/// 深度量子化）を潰すための最終保険。ワールド距離で与えてカスケードごとに
/// NDC へ換算するため、カスケードが変わっても「効き」が一定になる
/// （従来は NDC 定数 0.0012 固定で、遠カスケードでは 0.6m 相当まで膨らんでいた）。
pub const DEFAULT_DEPTH_BIAS_TEXELS: f32 = 1.0;

/// ラスタライザの slope-scaled 深度バイアス（シャドウ深度パスのパイプライン設定）。
/// 従来値 2.0 を据え置く（法線オフセットと役割が違い、両方あって初めて全角度を覆える）。
pub const DEFAULT_SLOPE_BIAS: f32 = 2.0;

/// ラスタライザの定数深度バイアス（深度フォーマットの最小表現単位の整数倍）。従来値据え置き。
pub const DEFAULT_RASTER_CONST_BIAS: i32 = 2;

/// PCF フィルタ半径（テクセル単位）。
///
/// 半径 1.5 テクセルの円板へ Vogel ディスク（黄金角らせん）でタップを撒き、
/// ピクセルごとに回転させる。比較サンプラーが Linear（＝1 タップが 2x2 バイリニア PCF）
/// なので実効の柔らかさは約 ±2 テクセル。
/// これ以上広げると接地部の影が弱くなり（光漏れに見える）、狭めると階段状の輪郭が戻る。
pub const DEFAULT_PCF_RADIUS_TEXELS: f32 = 1.5;

/// PCF のタップ数（1..=MAX_PCF_TAPS）。
/// 12 タップ ＋ ピクセルごとの回転で、3x3（9 タップ・固定格子）の階段状の縞を消す。
pub const DEFAULT_PCF_TAPS: u32 = 12;

/// PCF タップ数の上限。Vogel ディスクは解析式なので本来いくつでも撒けるが、
/// 1 タップ = 1 回の textureSampleCompare（＝2x2 バイリニア PCF）でコストが線形に増えるため、
/// 事故防止に頭を押さえる。上げるときは GPU 実測で確認すること。
pub const MAX_PCF_TAPS: u32 = 16;

// ─── 値域（sanitize のクランプ範囲）─────────────────────────────

/// 影距離の下限／上限 [ワールド単位]。
const SHADOW_DISTANCE_MIN: f32 = 1.0;
const SHADOW_DISTANCE_MAX: f32 = 100_000.0;
/// 法線オフセット・定数バイアス・PCF 半径の上限（テクセル単位）。
/// 4 テクセル以上はピーターパンが誰の目にも分かる領域なので、事故防止に頭を押さえる。
const TEXEL_SCALE_MAX: f32 = 8.0;
/// slope-scaled バイアスの上限。
const SLOPE_BIAS_MAX: f32 = 16.0;

// ============================================================
//  ShadowQuality
// ============================================================

/// シャドウマップ経路の品質パラメータ一式。
///
/// project_settings.json の `shadow` ブロックがそのまま写る構造体で、
/// **シャドウマップの見た目を決める値はすべてここにある**（マジックナンバー禁止の受け皿）。
/// RT 影（features.shadow = "rt"）では一切参照されない。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShadowQuality {
    /// CSM 1 カスケードの解像度（正方・起動時固定）。
    pub resolution: u32,
    /// 影を描画する最大距離 [ワールド単位]（カメラ far との小さい方が使われる）。
    pub distance: f32,
    /// practical split のブレンド係数（0=均等, 1=対数）。
    pub split_lambda: f32,
    /// 法線オフセット量（ワールド テクセル幅の倍数）。
    pub normal_offset_texels: f32,
    /// 定数深度バイアス（ワールド テクセル幅の倍数）。
    pub depth_bias_texels: f32,
    /// ラスタライザの slope-scaled 深度バイアス。
    pub slope_bias: f32,
    /// PCF フィルタ半径（テクセル単位）。
    pub pcf_radius_texels: f32,
    /// PCF のタップ数（1..=MAX_PCF_TAPS）。
    pub pcf_taps: u32,
}

impl Default for ShadowQuality {
    fn default() -> Self {
        Self {
            resolution:           DEFAULT_SHADOW_RESOLUTION,
            distance:             DEFAULT_SHADOW_DISTANCE,
            split_lambda:         DEFAULT_SPLIT_LAMBDA,
            normal_offset_texels: DEFAULT_NORMAL_OFFSET_TEXELS,
            depth_bias_texels:    DEFAULT_DEPTH_BIAS_TEXELS,
            slope_bias:           DEFAULT_SLOPE_BIAS,
            pcf_radius_texels:    DEFAULT_PCF_RADIUS_TEXELS,
            pcf_taps:             DEFAULT_PCF_TAPS,
        }
    }
}

impl ShadowQuality {
    /// 壊れた／極端な入力を「必ず描画できる形」へ正規化する純関数。
    ///
    /// - 非有限値（NaN / ∞）は既定値へ戻す（壊れた JSON でランタイムを壊さない）
    /// - 各値を使用可能な範囲へクランプする
    /// - resolution は `SHADOW_RESOLUTION_CHOICES` のうち**もっとも近い**値へ丸める
    ///   （任意の数値を渡されてもテクスチャ確保が破綻しないため）
    pub fn sanitize(self) -> Self {
        let d = Self::default();
        // 非有限値は既定へ、そのうえで範囲クランプするヘルパー。
        let fin = |v: f32, fallback: f32, lo: f32, hi: f32| {
            if v.is_finite() { v.clamp(lo, hi) } else { fallback }
        };
        Self {
            resolution:           nearest_resolution(self.resolution),
            distance:             fin(self.distance, d.distance, SHADOW_DISTANCE_MIN, SHADOW_DISTANCE_MAX),
            split_lambda:         fin(self.split_lambda, d.split_lambda, 0.0, 1.0),
            normal_offset_texels: fin(self.normal_offset_texels, d.normal_offset_texels, 0.0, TEXEL_SCALE_MAX),
            depth_bias_texels:    fin(self.depth_bias_texels, d.depth_bias_texels, 0.0, TEXEL_SCALE_MAX),
            slope_bias:           fin(self.slope_bias, d.slope_bias, 0.0, SLOPE_BIAS_MAX),
            pcf_radius_texels:    fin(self.pcf_radius_texels, d.pcf_radius_texels, 0.0, TEXEL_SCALE_MAX),
            pcf_taps:             self.pcf_taps.clamp(1, MAX_PCF_TAPS),
        }
    }

    /// 1 テクセルの UV サイズ（1 / resolution）。シェーダへ渡す PCF オフセットの基準。
    pub fn texel_uv(&self) -> f32 {
        1.0 / self.resolution.max(1) as f32
    }
}

/// 与えられた解像度を `SHADOW_RESOLUTION_CHOICES` のいずれかへ丸める。
/// 選択肢の最小未満は最小へ、最大超えは最大へ、途中は**一番近い**選択肢へ。
fn nearest_resolution(v: u32) -> u32 {
    let mut best = SHADOW_RESOLUTION_CHOICES[0];
    let mut best_d = u32::MAX;
    for &c in SHADOW_RESOLUTION_CHOICES.iter() {
        let d = c.abs_diff(v);
        if d < best_d {
            best_d = d;
            best = c;
        }
    }
    best
}

// ============================================================
//  project_settings.json のパース
// ============================================================

/// project_settings.json の `shadow` ブロックのキー名（エディタ側と一致させること）。
const KEY_SHADOW: &str = "shadow";
const KEY_RESOLUTION: &str = "resolution";
const KEY_DISTANCE: &str = "distance";
const KEY_SPLIT_LAMBDA: &str = "split_lambda";
const KEY_NORMAL_OFFSET: &str = "normal_offset";
const KEY_DEPTH_BIAS: &str = "depth_bias";
const KEY_SLOPE_BIAS: &str = "slope_bias";
const KEY_PCF_RADIUS: &str = "pcf_radius_texels";
const KEY_PCF_TAPS: &str = "pcf_taps";

/// project_settings.json の文字列から `shadow` ブロックを読み取る純関数。
///
/// - JSON が壊れている／`shadow` ブロックが無い → 既定値
/// - ブロックはあるがキーが欠けている → そのキーだけ既定値（部分指定を許す）
/// - 値が範囲外・非有限 → `sanitize` が補正する
pub fn parse_shadow_quality(json: &str) -> ShadowQuality {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(json) else {
        return ShadowQuality::default();
    };
    let Some(s) = v.get(KEY_SHADOW) else {
        return ShadowQuality::default();
    };
    let d = ShadowQuality::default();
    // 数値キーを読むヘルパー（欠落・型不一致は既定値）。
    let f = |key: &str, fallback: f32| s.get(key).and_then(|x| x.as_f64()).map(|x| x as f32).unwrap_or(fallback);
    let u = |key: &str, fallback: u32| s.get(key).and_then(|x| x.as_u64()).map(|x| x as u32).unwrap_or(fallback);
    ShadowQuality {
        resolution:           u(KEY_RESOLUTION, d.resolution),
        distance:             f(KEY_DISTANCE, d.distance),
        split_lambda:         f(KEY_SPLIT_LAMBDA, d.split_lambda),
        normal_offset_texels: f(KEY_NORMAL_OFFSET, d.normal_offset_texels),
        depth_bias_texels:    f(KEY_DEPTH_BIAS, d.depth_bias_texels),
        slope_bias:           f(KEY_SLOPE_BIAS, d.slope_bias),
        pcf_radius_texels:    f(KEY_PCF_RADIUS, d.pcf_radius_texels),
        pcf_taps:             u(KEY_PCF_TAPS, d.pcf_taps),
    }
    .sanitize()
}

// ============================================================
//  プロセスグローバルの現在値
// ============================================================

/// プロセス全体のシャドウ品質設定。起動時に 1 回書き込み、以後は読み取りのみ。
/// 毎フレーム 1 回（prepare_frame）読まれるだけなので RwLock で十分軽い。
static SHADOW_QUALITY: RwLock<ShadowQuality> = RwLock::new(ShadowQuality {
    resolution:           DEFAULT_SHADOW_RESOLUTION,
    distance:             DEFAULT_SHADOW_DISTANCE,
    split_lambda:         DEFAULT_SPLIT_LAMBDA,
    normal_offset_texels: DEFAULT_NORMAL_OFFSET_TEXELS,
    depth_bias_texels:    DEFAULT_DEPTH_BIAS_TEXELS,
    slope_bias:           DEFAULT_SLOPE_BIAS,
    pcf_radius_texels:    DEFAULT_PCF_RADIUS_TEXELS,
    pcf_taps:             DEFAULT_PCF_TAPS,
});

/// プロセス全体のシャドウ品質設定を差し替える（起動時・テスト用）。
/// 入力は必ず `sanitize` を通してから格納する。戻り値は実際に格納された値。
pub fn set_shadow_quality(q: ShadowQuality) -> ShadowQuality {
    let sane = q.sanitize();
    if let Ok(mut w) = SHADOW_QUALITY.write() {
        *w = sane;
    }
    sane
}

/// 現在のシャドウ品質設定を読む。
///
/// ロックが毒されている（他スレッドが保持中に panic した）場合は既定値を返す。
/// 影の品質設定のために描画を止める理由は無いため、ここでは panic させない。
pub fn shadow_quality() -> ShadowQuality {
    SHADOW_QUALITY
        .read()
        .map(|g| *g)
        .unwrap_or_else(|_| ShadowQuality::default())
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// shadow ブロックが無い JSON は「改善後の既定値」になること。
    #[test]
    fn missing_block_uses_improved_defaults() {
        let q = parse_shadow_quality(r#"{"game_name":"x"}"#);
        assert_eq!(q, ShadowQuality::default());
        assert_eq!(q.resolution, 2048, "解像度の既定は据え置き 2048");
        assert_eq!(q.split_lambda, DEFAULT_SPLIT_LAMBDA);
        assert!(q.split_lambda > 0.5, "既定は従来 0.5 より対数寄り（近景の解像度を優先）");
        assert_eq!(q.pcf_taps, DEFAULT_PCF_TAPS);
    }

    /// 壊れた JSON でも既定値へフォールバックすること。
    #[test]
    fn broken_json_falls_back_to_defaults() {
        assert_eq!(parse_shadow_quality("{not json"), ShadowQuality::default());
        assert_eq!(parse_shadow_quality(""), ShadowQuality::default());
    }

    /// 全キー指定がそのまま読めること。
    #[test]
    fn full_block_is_parsed() {
        let json = r#"{
            "shadow": {
                "resolution": 4096, "distance": 80.0, "split_lambda": 0.75,
                "normal_offset": 2.0, "depth_bias": 0.5, "slope_bias": 3.0,
                "pcf_radius_texels": 2.5, "pcf_taps": 16
            }
        }"#;
        let q = parse_shadow_quality(json);
        assert_eq!(q.resolution, 4096);
        assert_eq!(q.distance, 80.0);
        assert_eq!(q.split_lambda, 0.75);
        assert_eq!(q.normal_offset_texels, 2.0);
        assert_eq!(q.depth_bias_texels, 0.5);
        assert_eq!(q.slope_bias, 3.0);
        assert_eq!(q.pcf_radius_texels, 2.5);
        assert_eq!(q.pcf_taps, 16);
    }

    /// 部分指定は指定キーだけ上書きされ、残りは既定値になること。
    #[test]
    fn partial_block_keeps_defaults_for_missing_keys() {
        let q = parse_shadow_quality(r#"{"shadow":{"split_lambda":0.9}}"#);
        let d = ShadowQuality::default();
        assert_eq!(q.split_lambda, 0.9);
        assert_eq!(q.resolution, d.resolution);
        assert_eq!(q.pcf_taps, d.pcf_taps);
        assert_eq!(q.distance, d.distance);
    }

    /// 範囲外・非有限値が補正されること。
    #[test]
    fn out_of_range_values_are_clamped() {
        let json = r#"{"shadow":{"resolution":777,"distance":-5.0,"split_lambda":9.0,
                       "normal_offset":1000.0,"pcf_taps":999}}"#;
        let q = parse_shadow_quality(json);
        assert!(SHADOW_RESOLUTION_CHOICES.contains(&q.resolution), "解像度は選択肢へ丸める");
        assert_eq!(q.resolution, 1024, "777 は 1024 に最も近い");
        assert_eq!(q.distance, SHADOW_DISTANCE_MIN);
        assert_eq!(q.split_lambda, 1.0);
        assert_eq!(q.normal_offset_texels, TEXEL_SCALE_MAX);
        assert_eq!(q.pcf_taps, MAX_PCF_TAPS);
    }

    /// 非有限値（NaN / ±∞）は既定値へ戻ること（クランプでは潰せないため別扱い）。
    #[test]
    fn non_finite_values_fall_back_to_defaults() {
        let broken = ShadowQuality {
            distance:     f32::NAN,
            split_lambda: f32::INFINITY,
            pcf_radius_texels: f32::NEG_INFINITY,
            ..ShadowQuality::default()
        };
        let q = broken.sanitize();
        assert_eq!(q.distance, DEFAULT_SHADOW_DISTANCE);
        assert_eq!(q.split_lambda, DEFAULT_SPLIT_LAMBDA, "∞ も非有限なので既定へ");
        assert_eq!(q.pcf_radius_texels, DEFAULT_PCF_RADIUS_TEXELS);
        // 対照: 有限の範囲外はクランプされる（既定へは戻らない）。
        let huge = ShadowQuality { split_lambda: 9.0, ..ShadowQuality::default() }.sanitize();
        assert_eq!(huge.split_lambda, 1.0);
    }

    /// 解像度の丸め（選択肢の中で最も近い値）。
    #[test]
    fn resolution_snaps_to_choices() {
        assert_eq!(nearest_resolution(0), 1024);
        assert_eq!(nearest_resolution(1024), 1024);
        assert_eq!(nearest_resolution(1500), 1024, "1500 は 1024 との差 476 < 2048 との差 548");
        assert_eq!(nearest_resolution(2048), 2048);
        assert_eq!(nearest_resolution(4096), 4096);
        assert_eq!(nearest_resolution(99999), 4096);
    }

    /// テクセル UV サイズが解像度の逆数であること。
    #[test]
    fn texel_uv_is_inverse_resolution() {
        let q = ShadowQuality { resolution: 2048, ..ShadowQuality::default() };
        assert!((q.texel_uv() - 1.0 / 2048.0).abs() < 1e-9);
    }

    /// グローバル値の設定・取得（sanitize を通ること）。
    #[test]
    fn global_set_get_roundtrip() {
        let stored = set_shadow_quality(ShadowQuality { pcf_taps: 999, ..ShadowQuality::default() });
        assert_eq!(stored.pcf_taps, MAX_PCF_TAPS);
        assert_eq!(shadow_quality().pcf_taps, MAX_PCF_TAPS);
        // 他テストへ影響しないよう既定へ戻す。
        set_shadow_quality(ShadowQuality::default());
        assert_eq!(shadow_quality(), ShadowQuality::default());
    }
}
