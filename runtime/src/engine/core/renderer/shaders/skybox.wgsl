// ============================================================
//  skybox.wgsl — スカイボックス（天球）描画シェーダ
//
//  内向き UV 球メッシュ（位置のみ・単位半径）を 1 本の頂点バッファで受け取り、
//  「サンプル方向ベクトル → equirectangular（正距円筒）UV」へ変換して天球画像を貼る。
//
//  【配置モード（u_skybox.mode）】
//   - CameraLocked(0): 球をカメラ位置中心に置き（無限遠扱い）、深度を far に固定する。
//                      標準スカイボックス。深度書込は OFF（パイプライン側）。
//   - WorldAnchored(1): 球をアクターのワールド行列（model）で配置する。通常深度。
//                       内部へ入り込める実体の天球。
//
//  【サンプル方向】いずれのモードも「頂点のローカル位置を正規化した方向」でサンプルする。
//   - CameraLocked : ローカル位置＝ワールド方向（球はカメラ位置へ平行移動のみ）→ 天球は
//                    ワールドに固定され、カメラ回転で見回せる。
//   - WorldAnchored: ローカル方向でサンプルするため、テクスチャは球のローカル系にピン留めされ、
//                    アクター回転で天球ごと回る。
//
//  ライティングは適用しない（unlit）。出力 = テクスチャ色 × tint × intensity。
//  HDR メインパスへ描くため intensity>1 で発光的になり、Bloom と連動する。
// ============================================================

// ─── Group 0: カメラ（shader_common.wgsl の CameraUniform と同一レイアウト）─────
// メッシュ描画と同じ共有カメラ BindGroup をそのまま流用する（BGL は構造的に等価）。
struct CameraUniform {
    view_proj:  mat4x4<f32>,
    view:       mat4x4<f32>,
    position:   vec3<f32>,
    _pad:       f32,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 1: スカイボックスパラメータ＋テクスチャ ──────────────
// SkyboxUniform のレイアウトは Rust 側 renderer/skybox.rs の repr(C) 構造体と
// 厳密に一致させること（std140：vec3 tint の直後に intensity が同居し 16 バイト境界）。
struct SkyboxUniform {
    model:     mat4x4<f32>,   // WorldAnchored の配置行列（CameraLocked では単位行列）
    tint:      vec3<f32>,     // 色味（リニア RGB 乗算）
    intensity: f32,           // 強度（色への乗算）
    mode:      u32,           // 0=CameraLocked / 1=WorldAnchored
    _pad0:     u32,
    _pad1:     u32,
    _pad2:     u32,
}
@group(1) @binding(0) var<uniform> u_skybox:  SkyboxUniform;
@group(1) @binding(1) var          t_skybox:  texture_2d<f32>;
@group(1) @binding(2) var          s_skybox:  sampler;

// ─── 定数（マジックナンバー禁止）──────────────────────────────
// モードコード（skybox_component.rs の SkyboxMode::to_code() と一致）。
const SKYBOX_MODE_CAMERA_LOCKED: u32 = 0u;
const SKYBOX_MODE_WORLD_ANCHORED: u32 = 1u;
// 円周率と派生定数（equirectangular UV 変換用）。
const PI:     f32 = 3.14159265358979;
const INV_2PI: f32 = 0.15915494309189; // 1 / (2π)
const INV_PI:  f32 = 0.31830988618379; // 1 / π

// ─── 頂点入出力 ───────────────────────────────────────────────
struct VsIn {
    // 単位球のローカル位置（半径 1）。@location は "pos3" 頂点スロットと一致。
    @location(0) pos: vec3<f32>,
}

// ─── 頂点シェーダ ─────────────────────────────────────────────
struct VsOut4 {
    @builtin(position) clip_position: vec4<f32>,
    @location(0)       dir:           vec3<f32>,
}

@vertex
fn vs_main(in: VsIn) -> VsOut4 {
    var out: VsOut4;

    if (u_skybox.mode == SKYBOX_MODE_WORLD_ANCHORED) {
        // ワールド配置: model 行列で球を配置し通常深度で描く。
        let world_pos = (u_skybox.model * vec4<f32>(in.pos, 1.0)).xyz;
        out.clip_position = u_camera.view_proj * vec4<f32>(world_pos, 1.0);
        // ローカル方向でサンプル（テクスチャは球のローカル系に固定＝アクター回転で回る）。
        out.dir = in.pos;
    } else {
        // カメラ固定（無限遠）: 球をカメラ位置へ平行移動し、深度を far に固定する。
        let world_pos = u_camera.position + in.pos;
        var clip = u_camera.view_proj * vec4<f32>(world_pos, 1.0);
        // z = w にすると透視除算後の NDC.z = 1.0（far 平面）になり、常に最奥へ描かれる。
        clip.z = clip.w;
        out.clip_position = clip;
        // ワールド方向でサンプル（球はカメラ位置へ平行移動のみ＝天球はワールド固定）。
        out.dir = in.pos;
    }
    return out;
}

// ─── フラグメントシェーダ ─────────────────────────────────────
@fragment
fn fs_main(in: VsOut4) -> @location(0) vec4<f32> {
    // 方向ベクトルを正規化して equirectangular UV へ変換する。
    let d = normalize(in.dir);
    // 経度: atan2(z, x) を [0,1] に写像（u）。緯度: acos(y) を [0,1] に写像（v）。
    let u = atan2(d.z, d.x) * INV_2PI + 0.5;
    let v = acos(clamp(d.y, -1.0, 1.0)) * INV_PI;
    // ミップ derivative の継ぎ目アーティファクトを避けるため level 0 固定でサンプルする。
    let tex = textureSampleLevel(t_skybox, s_skybox, vec2<f32>(u, v), 0.0).rgb;
    // unlit: テクスチャ × tint × intensity（HDR。intensity>1 で発光的＝Bloom 連動）。
    let color = tex * u_skybox.tint * u_skybox.intensity;
    return vec4<f32>(color, 1.0);
}
