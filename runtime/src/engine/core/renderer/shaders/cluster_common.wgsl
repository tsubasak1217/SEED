// ============================================================
// cluster_common.wgsl  —  Clustered Lighting の共有定義（Phase C1）
//
// ## 役割（単一責任）
// クラスタ（視錐台を X×Y タイル × Z 深度スライスへ分割した 3D フロクセル）の
// **定数・構造体・純関数だけ**を持つ。バインディングは一切宣言しない。
//
// これは意図的な制約である:
//   - 本ファイルは「クラスタ構築 compute（cluster_build.wgsl, group 0 に自前の
//     バインディング）」と「メッシュのフラグメント（shader_common.wgsl, group 4）」の
//     **両方**へ連結される。バインディングを持たせると、リフレクション
//     （pipeline_config::reflect_bgls は module.global_variables を無条件に走査する）で
//     互いのグループが混入し、破綻したパイプラインレイアウトになる。
//   - ゆえに「共有ロジック（本ファイル）」と「バインディング（各利用側）」を分離する。
//
// ## Rust 側との対応
// 本ファイルの `const` は renderer/clustered.rs の Rust 定数と**必ず一致**させること
// （ズレると「ライトが誤って落ちて画面が暗くなる」という追跡困難なバグになる）。
// 一致は clustered.rs のユニットテスト（本ファイルを include_str! して定数値を検証）が
// 担保する。同ファイルには本ファイルの幾何ロジック（Z スライス／クラスタ AABB／
// 球-AABB 交差）の Rust ミラーとその境界ケーステストもある。
// ============================================================

// ─── ライト種別コード ────────────────────────────────────────
//
// Rust: lighting.rs の LIGHT_KIND_*、LightKind::to_code() と一致させること。
// （旧 shader_common.wgsl から本ファイルへ移設した。クラスタ構築 compute でも
//   種別ごとのバウンディング体積を求めるのに必要で、compute は shader_common を
//   連結できない＝group 2/4 のバインディングが混入するため。）
const LIGHT_KIND_DIRECTIONAL: u32 = 0u;
const LIGHT_KIND_POINT:       u32 = 1u;
const LIGHT_KIND_SPOT:        u32 = 2u;
const LIGHT_KIND_RECT:        u32 = 3u;

// ─── クラスタ分割数 ──────────────────────────────────────────
//
// 16×9（16:9 画面のタイル）× 24 スライス = 3456 クラスタ（Doom 2016 準拠）。
// タイル数を増やすほどカリングは効くが compute のスレッド数とグリッドバッファが増える。

/// 画面横方向のタイル数。
const CLUSTER_TILES_X: u32 = 16u;
/// 画面縦方向のタイル数。
const CLUSTER_TILES_Y: u32 = 9u;
/// 奥行き（ビュー空間 Z）のスライス数。
const CLUSTER_SLICES_Z: u32 = 24u;
/// クラスタ総数（= X * Y * Z）。
const CLUSTER_COUNT: u32 = 3456u;

/// 1 クラスタが保持できるライト数の上限。
/// これを超えて重なったライトは、そのクラスタでは評価されない（＝暗くなる）。
/// 実用上 256 は十分に大きいが、超えうる設計にするならここを増やすこと
/// （グローバルインデックスプールの容量 = CLUSTER_COUNT * この値、も自動で増える）。
const MAX_LIGHTS_PER_CLUSTER: u32 = 256u;

/// ライトのバウンディング球半径に掛ける安全マージン（保守側へ倒すための係数）。
/// 減衰式は range で厳密に 0 になるため理論上は 1.0 で足りるが、
/// クラスタ AABB の丸め・浮動小数の境界誤差でライトが 1 クラスタ分だけ
/// 落ちる事故を防ぐため、わずかに大きく見積もる（棄却しすぎない側へ倒す）。
const CLUSTER_LIGHT_BOUND_MARGIN: f32 = 1.05;

// ─── 構造体 ──────────────────────────────────────────────────

/// クラスタ 1 個のライトリスト参照（グローバルインデックスリストへの offset/count）。
/// Rust: clustered.rs の ClusterCell（repr(C), 8 バイト）と一致させること。
struct ClusterCell {
    /// グローバルライトインデックスリスト内の先頭位置。
    offset: u32,
    /// このクラスタに影響するライト数（0 なら参照しない）。
    count:  u32,
}

