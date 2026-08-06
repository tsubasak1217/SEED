// ============================================================
//  terrain/cover/material.rs — カバー素材定義（データドリブン）
//
//  【責務】
//    地表へ積もる「カバー素材」（雪・落ち葉の絨毯・濡れ など）の定義を保持し、
//    JSON（assets/terrain/cover_materials.json）から復元する。
//    ファイル IO・GPU・ECS への依存は一切持たない（JSON 文字列 in / 構造体 out）。
//    props.rs（散布プロップ定義）とまったく同じ役割分担・同じ流儀である。
//
//  【なぜ素材をアセットにするのか】
//    「雪が降る」「落ち葉が積もる」「雨で濡れる」は、地表の見た目としては
//    *まったく同じ処理*（アルベドと粗さを量に応じて上書きし、量に応じて盛り上げる）
//    であり、違うのは数値だけである。よってエンジンには 1 本の仕組みしか置かず、
//    表現の差は cover_materials.json の差し替えだけで作れるようにする。
//
//  【1 層仕様】
//    カバー場は 1 テクセルにつき「素材 ID 1 つ ＋ 量 1 つ」しか持たない
//    （field.rs 参照）。よって本ファイルの素材は互いに重ならず、後から積もる
//    素材が古い素材を削りながら置き換える。多層化は将来フェーズの判断とする。
// ============================================================

use serde::{Deserialize, Serialize};

// ─── 上限（マジックナンバー禁止のため定数化）─────────────────────────────────

/// cover_materials.json に定義できる素材数の上限。
///
/// カバー場は素材を `u8` の添字で指すため理論上は 256 まで持てるが、
/// GPU の uniform（`TerrainLayerUniform.cover`）へ全素材を載せる都合で
/// 実際の上限はここで切る。超えたぶんは読み込み時に切り詰める
/// （エラーにはしない＝データ差し替えでの試行錯誤を妨げない）。
///
/// WGSL 側 `TERRAIN_MAX_COVER_MATERIALS` と一致させること
/// （terrain_gbuffer.rs のテスト `terrain_cover_count_matches_shader` が固定する）。
pub const TERRAIN_MAX_COVER_MATERIALS: usize = 16;

/// カバー場が素材を指せない状態（＝量 0）を表す添字。
///
/// 量が 0 のテクセルでは素材添字は無意味だが、未定義値を残さないために
/// 常にこの値で埋める（＝シリアライズ結果が決定的になる）。
pub const COVER_MATERIAL_NONE: u8 = 0;

// ─── 既定値関数（serde default 用。マジックナンバー禁止）─────────────────────

/// アルベド（リニア RGB）の既定値。中間グレー（未設定の素材が真っ黒にならない）。
fn default_albedo() -> [f32; 3] {
    [0.5, 0.5, 0.5]
}

/// 粗さの既定値。土・雪など大半のカバーは拡散的なので粗い側に置く。
fn default_roughness() -> f32 {
    0.85
}

/// 変位（量 1.0 のときに地表を持ち上げる高さ・メートル）の既定値。
///
/// 0 = 盛り上がらない（濡れ・染みの類）。既定を 0 にしてあるのは、
/// 「高さを書き忘れた素材」が地形を予期せず膨らませるより、
/// 色だけ変わるほうが事故として軽いため。
fn default_displacement() -> f32 {
    0.0
}

/// 埋め戻し速度（量/秒）の既定値。0 = 積もった分でしか轍は埋まらない。
fn default_refill_rate() -> f32 {
    0.0
}

/// 足跡の残りやすさ（0..1）の既定値。0 = 足跡が残らない。
fn default_footprint_persistence() -> f32 {
    0.0
}

/// 踏み固め色係数（0..1）の既定値。0 = 踏んでも色が変わらない。
fn default_trample_darkening() -> f32 {
    0.0
}

/// 縁の盛り上がり比（0..1）の既定値。0 = 踏んでも縁が盛り上がらない。
///
/// 既定を 0 にするのは `displacement` と同じ理由である。書き忘れた素材が
/// 勝手に量を増やして地形を膨らませるより、何も起きないほうが事故として軽い。
fn default_rim_ratio() -> f32 {
    0.0
}

