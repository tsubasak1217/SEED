// ============================================================
//  app/prefab_live_patch/ — Play 中のプレハブのホットリロード（当て直し・書き戻し。docs/editor_prefab.md 8 章）
//
//  【何のためにあるか】
//  Play 中に画面（プレハブ .actor）の見た目を詰めたい。Edit 用の再展開（prefab_ops.rs の PREFAB_REAPPLY_PATH）は
//  インスタンスを丸ごと作り直すので、Play 中に使うとスクリプトの CLR インスタンスが作り直されて OnStart が
//  走り直し、画面が初期化される（進行・入力中の値・スクリプトが積んだ行が消える）。ここでは
//  **動いているインスタンスを壊さずに、ファイルが変えたところだけを当てる**。
//
//  【2 つの入口】
//  ・PREFAB_LIVE_PATCH_PATH:{path}（ops.rs）… そのパスの Play 中のインスタンス全部へ「状態を保ったまま」当て直す。
//      スクリプトの Instantiate で Play 中に作られたインスタンス（ScreenStack が積んだ画面など）も対象
//      （script_scene_ops.rs が prefab_source / prefab_hash と印 spawned_by_script を付ける）。
//  ・PREFAB_WRITE_BACK:{actor_dfs}（write_back.rs）… Play 中のインスタンスの部分木を .actor へ書き戻し、
//      続けて同じパスのほかのインスタンスへ当て直す。スクリプトが生成した部分木は書かない。
//  どちらも Play 中だけ。Undo は積まず SCENE_MODIFIED も送らない（Play の世界は停止で Play 前の写しへ戻る）。
//  Edit のシーンへの反映は、Play 停止後にエディタが PREFAB_REAPPLY_PATH を送る（editor/src/Reload/PrefabPlayReapplyQueue.cs）。
//
//  【当て直しの流れ】（1 インスタンスにつき）
//   1. 要約（summary.rs）: 動いている部分木から突き合わせに要る情報だけを抜く
//   2. 計画（plan.rs。純粋な関数・単体テストあり）: 名前のパス（同名の兄弟は出現順）でノードを、
//      （種別, 同種内の順番）でスロットを突き合わせ、作る・消す・当てる・並べ替えるを決める
//   3. 適用（apply.rs）: スロットは field_edit::apply_component_data_in_place でその場で差し替え、
//      NeedsRebuild のスロットだけ作り直す。スクリプトは型名の並びが変わったノードだけ作り直す
//   4. 値の合成（merge.rs）: 元の版が分かれば 3 方向（ファイルが変えた欄だけ当てる）、分からなければ 2 方向
//   元の版は base_cache.rs が Play の間だけ控える（Play 開始・Instantiate・当て直し・書き戻しの直前）。
//
//  【サブモジュール】
//  ・summary.rs    … 動いている部分木の要約（LiveNode / LiveSlot）
//  ・plan.rs       … 計画（NodePlan / ChildStep / SlotPlan。純粋な関数）
//  ・merge.rs      … JSON の 3 方向の合成（純粋な関数）
//  ・base_cache.rs … 元の版の控え（PrefabBaseCache）
//  ・apply.rs      … 計画を World へ当てる
//  ・ops.rs        … PREFAB_LIVE_PATCH_PATH と控えの出し入れ（impl App）
//  ・write_back.rs … PREFAB_WRITE_BACK（impl App）
//  ・wire.rs       … 応答の文字列
// ============================================================

mod apply;
mod base_cache;
mod merge;
mod ops;
mod plan;
mod summary;
mod wire;
mod write_back;

// App のフィールドの型（app/mod.rs）
pub(super) use base_cache::PrefabBaseCache;