/// クラスタ構築・参照の両方が使うカメラ／画面パラメータ。
/// Rust: clustered.rs の ClusterParams（repr(C), 112 バイト）と一致させること。
///
/// **カメラごとに固有**であることに注意（メインカメラ用とプレビュー用で別の
/// uniform バッファを持ち、group 4 の BindGroup ごと差し替える）。
struct ClusterParams {
    /// ビュー行列（列優先アップロード済み。view * vec4(world,1) でビュー座標）。
    view:               mat4x4<f32>,
    /// ビューポート左上（フレームバッファのピクセル座標）。
    /// Play のレターボックス時はゲーム領域の原点になる（frag_coord は
    /// フレームバッファ基準のため、ここを引いてビューポート相対にする）。
    vp_origin:          vec2<f32>,
    /// ビューポートの幅・高さ（ピクセル）。NDC ↔ ピクセルの対応はこの矩形で決まる。
    vp_size:            vec2<f32>,
    /// カメラのニアクリップ（> 0）。Z スライスの指数分割の基準。
    near:               f32,
    /// カメラのファークリップ（> near）。
    far:                f32,
    /// tan(fov_y/2) * aspect（ビュー空間 X = ndc.x * tan_half_x * view_z）。
    tan_half_x:         f32,
    /// tan(fov_y/2)（ビュー空間 Y = ndc.y * tan_half_y * view_z）。
    tan_half_y:         f32,
    /// クラスタ有効フラグ（0 = 無効。フラグメントは従来どおり全ライトを線形走査する）。
    /// 透視カメラ以外（2D オルソ・正射）やカメラプレビューでは 0 にする。
    enabled:            u32,
    /// クラスタ対象ライト（局所ライト）の開始インデックス。
    /// ライト配列は CPU 側で「平行光を先頭へ」安定分割済みで、
    /// [0, local_light_offset) = 平行光（常に全ピクセルで評価）、
    /// [local_light_offset, light_count) = 局所ライト（クラスタでカリング）。
    local_light_offset: u32,
    /// 有効ライト総数（LightMeta.count と同じ値）。
    light_count:        u32,
    /// 16 バイト境界のためのパディング。
    _pad:               u32,
}

/// クラスタ 1 個のビュー空間 AABB。
struct ClusterAabb {
    min: vec3<f32>,
    max: vec3<f32>,
}

// ─── Z スライス（指数分割＝対数深度）───────────────────────────
//
// 一様分割は手前に粗く奥に細かくなりすぎるため、Doom 2016 と同じ指数分割を使う。
//   slice_depth(k) = near * (far/near)^(k / NUM_Z)
//   slice(z)       = floor(log(z/near) / log(far/near) * NUM_Z)
// 上の 2 つは互いに逆関数であり、構築（compute）と参照（fragment）で同じ境界になる。

/// スライス境界 k（0..=NUM_Z）のビュー空間深度を返す。
fn cluster_slice_depth(k: u32, near: f32, far: f32) -> f32 {
    let t = f32(k) / f32(CLUSTER_SLICES_Z);
    return near * pow(far / near, t);
}

/// ビュー空間深度からスライス番号を返す（0..NUM_Z-1 にクランプ）。
///
/// near より手前・far より奥のフラグメントは深度クリップで存在しないはずだが、
/// 数値誤差での範囲外を安全側（端のスライス）へ寄せる。
fn cluster_z_slice(view_z: f32, near: f32, far: f32) -> u32 {
    let z = max(view_z, near);
    let t = log(z / near) / log(far / near);
    let s = i32(floor(t * f32(CLUSTER_SLICES_Z)));
    return u32(clamp(s, 0, i32(CLUSTER_SLICES_Z) - 1));
}

// ─── クラスタ索引 ────────────────────────────────────────────

/// (x, y, z) → 一次元クラスタ番号。並びは x が最内（compute のグリッド走査と一致）。
fn cluster_flat_index(x: u32, y: u32, z: u32) -> u32 {
    return x + y * CLUSTER_TILES_X + z * CLUSTER_TILES_X * CLUSTER_TILES_Y;
}

/// フラグメント座標（フレームバッファピクセル）とビュー空間深度から
/// そのフラグメントが属するクラスタ番号を返す。
///
/// タイル境界は「ビューポート矩形を等分」で定義する。Play のレターボックス時は
/// set_viewport によって NDC がビューポート矩形へ写像されるため、
/// フレームバッファ全体ではなくビューポート矩形で正規化しなければ
/// 構築側（NDC 等分）と参照側がズレる（＝ライトが誤って落ちる）。
fn cluster_index_for_fragment(frag_xy: vec2<f32>, view_z: f32, p: ClusterParams) -> u32 {
    let size = max(p.vp_size, vec2<f32>(1.0, 1.0));
    let rel  = (frag_xy - p.vp_origin) / size;
    // clamp で 0..1 未満に収めてから整数化する（負値の u32 変換を避ける）。
    let tx = u32(clamp(rel.x, 0.0, 0.99999) * f32(CLUSTER_TILES_X));
    let ty = u32(clamp(rel.y, 0.0, 0.99999) * f32(CLUSTER_TILES_Y));
    let tz = cluster_z_slice(view_z, p.near, p.far);
    return cluster_flat_index(
        min(tx, CLUSTER_TILES_X - 1u),
        min(ty, CLUSTER_TILES_Y - 1u),
        tz,
    );
}

// ─── クラスタのビュー空間 AABB ───────────────────────────────

