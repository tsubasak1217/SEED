// ============================================================
//  field_edit.rs — インスペクタのフィールド編集を丸ごと Undo/Redo へ載せる汎用機構
//
//  【解決したい問題】
//  インスペクタから飛ぶ「値を書き換える IPC」は 60 種類以上あり、
//  ハンドラごとに個別の Undo コマンドを書く方式では必ず対応漏れが出ていた
//  （実際、Transform とプラグインフィールド以外はすべて Ctrl+Z が効かなかった）。
//
//  【方式】
//  IPC ディスパッチの入口で 1 回だけ次を行う（ipc_handler.rs::process_ipc）:
//    1. コマンドを `field_edit_target()` で分類し、書き換え対象（アクタ・スロット）を得る
//    2. 対象の**現在値をスナップショット**（ComponentSlotData 等のシリアライズ表現）
//    3. 既存ハンドラをそのまま呼ぶ（ハンドラ側は 1 行も変えない）
//    4. 適用後の値と比較し、差分があれば Undo 履歴へ積む
//
//  これにより「新しい SET_* を足したのに Undo を忘れた」が起きない。
//  分類関数は IpcCommand に対する**網羅 match** なので、コマンドを追加すると
//  コンパイルエラーになり、分類を書くまでビルドが通らない（構造的な漏れ防止）。
//
//  【連続編集のまとめ】
//  スライダー／数値ドラッグは 1 ドラッグで数百回 IPC が飛ぶ。
//  同一 (アクタ, スロット, フィールド) への更新が `FIELD_EDIT_MERGE_WINDOW` 以内に
//  続く限り 1 コマンドへマージする（先頭の「編集前」を保持し、末尾の値で置き換える）。
//  間に別の操作が履歴へ積まれた場合はマージしない（履歴長で検証する）。
//
//  【Undo に載せないもの】
//  ・Play 中の変更（実行時の一時状態であってシーンの編集ではない）
//  ・ギズモ／地形ブラシ等、既に専用の Undo を持つ経路（二重記録の防止）
//  ・水位シミュレーションの揮発値（sim_level_y）などの非シリアライズ値
// ============================================================

use std::time::{Duration, Instant};

use crate::engine::components::ComponentData;
#[cfg(test)]
use crate::engine::components::ComponentKind;
use crate::engine::core::app_base::ipc::IpcCommand;
use crate::engine::core::app_base::undo::{
    ActorActiveCommand, CanvasTransformCommand, SceneShadingCommand, SceneShadingState,
    SlotFieldEditCommand,
};
use crate::engine::components::CanvasTransform;
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::actor::{ComponentSlotData, slot_to_data};

use super::{App, RuntimeMode, find_actor_by_dfs};

// ─── 定数 ─────────────────────────────────────────────────────

/// 連続編集を 1 コマンドへマージする時間窓。
/// スライダー／数値ドラッグの更新間隔（数 ms〜数十 ms）より十分長く、
/// 人が「別の操作」と認識する間隔（およそ 1 秒）より短い値を選ぶ。
pub(super) const FIELD_EDIT_MERGE_WINDOW: Duration = Duration::from_millis(400);

// ─── 編集対象の分類 ───────────────────────────────────────────

/// IPC コマンドが書き換える「インスペクタ上の対象」。
#[derive(Clone, PartialEq, Debug)]
pub(super) enum FieldEditTarget {
    /// Undo 記録の対象外（問い合わせ・エディタ状態・既に専用 Undo を持つ経路）。
    None,
    /// コンポーネントスロット 1 個の値編集。
    Slot {
        actor_dfs_id: u32,
        slot_idx: u32,
        /// 連続編集のマージ判定キー（対象＋フィールド名まで含める）。
        merge_key: String,
    },
    /// 2D アクターの CanvasTransform（アンカー・スケールモード）編集。
    CanvasTransform { actor_dfs_id: u32, merge_key: String },
    /// アクターのアクティブフラグ編集。
    ActorActive { actor_dfs_id: u32 },
    /// シーン設定ウィンドウの「シェーダ」まわり（アセットパス・パラメータ・`@ref` バインド）。
    ///
    /// コンポーネントスロットではないので `Slot` には載らないが、
    /// **編集であることに変わりはない**ので同じ機構で Undo に載せる。
    SceneShading { merge_key: String },
}

/// シーン設定のシェーディング編集の分類を組み立てる小ヘルパ。
fn scene_shading(cmd_name: &str, field: &str) -> FieldEditTarget {
    FieldEditTarget::SceneShading { merge_key: format!("{cmd_name}/scene/{field}") }
}

/// スロット編集の分類を組み立てる小ヘルパ。
fn slot(actor_dfs_id: u32, slot_idx: u32, cmd_name: &str, field: &str) -> FieldEditTarget {
    FieldEditTarget::Slot {
        actor_dfs_id,
        slot_idx,
        merge_key: format!("{cmd_name}/{actor_dfs_id}/{slot_idx}/{field}"),
    }
}

