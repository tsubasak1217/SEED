// ============================================================
//  GPU ユニフォームバッファ型
//
//  すべて repr(C) + bytemuck::Pod/Zeroable を実装し、
//  queue.write_buffer() で直接アップロードできる。
//
//  WGSL のアライメント規則（uniform address space）に準拠したレイアウト:
//    vec3<f32>   → align 16 (vec4 と同じ)
//    mat4x4<f32> → align 16, stride 64
// ============================================================

// ── カメラ (Group 0, Binding 0) ───────────────────────────────

/// カメラのビュー×プロジェクション行列一式。
///
/// WGSL レイアウト（計 224 bytes, Phase D3: G-Buffer デファード化 Phase A で末尾に
/// inv_view_proj を追加。160→224 bytes）:
/// | オフセット | フィールド     | サイズ |
/// |-----------|---------------|--------|
/// |   0       | view_proj     |  64    |
/// |  64       | view          |  64    |
/// | 128       | position      |  12    |
/// | 140       | _pad          |   4    |
/// | 144       | resolution    |   8    |
/// | 152       | _pad2         |   8    |
/// | 160       | inv_view_proj |  64    |
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct CameraUniform {
    pub view_proj:  [[f32; 4]; 4],
    pub view:       [[f32; 4]; 4],
    /// ワールド空間でのカメラ位置（スペキュラ計算用）
    pub position:   [f32; 3],
    pub _pad:       f32,
    /// ビューポートの解像度（ピクセル）。ギズモ太線計算に使用。
    pub resolution: [f32; 2],
    pub _pad2:      [f32; 2],
    /// 逆 ViewProjection 行列（NDC → ワールド座標の復元用）。
    ///
    /// Phase D3（G-Buffer デファード化）のライティングパス（deferred_lighting.wgsl）が、
    /// 深度バッファから読んだ NDC 座標をワールド座標へ復元するために使う
    /// （G-Buffer はワールド座標そのものを焼かない＝帯域節約のため、深度から逆算する）。
    /// `view_proj.inverse()` が特異行列で失敗した場合は呼び出し側が単位行列で
    /// フォールバックする（パニックさせない。Mat4x4::inverse() -> Option<Self> 参照）。
    /// 値は他のフィールドと同じ規約（列優先アップロード＝ `.transpose().data`）。
    pub inv_view_proj: [[f32; 4]; 4],
}

