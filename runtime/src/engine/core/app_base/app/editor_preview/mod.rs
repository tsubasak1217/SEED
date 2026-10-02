// ============================================================
//  app/editor_preview/ — エディタの「Edit 上の画面プレビュー」（PREVIEW_*。docs/editor_screen_preview.md）
//
//  【何のためにあるか】
//  画面をすべて実行時にスクリプトがプレハブから積むアプリでは、Edit 上で画面が見えない。
//  そこで Edit 中に任意のプレハブ（.actor）を、任意のノードの下へ **保存されないプレビュー** として
//  差し込んで見られるようにする。プレビューの根のアクタは印 `editor_preview`（＋作り直しの材料
//  `EditorPreviewInfo`。structs/objects/actor/editor_preview.rs）を持ち、中のノードは印を持たない。
//
//  【DFS の不変条件（最重要）】
//  Undo の各コマンドは対象を entity ではなく (world_line, DFS 番号) で持ち、適用時に引き直す
//  （play_mode_ops.rs の enter_play の 0-a）。履歴に載らない木の変更が挟まると、それより前に積んだ
//  Undo の DFS 番号が別のアクタを指してしまい、Ctrl+Z が別のアクタの値を書き換える。
//  だから **プレビューの出し入れ・作り直しは必ず Undo 履歴へ前後の木の写しを 1 件積む**（undo.rs）。
//  ただし保存されるシーンの中身は変えないので、その Undo は「シーンを変えない操作」の印を持ち、
//  出し入れと Undo/Redo の後のヒエラルキーは「未保存にしない」知らせ（HIERARCHY_QUIET。
//  hierarchy_sync.rs の send_hierarchy_quiet）付きで送る。SCENE_MODIFIED は送らない。
//
//  【保存の濾過（ファイル・コピー・写しへは入れない）】
//  ・.scene の保存・Play 用一時シーン … core/app_base/scene.rs の Scene::to_json
//  ・.actor の保存・書き出し       … core/app_base/actor_file.rs の save（根がプレビューなら拒否）
//  ・コピー                       … app/clipboard.rs の do_copy（プレビューの中はコピーしない）
//  ・端末の写し（SNAPSHOT_SCENE）  … core/app_base/scene_snapshot/collect.rs の collect_snapshot_actors
//  逆に **メモリ上の写しには残す**: Undo の写し（actor_ops.rs の snapshot_actors_for_wl）・
//  Play 開始時の写し（play_snapshot.rs）・AI の SCENE_INFO。
//
//  【Play の扱い】
//  Play を始めるとき（play_mode_ops.rs の enter_play）は、Play 開始の写しを取った直後にシーン（世界線 0）の
//  プレビューを外す（Undo には積まない。ops.rs の remove_previews_for_play）。Play の中にはプレビューが無く、
//  Play を止めると Play 前の編集状態の復元（写しにはプレビュー入り）で戻る。
//
//  【エディタへの印】
//  ・HIERARCHY の各行: preview（部分木の中）・preview_root（根）・preview_source（根の中身のプレハブ。無ければ null）
//    （actor_utils.rs の collect_actor_nodes）
//  ・ACTOR_COMPONENTS: "editor_preview"（いちばん近い根の情報。外なら null。component_ops.rs → wire.rs）
//
//  【木の編集の拒否】プレビューへ／から既存の中身を動かす・中へ足す命令は捨てる（guard.rs。データを守るため）。
//
//  【サブモジュール】
//  ・wire.rs  … 応答（PREVIEW_ADDED / CLEARED / REFRESHED / ERROR）・インスペクタの断片・ログの接頭辞
//  ・tree.rs  … App に依存しない木の処理（名前のパス・DFS からの根・根の集め方・入れ子の控え・印の外し方）
//  ・undo.rs  … シーンを変えない印付きの木の写しの Undo（EditorPreviewTreeCommand）
//  ・guard.rs … 木の編集の拒否（判定は純粋関数）
//  ・ops.rs   … 出し入れ・作り直し・Play 開始で外す（impl App）
// ============================================================

mod guard;
mod ops;
mod tree;
mod undo;
mod wire;

// ── app の他のファイルが使うもの ──
// コピー（clipboard.rs）・インスペクタ（component_ops.rs）がプレビューの中かを引く
pub(super) use tree::{is_dfs_in_preview, preview_root_of_dfs};
// インスペクタの "editor_preview" の値（component_ops.rs）と、写しの閲覧専用の拒否の応答（snapshot_view_ops.rs）
pub(super) use wire::{format_error as format_preview_error, inspector_preview_json};