/// 踏み固めキャビティ係数（0..1）の既定値。0 = 溝の中で環境光が遮られない。
fn default_trample_cavity() -> f32 {
    0.0
}

// ─── クランプ範囲（読み込み時の正規化に使う）─────────────────────────────────

/// 粗さの下限（完全鏡面によるスペキュラ発散を防ぐ。シェーダ側の下限と対）。
const ROUGHNESS_MIN: f32 = 0.02;
/// 粗さの上限。
const ROUGHNESS_MAX: f32 = 1.0;
/// 変位高さの下限（負の変位＝地形へめり込ませる用途は本フェーズでは扱わない）。
const DISPLACEMENT_MIN: f32 = 0.0;
/// 変位高さの上限（メートル）。
///
/// これ以上は「積もる」ではなく地形そのものの造形であり、頂点を法線方向へ
/// ずらすだけの本方式では三角形が破綻する（隣接頂点との段差が可視化する）。
const DISPLACEMENT_MAX: f32 = 2.0;
/// 正規化パラメータ（0..1）の下限・上限。
const UNIT_MIN: f32 = 0.0;
const UNIT_MAX: f32 = 1.0;

// ============================================================
//  CoverMaterial — カバー素材 1 種
// ============================================================

/// 地表へ積もるカバー素材 1 種の定義。
///
/// 「量（0..1）」を掛けて地表のアルベド・粗さへブレンドし、
/// `displacement` × 量 のぶんだけ地表を法線方向へ持ち上げる。
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct CoverMaterial {
    /// 素材 ID（`CoverEmitterComponent.material_id` から参照される一意なキー）。
    ///
    /// 添字ではなく **文字列 ID** で参照させるのは、cover_materials.json の
    /// 並び替えで既存シーンの指し先がずれないようにするため
    /// （props.json が添字参照で持っている弱点を、こちらでは最初から避けている）。
    #[serde(default)]
    pub id: String,
    /// 表示名（エディタ UI 用。空なら `id` を表示する）。
    #[serde(default)]
    pub name: String,
    /// アルベド（リニア RGB）。量 1.0 のとき地表の色はこの値へ完全に置き換わる。
    #[serde(default = "default_albedo")]
    pub albedo: [f32; 3],
    /// 粗さ（0..1）。量 1.0 のとき地表の粗さはこの値へ完全に置き換わる。
    #[serde(default = "default_roughness")]
    pub roughness: f32,
    /// 量 1.0 のときに地表を法線方向へ持ち上げる高さ（メートル）。
    #[serde(default = "default_displacement")]
    pub displacement: f32,
    /// 足跡・轍が埋め戻る速度（量/秒。I3.2）。
    ///
    /// 有効なエミッタがこのテクセルへ降っている間だけ効く「均され方」であり、
    /// 自然減衰ではない（エミッタが止まれば轍は永久に残る）。
    /// 雪のようにさらさらと崩れる素材ほど大きくする。
    #[serde(default = "default_refill_rate")]
    pub refill_rate: f32,
    /// 足跡の残りやすさ（0..1。I3.2）。
    ///
    /// 踏み込みの深さに掛かる係数。0 なら何度踏んでも痕が付かない
    /// （濡れ・水膜のように形を保てない素材）。
    #[serde(default = "default_footprint_persistence")]
    pub footprint_persistence: f32,
    /// 踏み固め色係数（0..1。I3.2）。
    ///
    /// 踏み固められた部分をどれだけ暗くするか。踏み固めが最大（1.0）のとき、
    /// 地表の色が `1 - trample_darkening` 倍になる。
    /// 雪は圧雪でわずかに灰色を帯び、泥は水を含んで明確に黒くなる。
    #[serde(default = "default_trample_darkening")]
    pub trample_darkening: f32,
    /// 縁の盛り上がり比（0..1。I3.2 の見栄え強化）。
    ///
    /// 踏み込んだぶんの素材が足跡の**外周へ押しのけられて盛れる**量を、
    /// 踏み込み深さに対する比で表す。0.5 なら「深さ 0.4 ぶん踏んだ縁に
    /// 量 0.2 が足される」。この盛り上がりは踏み固めチャネルではなく
    /// **量チャネルへの加算**として入るため、変位・色・法線のすべてが
    /// 既存の 1 本の経路をそのまま通る（新しい描画分岐が要らない）。
    ///
    /// 【なぜ「見えやすさ」に効くのか】
    ///   凹みだけの痕は、上から光が当たると溝の底も周りも同じ向きを向いていて
    ///   陰影が出ない。縁が盛れると痕の周囲に必ず「傾いた面」が生まれ、
    ///   どの角度から照らしても輪郭にハイライトと影の対が出る。
    #[serde(default = "default_rim_ratio")]
    pub rim_ratio: f32,
    /// 踏み固めキャビティ係数（0..1。I3.2 の見栄え強化）。
    ///
    /// 溝の中がどれだけ環境光を遮られるか（アンビエントオクルージョン）。
    /// `trample_darkening` がアルベドそのものを暗くする（＝直射日光の下でも暗い）
    /// のに対し、こちらは **G-Buffer の occlusion** へ書かれ、
    /// アンビエント・DDGI・疑似バウンスにだけ効く。
    ///
    /// 【なぜ 2 つに分けるのか】
    ///   圧雪が灰色を帯びるのは素材そのものの変化（アルベド）だが、
    ///   溝の底が暗く見える主因は「空が見えなくなること」であり、別物である。
    ///   分けておくと、日向では輪郭がくっきり・日陰では溝だけが沈む、という
    ///   参考画像どおりの見え方が素材の数値だけで作れる。
    #[serde(default = "default_trample_cavity")]
    pub trample_cavity: f32,
}

