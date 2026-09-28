// ============================================================
//  primitive2d — スクリプト用 2D プリミティブ描画（イミディエイトモード）
//
//  C# の `SEED.Draw.*` が「毎フレーム呼ばれるたびに図形を積む」方式の
//  2D 描画 API。Unity の Gizmos / Debug.DrawLine に近いが、こちらは
//  デバッグ用ではなく**ゲーム本編の UI** として使える描画物である。
//
//  【構成】
//   - queue.rs      : スクリプトが積むコマンドのスレッドローカルキューと型定義
//   - tessellate.rs : コマンド → 三角形メッシュ（純粋な 2D 幾何。GPU 非依存）
//   - tess_cache.rs : 三角形分割の使い回し（形が同じなら前のフレームの分割。色・行列は毎フレーム掛ける）
//   - 見た目の拡張（W2-8。queue.rs の PrimitiveStyle）: 画面の 1 画素のフェザー・線形グラデーション（頂点の色）。
//     グラフの線の下の塗り（PrimitiveKind::Area）とあわせて SEED.UI のグラフが使う。既定は従来と同じ見た目
//   - pass.rs       : NDC 変換・バッファ管理・wgpu パイプライン
//   - ../shaders/primitive2d.wgsl : 頂点は NDC 直値・フェザーでアンチエイリアス
//
//  【1 フレームの流れ】
//   1. スクリプト（Update 等）が `SEED.Draw.*` を呼ぶ
//      → FFI `ffi_draw_primitive`（scripting/host_api.rs）
//      → `queue::push_command`
//   2. frame_renderer が `queue::take_commands()` で引き取る（キューは空になる）
//   3. 座標空間（`space`）を `collect_sprite_items` が集めた行列マップで解決し、
//      レイヤー昇順に並べて `Primitive2dRenderer::push` へ流す
//   4. スプライトと同じパス／同じ順序規則で描画する
//
//  【座標空間】
//   - `space = null`     : スクリーンスペース（左上原点・px・Y 下向き）
//   - `space = Canvas..` : そのアクターのローカル空間。2D キャンバス配下なら
//     UI として最前面に、3D ワールドキャンバス配下ならワールド平面上に
//     （3D キャンバススプライトと同じ深度規則で）描かれる。
// ============================================================

/// 画面の外にある図形を積まない判定（外接矩形を射影して NDC の外なら捨てる。2026-09-28）
pub mod offscreen_cull;
pub mod pass;
pub mod queue;
/// 三角形分割の使い回し（同じ形なら前のフレームの分割を使う。2026-09-28）
pub mod tess_cache;
pub mod tessellate;

// よく使う型・関数だけを再エクスポートする
// （FEATHER_UNITS / PrimitiveSpaceMap / clear_commands などは `pass::` / `queue::` 経由で参照する）。
pub use pass::{
    Primitive2dRenderer, PrimitiveRange, PrimitiveSpaceCollector, PrimitiveSpaceTarget,
};
pub use queue::{
    push_command, take_commands, PrimitiveCommand, PrimitiveDrawMode, PrimitiveGradient, PrimitiveKind, PrimitiveStyle,
    Transform2d, MAX_POINTS_PER_PRIMITIVE, PRIM_EXTRA_FLOATS, PRIM_HEADER_FLOATS, PRIM_PARAM_FLOATS,
    PRIM_PARAM_FLOATS_STYLED, PRIM_STYLE_FLOATS,
};