/// IPC コマンドを Undo 記録の観点で分類する。
///
/// **網羅 match** であること（`_ =>` を書かないこと）。
/// IpcCommand に variant を足すとここがコンパイルエラーになり、
/// 「Undo に載せるか否か」を必ず決めさせるための仕掛けである。
///
/// 対象と判定したスロット系コマンドの一覧は下のマーカーコメントで囲ってあり、
/// `SET_*_FIELD` 系の網羅性を契約テスト（本ファイル末尾）が検査する。
pub(super) fn field_edit_target(cmd: &IpcCommand) -> FieldEditTarget {
    match cmd {
        // ── ここから: スロット値編集（Undo 記録の対象） ──
        // <FIELD-EDIT-TABLE:SLOT>
        IpcCommand::SetWaterField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetWaterField", key),
        // 水面シェーディングアセットのパラメータ（W8.2）。
        // マージキーにパラメータ名まで含めるので、カラーピッカーの連打・
        // スライダーのドラッグは「そのパラメータ 1 個ぶん」で 1 コマンドにまとまる。
        IpcCommand::SetWaterShaderParam { actor_dfs_id, slot_idx, name, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetWaterShaderParam", name),
        // 「デフォルトに戻す」ボタン（W8.2）。値編集と**別のコマンド名**でマージキーを作るので、
        // 直前のスライダー操作と 1 手にまとまらず、Ctrl+Z 1 回でリセット前へ戻る。
        IpcCommand::ResetWaterShaderParam { actor_dfs_id, slot_idx, name } =>
            slot(*actor_dfs_id, *slot_idx, "ResetWaterShaderParam", name),
        // `@ref` パラメータのバインド設定・解除（W8.3）。値編集とは別のコマンド名で
        // マージキーを作るので、直前の値操作と 1 手にまとまらない。
        IpcCommand::SetWaterShaderBinding { actor_dfs_id, slot_idx, name, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetWaterShaderBinding", name),
        IpcCommand::SetWaterLinkField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetWaterLinkField", key),
        IpcCommand::SetLightField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetLightField", key),
        IpcCommand::SetAudioField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetAudioField", key),
        IpcCommand::SetLineRendererField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetLineRendererField", key),
        // テキスト（内容・サイズ・色・整列・行送り・レイヤー）。
        // マージキーに key を含めるので、フォントサイズのドラッグは 1 手にまとまる。
        IpcCommand::SetTextField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetTextField", key),
        IpcCommand::SetSkinnedSpriteField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetSkinnedSpriteField", key),
        IpcCommand::SetSpriteField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetSpriteField", key),
        // ボーン対応表は「1 フィールド（bone_overrides）の一括差し替え」なので
        // フィールドキーを固定文字列にして 1 スロット 1 件の Undo にまとめる。
        IpcCommand::SetSkinnedSpriteBoneOverrides { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetSkinnedSpriteBoneOverrides", "bone_overrides"),
        IpcCommand::SetScriptField { actor_dfs_id, slot_idx, field, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetScriptField", field),
        IpcCommand::SetSkyboxField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetSkyboxField", key),
        IpcCommand::SetInteractionField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetInteractionField", key),
        IpcCommand::SetCoverField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCoverField", key),
        IpcCommand::SetJointAttachField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetJointAttachField", key),
        IpcCommand::SetParticleField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetParticleField", key),
        IpcCommand::SetModelField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetModelField", key),
        IpcCommand::SetPluginField { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetPluginField", key),
        // インスペクタ各行の「⟲ デフォルトに戻す」ボタン（全コンポーネント共通）。
        // コマンド名を SET 系（"SetLightField" 等）と**変えている**ので、
        // 直前のスライダー操作とはマージキーが一致せず 1 手にまとまらない。
        // その結果、Ctrl+Z 1 回で「リセットの直前の値」へ戻る
        // （まとまってしまうとドラッグ開始前の値まで飛んでしまう）。
        IpcCommand::ResetComponentField { actor_dfs_id, slot_idx, field } =>
            slot(*actor_dfs_id, *slot_idx, "ResetComponentField", field),
        // </FIELD-EDIT-TABLE:SLOT>

        // ── フィールド名を持たないスロット値編集（1 コマンド = 1 フィールド） ──
        IpcCommand::SetParticleCurve { actor_dfs_id, slot_idx, curve_id, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetParticleCurve", curve_id),
        IpcCommand::SetModelPath { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetModelPath", ""),
        IpcCommand::SetMaterialOverride { actor_dfs_id, slot_idx, mat_slot, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetMaterialOverride", &mat_slot.to_string()),
        IpcCommand::SetInputMapPath { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetInputMapPath", ""),
        IpcCommand::SetAnimatorClips { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetAnimatorClips", ""),
        IpcCommand::SetColliderData { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetColliderData", ""),
        IpcCommand::SetCollider2dData { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCollider2dData", ""),
        IpcCommand::SetCollider2dAspectRatio { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCollider2dAspectRatio", ""),
        IpcCommand::SetSpritePath { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetSpritePath", ""),
        IpcCommand::SetSpritePostfx { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetSpritePostfx", ""),
        IpcCommand::SetSpriteColor { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetSpriteColor", ""),
        IpcCommand::SetSpriteSize { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetSpriteSize", ""),
        IpcCommand::SetSpriteLayer { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetSpriteLayer", ""),
        IpcCommand::SetCanvasSize { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCanvasSize", ""),
        IpcCommand::SetCanvasAutoScale { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCanvasAutoScale", ""),
        IpcCommand::SetCanvasGravityMode { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCanvasGravityMode", ""),
        IpcCommand::SetCanvasDrawZone { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCanvasDrawZone", ""),
        IpcCommand::SetCanvas3dPivot { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCanvas3dPivot", ""),
        IpcCommand::SetCanvasViewportRefWindow { actor_dfs_id, slot_idx } =>
            slot(*actor_dfs_id, *slot_idx, "SetCanvasViewportRef", ""),
        IpcCommand::SetCanvasViewportRefMainCamera { actor_dfs_id, slot_idx } =>
            slot(*actor_dfs_id, *slot_idx, "SetCanvasViewportRef", ""),
        IpcCommand::SetCanvasViewportRefCamera { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCanvasViewportRef", ""),
        IpcCommand::SetCameraComponentFov { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraComponentFov", ""),
        IpcCommand::SetCameraComponentNear { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraComponentNear", ""),
        IpcCommand::SetCameraComponentFar { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraComponentFar", ""),
        IpcCommand::SetCameraComponentMain { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraComponentMain", ""),
        IpcCommand::SetCameraComponentClearColor { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraComponentClearColor", ""),
        IpcCommand::SetCameraComponentScalingMode { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraComponentScalingMode", ""),
        IpcCommand::SetCameraComponentTargetSize { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraComponentTargetSize", ""),
        IpcCommand::SetCameraBarColor { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraBarColor", ""),
        IpcCommand::SetCameraComponentProjection { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraComponentProjection", ""),
        IpcCommand::SetCameraComponentOrthoHeight { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraComponentOrthoHeight", ""),
        IpcCommand::SetCameraComponentShadingAsset { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraComponentShadingAsset", ""),
        // カメラ添付シェーディングアセットのパラメータ（L3）。マージキーにパラメータ名まで
        // 含めるので、スライダーのドラッグは「そのパラメータ 1 個ぶん」で 1 コマンドにまとまる。
        IpcCommand::SetCameraShadingParam { actor_dfs_id, slot_idx, name, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraShadingParam", name),
        // 「デフォルトに戻す」ボタン。値編集と**別のコマンド名**でマージキーを作るので、
        // 直前のスライダー操作と 1 手にまとまらず、Ctrl+Z 1 回でリセット前へ戻る。
        IpcCommand::ResetCameraShadingParam { actor_dfs_id, slot_idx, name } =>
            slot(*actor_dfs_id, *slot_idx, "ResetCameraShadingParam", name),
        // `@ref` バインドの設定・解除。これも値編集とは別コマンド名にする。
        IpcCommand::SetCameraShadingBinding { actor_dfs_id, slot_idx, name, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetCameraShadingBinding", name),
        // スロット名・有効フラグも ComponentSlotData に含まれるため同じ経路で戻せる。
        IpcCommand::SetSlotEnabled { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "SetSlotEnabled", ""),
        IpcCommand::RenameComponentSlot { actor_dfs_id, slot_idx, .. } =>
            slot(*actor_dfs_id, *slot_idx, "RenameComponentSlot", ""),
        // AI アシスタントによる値設定もインスペクタ編集と同じ扱い（戻せること）。
        IpcCommand::AiSetValue { actor_dfs_id, slot_idx, key, .. } =>
            slot(*actor_dfs_id, *slot_idx, "AiSetValue", key),

        // ── アクター単位の編集 ──
        IpcCommand::SetCanvasAnchor { actor_dfs_id, .. } => FieldEditTarget::CanvasTransform {
            actor_dfs_id: *actor_dfs_id,
            merge_key: format!("SetCanvasAnchor/{actor_dfs_id}"),
        },
        IpcCommand::SetCanvasTransformScaleMode { actor_dfs_id, .. } =>
            FieldEditTarget::CanvasTransform {
                actor_dfs_id: *actor_dfs_id,
                merge_key: format!("SetCanvasTransformScaleMode/{actor_dfs_id}"),
            },
        IpcCommand::SetActorActive { dfs_id, .. } =>
            FieldEditTarget::ActorActive { actor_dfs_id: *dfs_id },

        // ── シーン設定ウィンドウの「シェーダ」まわり ──────────
        // アセットパス・パラメータ値・`@ref` バインドの 3 種。まとめて 1 つの
        // スナップショット（SceneShadingState）で戻すので、分類先も 1 つにする。
        IpcCommand::SetSceneShadingAsset { .. } => scene_shading("SetSceneShadingAsset", ""),
        IpcCommand::SetSceneShadingParam { name, .. } =>
            scene_shading("SetSceneShadingParam", name),
        IpcCommand::ResetSceneShadingParam { name } =>
            scene_shading("ResetSceneShadingParam", name),
        IpcCommand::SetSceneShadingBinding { name, .. } =>
            scene_shading("SetSceneShadingBinding", name),

        // ── 対象外 ──────────────────────────────────────────
        // 【既に専用 Undo を持つ経路】ここで記録すると二重に積まれる。
        //   Transform 系      : transform_ops.rs（ドラッグは BEGIN/END_TRANSFORM_DRAG で 1 件化）
        //   制御点            : control_point_ops.rs（ドラッグ確定時に 1 件記録）
        //   ツリー・スロット構造: actor_ops.rs / component_ops.rs / slot_ops.rs
        //   地形              : terrain_ops.rs の専用スタック（ECS 外データのため）
        IpcCommand::SetTransform { .. }
        | IpcCommand::SetActorTransform { .. }
        | IpcCommand::SetCanvasTransform { .. }
        | IpcCommand::BeginTransformDrag { .. }
        | IpcCommand::EndTransformDrag
        | IpcCommand::SetControlPoints { .. }
        | IpcCommand::SetControlPointPos { .. }
        | IpcCommand::SelectControlPoint { .. }
        | IpcCommand::AddControlPointAtScreen { .. }
        | IpcCommand::ControlPointDragHover { .. }
        | IpcCommand::ControlPointDragEnd
        | IpcCommand::AddComponent { .. }
        | IpcCommand::RemoveComponentSlot { .. }
        | IpcCommand::DuplicateComponent { .. }
        | IpcCommand::AddActor { .. }
        | IpcCommand::AddActor2D { .. }
        | IpcCommand::AddActorChild { .. }
        | IpcCommand::AddActor2dChild { .. }
        // ボーンアクターの一括生成はアクターツリーの構造変更であり、
        // ハンドラ側が ActorTreeSnapshotCommand を 1 件積む（二重記録を避けるため対象外）。
        | IpcCommand::CreateSpriteBoneActors { .. }
        | IpcCommand::WrapActor { .. }
        | IpcCommand::RemoveActor(..)
        | IpcCommand::RenameActor { .. }
        | IpcCommand::Rename { .. }
        | IpcCommand::Reparent { .. }
        | IpcCommand::Delete(..)
        | IpcCommand::DeleteRecursive(..)
        | IpcCommand::Copy
        | IpcCommand::Paste
        | IpcCommand::CreateGroup { .. }
        | IpcCommand::CreateGroupWithChildren { .. }
        | IpcCommand::DropActor { .. }
        | IpcCommand::UnlinkPrefab { .. }
        | IpcCommand::ReapplyPrefab { .. }
        | IpcCommand::ReapplyAllPrefabs
        | IpcCommand::AiAddActor { .. }
        | IpcCommand::AiRemoveActor { .. }
        | IpcCommand::AiMoveActor { .. }
        | IpcCommand::AiAddComponent { .. }
        | IpcCommand::TerrainInit { .. }
        | IpcCommand::TerrainAddChunks { .. }
        | IpcCommand::TerrainBrush { .. }
        | IpcCommand::TerrainPaint { .. }
        // ブラシ形状マスクは「道具の設定」であってシーンの値ではないため Undo 対象外
        // （半径・強度スライダーが Undo に載らないのと同じ扱い）。
        | IpcCommand::TerrainBrushMask { .. }
        | IpcCommand::TerrainSave
        | IpcCommand::TerrainSaveAs { .. }
        | IpcCommand::TerrainBrushPreview { .. }
        | IpcCommand::TerrainBrushPreviewOff
        // チャンク当たり判定のトグルは terrain 専用 Undo スタック（1 クリック = 1 手）の
        // 管轄であり、メイン履歴のフィールド編集としては扱わない（二重記録を避ける）。
        | IpcCommand::TerrainCollisionToggle { .. }
        // オーバーレイ表示とデシメートは「道具・表示の設定」であってシーンの値ではない
        // （ブラシ半径スライダーが Undo に載らないのと同じ扱い）。
        | IpcCommand::TerrainCollisionOverlay { .. }
        | IpcCommand::TerrainDecimate { .. }
        | IpcCommand::TerrainUndo
        | IpcCommand::TerrainRedo
        | IpcCommand::TerrainStrokeEnd
        | IpcCommand::TerrainReloadLayers
        // カバー場のシミュレート・消去はカバー場そのものの編集であり、
        // スロットの値を変える「フィールド編集」ではない（ここでスナップショットしても差分ゼロ）。
        // これらはハンドラ側（terrain_cover_ops.rs::commit_cover_undo_session）が
        // メイン履歴へ CoverFieldEditCommand として積み、通常の Ctrl+Z（UNDO）で戻せる
        //（連続シミュレートは「開始〜停止」がまとめて 1 コマンドになる）。
        | IpcCommand::TerrainCoverSimStart
        | IpcCommand::TerrainCoverSimStop
        | IpcCommand::TerrainCoverStep { .. }
        | IpcCommand::TerrainCoverClear
        | IpcCommand::TerrainHeightmap { .. }
        | IpcCommand::TerrainScatterRules { .. }
        | IpcCommand::TerrainScatterBrush { .. }
        // カバーブラシは terrain 専用 Undo スタック（ストローク単位）の管轄であり、
        // メイン履歴のフィールド編集としては扱わない（二重記録を避ける）。
        | IpcCommand::TerrainCoverBrush { .. }
        // 【シーン／プロジェクト単位の設定】インスペクタではなく設定パネルの管轄。
        //
        // このうち**シェーダまわりだけ**は Undo 対象にしてある（下の SceneShading 腕）。
        // SET_SCENE_SETTINGS（ビューポート／レンダリング設定）を対象外のまま残すのは、
        // それらがエディタ側で個別 IPC（SET_POST_FX / SET_AMBIENT 等）により
        // **既にライブ適用済み**で、このコマンドは保存用データの格納しかしないためである。
        // データだけ巻き戻すと画面と設定ウィンドウの表示が食い違う（＝直せない状態になる）。
        | IpcCommand::SetSceneSettings { .. }
        | IpcCommand::SetPostFx { .. }
        | IpcCommand::SetAmbient { .. }
        | IpcCommand::SetRtShadows(..)
        // 【エディタのセッション状態】シーンに永続しない。
        | IpcCommand::SetToolMode(..)
        | IpcCommand::SetGizmoSpace(..)
        | IpcCommand::SetShowGrid(..)
        | IpcCommand::SetShowAxisGizmo(..)
        | IpcCommand::SetShowSpriteBones(..)
        | IpcCommand::SetCameraFov(..)
        | IpcCommand::SetCameraFar(..)
        | IpcCommand::SetCameraTransform { .. }
        | IpcCommand::SetCameraSpeed(..)
        | IpcCommand::SetEditViewMode { .. }
        | IpcCommand::SetEditorCameraOrtho(..)
        | IpcCommand::SetCanvasScreenSpaceOverlay(..)
        | IpcCommand::SetActiveWorldLine(..)
        | IpcCommand::RemoveWorldLine(..)
        | IpcCommand::EditCanvasBegin { .. }
        | IpcCommand::EditCanvasEnd(..)
        | IpcCommand::Select(..)
        | IpcCommand::SelectMulti(..)
        | IpcCommand::CamKeyDown(..)
        | IpcCommand::CamKeyUp(..)
        | IpcCommand::CamKeysClear
        | IpcCommand::CtrlDown
        | IpcCommand::CtrlUp
        | IpcCommand::PlayClamp(..)
        | IpcCommand::DragHover { .. }
        | IpcCommand::DragHoverEnd
        // 【実行・プレビュー・診断】一時状態であり編集ではない。
        | IpcCommand::Pause
        | IpcCommand::Resume
        | IpcCommand::Stop
        | IpcCommand::PauseRender
        | IpcCommand::ResumeRender
        | IpcCommand::EnterPlay
        | IpcCommand::ExitPlay
        | IpcCommand::ReloadScripts
        | IpcCommand::AnimPreview { .. }
        | IpcCommand::AnimPreviewStop { .. }
        | IpcCommand::AnimReload { .. }
        | IpcCommand::SetEditPhysics { .. }
        | IpcCommand::SetEditPhysicsAll { .. }
        | IpcCommand::SetEditPhysics2d { .. }
        | IpcCommand::EditPhysicsPlayPause
        | IpcCommand::EditPhysicsStep { .. }
        | IpcCommand::EditPhysicsSeek { .. }
        | IpcCommand::EditPhysicsApplyFrame
        | IpcCommand::SetPlayColliderDraw(..)
        | IpcCommand::SetPlayShaderHotReload(..)
        | IpcCommand::SetProfilerEnabled(..)
        | IpcCommand::SetDebugGuard(..)
        // 【Undo/Redo 自身・入出力・問い合わせ】
        | IpcCommand::Undo
        | IpcCommand::Redo
        | IpcCommand::SaveScene(..)
        | IpcCommand::SaveActor(..)
        | IpcCommand::LoadScene(..)
        | IpcCommand::OpenActor { .. }
        | IpcCommand::ExportActor { .. }
        | IpcCommand::GetActorData(..)
        | IpcCommand::GetActorComponents(..)
        | IpcCommand::GetCamState
        | IpcCommand::GetSceneInfo
        | IpcCommand::GetPluginList
        // バインド元候補の問い合わせ（W8.3）。読み取りのみでシーンを変えない。
        | IpcCommand::GetBindableSources { .. }
        // シーン既定のパラメータ一覧の問い合わせ。読み取りのみでシーンを変えない。
        | IpcCommand::GetSceneShadingParams
        | IpcCommand::ValidateWgsl { .. } => FieldEditTarget::None,
    }
}

// ─── 編集前後のスナップショット ───────────────────────────────

/// 編集対象の値スナップショット（編集前・編集後の比較と復元に使う）。
#[derive(Clone)]
pub(super) enum FieldEditSnapshot {
    Slot(ComponentSlotData),
    CanvasTransform(CanvasTransform),
    ActorActive(bool),
    /// シーン既定のシェーディング設定 1 式。
    SceneShading(SceneShadingState),
}

impl FieldEditSnapshot {
    /// 値が等しいかを JSON 表現で比較する。
    /// コンポーネントのデータ型は PartialEq を実装していないものが多いため、
    /// 保存と同じシリアライズ表現で比べる（＝「保存に出る差」だけを差分とみなす）。
    fn same_as(&self, other: &FieldEditSnapshot) -> bool {
        json_of(self) == json_of(other)
    }
}

/// スナップショットの JSON 表現。比較専用。
fn json_of(snap: &FieldEditSnapshot) -> String {
    match snap {
        FieldEditSnapshot::Slot(d) => serde_json::to_string(d).unwrap_or_default(),
        FieldEditSnapshot::CanvasTransform(ct) => serde_json::to_string(ct).unwrap_or_default(),
        FieldEditSnapshot::ActorActive(a) => a.to_string(),
        FieldEditSnapshot::SceneShading(st) => format!("{st:?}"),
    }
}

// ─── 連続編集のマージ状態 ─────────────────────────────────────

/// 直近に記録したフィールド編集。次の編集をこれへマージできるか判定するために保持する。
pub(super) struct FieldEditSession {
    /// マージ判定キー（対象＋フィールド）。
    key: String,
    /// このセッションで最初に記録した「編集前」の値。
    before: FieldEditSnapshot,
    /// 最後に記録した時刻。
    at: Instant,
    /// 記録直後の Undo 履歴長。他の操作が割り込むと一致しなくなる。
    past_len: usize,
}

/// 連続編集をマージしてよいかを判定する（純関数。単体テスト対象）。
///
/// マージ条件は 3 つすべて:
///   1. 同じ対象・同じフィールドであること
///   2. 前回の記録から時間窓以内であること
///   3. その間に他の操作が Undo 履歴へ積まれていないこと
pub(super) fn can_merge_field_edit(
    session_key: &str,
    new_key: &str,
    elapsed: Duration,
    session_past_len: usize,
    current_past_len: usize,
) -> bool {
    session_key == new_key
        && elapsed <= FIELD_EDIT_MERGE_WINDOW
        && session_past_len == current_past_len
}

// ─── スナップショットから除外する「別 Undo 経路の持ち物」 ─────

/// フィールド編集のスナップショットから、**別の Undo 経路が管理している大きなデータ**を落とす。
///
/// 現状の対象は ModelComponent のインスタンス配列（instances / meta / groups）。
/// インスタンスの配置はギズモドラッグ・複製・削除が `TransformCommand` /
/// `SceneSnapshotCommand` で個別に Undo しており、インスペクタのフィールド編集
/// （cast_shadows / render_tag / マテリアル）では一切変化しない。
///
/// 落とす理由は 2 つ:
///   1. マテリアルスライダーのドラッグ中に、数万要素の行列配列を毎回 2 回コピーしない
///   2. フィールド編集の Undo が、その後に行われたインスタンス配置まで巻き戻さない
///
/// 落とした分は復元時に「現在の生きている値」を差し戻す（`restore_stripped_fields`）。
fn strip_field_edit_irrelevant(data: &mut ComponentSlotData) {
    if let ComponentData::ModelComponent(d) = &mut data.component {
        d.instances.clear();
        d.meta.clear();
        d.groups.clear();
    }
}

/// `strip_field_edit_irrelevant` で落とした値を、World の現在値から埋め戻す。
/// 復元（Undo/Redo）の直前に呼ぶ。
fn restore_stripped_fields(world: &World, entity: Entity, data: &mut ComponentSlotData) {
    use crate::engine::components::ModelComponent;
    if let ComponentData::ModelComponent(d) = &mut data.component {
        if let Some(mc) = world.get::<ModelComponent>(entity) {
            d.instances = mc.instance_mats.clone();
            d.meta = mc.instance_meta.clone();
            d.groups = mc.group_meta.clone();
            d.next_group_id = mc.next_group_id;
        }
    }
}

// ─── ComponentData のその場適用 ───────────────────────────────

/// `apply_component_data_in_place` の結果。
#[derive(PartialEq, Debug)]
pub(super) enum SlotApply {
    /// 既存エンティティへその場で適用できた。
    Applied,
    /// 重い復元（モデルの再ロード／スクリプトの再生成）が要るため、
    /// 呼び出し側でスロット再構築へフォールバックすること。
    NeedsRebuild,
}

/// スロットのコンポーネント値を、**既存の ECS エンティティを維持したまま**上書きする。
///
/// エンティティを維持することが重要で、レンダラ側の
/// パーティクル状態・スカイボックス GPU 資源・再生中の音声は entity をキーに
/// 管理されているため、despawn を伴う再構築ではこれらが失われる。
///
/// ComponentData に対する**網羅 match**。variant を追加するとコンパイルエラーになり、
/// Undo の適用側を書き忘れられない。
pub(super) fn apply_component_data_in_place(
    world: &mut World,
    entity: Entity,
    data: &ComponentData,
) -> SlotApply {
    use crate::engine::components::*;

    match data {
        // ── モデル: パス／マテリアルが同じなら軽量フィールドのみ差し替える ──
        ComponentData::ModelComponent(d) => {
            let Some(mc) = world.get::<ModelComponent>(entity) else {
                return SlotApply::NeedsRebuild;
            };
            // モデル本体・マテリアルの差し替えは GPU 再アップロードが要るので再構築へ回す。
            // MaterialOverride は PartialEq を持たないため JSON 表現で比較する。
            let overrides_same = serde_json::to_string(&mc.material_overrides).ok()
                == serde_json::to_string(&d.material_overrides).ok();
            if mc.source_path != d.model_path || !overrides_same {
                return SlotApply::NeedsRebuild;
            }
            let Some(mc) = world.get_mut::<ModelComponent>(entity) else {
                return SlotApply::NeedsRebuild;
            };
            mc.cast_shadows = d.cast_shadows;
            mc.render_tag = d.render_tag;
            mc.instance_mats = d.instances.clone();
            mc.instance_meta = d.meta.clone();
            mc.group_meta = d.groups.clone();
            mc.next_group_id = d.next_group_id;
            mc.mark_batch_dirty();
            SlotApply::Applied
        }
        // ── スクリプト: 型が同じならフィールド値だけ書き戻す（CLR 再生成を避ける） ──
        ComponentData::ScriptComponent(d) => {
            if let Some(sc) = world.get_mut::<ScriptComponent>(entity) {
                if sc.type_name() != d.type_name {
                    return SlotApply::NeedsRebuild;
                }
                for (k, v) in &d.fields {
                    sc.set_field(k, v);
                }
                SlotApply::Applied
            } else if let Some(ps) = world.get_mut::<PlaceholderScriptSlot>(entity) {
                if ps.script_path != d.type_name {
                    return SlotApply::NeedsRebuild;
                }
                ps.fields = d.fields.clone();
                SlotApply::Applied
            } else {
                SlotApply::NeedsRebuild
            }
        }
        // ── 水ボリューム: 揮発の現在水位（sim_level_y）は保存対象外なので引き継ぐ ──
        ComponentData::WaterVolumeComponent(d) => {
            let sim_level_y = world
                .get::<WaterVolumeComponent>(entity)
                .and_then(|w| w.sim_level_y);
            let mut w = WaterVolumeComponent::from_data(d.clone());
            w.sim_level_y = sim_level_y;
            world.insert(entity, w);
            SlotApply::Applied
        }
        // ── 以下は純粋な値の詰め替えのみ（副作用なし） ──
        ComponentData::CanvasComponent(d) => {
            world.insert(entity, CanvasComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::SpriteComponent(d) => {
            world.insert(entity, SpriteComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::SkinnedSpriteComponent(d) => {
            world.insert(entity, SkinnedSpriteComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::InputMapComponent(d) => {
            world.insert(entity, InputMapComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::CameraComponent(d) => {
            world.insert(entity, CameraComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        // PluginComponentData は PluginComponent の型エイリアス（同一構造）なのでそのまま入れる。
        ComponentData::PluginComponent(d) => {
            world.insert(entity, d.clone());
            SlotApply::Applied
        }
        ComponentData::ColliderComponent(d) => {
            world.insert(entity, ColliderComponent::from(d.clone()));
            SlotApply::Applied
        }
        ComponentData::Collider2dComponent(d) => {
            world.insert(entity, Collider2dComponent::from(d.clone()));
            SlotApply::Applied
        }
        ComponentData::AudioComponent(d) => {
            world.insert(entity, AudioComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        // 3D ポリラインは純粋な値（点列・幅・色・フラグ）のみなので詰め替えで復元できる。
        ComponentData::LineRendererComponent(d) => {
            world.insert(entity, LineRendererComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        // テキストも純粋な値（文字列・サイズ・色・整列）のみ。GPU 資源を持たない。
        ComponentData::TextComponent(d) => {
            world.insert(entity, TextComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::AnimatorComponent(d) => {
            world.insert(entity, AnimatorComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::LightComponent(d) => {
            world.insert(entity, LightComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::JointAttachComponent(d) => {
            world.insert(entity, JointAttachComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::ParticleEmitterComponent(d) => {
            world.insert(entity, ParticleEmitterComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::SkyboxComponent(d) => {
            world.insert(entity, SkyboxComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::TerrainChunkComponent(d) => {
            world.insert(entity, TerrainChunkComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::WaterLinkComponent(d) => {
            world.insert(entity, WaterLinkComponent::from_data(d.clone()));
            SlotApply::Applied
        }
        ComponentData::InteractionSourceComponent(d) => {
            world.insert(entity, InteractionSourceComponent::from_data(d));
            SlotApply::Applied
        }
        // カバーエミッタ（I3.1）は純粋な値の詰め替えだけで復元できる
        // （GPU 資源も CLR インスタンスも持たない）。
        ComponentData::CoverEmitterComponent(d) => {
            world.insert(entity, CoverEmitterComponent::from_data(d));
            SlotApply::Applied
        }
        ComponentData::ControlPointComponent(d) => {
            world.insert(entity, ControlPointComponent::from_data(d));
            SlotApply::Applied
        }
        // 旧フォーマット互換。to_data が生成しないためスナップショットには現れない。
        ComponentData::LegacyRigidbodyComponent(_) => SlotApply::NeedsRebuild,
    }
}

// ─── App 側の記録・適用 ───────────────────────────────────────

impl App {
    /// 編集対象の現在値を取得する（IPC ハンドラを呼ぶ「前」と「後」の両方で使う）。
    pub(super) fn field_edit_snapshot(
        &self,
        target: &FieldEditTarget,
    ) -> Option<FieldEditSnapshot> {
        let scene = self.scene.as_ref()?;
        let wl = self.active_world_line;
        match target {
            FieldEditTarget::None => None,
            FieldEditTarget::Slot { actor_dfs_id, slot_idx, .. } => {
                let mut c = 0u32;
                let actor = find_actor_by_dfs(&scene.actors, wl, *actor_dfs_id, &mut c)?;
                // 対象スロットだけをシリアライズする。アクタ全体（to_data）を使うと、
                // 同居する ModelComponent の全インスタンス行列まで毎回コピーすることになり、
                // スライダードラッグ中の連続更新で無視できない負荷になる。
                let slot = actor.slots().get(*slot_idx as usize)?;
                let mut data = slot_to_data(&scene.world, slot)?;
                strip_field_edit_irrelevant(&mut data);
                Some(FieldEditSnapshot::Slot(data))
            }
            FieldEditTarget::CanvasTransform { actor_dfs_id, .. } => {
                let mut c = 0u32;
                let actor = find_actor_by_dfs(&scene.actors, wl, *actor_dfs_id, &mut c)?;
                let ct = scene.world.get::<CanvasTransform>(actor.entity)?.clone();
                Some(FieldEditSnapshot::CanvasTransform(ct))
            }
            FieldEditTarget::ActorActive { actor_dfs_id } => {
                let mut c = 0u32;
                let actor = find_actor_by_dfs(&scene.actors, wl, *actor_dfs_id, &mut c)?;
                Some(FieldEditSnapshot::ActorActive(actor.active))
            }
            // シーン設定は世界線に属さない（シーン全体で 1 つ）。
            FieldEditTarget::SceneShading { .. } =>
                Some(FieldEditSnapshot::SceneShading(SceneShadingState::capture(scene))),
        }
    }

    /// IPC ハンドラ適用後に呼び、値が変わっていれば Undo 履歴へ積む。
    ///
    /// 直前の記録と同一フィールドかつ時間窓以内なら 1 コマンドへマージする
    /// （スライダー 1 ドラッグ＝Undo 1 回）。
    pub(super) fn field_edit_record(
        &mut self,
        target: &FieldEditTarget,
        before: FieldEditSnapshot,
    ) {
        let Some(after) = self.field_edit_snapshot(target) else {
            return;
        };
        let (merge_key, wl) = match target {
            FieldEditTarget::None => return,
            FieldEditTarget::Slot { merge_key, .. }
            | FieldEditTarget::CanvasTransform { merge_key, .. }
            | FieldEditTarget::SceneShading { merge_key } => {
                (merge_key.clone(), self.active_world_line)
            }
            // アクティブ切替はトグルなのでマージ対象にしない（キーを毎回変える必要はなく、
            // 同じキーでも 2 回目は値が異なるため実質マージされない）。
            FieldEditTarget::ActorActive { actor_dfs_id } => (
                format!("SetActorActive/{actor_dfs_id}"),
                self.active_world_line,
            ),
        };

        // 直前の記録へマージできるか判定する。
        let mergeable = self
            .field_edit_session
            .as_ref()
            .map(|s| {
                can_merge_field_edit(
                    &s.key,
                    &merge_key,
                    s.at.elapsed(),
                    s.past_len,
                    self.undo_history.past_len(),
                )
            })
            .unwrap_or(false);

        // マージ時は「セッション開始時点の値」を編集前として扱う。
        let before_effective = if mergeable {
            self.field_edit_session
                .as_ref()
                .map(|s| s.before.clone())
                .unwrap_or(before)
        } else {
            before
        };

        // 値が変わっていなければ何も積まない（クランプで弾かれた入力・無効 key 等）。
        //
        // マージ中の場合は「積んである途中経過のコマンド」を取り下げる必要がある。
        // 例: スライダーを 1.0 → 5.0 → 1.0 と 1 ドラッグで往復した場合、
        // ここで取り下げないと履歴に (1.0 → 5.0) が残り、Redo が
        // ユーザーが確定していない 5.0 を復活させてしまう。
        if before_effective.same_as(&after) {
            if mergeable {
                self.undo_history.pop_last();
                self.field_edit_session = None;
            }
            return;
        }

        // マージする場合は直前の自分のコマンドを取り下げて積み直す。
        if mergeable {
            self.undo_history.pop_last();
        }

        match (target, &before_effective, &after) {
            (
                FieldEditTarget::Slot { actor_dfs_id, slot_idx, .. },
                FieldEditSnapshot::Slot(b),
                FieldEditSnapshot::Slot(a),
            ) => {
                self.undo_history.record(Box::new(SlotFieldEditCommand {
                    world_line: wl,
                    actor_dfs_id: *actor_dfs_id,
                    slot_idx: *slot_idx,
                    before: b.clone(),
                    after: a.clone(),
                }));
            }
            (
                FieldEditTarget::CanvasTransform { actor_dfs_id, .. },
                FieldEditSnapshot::CanvasTransform(b),
                FieldEditSnapshot::CanvasTransform(a),
            ) => {
                self.undo_history.record(Box::new(CanvasTransformCommand {
                    world_line: wl,
                    dfs_id: *actor_dfs_id,
                    old_ct: b.clone(),
                    new_ct: a.clone(),
                }));
            }
            (
                FieldEditTarget::ActorActive { actor_dfs_id },
                FieldEditSnapshot::ActorActive(b),
                FieldEditSnapshot::ActorActive(a),
            ) => {
                self.undo_history.record(Box::new(ActorActiveCommand {
                    world_line: wl,
                    dfs_id: *actor_dfs_id,
                    before: *b,
                    after: *a,
                }));
            }
            (
                FieldEditTarget::SceneShading { .. },
                FieldEditSnapshot::SceneShading(b),
                FieldEditSnapshot::SceneShading(a),
            ) => {
                self.undo_history.record(Box::new(SceneShadingCommand {
                    before: b.clone(),
                    after:  a.clone(),
                }));
            }
            // 前後でスナップショット種別が変わることは無い（対象が同じなら型も同じ）。
            _ => return,
        }

        self.field_edit_session = Some(FieldEditSession {
            key: merge_key,
            before: before_effective,
            at: Instant::now(),
            past_len: self.undo_history.past_len(),
        });
    }

    /// Undo/Redo で 1 スロット分の値を復元する。
    ///
    /// まず既存エンティティへその場適用を試み、モデルの差し替えやスクリプトの
    /// 型変更など重い復元が要る場合のみ、既存の `rebuild_actor_slots` へ委譲する。
    pub(super) fn apply_slot_data(
        &mut self,
        wl: u32,
        actor_dfs_id: u32,
        slot_idx: u32,
        data: &ComponentSlotData,
    ) {
        // 対象スロットの entity と現在の種別を解決する。
        let slot_info = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) else {
                return;
            };
            actor.slots().get(slot_idx as usize).map(|s| s.entity)
        };
        let Some(entity) = slot_info else { return };

        // スナップショットから落としてある値（モデルのインスタンス配列）を、
        // 現在の生きている値で埋め戻してから適用する。
        let mut data = data.clone();
        {
            let Some(scene) = &self.scene else { return };
            restore_stripped_fields(&scene.world, entity, &mut data);
        }

        // その場適用を試みる。
        let outcome = {
            let Some(scene) = &mut self.scene else { return };
            apply_component_data_in_place(&mut scene.world, entity, &data.component)
        };

        if outcome == SlotApply::NeedsRebuild {
            // 重い復元が必要: 該当スロットだけ差し替えた全スロットデータで再構築する。
            let mut slots = self.snapshot_actor_slots(wl, actor_dfs_id);
            let idx = slot_idx as usize;
            if idx >= slots.len() {
                return;
            }
            slots[idx] = data.clone();
            self.rebuild_actor_slots(wl, actor_dfs_id, slots);
        }

        // スロット名・有効フラグも ComponentSlotData の一部なので復元する
        // （RENAME_COMPONENT_SLOT / SET_SLOT_ENABLED の Undo はここで効く）。
        // kind は Script ⇄ Placeholder の降格でのみ変わり、その場合は
        // 再構築側が正しい kind を設定済みなのでここでは触らない。
        if let Some(scene) = &mut self.scene {
            use crate::engine::core::app_base::app::find_actor_by_dfs_mut;
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c)
            {
                if let Some(s) = actor.slots_mut().get_mut(slot_idx as usize) {
                    s.name = data.name.clone();
                    s.enabled = data.enabled;
                }
            }
        }
    }

    /// フィールド編集のマージセッションを打ち切る。
    /// シーン切替・世界線切替など「編集の連続性が途切れる」タイミングで呼ぶ。
    pub(super) fn reset_field_edit_session(&mut self) {
        self.field_edit_session = None;
    }
}

/// 対象スロットの種別が期待どおりかを確認するための補助（テストで使用）。
#[cfg(test)]
pub(super) fn component_kind_of(data: &ComponentData) -> ComponentKind {
    match data {
        ComponentData::ModelComponent(_) => ComponentKind::Model,
        ComponentData::ScriptComponent(_) => ComponentKind::Script,
        ComponentData::CanvasComponent(_) => ComponentKind::Canvas,
        ComponentData::SpriteComponent(_) => ComponentKind::Sprite,
        ComponentData::SkinnedSpriteComponent(_) => ComponentKind::SkinnedSprite,
        ComponentData::InputMapComponent(_) => ComponentKind::InputMap,
        ComponentData::CameraComponent(_) => ComponentKind::Camera,
        ComponentData::PluginComponent(_) => ComponentKind::Plugin,
        ComponentData::ColliderComponent(_) => ComponentKind::Collider,
        ComponentData::Collider2dComponent(_) => ComponentKind::Collider2d,
        ComponentData::LegacyRigidbodyComponent(_) => ComponentKind::Collider,
        ComponentData::AudioComponent(_) => ComponentKind::Audio,
        ComponentData::AnimatorComponent(_) => ComponentKind::Animator,
        ComponentData::LightComponent(_) => ComponentKind::Light,
        ComponentData::JointAttachComponent(_) => ComponentKind::JointAttach,
        ComponentData::ParticleEmitterComponent(_) => ComponentKind::ParticleEmitter,
        ComponentData::SkyboxComponent(_) => ComponentKind::Skybox,
        ComponentData::TerrainChunkComponent(_) => ComponentKind::TerrainChunk,
        ComponentData::WaterVolumeComponent(_) => ComponentKind::WaterVolume,
        ComponentData::WaterLinkComponent(_) => ComponentKind::WaterLink,
        ComponentData::InteractionSourceComponent(_) => ComponentKind::InteractionSource,
        ComponentData::CoverEmitterComponent(_) => ComponentKind::CoverEmitter,
        ComponentData::ControlPointComponent(_) => ComponentKind::ControlPoint,
        ComponentData::LineRendererComponent(_) => ComponentKind::LineRenderer,
        ComponentData::TextComponent(_) => ComponentKind::Text,
    }
}

/// Play 中かどうか（Undo 記録の抑止条件）。呼び出し側の意図を明示するためのヘルパ。
pub(super) fn is_recording_suppressed(mode: RuntimeMode) -> bool {
    mode == RuntimeMode::Play
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::{
        LightComponent, PlaceholderScriptSlot, WaterVolumeComponent,
    };

    /// 分類関数: 代表的なフィールド編集コマンドが Slot 対象になること。
    #[test]
    fn field_edit_target_classifies_component_fields_as_slot() {
        let cmd = IpcCommand::SetWaterField {
            actor_dfs_id: 3,
            slot_idx: 1,
            key: "wave_amplitude".into(),
            value: "0.5".into(),
        };
        match field_edit_target(&cmd) {
            FieldEditTarget::Slot { actor_dfs_id, slot_idx, merge_key } => {
                assert_eq!((actor_dfs_id, slot_idx), (3, 1));
                assert!(merge_key.contains("wave_amplitude"));
            }
            other => panic!("Slot 対象になること: {other:?}"),
        }
    }

    /// 分類関数: 水面シェーディングアセットのパラメータ（W8.2）が
    /// スロット編集として分類され、パラメータ名でマージキーが分かれること。
    ///
    /// ここが `None` に落ちると、インスペクタでパラメータを触っても
    /// Ctrl+Z が効かなくなる（＝この機能だけ Undo の穴になる）。
    #[test]
    fn field_edit_target_classifies_water_shader_param() {
        let mk = |name: &str| field_edit_target(&IpcCommand::SetWaterShaderParam {
            actor_dfs_id: 3, slot_idx: 1, name: name.into(), value: "1,0,0,0".into(),
        });
        match mk("emission_color") {
            FieldEditTarget::Slot { actor_dfs_id, slot_idx, .. } => {
                assert_eq!((actor_dfs_id, slot_idx), (3, 1));
            }
            other => panic!("スロット編集として分類されること: {other:?}"),
        }
        assert_ne!(mk("emission_color"), mk("glow_boost"),
            "別パラメータの編集は別コマンドとしてまとめられること");
    }

    /// 分類関数: 「デフォルトに戻す」（`@reset` ボタン）も Undo 対象であり、
    /// **直前の値編集とはマージされない**こと。
    ///
    /// マージされてしまうと「スライダーを動かす → リセット」が 1 手に潰れ、
    /// Ctrl+Z でリセット直前の値へ戻れなくなる。
    #[test]
    fn field_edit_target_classifies_water_shader_param_reset_separately() {
        let reset = field_edit_target(&IpcCommand::ResetWaterShaderParam {
            actor_dfs_id: 3, slot_idx: 1, name: "glow_boost".into(),
        });
        let edit = field_edit_target(&IpcCommand::SetWaterShaderParam {
            actor_dfs_id: 3, slot_idx: 1, name: "glow_boost".into(), value: "1,0,0,0".into(),
        });
        match &reset {
            FieldEditTarget::Slot { actor_dfs_id, slot_idx, merge_key } => {
                assert_eq!((*actor_dfs_id, *slot_idx), (3, 1));
                assert!(merge_key.contains("glow_boost"), "{merge_key}");
            }
            other => panic!("スロット編集として分類されること: {other:?}"),
        }
        assert_ne!(reset, edit, "値編集とリセットは別コマンドとして積まれること");
    }

    /// 分類関数: カメラ添付シェーディングアセットのパラメータ（L3）が
    /// スロット編集として分類され、値編集・リセット・バインドが別コマンドに分かれること。
    ///
    /// ここが `None` に落ちると、カメラのシェーディングパラメータだけ Ctrl+Z が効かない
    /// という「この機能だけ Undo の穴」になる。
    #[test]
    fn field_edit_target_classifies_camera_shading_param() {
        let edit = field_edit_target(&IpcCommand::SetCameraShadingParam {
            actor_dfs_id: 2, slot_idx: 1, name: "toon_steps".into(), value: "3,0,0,0".into(),
        });
        match &edit {
            FieldEditTarget::Slot { actor_dfs_id, slot_idx, merge_key } => {
                assert_eq!((*actor_dfs_id, *slot_idx), (2, 1));
                assert!(merge_key.contains("toon_steps"), "{merge_key}");
            }
            other => panic!("スロット編集として分類されること: {other:?}"),
        }
        let reset = field_edit_target(&IpcCommand::ResetCameraShadingParam {
            actor_dfs_id: 2, slot_idx: 1, name: "toon_steps".into(),
        });
        let bind = field_edit_target(&IpcCommand::SetCameraShadingBinding {
            actor_dfs_id: 2, slot_idx: 1, name: "toon_steps".into(), binding: "A|B|c".into(),
        });
        assert_ne!(edit, reset, "値編集とリセットは 1 手にまとまらないこと");
        assert_ne!(edit, bind,  "値編集とバインド設定は 1 手にまとまらないこと");
        // 別パラメータは別コマンド（スライダー往復が他のパラメータを巻き込まない）。
        assert_ne!(edit, field_edit_target(&IpcCommand::SetCameraShadingParam {
            actor_dfs_id: 2, slot_idx: 1, name: "toon_rim_strength".into(),
            value: "1,0,0,0".into(),
        }));
    }

    /// 分類関数: シーン設定ウィンドウの「シェーダ」まわりが Undo 対象になること。
    ///
    /// シーン設定は長らく Undo の対象外だったが、シェーダのパラメータは
    /// スライダーで詰める性質上 Ctrl+Z が必須なので、専用の分類を持たせてある。
    #[test]
    fn field_edit_target_classifies_scene_shading_edits() {
        let asset = field_edit_target(&IpcCommand::SetSceneShadingAsset {
            path: "assets://shaders/toon.wgsl".into(),
        });
        let param = field_edit_target(&IpcCommand::SetSceneShadingParam {
            name: "toon_steps".into(), value: "3,0,0,0".into(),
        });
        let reset = field_edit_target(&IpcCommand::ResetSceneShadingParam {
            name: "toon_steps".into(),
        });
        let bind  = field_edit_target(&IpcCommand::SetSceneShadingBinding {
            name: "toon_light_boost".into(), binding: "Sun|MainLight|intensity".into(),
        });
        for t in [&asset, &param, &reset, &bind] {
            assert!(matches!(t, FieldEditTarget::SceneShading { .. }),
                "SceneShading として分類されること: {t:?}");
        }
        assert_ne!(param, reset, "値編集とリセットは別コマンド");
        assert_ne!(param, bind,  "値編集とバインド設定は別コマンド");
        assert_ne!(param, asset, "アセット差し替えと値編集は別コマンド");
        // 読み取り専用の問い合わせは対象外であること。
        assert_eq!(field_edit_target(&IpcCommand::GetSceneShadingParams), FieldEditTarget::None);
        // ビューポート／レンダリング設定は従来どおり対象外（理由は分類表のコメント参照）。
        assert_eq!(
            field_edit_target(&IpcCommand::SetSceneSettings { json: "{}".into() }),
            FieldEditTarget::None,
        );
    }

    /// シーン設定のシェーディング状態が set → undo → redo で往復すること。
    #[test]
    fn scene_shading_state_round_trips() {
        use crate::engine::core::app_base::scene::Scene;
        use crate::engine::core::app_base::undo::{Command, SceneShadingCommand, SceneShadingState};

        let mut scene = Scene::new("test");
        scene.shading_asset = Some("assets://shaders/toon.wgsl".into());
        let before = SceneShadingState::capture(&scene);

        scene.shading_params.insert("toon_steps".into(), [5.0, 0.0, 0.0, 0.0]);
        scene.shading_bindings.insert("toon_light_boost".into(), "Sun|MainLight|intensity".into());
        let after = SceneShadingState::capture(&scene);
        assert_ne!(before, after);

        let mut cmd = SceneShadingCommand { before: before.clone(), after: after.clone() };
        cmd.undo(&mut scene);
        assert!(scene.shading_params.is_empty(), "Undo で上書き値が消えること");
        assert!(scene.shading_bindings.is_empty(), "Undo でバインドも消えること");
        assert_eq!(scene.shading_asset.as_deref(), Some("assets://shaders/toon.wgsl"),
            "触っていないアセットパスは巻き添えにならないこと");
        cmd.execute(&mut scene);
        assert_eq!(scene.shading_params["toon_steps"], [5.0, 0.0, 0.0, 0.0], "Redo で戻ること");
    }

    /// 分類関数: 汎用の「デフォルトに戻す」（RESET_COMPONENT_FIELD）が
    /// Slot に分類され、**SET 系とマージキーが異なる**こと。
    ///
    /// マージされてしまうと「スライダーを動かす → ⟲ を押す」が 1 手に潰れ、
    /// Ctrl+Z でリセット直前の値へ戻れなくなる。
    #[test]
    fn field_edit_target_classifies_component_field_reset_separately() {
        let reset = field_edit_target(&IpcCommand::ResetComponentField {
            actor_dfs_id: 4, slot_idx: 2, field: "intensity".into(),
        });
        match &reset {
            FieldEditTarget::Slot { actor_dfs_id, slot_idx, merge_key } => {
                assert_eq!((*actor_dfs_id, *slot_idx), (4, 2));
                assert!(merge_key.contains("intensity"), "{merge_key}");
            }
            other => panic!("スロット編集として分類されること: {other:?}"),
        }
        // 同じアクタ・スロット・フィールドを指す値編集とはマージキーが異なること。
        let edit = field_edit_target(&IpcCommand::SetLightField {
            actor_dfs_id: 4, slot_idx: 2, key: "intensity".into(), value: "5".into(),
        });
        assert_ne!(reset, edit, "値編集とリセットは 1 手にまとまらないこと");
        // 別フィールドのリセットどうしも別コマンドであること。
        assert_ne!(reset, field_edit_target(&IpcCommand::ResetComponentField {
            actor_dfs_id: 4, slot_idx: 2, field: "range".into(),
        }));
    }

    /// 分類関数: 同じスロットでも別フィールドならマージキーが異なること。
    #[test]
    fn field_edit_merge_key_separates_fields() {
        let a = field_edit_target(&IpcCommand::SetLightField {
            actor_dfs_id: 0, slot_idx: 0, key: "intensity".into(), value: "1".into(),
        });
        let b = field_edit_target(&IpcCommand::SetLightField {
            actor_dfs_id: 0, slot_idx: 0, key: "range".into(), value: "1".into(),
        });
        assert_ne!(a, b, "フィールドが違えば別コマンドとして扱うこと");
    }

    /// 分類関数: 既に専用 Undo を持つ経路は対象外であること（二重記録の防止）。
    #[test]
    fn field_edit_target_excludes_paths_with_own_undo() {
        let excluded = [
            IpcCommand::SetActorTransform {
                dfs_id: 0, px: 0.0, py: 0.0, pz: 0.0,
                ex: 0.0, ey: 0.0, ez: 0.0, sx: 1.0, sy: 1.0, sz: 1.0,
            },
            IpcCommand::SetControlPointPos {
                actor_dfs_id: 0, slot_idx: 0, index: 0, x: 0.0, y: 0.0, z: 0.0,
            },
            IpcCommand::TerrainStrokeEnd,
            IpcCommand::EnterPlay,
            IpcCommand::Undo,
        ];
        for cmd in excluded {
            assert_eq!(
                field_edit_target(&cmd),
                FieldEditTarget::None,
                "専用 Undo / 非編集コマンドは記録対象外であること"
            );
        }
    }

    /// 契約テスト: ipc.rs の `Set*Field` 系 variant が
    /// すべて分類表（FIELD-EDIT-TABLE:SLOT）に載っていること。
    ///
    /// 網羅 match のおかげで「分類し忘れ」はコンパイルエラーになるが、
    /// 「対象外へ流し込んで無効化した」ことは検出できない。
    /// フィールド編集コマンドが黙って Undo 対象から外れることを防ぐ。
    #[test]
    fn all_set_field_commands_are_registered_in_slot_table() {
        let ipc_src = include_str!("../ipc.rs");
        let table_src = include_str!("field_edit.rs");
        let table = table_src
            .split("// <FIELD-EDIT-TABLE:SLOT>")
            .nth(1)
            .and_then(|s| s.split("// </FIELD-EDIT-TABLE:SLOT>").next())
            .expect("分類表のマーカーコメントが存在すること");

        // enum 定義の本体だけを対象にする（改行コードに依存しない走査）。
        let enum_body = ipc_src
            .split("pub enum IpcCommand {")
            .nth(1)
            .expect("IpcCommand の定義が存在すること");

        let mut missing = Vec::new();
        let mut found = 0usize;
        for line in enum_body.lines() {
            let t = line.trim();
            // "SetXxxField { ..." の形の variant 宣言行だけを拾う。
            let Some(name) = t.split(|c: char| !c.is_alphanumeric()).next() else { continue };
            if name.starts_with("Set") && name.ends_with("Field") && t.starts_with(name) {
                found += 1;
                if !table.contains(&format!("IpcCommand::{name} ")) {
                    missing.push(name.to_string());
                }
            }
        }
        assert!(
            missing.is_empty(),
            "SET_*_FIELD 系コマンドが Undo 分類表から漏れている: {missing:?}"
        );
        // 走査そのものが空振りして「常に成功するテスト」になっていないことを担保する。
        // 現状の SET_*_FIELD 系コマンド数（11 件）を下回ったら走査条件を疑うこと。
        assert!(found >= 11, "SET_*_FIELD 系コマンドの走査が機能していない（{found} 件）");
    }

    /// マージ規則: 同一フィールド・時間窓内・履歴無変化のときだけマージする。
    #[test]
    fn merge_rules() {
        assert!(can_merge_field_edit("k", "k", Duration::from_millis(10), 3, 3));
        assert!(!can_merge_field_edit("k", "other", Duration::from_millis(10), 3, 3),
            "別フィールドはマージしないこと");
        assert!(!can_merge_field_edit("k", "k", FIELD_EDIT_MERGE_WINDOW + Duration::from_millis(1), 3, 3),
            "時間窓を超えたらマージしないこと");
        assert!(!can_merge_field_edit("k", "k", Duration::from_millis(10), 3, 4),
            "間に別操作が積まれたらマージしないこと");
    }

    /// 適用: 水フィールドの set → undo（旧値の再適用）で値が戻ること。
    /// 併せて、揮発の現在水位（sim_level_y）が Undo で巻き添えにならないこと。
    #[test]
    fn water_field_round_trips_and_keeps_volatile_sim_level() {
        let mut world = World::new();
        let e = world.spawn();
        let mut w = WaterVolumeComponent::default();
        w.wave_amplitude = 0.25;
        world.insert(e, w);

        // 編集前スナップショット
        let before = ComponentData::WaterVolumeComponent(
            world.get::<WaterVolumeComponent>(e).unwrap().to_data(),
        );

        // 「インスペクタで編集」＋「Play 中に水位が入った」状態を作る
        {
            let w = world.get_mut::<WaterVolumeComponent>(e).unwrap();
            w.wave_amplitude = 9.0;
            w.sim_level_y = Some(42.0);
        }
        let after = ComponentData::WaterVolumeComponent(
            world.get::<WaterVolumeComponent>(e).unwrap().to_data(),
        );

        // Undo
        assert_eq!(apply_component_data_in_place(&mut world, e, &before), SlotApply::Applied);
        let w = world.get::<WaterVolumeComponent>(e).unwrap();
        assert_eq!(w.wave_amplitude, 0.25, "旧値へ戻ること");
        assert_eq!(w.sim_level_y, Some(42.0), "揮発の現在水位は Undo で消えないこと");

        // Redo
        assert_eq!(apply_component_data_in_place(&mut world, e, &after), SlotApply::Applied);
        assert_eq!(world.get::<WaterVolumeComponent>(e).unwrap().wave_amplitude, 9.0);
    }

    /// 適用: ライトの値が set → undo → redo で往復すること。
    #[test]
    fn light_field_round_trips() {
        let mut world = World::new();
        let e = world.spawn();
        let mut l = LightComponent::default();
        l.intensity = 1.0;
        world.insert(e, l);

        let before = ComponentData::LightComponent(
            world.get::<LightComponent>(e).unwrap().to_data(),
        );
        world.get_mut::<LightComponent>(e).unwrap().intensity = 7.5;
        let after = ComponentData::LightComponent(
            world.get::<LightComponent>(e).unwrap().to_data(),
        );

        apply_component_data_in_place(&mut world, e, &before);
        assert_eq!(world.get::<LightComponent>(e).unwrap().intensity, 1.0);
        apply_component_data_in_place(&mut world, e, &after);
        assert_eq!(world.get::<LightComponent>(e).unwrap().intensity, 7.5);
    }

    /// 適用: スクリプトの [SerializeField] 値が往復すること。
    /// CLR 無しの環境で動くよう Placeholder スロット（CLR 起動前の姿）で検証する。
    #[test]
    fn script_field_round_trips_on_placeholder() {
        let mut world = World::new();
        let e = world.spawn();
        world.insert(
            e,
            PlaceholderScriptSlot {
                script_path: "Player".into(),
                fields: Default::default(),
            },
        );

        let before = ComponentData::ScriptComponent(
            world.get::<PlaceholderScriptSlot>(e).unwrap().to_data(),
        );
        world
            .get_mut::<PlaceholderScriptSlot>(e)
            .unwrap()
            .fields
            .insert("speed".into(), "12".into());
        let after = ComponentData::ScriptComponent(
            world.get::<PlaceholderScriptSlot>(e).unwrap().to_data(),
        );

        apply_component_data_in_place(&mut world, e, &before);
        assert!(
            world.get::<PlaceholderScriptSlot>(e).unwrap().fields.is_empty(),
            "Undo でフィールドが編集前へ戻ること"
        );
        apply_component_data_in_place(&mut world, e, &after);
        assert_eq!(
            world.get::<PlaceholderScriptSlot>(e).unwrap().fields.get("speed").map(String::as_str),
            Some("12"),
            "Redo で編集後の値へ戻ること"
        );
    }

    /// 適用: スクリプトの型が変わる場合は再構築へフォールバックすること。
    #[test]
    fn script_type_change_needs_rebuild() {
        let mut world = World::new();
        let e = world.spawn();
        world.insert(
            e,
            PlaceholderScriptSlot { script_path: "A".into(), fields: Default::default() },
        );
        let other = ComponentData::ScriptComponent(crate::engine::components::ScriptComponentData {
            type_name: "B".into(),
            fields: Default::default(),
        });
        assert_eq!(
            apply_component_data_in_place(&mut world, e, &other),
            SlotApply::NeedsRebuild
        );
    }

    /// モデルのインスタンス配列はフィールド編集の Undo が触らないこと。
    ///
    /// インスペクタで cast_shadows を切り替えたあとにインスタンスを移動し、
    /// そこで Ctrl+Z を押しても「移動が巻き戻る」ことがあってはならない
    /// （インスタンス配置は TransformCommand 側の管轄）。
    #[test]
    fn model_instances_are_not_owned_by_field_edit_undo() {
        use crate::engine::components::{ModelComponent, ModelComponentData};

        // フィールド編集のスナップショットからインスタンスが落ちること
        let mut snap = ComponentSlotData {
            name: "Body".into(),
            enabled: true,
            component: ComponentData::ModelComponent(ModelComponentData {
                model_path: "a.gltf".into(),
                instances: vec![[[1.0; 4]; 4]; 3],
                meta: Vec::new(),
                groups: Vec::new(),
                next_group_id: 0,
                cast_shadows: true,
                material_overrides: Vec::new(),
                render_tag: 0,
            }),
        };
        strip_field_edit_irrelevant(&mut snap);
        match &snap.component {
            ComponentData::ModelComponent(d) => assert!(d.instances.is_empty()),
            _ => panic!("ModelComponent であること"),
        }

        // 復元時には World の現在値（＝ユーザーがその後に動かした配置）が埋め戻ること
        let mut world = World::new();
        let e = world.spawn();
        let mut mc = ModelComponent::empty();
        mc.instance_mats = vec![[[7.0; 4]; 4]; 2];
        world.insert(e, mc);

        restore_stripped_fields(&world, e, &mut snap);
        match &snap.component {
            ComponentData::ModelComponent(d) => {
                assert_eq!(d.instances.len(), 2, "現在の配置が使われること");
                assert_eq!(d.instances[0][0][0], 7.0);
            }
            _ => panic!("ModelComponent であること"),
        }
    }

    /// 種別ヘルパが ComponentData と ComponentKind を取り違えないこと（回帰防止）。
    #[test]
    fn component_kind_of_matches_data() {
        let d = ComponentData::LightComponent(LightComponent::default().to_data());
        assert_eq!(component_kind_of(&d), ComponentKind::Light);
    }
}
