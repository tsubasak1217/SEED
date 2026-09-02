// ============================================================
//  hint_plate.wgsl — 角丸の単色プレート（操作ガイドの背景板）
//
//  【何のためのシェーダーか】
//  カーソル脇の操作ガイド（font/screen_hint.rs）の文字の下に敷く、
//  半透明の黒い角丸クアッドを描く。明るい背景（雪原・空・白い床）でも
//  文字が沈まないようにするための「読み取り面」を作るのが目的。
//
//  【なぜ SDF で角を丸めるのか】
//  角丸を「小さな矩形を重ねて」作ると、半透明どうしが重なった部分だけ
//  濃くなってムラになる。1 枚のクアッドの中で符号付き距離（SDF）を評価すれば
//  重なりが原理的に発生せず、アンチエイリアスも 1 行で付けられる。
//
//  【座標系】
//  頂点は NDC を直に受け取る（スクリーン空間 UI なのでカメラ行列は不要）。
//  角丸の評価だけはピクセル単位で行いたいので、矩形中心からの
//  ピクセルオフセット（local）と半サイズ（half）を別途渡す。
// ============================================================

struct VertIn {
    /// NDC 座標（-1..1）。
    @location(0) position : vec2<f32>,
    /// 矩形中心からのオフセット [px]（角丸 SDF の評価に使う）。
    @location(1) local    : vec2<f32>,
    /// 矩形の半サイズ [px]。
    @location(2) half     : vec2<f32>,
    /// 角丸の半径 [px]（x のみ使用。y はアライメント用の詰め物）。
    @location(3) radius   : vec2<f32>,
    /// プレートの色（RGBA、ストレートアルファ）。
    @location(4) color    : vec4<f32>,
}

struct VertOut {
    @builtin(position) clip_pos : vec4<f32>,
    @location(0)       local    : vec2<f32>,
    @location(1)       half     : vec2<f32>,
    @location(2)       radius   : vec2<f32>,
    @location(3)       color    : vec4<f32>,
}

@vertex
fn vs_main(in: VertIn) -> VertOut {
    var out : VertOut;
    out.clip_pos = vec4<f32>(in.position, 0.0, 1.0);
    out.local    = in.local;
    out.half     = in.half;
    out.radius   = in.radius;
    out.color    = in.color;
    return out;
}

/// 角丸矩形の符号付き距離（中心原点・半サイズ h・角半径 r）。
/// 内側で負、境界で 0、外側で正。
fn sd_round_rect(p: vec2<f32>, h: vec2<f32>, r: f32) -> f32 {
    let d = abs(p) - (h - vec2<f32>(r, r));
    return length(max(d, vec2<f32>(0.0, 0.0))) + min(max(d.x, d.y), 0.0) - r;
}

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    // 角半径は半サイズを超えられない（超えると SDF が破綻して穴が開く）。
    let r = min(in.radius.x, min(in.half.x, in.half.y));
    let d = sd_round_rect(in.local, in.half, r);
    // 1px 幅のアンチエイリアス（境界の内側 1px でアルファを 0 まで落とす）。
    let a = in.color.a * clamp(0.5 - d, 0.0, 1.0);
    if a < 0.004 { discard; }
    return vec4<f32>(in.color.rgb, a);
}
