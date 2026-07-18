// ============================================================
//  bindless_common.wgsl — バインドレス基盤の WGSL ミラー（フェーズ B1）
//
//  Rust 側 renderer/bindless.rs の BindlessInstanceRecord（repr(C), 64B, 16B 整列）と
//  バイト単位で一致する WGSL 定義。B1 ではまだどのパイプラインにも連結しない
//  （消費は B2/B3）。Rust の bindless::tests がこのファイルの構造体・定数を照合する。
//
//  B2 が消費するときの連結例（group 番号・binding は B2 で確定させる）:
//    @group(N) @binding(17) var          bindless_tex: binding_array<texture_2d<f32>>;
//    @group(N) @binding(18) var          bindless_smp: sampler;
//    @group(N) @binding(19) var<storage> bindless_uv:  array<vec2<f32>>;
//    @group(N) @binding(20) var<storage> bindless_idx: array<u32>;
//    @group(N) @binding(21) var<storage> bindless_rec: array<BindlessInstanceRecord>;
//  ヒット先の UV は bindless_uv[rec.uv_offset + bindless_idx[rec.index_offset + corner]] で引く。
// ============================================================

// 未登録／解放後を指す予約テクスチャインデックス（1x1 白ダミー）。Rust の
// BINDLESS_DUMMY_TEX_INDEX と一致させること（bindless::tests::wgsl_mirror_constants_match）。
const BINDLESS_DUMMY_TEX_INDEX: u32 = 0u;

// このインスタンスがバインドレス対象（UV/index/テクスチャを引ける）ことを示すフラグ。
// Rust の BINDLESS_FLAG_ELIGIBLE と一致させること。
const BINDLESS_FLAG_ELIGIBLE: u32 = 1u;

// TLAS custom_data（インスタンス番号）で引く 1 レコード（std430, 64B, 16B 整列）。
// フィールド順・オフセットは renderer/bindless.rs の BindlessInstanceRecord と一致させること。
//   offset 0  : avg_albedo        vec4<f32>  (.rgb=生アルベド, .a=α/transmission パック)
//   offset 16 : base_color_factor vec4<f32>
//   offset 32 : albedo_tex_index  u32
//   offset 36 : uv_offset         u32        (vec2 単位)
//   offset 40 : index_offset      u32        (u32 単位)
//   offset 44 : flags             u32
//   offset 48 : _pad0.._pad3      u32 ×4     (16B 整列パディング)
struct BindlessInstanceRecord {
    avg_albedo:        vec4<f32>,
    base_color_factor: vec4<f32>,
    albedo_tex_index:  u32,
    uv_offset:         u32,
    index_offset:      u32,
    flags:             u32,
    _pad0:             u32,
    _pad1:             u32,
    _pad2:             u32,
    _pad3:             u32,
};