/// クラスタ (x, y, z) のビュー空間 AABB を返す（8 隅を包む軸平行境界＝保守側）。
///
/// 透視投影（Mat4x4::perspective_lh）は
///   ndc.x = view.x / (tan_half_x * view.z),  ndc.y = view.y / (tan_half_y * view.z)
/// なので、逆に view.x = ndc.x * tan_half_x * view.z（view.z > 0）となる。
/// タイルの NDC 境界 2 本 × スライス深度 2 枚の計 4 通りの積の min/max を取れば、
/// そのクラスタの錐台セルを包む AABB になる（錐台そのものより広い＝棄却しすぎない）。
fn cluster_view_aabb(x: u32, y: u32, z: u32, p: ClusterParams) -> ClusterAabb {
    // NDC タイル境界。x は左(-1)→右(+1)、y は上(+1)→下(-1)（wgpu の NDC）。
    let nx0 = f32(x)       / f32(CLUSTER_TILES_X) * 2.0 - 1.0;
    let nx1 = f32(x + 1u)  / f32(CLUSTER_TILES_X) * 2.0 - 1.0;
    let ny0 = 1.0 - f32(y)      / f32(CLUSTER_TILES_Y) * 2.0;
    let ny1 = 1.0 - f32(y + 1u) / f32(CLUSTER_TILES_Y) * 2.0;

    let z0 = cluster_slice_depth(z,      p.near, p.far);
    let z1 = cluster_slice_depth(z + 1u, p.near, p.far);

    // x/y は深度に比例して広がるため、境界 2 本 × 深度 2 枚の 4 積から min/max を取る。
    let xs = vec4<f32>(nx0 * p.tan_half_x * z0, nx0 * p.tan_half_x * z1,
                       nx1 * p.tan_half_x * z0, nx1 * p.tan_half_x * z1);
    let ys = vec4<f32>(ny0 * p.tan_half_y * z0, ny0 * p.tan_half_y * z1,
                       ny1 * p.tan_half_y * z0, ny1 * p.tan_half_y * z1);

    let min_x = min(min(xs.x, xs.y), min(xs.z, xs.w));
    let max_x = max(max(xs.x, xs.y), max(xs.z, xs.w));
    let min_y = min(min(ys.x, ys.y), min(ys.z, ys.w));
    let max_y = max(max(ys.x, ys.y), max(ys.z, ys.w));

    return ClusterAabb(
        vec3<f32>(min_x, min_y, min(z0, z1)),
        vec3<f32>(max_x, max_y, max(z0, z1)),
    );
}

// ─── ライトのバウンディング体積 ──────────────────────────────

/// ライトのバウンディング球（xyz = ワールド中心, w = 半径）を返す。
///
/// **w <= 0 は「クラスタ対象外」を意味する**（平行光。視錐台全体に影響するため
/// クラスタに入れず、フラグメント側で常に全ピクセル評価する別枠にする）。
///
/// 保守側（棄却しすぎない側）に倒す方針:
///   - point : 距離減衰は range で厳密に 0 になる（lighting_eval の window 関数）ので
///             半径 = range。マージンを掛けて丸め誤差ぶんを広げる。
///   - spot  : 円錐の頂点（= 光源位置）から見て、円錐上の全点は距離 range 以内にある。
///             よって中心 = 位置・半径 = range の球は円錐を厳密に包含する（保守的）。
///             より厳密な円錐 vs AABB 判定は将来課題（誤って落とすほうが致命的なため
///             まず「落とさない」ことを優先する）。
///   - rect  : 発光面上の最近接点からの点光源として減衰するため、影響範囲は
///             「矩形の各点を中心とする半径 range の球の和集合」。それを包む球は
///             中心 = 矩形中心・半径 = range + 矩形の半対角。
fn cluster_light_bounds(
    kind:   u32,
    position: vec3<f32>,
    range:  f32,
    half_w: f32,
    half_h: f32,
) -> vec4<f32> {
    if kind == LIGHT_KIND_DIRECTIONAL {
        // 平行光はクラスタに入れない（別枠で常時評価）。
        return vec4<f32>(position, -1.0);
    }
    if kind == LIGHT_KIND_RECT {
        let half_diag = sqrt(half_w * half_w + half_h * half_h);
        return vec4<f32>(position, (range + half_diag) * CLUSTER_LIGHT_BOUND_MARGIN);
    }
    // point / spot（および未知の種別）は位置中心・半径 range の球で保守的に包む。
    return vec4<f32>(position, range * CLUSTER_LIGHT_BOUND_MARGIN);
}

/// 球（center, radius）と AABB（min, max）が交差するか。
///
/// AABB 上で球中心に最も近い点までの二乗距離が半径二乗以下なら交差。
/// 接する（距離 == 半径）ケースは**交差扱い**にする（保守側）。
fn sphere_aabb_intersects(
    center: vec3<f32>, radius: f32,
    aabb_min: vec3<f32>, aabb_max: vec3<f32>,
) -> bool {
    let closest = clamp(center, aabb_min, aabb_max);
    let d       = center - closest;
    return dot(d, d) <= radius * radius;
}
