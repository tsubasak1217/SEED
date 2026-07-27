// ============================================================
// velocity_debug.wgsl — 速度バッファ（モーションベクタ）のデバッグ可視化
//
// ## 役割（単一責任）
// 速度 RT（`Rg16Float`・G-Buffer の RT4）を読み、人間が一目で正誤を判定できる
// 疑似カラーで HDR シーンへ**上書き**するだけのフルスクリーンパス。
// 環境変数 `SEED_DEBUG_VELOCITY=1` のときだけ実行される（既定は 0 コスト）。
//
// ## なぜ view_mode（lit/unlit/wireframe）に足さなかったのか
// `SceneViewMode` は `is_lit()` が false になると **deferred 自体が無効化**され
// （`deferred_active` の判定に入っている）、フォワード経路へ落ちる。
// 速度は G-Buffer パスでしか焼かれないため、view_mode に "velocity" を足すと
// 「速度表示にした瞬間に速度が生成されなくなる」という自己矛盾になる。
// 環境変数ゲートの追加パスなら Lit（＝deferred 有効）のまま上に重ねられる。
//
// ## 読み方（実機で 1 回見れば正しさが分かる形）
//   ・**灰色（0.5, 0.5）** … 速度ゼロ。カメラも物体も止まっている。
//   ・**赤 / シアン**       … 画面右向き / 左向きの移動（velocity.x の符号）。
//   ・**緑 / マゼンタ**     … 画面下向き / 上向きの移動（velocity.y の符号）。
//   ・**明るさ**            … 移動量の大きさ（VELOCITY_DEBUG_GAIN で増幅）。
//   ・**青**                … 速度が RG 表示のレンジを大きく超えた画素（飽和警告）。
//
// 期待される見え方:
//   - カメラを右へパンすると、画面全体が一様に「左向き（シアン寄り）」に染まる
//     （ワールドが相対的に左へ流れるため）。カメラ静止で灰色一色に戻る。
//   - 動いているアクタだけが背景と違う色で浮き上がる。
//   - Play 開始直後・Edit⇄Play 切替の 1 フレーム目が灰色一色になる
//     （prev=curr リセットが効いている証拠。ここが極彩色ならリセットが壊れている）。
// ============================================================

// ─── Group 0: 速度テクスチャ ────────────────────────────────
@group(0) @binding(0) var t_velocity: texture_2d<f32>;

/// 表示ゲイン。1 画素ぶん（= 1/解像度）の移動でも見えるように増幅する。
/// 0.05（画面の 5%）の移動でフルスケールに届く値。
const VELOCITY_DEBUG_GAIN: f32 = 20.0;

/// 飽和警告（青）を出す閾値（増幅後の絶対値）。
const VELOCITY_DEBUG_SATURATION: f32 = 1.0;

/// ゼロ速度の表示色（中間灰）。
const VELOCITY_DEBUG_MID: f32 = 0.5;

/// フルスクリーン三角形（頂点バッファなし。deferred_lighting.wgsl と同じ流儀）。
@vertex
fn vs_fullscreen(@builtin(vertex_index) vi: u32) -> @builtin(position) vec4<f32> {
    // (-1,-1) / (3,-1) / (-1,3) の巨大三角形で画面全体を覆う。
    let x = f32((vi << 1u) & 2u) * 2.0 - 1.0;
    let y = f32(vi & 2u) * 2.0 - 1.0;
    return vec4<f32>(x, y, 0.0, 1.0);
}

@fragment
fn fs_velocity_debug(@builtin(position) frag_pos: vec4<f32>) -> @location(0) vec4<f32> {
    // 速度 RT はライティング結果と同解像度・同ピクセル配置なので textureLoad で直読みする
    // （フィルタ不要＝サンプラーも不要）。
    let v = textureLoad(t_velocity, vec2<i32>(frag_pos.xy), 0).rg * VELOCITY_DEBUG_GAIN;

    // R = 右向き / G = 下向き を 0.5 中心の符号付き表示にする。
    let rg = clamp(vec2<f32>(VELOCITY_DEBUG_MID) + v * VELOCITY_DEBUG_MID,
                   vec2<f32>(0.0), vec2<f32>(1.0));

    // 表示レンジを超えた画素は青を混ぜて「飽和している」ことを明示する
    // （灰色一色に見えているのが「速度ゼロ」なのか「表示が飽和して潰れている」のかを
    //   取り違えないため）。
    let over = max(abs(v.x), abs(v.y)) - VELOCITY_DEBUG_SATURATION;
    let b    = clamp(over, 0.0, 1.0);

    return vec4<f32>(rg.x, rg.y, b, 1.0);
}
