// ============================================================
//  primitive3d — スクリプト用 3D プリミティブ描画（イミディエイトモード）
//
//  C# の `SEED.Draw3D.*` が「毎フレーム呼ばれるたびにワールド空間の図形を積む」
//  方式の 3D 描画 API。釣り糸のたるみ・水面の距離リング・索敵範囲のワイヤ球など、
//  デバッグ表示にもゲーム本編の表現にも使える。
//
//  【構成】
//   - queue.rs : スクリプトが積むコマンドのスレッドローカルキューと型定義
//   - build.rs : コマンド → ワールド空間の線分／三角形／点（純幾何。GPU 非依存）
//   - pass.rs  : 近平面クリップ・バッファ管理・wgpu パイプライン
//   - ../shaders/primitive3d.wgsl : 頂点はワールド座標。線の太さと点の大きさは
//     頂点シェーダーが**画面 px**で押し出す（距離に依らず一定幅）。
//
//  【1 フレームの流れ】
//   1. スクリプト（Update 等）が `SEED.Draw3D.*` を呼ぶ
//      → FFI `ffi_draw_primitive3d`（scripting/host_api.rs）
//      → `queue::push_command`
//   2. frame_renderer が `queue::take_commands()` で引き取る（キューは空になる）
//   3. `depth_test` の有無で 2 レンジに分けて `Primitive3dRenderer::push`
//   4. メインパスの「半透明・3D キャンバススプライトの後／2D UI の前」で描画
//
//  【2D 版（primitive2d）との違い】
//   - 座標はワールド空間の Vector3（キャンバス・スクリーン座標の概念なし）
//   - 太さは画面 px（GPU 側で押し出す）。CPU では NDC 変換しない
//   - レイヤーではなく深度テストの有無とコマンド順で前後が決まる
// ============================================================

pub mod build;
pub mod pass;
pub mod queue;

// よく使う型・関数だけを再エクスポートする
// （MIN_SEGMENTS / clear_commands などは `build::` / `queue::` 経由で参照する）。
pub use pass::{Primitive3dRange, Primitive3dRenderer};
pub use queue::{
    push_command, take_commands, Primitive3dCommand, Primitive3dDrawMode, Primitive3dKind,
    MAX_POINTS_PER_PRIMITIVE3D, PRIM3D_EXTRA_FLOATS, PRIM3D_HEADER_FLOATS, PRIM3D_PARAM_FLOATS,
};
