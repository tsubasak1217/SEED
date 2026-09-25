// ============================================================
//  scene_snapshot/mod.rs — シーンの写し（いまの世界を .scene に書き出す）と、写しの閲覧（エディタの編集用ランタイム）の
//                          純粋な処理（docs/android.md §20.17・§21.13）
//
//  【何をするか】
//  Android の端末で動いているゲームを実行バーで一時停止したとき、エディタは端末のシーンの「いまの状態」
//  （スクリプトが生成したアクタを含む全アクタ・現在の Transform とコンポーネントの値・メインカメラの位置と向き）を
//  IPC で書き出させて PC へ取り出し、シーンパネル（PC の編集用ランタイム）に閲覧専用で読み込む。
//    SNAPSHOT_SCENE:<パス>          … Play 中のランタイム（端末のアプリ・PC の Play）が、いまの世界を既存のシーン形式で書く
//    SNAPSHOT_VIEW_BEGIN:<パス>     … エディタの編集用ランタイムが、編集中のシーンをメモリへ退避してから写しを読み込む
//    SNAPSHOT_VIEW_END              … 編集用ランタイムが、退避した編集中のシーンへ戻す
//  応答の書式は wire.rs（エディタは editor/src/SceneSnapshot/SceneSnapshotWire.cs）。
//
//  【ファイル】
//    wire.rs    … 命令の解釈と応答の組み立て（書式の正典）
//    collect.rs … 書き出すアクタの集め方（保存できないアクタを飛ばす）とメインカメラの姿勢（GPU 不要・単体テスト付き）
//  App への適用（書き出し・退避・読み込み・戻し・閲覧専用の拒否）は app/scene_snapshot_ops.rs と app/snapshot_view_ops.rs。
// ============================================================

pub mod collect;
pub mod wire;

pub use collect::{collect_snapshot_actors, CollectedActors};
pub use wire::{CameraPose, RestoreKind, SnapshotCommand, ViewFailStage};