impl Default for CoverMaterial {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            albedo: default_albedo(),
            roughness: default_roughness(),
            displacement: default_displacement(),
            refill_rate: default_refill_rate(),
            footprint_persistence: default_footprint_persistence(),
            trample_darkening: default_trample_darkening(),
            rim_ratio: default_rim_ratio(),
            trample_cavity: default_trample_cavity(),
        }
    }
}

impl CoverMaterial {
    /// 読み込み直後の値を安全な範囲へ丸める。
    ///
    /// 手書き JSON の異常値（負の粗さ・NaN・巨大な変位）がそのまま
    /// GPU uniform や頂点位置へ流れると描画が破綻するため、
    /// **読み込みの 1 か所**でだけ正規化する。
    fn sanitize(&mut self) {
        // NaN / 無限大は clamp を素通りするので、先に有限性で既定値へ落とす。
        for c in self.albedo.iter_mut() {
            if !c.is_finite() {
                *c = default_albedo()[0];
            }
            *c = c.clamp(UNIT_MIN, UNIT_MAX);
        }
        self.roughness = if self.roughness.is_finite() {
            self.roughness.clamp(ROUGHNESS_MIN, ROUGHNESS_MAX)
        } else {
            default_roughness()
        };
        self.displacement = if self.displacement.is_finite() {
            self.displacement.clamp(DISPLACEMENT_MIN, DISPLACEMENT_MAX)
        } else {
            default_displacement()
        };
        self.refill_rate = if self.refill_rate.is_finite() {
            self.refill_rate.max(0.0)
        } else {
            default_refill_rate()
        };
        self.footprint_persistence = if self.footprint_persistence.is_finite() {
            self.footprint_persistence.clamp(UNIT_MIN, UNIT_MAX)
        } else {
            default_footprint_persistence()
        };
        self.trample_darkening = if self.trample_darkening.is_finite() {
            self.trample_darkening.clamp(UNIT_MIN, UNIT_MAX)
        } else {
            default_trample_darkening()
        };
        self.rim_ratio = if self.rim_ratio.is_finite() {
            self.rim_ratio.clamp(UNIT_MIN, UNIT_MAX)
        } else {
            default_rim_ratio()
        };
        self.trample_cavity = if self.trample_cavity.is_finite() {
            self.trample_cavity.clamp(UNIT_MIN, UNIT_MAX)
        } else {
            default_trample_cavity()
        };
    }
}

