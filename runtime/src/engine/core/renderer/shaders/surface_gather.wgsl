// ============================================================
// surface_gather.wgsl  —  マテリアル採取（VertexOutput + group 2 → Surface）
//
// ## 役割（単一責任）
// 補間頂点属性（VertexOutput）とマテリアル（group 2: uniform + 5 種のテクスチャ）から
// Surface（surface.wgsl）を組み立てる「採取段」。ライト評価は一切行わない。
//
// ## 依存
//   - shader_common.wgsl : VertexOutput / MaterialUniform / t_*・s_*（group 2）
//   - surface.wgsl       : Surface
// 連結順は各 pipelines/*.toml の shader_sources を参照（WGSL のモジュールスコープ宣言は
// 順序非依存だが、依存の向きが読み取れるよう common → surface → surface_gather と並べている）。
//
// ## ステージ制約（重要）
// gather_surface は dpdx/dpdy（画面微分）を経由する geometric_normal を呼ぶため、
// **フラグメントステージ専用**である。頂点／コンピュートステージから到達する経路に
// 混ぜてはならない（naga の一様性解析で検証エラーになる）。
// 本ファイルを連結するのはフラグメントで採取するパイプラインのみに限ること。
//
// ## 将来（Deferred）
// G-Buffer ジオメトリパスのフラグメントシェーダが本関数を呼び、返った Surface を
// MRT へ焼く。ライティングパスは本ファイルを連結しない（group 2 を要求しないため）。
// ============================================================

/// 幾何法線を画面微分から復元する際の、外積の長さの下限。
/// 三角形が画面上でほぼ縮退（面積ゼロ）していると cross(dpdx, dpdy) が 0 ベクトルになり
/// normalize が NaN になるため、この閾値未満なら補間法線へフォールバックする。
const GEOM_NORMAL_MIN_LEN: f32 = 1e-8;

// ── 幾何法線（RT 影のレイ原点バイアス専用）──────────────────
//
/// フラグメントの**幾何法線**（＝三角形の面法線）をワールド位置の画面微分から復元する。
///
/// なぜ必要か: RT 影のレイ原点は「面から少し浮かせる」必要があるが、押し出し方向に
/// 法線マップ適用後のシェーディング法線を使うと、法線が面から傾いている分だけ実効的な
/// クリアランスが落ち、面が自分自身に遮蔽されて真っ黒になる（Sponza の壁・柱で発生）。
/// 押し出しは必ず面そのものの向き（幾何法線）で行う。
///
/// - `world_pos`   : 補間済みワールド座標（dpdx/dpdy を取る＝フラグメントステージ限定）。
/// - `world_normal`: 法線マップ適用**前**の補間法線。外積の向き（符号）合わせにのみ使う
///                   （glTF の巻き順・スケール反転で外積の向きが反転しうるため）。
///
/// 注意: dpdx/dpdy は一様制御フロー内で呼ぶ必要があるため、必ず関数の先頭側
///       （分岐・discard より前）で呼ぶこと。
fn geometric_normal(world_pos: vec3<f32>, world_normal: vec3<f32>) -> vec3<f32> {
    let n_interp = normalize(world_normal);
    let ng_raw   = cross(dpdx(world_pos), dpdy(world_pos));
    let ng_len   = length(ng_raw);
    // 縮退時は補間法線で代用（従来挙動と同等の押し出しになる）。
    if ng_len < GEOM_NORMAL_MIN_LEN {
        return n_interp;
    }
    let ng = ng_raw / ng_len;
    // 補間法線と同じ半球へ向ける（faceforward 相当）。
    return select(-ng, ng, dot(ng, n_interp) >= 0.0);
}

