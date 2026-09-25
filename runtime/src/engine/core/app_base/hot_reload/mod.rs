// ============================================================
//  hot_reload/mod.rs — 実行中の差し替え（アセット・シーン）の純粋な処理（docs/android.md §23）
//
//  【何をするか】
//  端末（Android）や PC で動いているゲームへ、エディタ／SeedAndroid が IPC で「差し替え」を頼む。
//    RELOAD_SCENE           … 今のシーンをディスク（Android は上書き層 files/assets → pak の順）から読み直す
//    RELOAD_SCENE:<相対パス> … 今のシーンがそのパスのときだけ読み直す（エディタがシーンを保存したとき）
//    RELOAD_ASSET:<相対パス> … そのアセットのキャッシュを捨て、種類に応じて「次に使うときに読み直す」か
//                              「今のシーンを読み直して取り込み直す」（表は asset_kind.rs）
//    RELOAD_SCRIPTS         … 従来の命令（PC はその場で再コンパイル、Android は files/bin の DLL を読み直す。app/script_ops.rs）
//  応答は RELOAD_DONE: / RELOAD_SKIPPED: / RELOAD_FAILED:（書式は wire.rs。RELOAD_SCRIPTS は従来の SCRIPTS_RELOADED:）。
//
//  【フレームの境界で適用する】
//  IPC の受信スレッドは命令を App へ積むだけで、App は 1 フレームの IPC をまとめて処理する（ipc_handler.rs）。
//  差し替えの要求はその場で適用せず、このフレームの分を HotReloadBatch に貯め、IPC の処理の最後（フレームの境界）に
//  1 回だけ適用する（シーンの読み直しは何件の要求があっても 1 回。batch.rs）。
//
//  【ファイル】
//    wire.rs       … 命令の解釈と応答の組み立て（書式の正典。エディタは editor/src/Ipc/RuntimeIpcCommands.cs）
//    asset_kind.rs … アセットの種類の表（拡張子・ファイル名 → 反映のしかた。データ）
//    asset_key.rs  … キャッシュのキー（assets:// ／ アセットルート内の絶対パス）が差し替え対象を指すかの照合
//    batch.rs      … フレーム内の要求のまとめ方（どの順で何をするか・誰に何を返すか）
//  App への適用（キャッシュの破棄・シーンの読み直し・応答の送信）は app/hot_reload_ops.rs。
// ============================================================

pub mod asset_key;
pub mod asset_kind;
pub mod batch;
pub mod wire;

pub use asset_kind::AssetRefresh;
pub use batch::{HotReloadBatch, HotReloadPlan, PlanContext, PlannedAsset};
pub use wire::HotReloadRequest;