impl CameraUniform {
    pub fn identity() -> Self {
        let id = [
            [1.0, 0.0, 0.0, 0.0f32],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        Self { view_proj: id, view: id, position: [0.0; 3], _pad: 0.0,
               resolution: [1280.0, 720.0], _pad2: [0.0; 2], inv_view_proj: id }
    }
}

// ── モデル変換 (Group 1, Binding 0) ──────────────────────────

/// ノードのモデル変換行列 + 法線行列。
///
/// WGSL レイアウト（計 128 bytes）:
/// | オフセット | フィールド     | サイズ |
/// |-----------|---------------|--------|
/// |   0       | model         |  64    |
/// |  64       | normal_matrix |  64    |
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ModelUniform {
    /// モデル行列（ローカル空間→ワールド空間）
    pub model:         [[f32; 4]; 4],
    /// 法線変換行列 = transpose(inverse(model)) の 3x3 部分を 4x4 に拡張
    pub normal_matrix: [[f32; 4]; 4],
}

impl ModelUniform {
    pub fn identity() -> Self {
        let id = [
            [1.0, 0.0, 0.0, 0.0f32],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        Self { model: id, normal_matrix: id }
    }

    /// モデル行列から生成する（法線行列は自動計算）。
    pub fn from_matrix(model: [[f32; 4]; 4]) -> Self {
        let nm = normal_matrix_from_model(&model);
        Self { model, normal_matrix: nm }
    }
}

/// 法線変換行列 = cofactor(M_3x3) / det = transpose(inverse(M_3x3))
fn normal_matrix_from_model(m: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let (a, b, c) = (m[0][0], m[0][1], m[0][2]);
    let (d, e, f) = (m[1][0], m[1][1], m[1][2]);
    let (g, h, i) = (m[2][0], m[2][1], m[2][2]);

    let det = a*(e*i - f*h) - b*(d*i - f*g) + c*(d*h - e*g);
    if det.abs() < 1e-6 {
        return *m; // 特異行列フォールバック
    }
    let inv = 1.0 / det;

    [
        [ (e*i-f*h)*inv, -(d*i-f*g)*inv,  (d*h-e*g)*inv, 0.0],
        [-(b*i-c*h)*inv,  (a*i-c*g)*inv, -(a*h-b*g)*inv, 0.0],
        [ (b*f-c*e)*inv, -(a*f-c*d)*inv,  (a*e-b*d)*inv, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]
}

// ── マテリアル (Group 2, Binding 0) ──────────────────────────

/// PBR マテリアルパラメータ。
///
/// WGSL レイアウト（計 80 bytes。ガラス表現で transmission を追加し 64→80 へ拡張）:
/// | オフセット | フィールド         | サイズ |
/// |-----------|-------------------|--------|
/// |  0        | base_color_factor |  16    |
/// | 16        | metallic_factor   |   4    |
/// | 20        | roughness_factor  |   4    |
/// | 24        | alpha_cutoff      |   4    |
/// | 28        | has_base_color    |   4    |
/// | 32        | emissive_factor   |  12    |  ← vec3 align=16 の offset 32 は ✓
/// | 44        | has_normal_tex    |   4    |
/// | 48        | has_mr_tex        |   4    |
/// | 52        | has_occlusion_tex |   4    |
/// | 56        | has_emissive_tex  |   4    |
/// | 60        | ior               |   4    |  ← 旧 _pad を転用（Phase RT-Translucency）
/// | 64        | transmission      |   4    |  ← ガラス表現（透過率）で追加
/// | 68        | mr_tex_ignore     |   4    |  ← 旧 _pad0 を転用（MR テクスチャ無視トグル, 0/1）
/// | 72        | diffuse_transmission | 4  |  ← 旧 _pad1 を転用（拡散透過＝葉/布の逆光透け, 0..1）
/// | 76        | _pad2             |   4    |
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct MaterialUniform {
    pub base_color_factor:  [f32; 4],
    pub metallic_factor:    f32,
    pub roughness_factor:   f32,
    /// Mask モードのカットオフ値（0.0 の場合は破棄なし）
    pub alpha_cutoff:       f32,
    pub has_base_color_tex: u32,
    pub emissive_factor:    [f32; 3],
    pub has_normal_tex:     u32,
    pub has_mr_tex:         u32,
    pub has_occlusion_tex:  u32,
    pub has_emissive_tex:   u32,
    /// 屈折率（IOR, Phase RT-Translucency）。旧 _pad（offset 60）を転用。
    /// RT-Translucency 有効時、Blend 半透明のスクリーンスペース屈折で使う（1.0=屈折なし）。
    pub ior:                f32,
    /// 透過率（transmission, 0..1。ガラス表現）。アルファ（被覆）と分離した透け具合。
    /// 0.0=従来動作（後方互換）。半透明フラグメントの合成でフレネル配分に使う。
    pub transmission:       f32,
    /// MR テクスチャ無視トグル（旧 _pad0, offset 68 を転用）。0=無視しない（従来の乗算）、
    /// 1=無視（metallic/roughness factor をそのまま実効値にする）。surface_gather.wgsl の
    /// MR 採取 1 箇所が参照する（forward / G-Buffer 共通の合流点）。
    pub mr_tex_ignore:      u32,
    /// 拡散透過（diffuse_transmission, 0..1。葉・布・紙の逆光透け）。旧 _pad1（offset 72）を転用。
    /// 0.0=従来動作（後方互換）。lighting_eval.wgsl の逆光項（radiance×back×dt×albedo/PI）で使う。
    /// Surface.diffuse_transmission 経由で forward / G-Buffer（RT2.b）双方に届く。
    pub diffuse_transmission: f32,
    /// std140 の 16 バイトアラインへ構造体サイズを揃えるパディング（GPU では未使用）。
    pub _pad2:              f32,
}

// ── スキニング用ジョイント行列 (Group 3, Binding 0) ───────────

/// スケルタルアニメーション用ジョイント行列（最大 128 本）。
///
/// サイズ: 128 × 64 = 8192 bytes（uniform buffer 上限 65536 bytes 以内）
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct JointUniform {
    pub matrices: [[[f32; 4]; 4]; 128],
}

impl JointUniform {
    pub fn identity() -> Self {
        let id = [
            [1.0, 0.0, 0.0, 0.0f32],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        Self { matrices: [id; 128] }
    }
}

// ── GPU カリング用データ ───────────────────────────────────────

/// コンピュートシェーダが AABB テストに使うインスタンス単位データ。
///
/// WGSL レイアウト（32 bytes, align 16）:
/// | オフセット | フィールド  | サイズ |
/// |-----------|------------|--------|
/// |   0       | aabb_min   |  12    |
/// |  12       | _pad0      |   4    |
/// |  16       | aabb_max   |  12    |
/// |  28       | _pad1      |   4    |
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GpuCullData {
    pub aabb_min: [f32; 3],
    pub _pad0:    f32,
    pub aabb_max: [f32; 3],
    pub _pad1:    f32,
}

/// CPU から `indirect_cmds_buf` へ書き込む DrawIndexedIndirect コマンド。
///
/// wgpu / DX12 の DrawIndexedIndirect レイアウト（20 bytes）:
/// | オフセット | フィールド      | サイズ |
/// |-----------|----------------|--------|
/// |   0       | index_count    |   4    |
/// |   4       | instance_count |   4    |
/// |   8       | first_index    |   4    |
/// |  12       | base_vertex    |   4    |
/// |  16       | first_instance |   4    |
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct DrawIndexedIndirectCmd {
    pub index_count:    u32,
    pub instance_count: u32,
    pub first_index:    u32,
    pub base_vertex:    i32,
    pub first_instance: u32,
}

// ── デバッグ描画用頂点 ────────────────────────────────────────

/// ラインバッチ描画用頂点（位置 + 色）。
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ColorVertex {
    pub position: [f32; 3],
    pub color:    [f32; 4],
}

/// ギズモ太線描画用頂点。
///
/// 1 セグメントにつき 6 頂点（TriangleList で 2 三角形 = 1 クワッド）を生成する。
/// 頂点シェーダがスクリーン空間の垂直オフセットを計算して線幅を付与する。
///
/// WGSL レイアウト（計 48 bytes）:
/// | オフセット | フィールド | サイズ |
/// |-----------|-----------|--------|
/// |   0       | pos_a     |  12    |
/// |  12       | t         |   4    |  0.0=pos_a 側, 1.0=pos_b 側
/// |  16       | pos_b     |  12    |
/// |  28       | side      |   4    |  -1.0 or +1.0
/// |  32       | color     |  16    |
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GizmoVertex {
    pub pos_a: [f32; 3],
    pub t:     f32,
    pub pos_b: [f32; 3],
    pub side:  f32,
    pub color: [f32; 4],
}


// ============================================================
//  レイアウト検証テスト
// ============================================================
#[cfg(test)]
mod layout_tests {
    use super::*;

    /// CameraUniform が Rust/WGSL 双方で 224 バイト（160→224, inv_view_proj 追加分 +64）
    /// であることを固定する。ズレると shader_common.wgsl / deferred_lighting.wgsl の
    /// CameraUniform 定義との対応が崩れ、GPU が誤ったバイトを読む（静かな描画バグ）。
    #[test]
    fn camera_uniform_size_is_224_bytes() {
        assert_eq!(std::mem::size_of::<CameraUniform>(), 224);
    }

    /// MaterialUniform が Rust/WGSL 双方で 80 バイト（ガラス表現で 64→80 へ拡張）であることと、
    /// 主要フィールドのオフセットを固定する。ズレると shader_common.wgsl の MaterialUniform 定義
    /// との対応が崩れ、GPU が誤ったバイトを読む（静かな描画バグ）。
    #[test]
    fn material_uniform_layout_is_80_bytes() {
        assert_eq!(std::mem::size_of::<MaterialUniform>(), 80, "MaterialUniform は 80 バイト");
        // 代表オフセットの検証（base アドレスからのバイト差）。
        let m = MaterialUniform {
            base_color_factor: [0.0; 4], metallic_factor: 0.0, roughness_factor: 0.0,
            alpha_cutoff: 0.0, has_base_color_tex: 0, emissive_factor: [0.0; 3],
            has_normal_tex: 0, has_mr_tex: 0, has_occlusion_tex: 0, has_emissive_tex: 0,
            ior: 0.0, transmission: 0.0, mr_tex_ignore: 0, diffuse_transmission: 0.0, _pad2: 0.0,
        };
        let base = &m as *const _ as usize;
        let off = |p: *const f32| p as usize - base;
        assert_eq!(off(&m.ior),          60, "ior は offset 60");
        assert_eq!(off(&m.transmission), 64, "transmission は offset 64");
        assert_eq!(&m.mr_tex_ignore as *const u32 as usize - base, 68, "mr_tex_ignore は offset 68");
        assert_eq!(off(&m.diffuse_transmission), 72, "diffuse_transmission は offset 72");
    }
}
