// ============================================================
//  control_point_component.rs — コントロールポイント（汎用パス）コンポーネント
//
//  「シーン上に順序付きの点列を置く」ためだけの、用途に依存しない ECS スロット
//  コンポーネント。川のスプライン・キャラクターの巡回ルート・カメラのフライスルー
//  パス・スポーン地点列など、**点列で表せるものすべての共通の土台**にする。
//
//  ## ECS 理念（データとロジックの分離）
//  本コンポーネントは点列の *データのみ* を持ち、パスの意味づけを一切知らない。
//    ・点列 → 曲線・折れ線への評価  … `engine::path::PathEval`
//    ・ビューポートへの可視化        … `app::control_point_scene_gizmo`
//    ・消費（川・巡回・カメラ）      … 各消費側システム
//  「点を置く側」と「点を使う側」を分離してあるので、消費側を増やしても
//  本コンポーネントには一切手を入れなくてよい。
//
//  ## 座標系
//  `position` は **アクタ相対**（ローカル）で持つ。ワールドへ出すのは評価時
//  （`PathEval`）に、そのアクタの Transform を掛けて行う。
//  こうしておくと「アクタを動かすとパスごと動く」「プレハブ化してもパスが崩れない」
//  という当たり前の挙動が無料で手に入る。
//
//  ## `time` の意味づけ
//  `time` は「この点に到達する時刻」または「この点における基準時刻」であり、
//  **単位も原点も消費側に委ねる**（秒・拍・進捗率のいずれとしても使える）。
//  エンジンは「点列に沿って単調増加する 1 次元パラメータ」としてのみ扱う。
//  既定値は「点の追加順に `DEFAULT_TIME_STEP` ずつ増える連番」で、
//  何も設定しなければ「1 点 1 秒で進むパス」として素直に振る舞う。
//
//  ## `interp` の意味づけ
//  `interp` は **その点から次の点へ向かう区間**の補間方法である
//  （「前の点から来る方法」ではない）。最後の点の `interp` は次の点が無いので
//  評価に使われないが、点を後ろに足したときの挙動が予測できるよう保持する。
//
//  ## シリアライズ
//  全フィールドに `#[serde(default)]`（非ゼロ既定値は default 関数）を付け、
//  本コンポーネントを知らない旧 `.scene` でも読み込みが失敗しないようにする。
// ============================================================

// 点の追加補助（`next_default_time`）や補間方法の文字列化（`as_str` / `from_str_opt`）は
// エディタ側のリスト UI（次フェーズ）が使う公開 API で、ランタイム内にはまだ呼び出し元が
// 無いため dead_code 警告になる。API は確定済みなのでファイル単位で抑止する。
#![allow(dead_code)]

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// 点を 1 つ追加したときに `time` が進む既定量。
///
/// 単位は消費側依存だが「1 点 = 1 秒」を既定の読み替えとする。
/// これにより、時刻を一切触らずに点を並べただけでも
/// `position_at_time(t)` が「t 秒後の位置」として意味を持つ。
pub const DEFAULT_TIME_STEP: f32 = 1.0;

/// 1 コンポーネントが保持できる制御点の最大数。
///
/// 上限が無いと、ビューポートの点キューブ描画とピッキング ID 帯が
/// 際限なく膨らむ（1 点 = ワイヤキューブ 12 本 + ID 1 個）。
/// 川スプライン（`RIVER_MAX_CONTROL_POINTS` = 64）より十分大きく取りつつ、
/// 1 パスあたりの GPU コストを予測可能な範囲に抑える値。
pub const MAX_CONTROL_POINTS: usize = 256;

// ─── デフォルト値関数 ─────────────────────────────────────────

/// `rotation` の既定値（度・YXZ 順）。無回転。
fn default_rotation() -> [f32; 3] { [0.0, 0.0, 0.0] }

/// `scale` の既定値（等倍）。
///
/// 0 埋めの `Default` に任せると「スケール 0 の点」になり、
/// ビューポートのキューブが潰れて掴めなくなる（＝壊れた既定値）。
/// 非ゼロ既定なので `#[serde(default = "...")]` で明示する。
fn default_scale() -> [f32; 3] { [1.0, 1.0, 1.0] }

/// `interp` の既定値。
///
/// CatmullRom を既定にする理由は、川・カメラパス・巡回いずれでも
/// 「点を置くだけで滑らかに繋がる」のが最も期待に近いため
/// （制御点を必ず通り、追加のハンドルを要さない）。
fn default_interp() -> ControlPointInterp { ControlPointInterp::CatmullRom }

// ─── ControlPointInterp ───────────────────────────────────────