/// マテリアル uniform ＋テクスチャをサンプリングして Surface を組み立てる。
///
/// `front_facing` は `@builtin(front_facing)`（各エントリポイントが受け取る）。
/// カリング面 None（両面描画）／Front のマテリアルでは裏面フラグメントが生成されるが、
/// 頂点法線は表面向きに定義されているため、そのままだと N・V が逆半球になり
/// ndl / ndv がほぼ 0 に潰れて面が真っ黒になる。裏面ではシェーディング法線 N と
/// 幾何法線 Ng の両方を反転して「見えている側の法線」に揃える。
///
/// ## discard について
/// Mask マテリアルのアルファテスト（discard）は**本関数内**で行う。
/// 分割前（shade_pbr 一本）と同じ位置（ベースカラー確定直後・メタリック採取より前）に
/// 置くことで、フラグメントの破棄タイミングと discard 条件を厳密に維持している。
/// 呼び出し側（fs_main / fs_wboit）へ移すとカットオフ挙動が両パスで分岐しうるため移さない。
/// alpha_cutoff は Mask のときのみ正値・Opaque/Blend では 0.0（GpuMaterial::upload 参照）。
fn gather_surface(in: VertexOutput, front_facing: bool) -> Surface {

    // 裏面のときだけ法線を反転させる符号（表面 = +1 / 裏面 = -1）。
    // 背面カリング（CullFace::Back）のマテリアルでは裏面が生成されないため常に +1 となり、
    // 従来と完全に同一の結果になる。
    let facing_sign = select(-1.0, 1.0, front_facing);

    // ── 幾何法線（RT 影のレイ原点バイアス専用）────────────────
    // 画面微分（dpdx/dpdy）を使うため、discard やライトループなどの分岐に入る前
    // （＝一様制御フロー）でここ一度だけ求める。シェーディングには使わない。
    // Ng も裏面では反転する。反転しないと裏面でレイ原点のバイアス押し出しが面の内側
    // （＝可視側と反対）へ向き、自分自身に遮蔽されて影が真っ黒になる。
    let Ng = geometric_normal(in.world_pos, in.world_normal) * facing_sign;

    // ── ベースカラー ──────────────────────────────────────────
    // 頂点カラー（in.color）はここで畳み込む。G-Buffer には畳み込んだ結果だけを載せる
    // （頂点カラーは補間属性であり、ライティングパスからは参照できないため）。
    var base_color = u_material.base_color_factor;
    if u_material.has_base_color_tex != 0u {
        base_color *= textureSample(t_base_color, s_base_color, in.uv0);
    }
    base_color *= in.color;

    // アルファテスト（Mask モード: alpha_cutoff > 0）。
    // GpuMaterial::upload により alpha_cutoff は Mask のときのみ正値、
    // Opaque/Blend では 0.0 になるため、この分岐は Mask のみで発火する。
    if u_material.alpha_cutoff > 0.0 && base_color.a < u_material.alpha_cutoff {
        discard;
    }

    // ── メタリック・ラフネス ──────────────────────────────────
    var metallic  = u_material.metallic_factor;
    var roughness = u_material.roughness_factor;
    // MR テクスチャ無視トグル（mr_tex_ignore）が立っているときはテクスチャ乗算をスキップし、
    // metallic/roughness factor をそのまま実効値にする（glTF 標準の乗算は既定で維持）。
    // これは forward / G-Buffer 双方が通る唯一の MR 採取箇所なので、両パスへ自動で効く。
    if u_material.has_mr_tex != 0u && u_material.mr_tex_ignore == 0u {
        let mr = textureSample(t_metallic_roughness, s_metallic_roughness, in.uv0);
        metallic  *= mr.b;   // glTF: B = metallic
        roughness *= mr.g;   // glTF: G = roughness
    }
    roughness = clamp(roughness, 0.04, 1.0);

    // ── エミッシブ ────────────────────────────────────────────
    var emissive = u_material.emissive_factor;
    if u_material.has_emissive_tex != 0u {
        emissive *= textureSample(t_emissive, s_emissive, in.uv0).rgb;
    }

    // ── アンビエントオクルージョン ────────────────────────────
    var ao = 1.0;
    if u_material.has_occlusion_tex != 0u {
        ao = textureSample(t_occlusion, s_occlusion, in.uv0).r;
    }

    // ── 法線（法線マップ対応）────────────────────────────────
    // 裏面では補間法線を反転してから TBN を組む（接空間法線マップも反転後の N を基準に載る）。
    //
    // Nv（補間頂点法線・法線マップ適用前）は Surface へそのまま持ち越す。
    // RT 影の減衰カーブ（ターミネータのランプ／スロープスケールバイアス）は、
    // フラットな Ng ではなくこの滑らかな Nv で判定する（surface.wgsl の解説を参照）。
    let Nv = normalize(in.world_normal) * facing_sign;
    var N = Nv;
    if u_material.has_normal_tex != 0u {
        // 法線マップの Z は RG から再構築する。
        // BC5 圧縮（RG 2ch）フォーマットは B チャンネルを持たないため直接読めない。
        // 接空間法線は単位ベクトルなので z = sqrt(1 - x^2 - y^2) で復元でき、
        // 非圧縮 RGB 法線マップでも同じ結果になる（後方互換）。
        let nxy = textureSample(t_normal, s_normal, in.uv0).rg * 2.0 - 1.0;
        let nz  = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
        let tn  = vec3<f32>(nxy, nz);
        let T   = normalize(in.world_tan);
        let B   = normalize(in.world_bitan);
        let tbn = mat3x3<f32>(T, B, N);
        N = normalize(tbn * tn);
    }

    // ── Surface へ詰める ──────────────────────────────────────
    // UV（uv0 / uv1）と接空間（world_tan / world_bitan）はここで役目を終えるため持ち越さない。
    var s: Surface;
    s.world_pos     = in.world_pos;
    s.normal        = N;
    s.geo_normal    = Ng;
    s.vertex_normal = Nv;
    s.albedo     = base_color.rgb;
    s.alpha      = base_color.a;
    s.metallic   = metallic;
    s.roughness  = roughness;
    s.emissive   = emissive;
    s.occlusion  = ao;
    // clip_pos はフラグメントステージではフレームバッファ座標（ピクセル単位）。
    s.frag_coord = in.clip_pos.xy;
    return s;
}
