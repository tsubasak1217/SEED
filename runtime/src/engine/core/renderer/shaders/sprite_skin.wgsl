// ============================================================
//  sprite_skin.wgsl — 2D メッシュ変形スキニング（コンピュート）
//
//  1 スレッド = 1 頂点。バインドポーズ頂点にボーンパレットを適用し、
//  **変形後の頂点を `sprite_vertex` レイアウト（pos.xy + uv.xy = 16 bytes）で**
//  出力する。出力バッファはそのまま既存のスプライト描画パイプライン／
//  キャンバス ID パスの slot0 頂点バッファとして使える
//  （＝ 描画側に新しいシェーダを 1 本も足さずにメッシュ描画へ拡張できる）。
//
//  group 0 binding 0: バインドポーズ頂点（read-only storage・メッシュ単位で共有）
//  group 0 binding 1: ボーンパレット（read-only storage・インスタンス単位）
//  group 0 binding 2: パラメータ（uniform）
//  group 0 binding 3: 変形後頂点（read-write storage・インスタンス単位）
//
//  【ボーンパレットの持ち方】
//  2D アフィン変換は 6 成分しか要らないため、mat4x4 ではなく
//  **1 ボーン = vec4 × 2**（r0 = (a, b, tx, 0) / r1 = (c, d, ty, 0)）で持つ。
//  変換式は  x' = a*x + b*y + tx ,  y' = c*x + d*y + ty 。
//  これは Rust 側の行優先 [[f32;4];4] の 0/1 行目をそのまま並べたものである。
// ============================================================

/// 1 頂点あたりのボーン影響本数（Rust 側 MAX_BONE_INFLUENCES と一致必須）。
const MAX_BONE_INFLUENCES: u32 = 4u;
/// ワークグループサイズ（Rust 側 SPRITE_SKIN_WORKGROUP_SIZE と一致必須）。
const WORKGROUP_SIZE: u32 = 64u;

/// バインドポーズ頂点（Rust 側 SpriteSkinVertex と 1:1・48 bytes）。
struct SkinVertex {
    /// スプライトローカル位置（キャンバスピクセル）
    pos:     vec2<f32>,
    /// UV 座標
    uv:      vec2<f32>,
    /// 影響ボーンインデックス
    bones:   vec4<u32>,
    /// 正規化済みウェイト（合計 1.0。読込時に正規化済みのため再正規化しない）
    weights: vec4<f32>,
}

/// ディスパッチのパラメータ。
struct SkinParams {
    /// 頂点数（範囲外スレッドの早期 return に使う）
    vertex_count: u32,
    /// ボーン数（パレット範囲チェック用）
    bone_count:   u32,
    _pad0:        u32,
    _pad1:        u32,
}

@group(0) @binding(0) var<storage, read>       bind_vertices: array<SkinVertex>;
@group(0) @binding(1) var<storage, read>       bone_palette:  array<vec4<f32>>;
@group(0) @binding(2) var<uniform>             params:        SkinParams;
/// 出力: xy = 変形後位置, zw = UV（sprite_vertex レイアウトそのもの）
@group(0) @binding(3) var<storage, read_write> out_vertices:  array<vec4<f32>>;

/// パレットの 1 ボーンぶん（vec4 × 2）で点を変換する。
fn apply_bone(bone: u32, p: vec2<f32>) -> vec2<f32> {
    let r0 = bone_palette[bone * 2u];
    let r1 = bone_palette[bone * 2u + 1u];
    return vec2<f32>(
        r0.x * p.x + r0.y * p.y + r0.z,
        r1.x * p.x + r1.y * p.y + r1.z,
    );
}

@compute @workgroup_size(WORKGROUP_SIZE)
fn cs_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let vi = gid.x;
    if vi >= params.vertex_count {
        return;
    }

    let v = bind_vertices[vi];
    var acc = vec2<f32>(0.0, 0.0);
    for (var k: u32 = 0u; k < MAX_BONE_INFLUENCES; k = k + 1u) {
        let w = v.weights[k];
        if w == 0.0 {
            continue;
        }
        let b = v.bones[k];
        // 範囲外ボーンは読込時に弾かれているが、GPU 側でも念のため無視する
        // （不正データが混ざっても未定義読み出しにならないようにする）
        if b >= params.bone_count {
            continue;
        }
        acc = acc + apply_bone(b, v.pos) * w;
    }

    out_vertices[vi] = vec4<f32>(acc, v.uv);
}