/// 制御点から**次の制御点へ向かう区間**の補間方法。
#[derive(Clone, Copy, PartialEq, Eq, Debug, Serialize, Deserialize)]
pub enum ControlPointInterp {
    /// 直線補間（線分で繋ぐ）。曲がり角が角ばる代わりに、点の位置が正確に効く。
    Linear,
    /// Catmull-Rom スプライン（uniform, τ = 0.5）。制御点を必ず通る滑らかな曲線。
    CatmullRom,
    /// 階段保持。次の点に到達するまで**この点の値を保ち続ける**（瞬間移動）。
    Step,
}

impl ControlPointInterp {
    /// IPC・インスペクタ表示用の文字列表現。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Linear     => "Linear",
            Self::CatmullRom => "CatmullRom",
            Self::Step       => "Step",
        }
    }

    /// 文字列から補間方法を復元する（未知の文字列は None）。
    ///
    /// 大文字小文字は無視する（エディタ側の表記ゆれで壊れないようにするため）。
    pub fn from_str_opt(s: &str) -> Option<Self> {
        match s.trim().to_ascii_lowercase().as_str() {
            "linear"     => Some(Self::Linear),
            "catmullrom" => Some(Self::CatmullRom),
            "step"       => Some(Self::Step),
            _            => None,
        }
    }
}

impl Default for ControlPointInterp {
    fn default() -> Self { default_interp() }
}

// ─── ControlPoint ─────────────────────────────────────────────

/// 制御点 1 個。ランタイム表現とシリアライズ表現を兼ねる
/// （数値だけの平坦な構造なので、変換層を挟む価値が無い）。
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct ControlPoint {
    /// 位置（**アクタ相対**）。ワールドへの変換は `PathEval` が行う。
    #[serde(default)]
    pub position: [f32; 3],
    /// 姿勢（度・YXZ 順）。カメラパスの向きや、巡回中の向き指定に使う。既定は無回転。
    #[serde(default = "default_rotation")]
    pub rotation: [f32; 3],
    /// 拡縮（XYZ 倍率）。既定は等倍。
    ///
    /// **点そのものは大きさを持たない**が、点に紐づく「その地点でのスケール」を
    /// 持たせておくと、カメラパスのズーム・巡回中のキャラサイズ・スポーン地点の
    /// 生成スケールなどを点列だけで表現できる。
    /// 川（`WaterVolumeComponent`）は位置しか使わないため、この値の影響を受けない。
    /// ビューポートのワイヤキューブは rotation と併せてこの値で変形して描く
    /// （＝設定値が見た目で分かる）。
    #[serde(default = "default_scale")]
    pub scale: [f32; 3],
    /// この点の時刻パラメータ。単位・原点は消費側に委ねる（ファイル冒頭コメント参照）。
    #[serde(default)]
    pub time: f32,
    /// **この点から次の点へ**向かう区間の補間方法。既定 CatmullRom。
    #[serde(default = "default_interp")]
    pub interp: ControlPointInterp,
}

impl Default for ControlPoint {
    fn default() -> Self {
        Self {
            position: [0.0, 0.0, 0.0],
            rotation: default_rotation(),
            scale:    default_scale(),
            time:     0.0,
            interp:   default_interp(),
        }
    }
}

// ─── ControlPointComponentData（シリアライズ用）───────────────

/// `ControlPointComponent` のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct ControlPointComponentData {
    /// 制御点の並び。**添字の順序がパスの順序**であり、`time` の昇順とは独立に扱う
    /// （時刻が前後した点列も壊れずに読み込めるようにするため）。
    #[serde(default)]
    pub points: Vec<ControlPoint>,
}

// ─── ControlPointComponent（ランタイム）───────────────────────

/// 汎用パスの制御点列を保持するコンポーネント。
///
/// 川・巡回ルート・カメラパスなど、点列で表現できるあらゆる機能の共通土台。
/// 本体は「順序付きの点の入れ物」でしかなく、評価も描画も他モジュールが行う。
#[derive(Clone, Debug, Default)]
pub struct ControlPointComponent {
    /// 制御点の並び（アクタ相対座標）。
    pub points: Vec<ControlPoint>,
}

impl ControlPointComponent {
    /// シリアライズ用データからランタイム表現を作る（.scene 読込・複製・Undo 復元）。
    ///
    /// 点数が `MAX_CONTROL_POINTS` を超える入力は**超過分を切り捨てる**
    /// （壊れたデータでランタイムのコストが青天井にならないようにするため）。
    pub fn from_data(data: &ControlPointComponentData) -> Self {
        let mut points = data.points.clone();
        points.truncate(MAX_CONTROL_POINTS);
        Self { points }
    }

    /// ランタイム表現をシリアライズ用データへ変換する（.scene 保存・Undo スナップショット）。
    pub fn to_data(&self) -> ControlPointComponentData {
        ControlPointComponentData { points: self.points.clone() }
    }