// ============================================================
//  CoverMaterialSet — cover_materials.json 全体
// ============================================================

/// カバー素材定義の一式（cover_materials.json の実体）。
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct CoverMaterialSet {
    /// 素材定義の配列（最大 `TERRAIN_MAX_COVER_MATERIALS` 件）。
    #[serde(default)]
    pub materials: Vec<CoverMaterial>,
}

impl Default for CoverMaterialSet {
    /// cover_materials.json が読めないときの組み込み既定セット。
    ///
    /// アセットが無い環境（テスト・最小構成）でも「雪を降らせる」が
    /// そのまま動くように、サンプルアセットと同じ 3 種を内蔵する。
    fn default() -> Self {
        Self {
            materials: vec![
                CoverMaterial {
                    id: "snow".to_string(),
                    name: "雪".to_string(),
                    // 純白ではなくわずかに青い白（雪は空の色を拾う）。
                    albedo: [0.86, 0.90, 0.95],
                    // 新雪は粉で拡散的。
                    roughness: 0.75,
                    // 量 1.0 で 22cm 積もる。頂点間隔 0.5m に対して 22cm の段差は
                    // 約 24 度の傾きになり、足跡の縁がはっきり陰影として立つ
                    // （15cm ではメッシュの傾きが浅く「跡は付くが地味」になっていた）。
                    displacement: 0.22,
                    // 新雪はさらさらと崩れて轍がゆっくり均される。
                    refill_rate: 0.02,
                    // 雪は足跡が最もよく残る素材。
                    footprint_persistence: 0.9,
                    // 圧雪は空気が抜けて明確に灰色を帯びる。
                    trample_darkening: 0.32,
                    // 新雪は軽く、踏んだぶんの半分ほどが縁へ押しのけられる。
                    rim_ratio: 0.5,
                    // 深い溝の底からは空がほとんど見えない。
                    trample_cavity: 0.7,
                },
                CoverMaterial {
                    id: "leaf_carpet".to_string(),
                    name: "落ち葉の絨毯".to_string(),
                    // 枯葉の茶色。
                    albedo: [0.24, 0.13, 0.05],
                    roughness: 0.90,
                    // 葉は薄いので盛り上がりは小さい。
                    displacement: 0.06,
                    // 落ち葉は風で戻らない（降り積もる分だけで埋まる）。
                    refill_rate: 0.0,
                    // 踏むと潰れるが、雪ほど形は残らない。
                    footprint_persistence: 0.5,
                    // 潰れた葉は湿って暗くなる。
                    trample_darkening: 0.3,
                    // 葉は絡み合っていて雪ほど横へ逃げない。
                    rim_ratio: 0.3,
                    trample_cavity: 0.5,
                },
                CoverMaterial {
                    id: "wet".to_string(),
                    name: "濡れ".to_string(),
                    // 濡れた地面は暗くなる（水膜が拡散反射を減らす）。
                    albedo: [0.06, 0.06, 0.07],
                    // 粗さが落ちて鏡面反射が立つ＝「濡れて見える」の本体。
                    roughness: 0.18,
                    // 水膜は厚みを持たない。
                    displacement: 0.0,
                    refill_rate: 0.0,
                    // 水膜は形を保てない＝足跡が一切残らない。
                    footprint_persistence: 0.0,
                    trample_darkening: 0.0,
                    // 形を持たない素材なので縁も溝も作らない。
                    rim_ratio: 0.0,
                    trample_cavity: 0.0,
                },
            ],
        }
    }
}

impl CoverMaterialSet {
    /// JSON 文字列から素材セットを復元する。
    ///
    /// 上限を超えた定義は切り詰め、各素材の値は安全な範囲へ丸める。
    /// パースに失敗した場合のみ `Err`（呼び出し側は既定セットへ落とす）。
    pub fn from_json_str(text: &str) -> Result<Self, serde_json::Error> {
        let mut set: Self = serde_json::from_str(text)?;
        set.materials.truncate(TERRAIN_MAX_COVER_MATERIALS);
        for m in set.materials.iter_mut() {
            m.sanitize();
        }
        Ok(set)
    }

