// ============================================================
// velocity_common.wgsl — 速度バッファ用「前フレームのインスタンス行列」バインディング
//
// ## 役割（単一責任）
// group 4 binding 0（前フレームのモデル行列配列）の宣言だけを持つ。
// 速度の計算式は velocity_math.wgsl（純関数・バインディングなし）にあり、
// 本ファイルを連結するシェーダは velocity_math.wgsl も併せて連結すること。
//
// ## 連結してよいのは G-Buffer のメッシュ系だけ
// 本ファイルは **group 4 binding 0** を占有する。フォワード系（mesh / skinned /
// transparent）は group 4 をライト（light_common.wgsl）で使っているため、
// 本ファイルを連結してはならない。G-Buffer 書き込みパスはライト情報を必要と
// しないので group 4 が丸ごと空いており、そこを速度用に転用している
// （新しい bind group 番号を増やさない＝max_bind_groups=5 の制約内に収める）。
// 草（grass_gbuffer.wgsl）は静的扱いで前フレーム行列が不要なため連結しない。
// ============================================================

// ============================================================
//  Group 4: 前フレームのインスタンス行列（G-Buffer パス専用）
// ============================================================

/// 前フレームのモデル行列（Rust `uniforms::PrevModelUniform`・64 バイトと一致必須）。
struct PrevModelUniform {
    /// 前フレームのモデル行列（ローカル空間 → ワールド空間・列優先）。
    prev_model: mat4x4<f32>,
}

/// ノードごとの「前フレーム」ワールド行列配列。
///
/// 添字は現行の `u_instances`（group 1）と**完全に同じ compact インスタンス添字**
/// （`@builtin(instance_index)`）。同順であることは Rust 側
/// `InstancedModelBatch::update` が両配列を同一ループで詰めることで保証する。
/// 静止しているインスタンスでは `prev_model == model` になり、
/// 速度は自動的に「カメラ由来ぶんだけ」へ縮退する（分岐不要・二重計算なし）。
@group(4) @binding(0) var<storage, read> u_prev_instances: array<PrevModelUniform>;

