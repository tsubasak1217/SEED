// ============================================================
//  water_shade_params.wgsl — 水面シェーディングアセットのパラメータ受け皿（Phase W8.2）
//
//  ## 役割（単一責任）
//  アセットが `//! param ...` で宣言したパラメータの**値**を、
//  水域（インスタンス）ごとに GPU から読むための storage バインディングと
//  アクセサ関数を 1 本ずつ提供する。それ以外は何もしない。
//
//  ## なぜ契約ファイルではなくここに置くのか
//  `water_shading_contract.wgsl` は「バインディングを一切持たない純粋な関数の集まり」
//  という不変条件を持つ（アセット作者に group/binding を意識させないため）。
//  値の運搬はレイアウトの都合そのものなので、契約を汚さないよう別ファイルにしてある。
//
//  ## なぜ WaterParams に足さないのか
//  `WaterParams`（`water_height_field.wgsl`）は ID パス・水面反射パス・
//  コースティクスパスと**共有**する構造体で、既に空きスロットがほぼ無い。
//  ここへ 8 本の vec4 を足すと、パラメータを一切使わない全パスのストライドまで
//  128 バイト増える。アセットのパラメータは**水面パスでしか読まない**ので、
//  独立した storage（group1 binding7）として水面パスだけに宣言する。
//  ID パス（`water_id.toml`）・反射パスはこのファイルを連結しないため一切影響を受けない。
//
//  ## 連結順序
//  `water_shading_contract.wgsl` の**直後**、アセット本体（または
//  `water_shade_dispatch.wgsl`）の**直前**。正典は `pipelines/water_surface.toml`。
//
//  ## 依存
//  なし（型と自前のバインディングだけで閉じている）。
// ============================================================

/// 1 水域が持てるパラメータの本数。
/// Rust 側 `water/shade_params.rs` の `WATER_SHADE_PARAM_MAX` と**必ず一致**させる
/// （ユニットテスト `rust_and_wgsl_param_slot_counts_match` が固定する）。
const WATER_SHADE_PARAM_SLOTS: u32 = 8u;

/// 水域 1 個ぶんのパラメータ値。
///
/// Rust 側 `WaterShadeParamBlock` と**要素数まで一致必須**。
/// `vec4` の配列なので std430 のアラインメント問題は構造的に起きない。
/// スロット番号は**アセット内の宣言の出現順**であり、Rust の生成コードが同じ順で添字を焼く。
struct WaterShadeParamBlock {
    /// スロットごとの値。color は xyz、スカラーは x を使う（残りは 0）。
    values: array<vec4<f32>, 8>,
}

/// 水域ごとのパラメータブロック配列（`u_water` と**同じインスタンス添字**で引ける）。
///
/// アセット未指定の水域・宣言の無いスロットは全ゼロが入っている
/// （＝読んでも 0 が返るだけで、既定経路の見た目は 1 ビットも変わらない）。
@group(1) @binding(7) var<storage, read> u_water_shade_params: array<WaterShadeParamBlock>;

/// インスタンス添字とスロット番号から生の `vec4` を読む。
///
/// 呼ぶのは**エンジンが生成するディスパッチ**だけで、アセット作者は使わない
/// （アセットは宣言した名前をそのまま値として読む）。
fn water_shade_param(instance: u32, slot: u32) -> vec4<f32> {
    // 範囲外スロットはゼロを返す（生成コードは常に範囲内だが、
    // 手書きで呼ばれても未定義動作にならないようにしておく）。
    if (slot >= WATER_SHADE_PARAM_SLOTS) {
        return vec4<f32>(0.0);
    }
    return u_water_shade_params[instance].values[slot];
}
