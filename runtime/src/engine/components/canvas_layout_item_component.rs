// ============================================================
//  canvas_layout_item_component.rs — レイアウトの子の側の指定（W2-1b）
//
//  コンテナ（CanvasStack・CanvasWrap・CanvasGrid）の子に付けて、その子の並べられ方を指定する。
//  付けなくても子は「自分の大きさ・コンテナの揃え」で並ぶ（付けたときだけ上書き）。
//    - ignore_layout … コンテナに無視させる（自分のアンカー・位置のまま。重ねる飾りなど）
//    - flex          … 主軸の余りを分ける重み（CanvasStack のみ。0 = 自分の大きさ）
//    - preferred_*   … 大きさの指定（0 = 中身の大きさ。CanvasComponent・スプライト・テキストの枠から決まる）
//    - min_* / max_* … 大きさの下限・上限（max は 0 = 上限なし）
//    - align_self    … 交差軸の揃えの上書き（Grid はセルの中の置き方の上書き）
//    - fill_*        … 親に合わせる（コンテナの外の子で使う。親の CanvasComponent の領域いっぱいに広げる）
//  大きさの単位はキャンバスの単位（スプライトの幅・高さと同じ。親の累積スケールが掛かる）。
//
//  【実行中だけの見た目の上書き（W2-7。保存しない・インスペクタに出ない）】
//    - translate          … 置かれた後に足す平行移動（キャンバスの単位。レイアウト〈大きさ・並び・安全領域〉は変えない＝CSS の transform）
//    - translate_fraction … 同じく、自分の大きさ（置かれた矩形）に対する割合（画面の出入り: 右から = (1, 0) → (0, 0)）
//    - layer_bias         … 自分と子孫の表示（スプライト・テキスト・パーティクル・図形）のレイヤーに足す値（重なる画面・覆い・ダイアログの前後）
//  画面の組み立て（SEED.UI の ScreenStack・ダイアログ・シート・トースト）がスクリプトから毎フレーム書く。
//  #[serde(skip)] なので .scene / .actor には出ない（読み込むと 0）。
//
//  【データとロジックの分離】このファイルはデータだけ。読むのはレイアウトの走査（canvas_layout/measure.rs・pass.rs）。
//  規則の正典は docs/canvas_camera_rework.md §6.3（見た目の上書きは §6.7）。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

use super::canvas_layout_params::ItemAlign;

/// レイアウトの子の側の指定（World に置く実体。シリアライズもこの型のまま行う）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, Default)]
pub struct CanvasLayoutItemComponent {
    /// コンテナに無視させる（並べる対象から外し、自分のアンカー・位置で置く）。
    #[serde(default)]
    pub ignore_layout: bool,
    /// 主軸の余りを分ける重み（CanvasStack のみ。0 = 伸ばさない）。
    #[serde(default)]
    pub flex: f32,
    /// 幅の指定（0 = 中身の大きさ）。
    #[serde(default)]
    pub preferred_width: f32,
    /// 高さの指定（0 = 中身の大きさ）。
    #[serde(default)]
    pub preferred_height: f32,
    /// 幅の下限（0 = 下限なし）。
    #[serde(default)]
    pub min_width: f32,
    /// 高さの下限（0 = 下限なし）。
    #[serde(default)]
    pub min_height: f32,
    /// 幅の上限（0 = 上限なし）。
    #[serde(default)]
    pub max_width: f32,
    /// 高さの上限（0 = 上限なし）。
    #[serde(default)]
    pub max_height: f32,
    /// 交差軸の揃えの上書き（既定 Auto = コンテナに従う）。
    #[serde(default)]
    pub align_self: ItemAlign,
    /// 親の幅に合わせる（コンテナの外で使う。親の CanvasComponent の領域の幅いっぱい）。
    #[serde(default)]
    pub fill_width: bool,
    /// 親の高さに合わせる（同上）。
    #[serde(default)]
    pub fill_height: bool,
    /// 見た目の平行移動（キャンバスの単位。置かれた後に足す。実行中だけ・保存しない。W2-7）。
    #[serde(skip)]
    pub translate: [f32; 2],
    /// 見た目の平行移動（自分の置かれた矩形の大きさに対する割合。実行中だけ・保存しない。W2-7）。
    #[serde(skip)]
    pub translate_fraction: [f32; 2],
    /// 自分と子孫の表示のレイヤーに足す値（実行中だけ・保存しない。W2-7）。
    #[serde(skip)]
    pub layer_bias: i32,
}

/// シリアライズ用データ（実体と同じ型）。
pub type CanvasLayoutItemComponentData = CanvasLayoutItemComponent;

impl CanvasLayoutItemComponent {
    /// シリアライズ用データから実体を作る。
    pub fn from_data(data: CanvasLayoutItemComponentData) -> Self {
        data
    }

    /// 実体からシリアライズ用データを作る。
    pub fn to_data(&self) -> CanvasLayoutItemComponentData {
        self.clone()
    }

    /// 大きさの指定（軸ごと。0 以下は指定なし＝None）。
    pub fn preferred(&self) -> [Option<f32>; 2] {
        [positive(self.preferred_width), positive(self.preferred_height)]
    }

    /// 大きさの下限（軸ごと。0 以下は 0）。
    pub fn min(&self) -> [f32; 2] {
        [self.min_width.max(0.0), self.min_height.max(0.0)]
    }

