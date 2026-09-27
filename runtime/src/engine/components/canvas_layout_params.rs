// ============================================================
//  canvas_layout_params.rs — キャンバスのレイアウト部品が共有する値の型（W2-1b）
//
//  レイアウトのコンテナ（CanvasStack・CanvasWrap・CanvasGrid）と子の側の指定（CanvasLayoutItem）が
//  使う列挙と余白の型をここに集める（1 ファイル 1 責務: 「レイアウトの語彙」）。
//
//  【データとロジックの分離】ここは値の型だけ。並べ方の計算は engine/core/canvas_layout/containers/。
//
//  【スクリプトとの受け渡し】列挙はスクリプト（C# の SEED.LayoutDirection など）と
//  「添字（0, 1, 2, …）」で受け渡す（host_api の数値の欄は f32 の配列のため）。
//  添字は各列挙の `ALL` の並びそのもの。**並びを変えるとスクリプトの値が変わる**ので、足すときは末尾へ。
//  C# 側の列挙の値（scripting/src/Api/CanvasLayoutTypes.cs）と必ず一致させる。
// ============================================================

use serde::{Deserialize, Serialize};

/// 添字で受け渡す列挙の共通の振る舞い（スクリプトの数値の欄 ⇔ 列挙）。
pub trait IndexedEnum: Copy + PartialEq + 'static {
    /// すべての値（添字の順。スクリプトの列挙の値と一致させる）。
    const ALL: &'static [Self];

    /// 値の添字。
    fn index(self) -> usize {
        Self::ALL.iter().position(|v| *v == self).unwrap_or(0)
    }

    /// 添字から値を引く（範囲外は None）。
    fn from_index(index: usize) -> Option<Self> {
        Self::ALL.get(index).copied()
    }

    /// スクリプトの数値（f32）から値を引く（整数でない・範囲外・NaN は None）。
    fn from_script_value(value: f32) -> Option<Self> {
        if !value.is_finite() || value < 0.0 || value.fract() != 0.0 {
            return None;
        }
        Self::from_index(value as usize)
    }

    /// スクリプトへ渡す数値（添字を f32 にしたもの）。
    fn to_script_value(self) -> f32 {
        self.index() as f32
    }
}

/// 並べる向き（主軸）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum LayoutDirection {
    /// 縦に並べる（上から下。主軸 = Y）。
    #[default]
    Vertical,
    /// 横に並べる（左から右。主軸 = X）。
    Horizontal,
}

impl IndexedEnum for LayoutDirection {
    const ALL: &'static [Self] = &[Self::Vertical, Self::Horizontal];
}

/// 主軸の揃え（子を並べ終えて余った長さの配り方）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum MainAlign {
    /// 先頭へ寄せる（余りは末尾）。
    #[default]
    Start,
    /// 中央へ寄せる（余りを両端へ半分ずつ）。
    Center,
    /// 末尾へ寄せる（余りは先頭）。
    End,
    /// 両端をそろえ、余りを子の間へ等分する（子が 1 つなら先頭）。
    SpaceBetween,
    /// 余りを子の前後へ等分する（端は子の間の半分）。
    SpaceAround,
    /// 余りを端と子の間へすべて等分する。
    SpaceEvenly,
}

impl IndexedEnum for MainAlign {
    const ALL: &'static [Self] = &[
        Self::Start,
        Self::Center,
        Self::End,
        Self::SpaceBetween,
        Self::SpaceAround,
        Self::SpaceEvenly,
    ];
}

/// 交差軸の揃え（コンテナの側の既定）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum CrossAlign {
    /// 先頭（縦に並べるなら左・横に並べるなら上）へ寄せる。
    #[default]
    Start,
    /// 中央へ寄せる。
    Center,
    /// 末尾へ寄せる。
    End,
    /// 交差軸いっぱいに伸ばす（コンテナの交差軸の大きさが決まっているときだけ。決まっていなければ先頭）。
    Stretch,
}

impl IndexedEnum for CrossAlign {
    const ALL: &'static [Self] = &[Self::Start, Self::Center, Self::End, Self::Stretch];
}

