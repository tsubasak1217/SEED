// ============================================================
//  jointattach_component.rs — ジョイントアタッチ（ソケット）コンポーネント
//
//  Actor を「別モデルのジョイント（ボーン）」へ追従させるための ECS スロット
//  コンポーネント。剣を手のボーンに持たせる・エフェクトを頭に固定する等の
//  ソケット機構を実現する（ECS 理念：データのみを保持し、追従ロジックは
//  jointattach_ops.rs のシステムが毎フレーム実行する）。
//
//  【対象モデルの決定】
//  本コンポーネントを持つアクターから「親（祖先）アクターを上方向へ辿り、
//  最初に Model スロットを持つアクター」をターゲットモデルとする
//  （jointattach_ops.rs の update_joint_attachments が祖先スタックで解決する）。
//
//  【追従の流れ】（毎フレーム・Edit/Play 両対応）
//    1. ターゲットモデルのノード階層を、そのモデルの現在アニメ時刻
//       （ModelComponent.anim_drive。無ければ静止＝バインドポーズ）で解決する。
//    2. joint_name 一致ノードのワールド行列（モデル空間）を取得する。
//    3. `モデルアクタのワールド行列 × ジョイントワールド行列 × オフセット行列`
//       を自アクターの Transform（＋ Model instance_mats）へ書き込む。
//
//  【シリアライズ】
//  全フィールドに #[serde(default)]（非ゼロ既定は default fn）を付け、
//  旧 .scene（フィールド欠落）でも読み込みが失敗しないようにする。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

// ─── デフォルト値関数 ─────────────────────────────────────────
// マジックナンバー禁止のため、非ゼロ既定値はすべて関数に切り出す。

/// offset_scale の既定値（等倍）。
fn default_offset_scale() -> [f32; 3] { [1.0, 1.0, 1.0] }

// ─── JointAttachComponentData（シリアライズ用）───────────────

/// JointAttachComponent のシリアライズ用データ（.scene 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct JointAttachComponentData {
    /// 追従対象のジョイント（ノード）名。空文字なら無効（追従しない）。
    /// ターゲットモデルの skin ジョイント名（無ければノード名）と一致させる。
    #[serde(default)]
    pub joint_name: String,
    /// ジョイントローカル基準の位置オフセット（ワールド単位）。既定 [0,0,0]。
    #[serde(default)]
    pub offset_pos: [f32; 3],
    /// ジョイントローカル基準の回転オフセット（YXZ オイラー角・度）。既定 [0,0,0]。
    #[serde(default)]
    pub offset_rot_deg: [f32; 3],
    /// ジョイントローカル基準のスケールオフセット。既定 [1,1,1]。
    #[serde(default = "default_offset_scale")]
    pub offset_scale: [f32; 3],
}

impl Default for JointAttachComponentData {
    fn default() -> Self {
        Self {
            joint_name:     String::new(),
            offset_pos:     [0.0, 0.0, 0.0],
            offset_rot_deg: [0.0, 0.0, 0.0],
            offset_scale:   default_offset_scale(),
        }
    }
}

// ─── JointAttachComponent（ECS 実体）─────────────────────────

/// ジョイントアタッチコンポーネント（ECS 実体）。
///
/// 保持するのは追従パラメータ（ジョイント名・オフセット）のみ。
/// 実際の追従計算・書き込みは jointattach_ops.rs のシステムが毎フレーム行う。
/// 揮発状態は持たない（Data と同一構成）。
#[derive(Clone, Debug)]
pub struct JointAttachComponent {
    pub joint_name:     String,
    pub offset_pos:     [f32; 3],
    pub offset_rot_deg: [f32; 3],
    pub offset_scale:   [f32; 3],
}

impl JointAttachComponent {
    /// シリアライズ用データからコンポーネントを構築する。
    pub fn from_data(data: JointAttachComponentData) -> Self {
        Self {
            joint_name:     data.joint_name,
            offset_pos:     data.offset_pos,
            offset_rot_deg: data.offset_rot_deg,
            offset_scale:   data.offset_scale,
        }
    }

    /// シリアライズ用データへ変換する。
    pub fn to_data(&self) -> JointAttachComponentData {
        JointAttachComponentData {
            joint_name:     self.joint_name.clone(),
            offset_pos:     self.offset_pos,
            offset_rot_deg: self.offset_rot_deg,
            offset_scale:   self.offset_scale,
        }
    }
}

impl Default for JointAttachComponent {
    fn default() -> Self { Self::from_data(JointAttachComponentData::default()) }
}

impl Component for JointAttachComponent {}