    /// 点を 1 つ末尾に追加するときの既定時刻を返す。
    ///
    /// 「最後の点の時刻 + `DEFAULT_TIME_STEP`」。空なら 0。
    /// エディタが点を追加する際に、時刻欄を空のままでも連番になるようにする。
    pub fn next_default_time(&self) -> f32 {
        match self.points.last() {
            Some(last) => last.time + DEFAULT_TIME_STEP,
            None       => 0.0,
        }
    }
}

impl Component for ControlPointComponent {}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 制御点の既定値が「原点・無回転・等倍・時刻 0・CatmullRom」であること
    /// （インスペクタで点を足した直後の見え方の契約）。
    #[test]
    fn control_point_defaults_are_documented_values() {
        let p = ControlPoint::default();
        assert_eq!(p.position, [0.0, 0.0, 0.0]);
        assert_eq!(p.rotation, [0.0, 0.0, 0.0]);
        assert_eq!(p.scale, [1.0, 1.0, 1.0], "既定は等倍（0 だとキューブが潰れる）");
        assert_eq!(p.time, 0.0);
        assert_eq!(p.interp, ControlPointInterp::CatmullRom);
    }

    /// **フィールドが 1 つも無い旧 .scene（`{}`）でも読み込めること**（serde default の要）。
    #[test]
    fn deserializes_from_empty_json_with_defaults() {
        let d: ControlPointComponentData =
            serde_json::from_str("{}").expect("空 JSON から既定値で復元できること");
        assert!(d.points.is_empty());
    }

    /// 点の一部フィールドだけを持つ JSON でも、残りが既定値で埋まること。
    #[test]
    fn control_point_deserializes_partial_json() {
        let d: ControlPointComponentData =
            serde_json::from_str(r#"{"points":[{"position":[1.0,2.0,3.0]}]}"#)
                .expect("部分 JSON から復元できること");
        assert_eq!(d.points.len(), 1);
        assert_eq!(d.points[0].position, [1.0, 2.0, 3.0]);
        assert_eq!(d.points[0].rotation, [0.0, 0.0, 0.0], "未指定は既定値");
        assert_eq!(d.points[0].scale, [1.0, 1.0, 1.0], "**scale が無い旧 .scene でも等倍で復元される**");
        assert_eq!(d.points[0].time, 0.0);
        assert_eq!(d.points[0].interp, ControlPointInterp::CatmullRom, "未指定は CatmullRom");
    }

    /// to_data → from_data で点列が完全に往復すること。
    #[test]
    fn data_round_trips() {
        let c = ControlPointComponent {
            points: vec![
                ControlPoint { position: [1.0, 0.0, 0.0], rotation: [0.0, 90.0, 0.0], scale: [2.0, 1.0, 0.5], time: 0.0, interp: ControlPointInterp::Linear },
                ControlPoint { position: [2.0, 1.0, 0.0], rotation: [0.0, 0.0, 0.0],  scale: [1.0, 1.0, 1.0], time: 1.5, interp: ControlPointInterp::Step },
            ],
        };
        let back = ControlPointComponent::from_data(&c.to_data());
        assert_eq!(back.points, c.points);
    }

    /// 点数の上限を超えるデータは切り捨てられること（ランタイムコストの上限保証）。
    #[test]
    fn from_data_truncates_beyond_max() {
        let data = ControlPointComponentData {
            points: vec![ControlPoint::default(); MAX_CONTROL_POINTS + 10],
        };
        let c = ControlPointComponent::from_data(&data);
        assert_eq!(c.points.len(), MAX_CONTROL_POINTS);
    }

    /// 補間方法の文字列往復（IPC の要）。未知の文字列は None。
    #[test]
    fn interp_string_round_trips() {
        for k in [ControlPointInterp::Linear, ControlPointInterp::CatmullRom, ControlPointInterp::Step] {
            assert_eq!(ControlPointInterp::from_str_opt(k.as_str()), Some(k));
        }
        // 表記ゆれを吸収すること
        assert_eq!(ControlPointInterp::from_str_opt("linear"), Some(ControlPointInterp::Linear));
        assert_eq!(ControlPointInterp::from_str_opt("nonsense"), None);
    }

    /// 末尾追加時の既定時刻が連番になること。
    #[test]
    fn next_default_time_is_sequential() {
        let mut c = ControlPointComponent::default();
        assert_eq!(c.next_default_time(), 0.0, "空なら 0 から始まる");
        c.points.push(ControlPoint { time: c.next_default_time(), ..Default::default() });
        assert_eq!(c.next_default_time(), DEFAULT_TIME_STEP);
        c.points.push(ControlPoint { time: c.next_default_time(), ..Default::default() });
        assert_eq!(c.next_default_time(), DEFAULT_TIME_STEP * 2.0);
    }
}