/// 子の側の揃えの上書き（CanvasLayoutItem.align_self）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum ItemAlign {
    /// コンテナの揃えに従う。
    #[default]
    Auto,
    /// 先頭へ寄せる。
    Start,
    /// 中央へ寄せる。
    Center,
    /// 末尾へ寄せる。
    End,
    /// いっぱいに伸ばす。
    Stretch,
}

impl IndexedEnum for ItemAlign {
    const ALL: &'static [Self] = &[Self::Auto, Self::Start, Self::Center, Self::End, Self::Stretch];
}

impl ItemAlign {
    /// コンテナの揃えと合わせた、この子の実際の揃え（Auto ならコンテナの揃え）。
    pub fn resolve(self, container: CrossAlign) -> CrossAlign {
        match self {
            Self::Auto => container,
            Self::Start => CrossAlign::Start,
            Self::Center => CrossAlign::Center,
            Self::End => CrossAlign::End,
            Self::Stretch => CrossAlign::Stretch,
        }
    }
}

/// 非表示（visible = false）・無効（active = false）の子の扱い。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum HiddenChildren {
    /// 詰める（並べる対象から外す。場所を取らない）。
    #[default]
    Collapse,
    /// 場所を残す（見えている子と同じように並べる。描画・当たり判定はされない）。
    KeepSpace,
}

impl IndexedEnum for HiddenChildren {
    const ALL: &'static [Self] = &[Self::Collapse, Self::KeepSpace];
}

/// 内側の余白（上下左右。コンテナのキャンバスの単位＝子の位置と同じ単位）。
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize, Default)]
pub struct CanvasPadding {
    /// 左の余白。
    #[serde(default)]
    pub left: f32,
    /// 上の余白。
    #[serde(default)]
    pub top: f32,
    /// 右の余白。
    #[serde(default)]
    pub right: f32,
    /// 下の余白。
    #[serde(default)]
    pub bottom: f32,
}

impl CanvasPadding {
    /// スクリプトとの受け渡しの並び（左・上・右・下）。
    pub fn to_array(self) -> [f32; 4] {
        [self.left, self.top, self.right, self.bottom]
    }

    /// スクリプトから受けた並び（左・上・右・下）から作る。
    pub fn from_array(values: [f32; 4]) -> Self {
        let [left, top, right, bottom] = values;
        Self { left, top, right, bottom }
    }
}

/// serde の既定値: true（有効フラグ・安全領域の辺など）。
pub fn default_true() -> bool {
    true
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 添字 ⇔ 列挙の往復と、スクリプトの数値の検査（整数でない・範囲外・負は受けない）。
    #[test]
    fn indexed_enum_round_trip_and_rejects_bad_values() {
        for (i, v) in MainAlign::ALL.iter().enumerate() {
            assert_eq!(v.index(), i);
            assert_eq!(MainAlign::from_script_value(i as f32), Some(*v));
        }
        assert_eq!(MainAlign::from_script_value(6.0), None);
        assert_eq!(MainAlign::from_script_value(1.5), None);
        assert_eq!(MainAlign::from_script_value(-1.0), None);
        assert_eq!(MainAlign::from_script_value(f32::NAN), None);
        assert_eq!(LayoutDirection::Horizontal.to_script_value(), 1.0);
    }

    /// 列挙の JSON は snake_case（.scene の書式）。
    #[test]
    fn enums_serialize_as_snake_case() {
        assert_eq!(serde_json::to_string(&MainAlign::SpaceBetween).unwrap(), "\"space_between\"");
        assert_eq!(serde_json::to_string(&HiddenChildren::KeepSpace).unwrap(), "\"keep_space\"");
        let d: ItemAlign = serde_json::from_str("\"stretch\"").unwrap();
        assert_eq!(d, ItemAlign::Stretch);
    }

    /// 子の揃えの上書き: Auto はコンテナに従い、それ以外は上書きする。
    #[test]
    fn item_align_resolves_against_container() {
        assert_eq!(ItemAlign::Auto.resolve(CrossAlign::Center), CrossAlign::Center);
        assert_eq!(ItemAlign::Stretch.resolve(CrossAlign::Start), CrossAlign::Stretch);
        assert_eq!(ItemAlign::End.resolve(CrossAlign::Stretch), CrossAlign::End);
    }
}