    /// 大きさの上限（軸ごと。0 以下は上限なし＝無限大）。
    pub fn max(&self) -> [f32; 2] {
        [
            positive(self.max_width).unwrap_or(f32::INFINITY),
            positive(self.max_height).unwrap_or(f32::INFINITY),
        ]
    }

    /// 親に合わせる軸（幅・高さ）。
    pub fn fill(&self) -> [bool; 2] {
        [self.fill_width, self.fill_height]
    }

    /// 見た目の平行移動を持つか（どちらかの成分が 0 でない有限値。NaN・無限大は持たないとみなす）。
    pub fn has_translation(&self) -> bool {
        self.translate
            .iter()
            .chain(self.translate_fraction.iter())
            .any(|v| v.is_finite() && *v != 0.0)
    }

    /// 見た目の平行移動（親のローカルの画素）【純関数】。
    ///
    /// 平行移動 = translate × 親までの累積スケール ＋ translate_fraction × 自分の矩形の大きさ（画素）。
    /// 有限でない成分は 0 として扱う（壊れた値で行列を壊さない）。
    ///
    /// # 引数
    /// * `parent_cumul_scale` - 親までの累積スケール（キャンバスの単位 → 画素。dp のルートの下なら 1 dp の画素数）
    /// * `own_size_px`        - 自分の置かれた矩形の大きさ（親のローカルの画素。矩形が無ければ 0）
    pub fn translation_px(&self, parent_cumul_scale: [f32; 2], own_size_px: [f32; 2]) -> [f32; 2] {
        let finite = |v: f32| if v.is_finite() { v } else { 0.0 };
        [0, 1].map(|a| {
            finite(self.translate[a]) * parent_cumul_scale[a] + finite(self.translate_fraction[a]) * own_size_px[a]
        })
    }
}

/// 正の有限値だけを Some にする（0・負・NaN・無限大は「指定なし」）。
fn positive(value: f32) -> Option<f32> {
    (value.is_finite() && value > 0.0).then_some(value)
}

impl Component for CanvasLayoutItemComponent {}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（何も上書きしない）で読める。
    #[test]
    fn empty_data_defaults() {
        let d: CanvasLayoutItemComponentData = serde_json::from_str("{}").unwrap();
        assert_eq!(d, CanvasLayoutItemComponent::default());
        assert_eq!(d.preferred(), [None, None]);
        assert_eq!(d.max(), [f32::INFINITY, f32::INFINITY]);
    }

    /// 0・負は「指定なし」。上限 0 は上限なし。
    #[test]
    fn zero_means_unspecified() {
        let d = CanvasLayoutItemComponent {
            preferred_width: 120.0,
            preferred_height: -1.0,
            min_width: -5.0,
            max_height: 40.0,
            ..CanvasLayoutItemComponent::default()
        };
        assert_eq!(d.preferred(), [Some(120.0), None]);
        assert_eq!(d.min(), [0.0, 0.0]);
        assert_eq!(d.max(), [f32::INFINITY, 40.0]);
    }

    /// 実体 ⇔ データの往復で値が保たれる。
    #[test]
    fn round_trip() {
        let c = CanvasLayoutItemComponent {
            flex: 2.0,
            align_self: ItemAlign::Center,
            fill_height: true,
            ..CanvasLayoutItemComponent::default()
        };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        let back: CanvasLayoutItemComponentData = serde_json::from_str(&json).unwrap();
        assert_eq!(CanvasLayoutItemComponent::from_data(back), c);
    }

    /// 実行中だけの見た目の上書き（W2-7）は保存しない（書き出さず、読むと 0）。
    #[test]
    fn runtime_visual_fields_are_not_serialized() {
        let c = CanvasLayoutItemComponent {
            fill_width: true,
            translate: [12.0, -3.0],
            translate_fraction: [1.0, 0.0],
            layer_bias: 3_000_000,
            ..CanvasLayoutItemComponent::default()
        };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        assert!(!json.contains("translate"), "{json}");
        assert!(!json.contains("layer_bias"), "{json}");
        let back = CanvasLayoutItemComponent::from_data(serde_json::from_str(&json).unwrap());
        assert_eq!(back.translate, [0.0, 0.0]);
        assert_eq!(back.translate_fraction, [0.0, 0.0]);
        assert_eq!(back.layer_bias, 0);
        assert!(back.fill_width, "保存する欄はそのまま");
    }

    /// 見た目の平行移動 = 単位 × 累積スケール ＋ 割合 × 矩形。壊れた値は 0 として扱う。
    #[test]
    fn translation_px_combines_units_and_fraction() {
        let mut c = CanvasLayoutItemComponent::default();
        assert!(!c.has_translation());
        assert_eq!(c.translation_px([2.625, 2.625], [400.0, 800.0]), [0.0, 0.0]);
        c.translate = [10.0, 0.0];
        c.translate_fraction = [0.0, -0.5];
        assert!(c.has_translation());
        assert_eq!(c.translation_px([2.0, 3.0], [400.0, 800.0]), [20.0, -400.0]);
        c.translate = [f32::NAN, 0.0];
        c.translate_fraction = [0.0, f32::INFINITY];
        assert!(!c.has_translation(), "有限でない値だけなら持たない");
        assert_eq!(c.translation_px([2.0, 3.0], [400.0, 800.0]), [0.0, 0.0]);
    }
}
