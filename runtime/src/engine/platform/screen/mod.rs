// ============================================================
//  platform/screen/ — 実行時に更新される画面情報（安全領域・画面の向き・DPI）
//
//  【構成】
//    report      … OS から届く報告（安全領域の各辺の距離・表示の回転・報告時の描画面の大きさ）の保持。
//                  Android の糊が UI スレッドから書き、エンジンがフレームごとに読む（Mutex）
//    orientation … 画面の向き（ScreenOrientation）の判定（回転＋縦横、または縦横比だけ）
//    snapshot    … スクリプト（SEED.Screen）へ見せる 1 フレーム分の値を作る純関数
//                  （描画ターゲット座標への写像・レターボックス・DPI）
//
//  【流れ】
//    Android: MainActivity（WindowInsets・Display.getRotation）→ JNI → report::submit
//    毎フレーム: App（app/screen_publish.rs）が report::select_for_frame と描画ターゲットの寸法から
//               ScreenSnapshot::compute → スクリプトへ公開（core/scripting/screen_bridge.rs）
//    デスクトップ: 報告は来ない。全画面が安全領域・向きはウィンドウの縦横比・DPI は表示倍率 × 96
//
//  特性表（mod.rs の PlatformTraits）がコンパイル時定数なのに対し、ここは実行中に変わる値を扱う。
//  全体像は docs/android.md「画面の向きと安全領域」。
// ============================================================

/// 画面の向き（ScreenOrientation）と、その判定の純関数。
pub mod orientation;
/// OS から届く画面の報告（安全領域・回転）の保持。
pub mod report;
/// スクリプトへ見せる 1 フレーム分の画面情報を作る純関数。
pub mod snapshot;

pub use orientation::ScreenOrientation;
pub use report::{EdgeInsets, ScreenReport};
pub use snapshot::{ScreenRect, ScreenSnapshot, SnapshotInputs};
