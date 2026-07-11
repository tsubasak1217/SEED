// ============================================================
// rt_shadow_off.wgsl  —  インラインレイトレ影 スタブ（非対応/従来経路用）
//
// RT 非対応 GPU のパイプライン（mesh.toml / skinned_mesh.toml）に連結される。
// acceleration_structure バインディングを一切宣言しないため、非対応デバイスでも
// シェーダ・パイプラインが従来と完全に同一の構成でコンパイルできる。
//
// 関数シグネチャは rt_shadow_on.wgsl と一致させること（shader_fragment.wgsl から
// 同じ呼び出しで参照される）。本スタブは常に「RT 無効」「遮蔽なし」を返し、
// フラグメントは従来のシャドウマップ経路（shadow.wgsl）へ分岐する。
// ============================================================

/// RT 影が有効か。非対応パイプラインでは常に false（→ シャドウマップ経路）。
fn rt_shadow_enabled() -> bool {
    return false;
}

/// 遮蔽率（1=非遮蔽/照射, 0=遮蔽/影）。スタブは常に非遮蔽。
/// シグネチャは rt_shadow_on.wgsl と一致させること（ソフト影の cone_radius / frag_xy を含む）。
fn rt_shadow_factor(
    origin:      vec3<f32>,
    n:           vec3<f32>,
    l:           vec3<f32>,
    tmax:        f32,
    cone_radius: f32,
    frag_xy:     vec2<f32>,
) -> f32 {
    return 1.0;
}