    /// 素材 ID から添字を引く。未定義 ID は `None`。
    ///
    /// 添字はカバー場（`CoverField`）と GPU uniform の両方で素材を指す値であり、
    /// この 1 関数だけが「ID → 添字」の解決を担う。
    pub fn index_of(&self, id: &str) -> Option<usize> {
        self.materials.iter().position(|m| m.id == id)
    }

    /// 添字から素材定義を引く。範囲外は `None`。
    pub fn get(&self, index: usize) -> Option<&CoverMaterial> {
        self.materials.get(index)
    }

    /// 定義されている素材数。
    pub fn len(&self) -> usize {
        self.materials.len()
    }

    /// 素材が 1 つも定義されていないか。
    pub fn is_empty(&self) -> bool {
        self.materials.is_empty()
    }
}

// ─── ユニットテスト ──────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 空 JSON（`{}`）でも読め、素材 0 件になること（＝カバー機能が単に無効になる）。
    #[test]
    fn empty_json_yields_no_materials() {
        let set = CoverMaterialSet::from_json_str("{}").expect("空 JSON は読めること");
        assert!(set.is_empty());
    }

    /// ID で添字が引けること（並び順に依存する参照を作らないための要）。
    #[test]
    fn index_of_resolves_id() {
        let set = CoverMaterialSet::default();
        assert_eq!(set.index_of("snow"), Some(0));
        assert_eq!(set.index_of("wet"), Some(2));
        assert_eq!(set.index_of("nonexistent"), None);
    }

    /// 異常値（負の粗さ・NaN・巨大変位）が読み込みの 1 か所で丸められること。
    #[test]
    fn sanitize_clamps_out_of_range_values() {
        let json = r#"{"materials":[
            {"id":"bad","roughness":-5.0,"displacement":1000.0,"albedo":[2.0,-1.0,0.5]}
        ]}"#;
        let set = CoverMaterialSet::from_json_str(json).expect("読めること");
        let m = set.get(0).expect("1 件目があること");
        assert_eq!(m.roughness, ROUGHNESS_MIN);
        assert_eq!(m.displacement, DISPLACEMENT_MAX);
        assert_eq!(m.albedo, [1.0, 0.0, 0.5]);
    }

    /// NaN は clamp を素通りするため、有限性検査で既定値へ落ちること。
    #[test]
    fn sanitize_replaces_non_finite_with_defaults() {
        let json = r#"{"materials":[{"id":"nan","roughness":null}]}"#;
        // null は serde でエラーになるので、NaN は直接構造体を作って検証する。
        assert!(CoverMaterialSet::from_json_str(json).is_err());

        let mut m = CoverMaterial {
            id: "nan".to_string(),
            roughness: f32::NAN,
            displacement: f32::INFINITY,
            ..CoverMaterial::default()
        };
        m.sanitize();
        assert_eq!(m.roughness, default_roughness());
        assert_eq!(m.displacement, default_displacement());
    }

    /// 上限を超えた定義は切り詰められること（エラーにはしない）。
    #[test]
    fn truncates_beyond_max() {
        let mut json = String::from(r#"{"materials":["#);
        for i in 0..(TERRAIN_MAX_COVER_MATERIALS + 5) {
            if i > 0 {
                json.push(',');
            }
            json.push_str(&format!(r#"{{"id":"m{i}"}}"#));
        }
        json.push_str("]}");
        let set = CoverMaterialSet::from_json_str(&json).expect("読めること");
        assert_eq!(set.len(), TERRAIN_MAX_COVER_MATERIALS);
    }

    /// 既定セットが「雪・落ち葉・濡れ」の 3 種であること（サンプルアセットとの契約）。
    #[test]
    fn default_set_has_three_samples() {
        let set = CoverMaterialSet::default();
        assert_eq!(set.len(), 3);
        // 濡れは変位ゼロ・粗さ低下（雨用）という仕様上の約束。
        let wet = set.get(2).expect("3 件目があること");
        assert_eq!(wet.displacement, 0.0);
        assert!(wet.roughness < 0.5);
    }
}
