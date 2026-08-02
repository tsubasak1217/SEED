// ============================================================
//  water_id.wgsl — 水面の ID パス（エディタのピッキング用）
//
//  ## 役割（単一責任）
//  水面の格子を ID バッファ（Rgba32Float。RGB=ワールド座標／A=raw アクタ ID）へ描き、
//  「Edit で水面をクリックしたら WaterVolume を持つアクタが選択される」を成立させる。
//  見た目には一切関与しない（色・屈折・泡は water_surface.wgsl の担当）。
//
//  ## 形状は共有モジュールと完全に同一（Phase W5.1）
//  W1〜W6 の波は「フラグメントで法線だけを揺らす」実装で頂点が動かなかったため、
//  ピック形状は静的な矩形でよかった。**W5.1 で水面が実際に上下するようになったので、
//  ID パスも同じ変位を掛けなければ「見えている波をクリックできない」**ことになる。
//
//  そこで頂点位置の生成（格子・放射状ワープ・川リボン・高さ場による変位）は
//  `water_height_field.wgsl` に切り出し、**本ファイルと water_surface.wgsl が
//  同じ関数 `water_grid_vertex()` を呼ぶ**。二重実装が存在しないので、
//  形状が食い違う余地が構造的に無い。
//
//  この結果、本パスも波紋（group2）とショアフィールド（group1 binding4/5）を
//  **頂点段から**読む。TOML の `vertex_visible_groups` で VERTEX 可視にしてある。
//
//  ## 深度整合（水面より手前の物体を優先して選択する）
//  水面描画本体はシェーダ内の手動深度テストを使うが、ID パスは深度アタッチメントを
//  持つ（メインパスのシーン深度を Load 済み）。したがってここでは **通常のハードウェア
//  深度テスト**（depth_compare = LessEqual / depth_write = false）に任せる:
//    ・水面より手前に不透明物 → 深度で落ちる  → 手前の物体が選択される
//    ・水面より奥に不透明物   → 深度を通過し、後から描く水面が上書き → 水面が選択される
//  水面は深度を書かない（描画順の後段にいるギズモ等の判定を汚さない）。
//
//  ## バインディング規約
//    group 0: カメラ（CameraUniform, binding 0。他パスと同一の BindGroup を共有）
//    group 1: binding0 = 水パラメータ配列（**共有モジュール**。water_surface と同一バッファ）
//             binding4/5 = ショアフィールド配列とサンプラー（**共有モジュール**）
//    group 2: インタラクションフィールド一式（**共有モジュール**）
//  ※ binding1〜3（屈折の背景・シーン深度）は本パスでは宣言しない。
//     リフレクションは宣言された binding だけを拾うので、BindGroup も
//     water_surface とは別に（binding 0/4/5 だけで）作ること。
// ============================================================

// ─── Group 0: カメラ ─────────────────────────────────────────
//
// クアッド生成に必要なのは view_proj と time（波の位相）だけだが、
// Rust 側 `uniforms::CameraUniform` の先頭レイアウトと一致していなければならない
// （このシェーダはその前半だけを読む）。
struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    /// ゲーム内累計時間（秒）。**頂点変位の位相に使う**ので水面パスと同じ値であること
    /// （ここがズレると「見えている波」と「クリックできる波」が別の時刻になる）。
    time:      f32,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── 頂点シェーダ ────────────────────────────────────────────

struct WaterIdVsOut {
    @builtin(position) clip:      vec4<f32>,
    @location(0)       world_pos: vec3<f32>,
    /// raw アクタ ID（補間してはならない離散値なので flat）
    @location(1) @interpolate(flat) actor_id: u32,
}

/// 頂点バッファ無しで水面の格子頂点を生成する（water_surface.wgsl の vs_water と同一形状）。
@vertex
fn vs_water_id(
    @builtin(vertex_index)   vi: u32,
    @builtin(instance_index) ii: u32,
) -> WaterIdVsOut {
    let p = u_water[ii];
    let v = water_grid_vertex(p, vi, u_camera.time);

    var out: WaterIdVsOut;
    out.clip      = u_camera.view_proj * vec4<f32>(v.world, 1.0);
    out.world_pos = v.world;
    out.actor_id  = p.actor_id.x;
    return out;
}

// ─── フラグメントシェーダ ────────────────────────────────────

/// RGB にワールド座標（D&D のスポーン位置に使われる＝水面へのドロップが水面上に落ちる）、
/// A に raw アクタ ID のビットパターンを書き込む（ID パス共通規約）。
@fragment
fn fs_water_id(in: WaterIdVsOut) -> @location(0) vec4<f32> {
    return vec4<f32>(in.world_pos, bitcast<f32>(in.actor_id));
}
