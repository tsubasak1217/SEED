// ============================================================
// shading_contract.wgsl  —  シェーディング契約 v1（第 3 層「合成」のアセット化・正典）
//
// ## 役割（単一責任）
// 「エンジンが計算した面の情報（ShadingSurface）」と「エンジンが計算した 1 ライト分の
// 光（LightSample）」という **2 つの固定インターフェース**、およびアセット側シェーダが
// 使ってよい **最小限の標準ライブラリ**（saturate / NaN ガード / 色空間 / 階調量子化）
// だけを定義する。バインディング（uniform / texture / storage）は一切宣言しない。
//
// この分離の意図は surface.wgsl と同じである:
//   バインディングを持たない純粋な型・純関数だけに閉じておけば、フォワード（不透明・半透明）
//   とデファードのライティングパスという **バインドグループ構成が異なる全パス**へ無条件に
//   連結でき、リフレクション（pipeline_config::reflect_bgls）に余計な BindGroup が載らない。
//
// ## 契約バージョンの規約（アセット側の宣言）
// シェーディングモデルを実装するアセット（WGSL 断片）は、**ファイル先頭のコメント**に
//     // @shading_contract 1
// と書いて、自分がどのバージョンの契約に対して書かれたかを宣言する。Rust 側のローダは
// この数値と `SHADING_CONTRACT_VERSION` を突き合わせ、不一致なら読み込みを拒否して
// モデル 0（エンジン標準 PBR）へフォールバックする（ローダ実装は L3-a 第 2 段）。
//
// バージョンを上げるのは **ShadingSurface / LightSample のフィールドの削除・改名・意味変更、
// または標準ライブラリ関数のシグネチャ変更**を行うときに限る。フィールドの「末尾への追加」は
// アセット側のコードを壊さないため、バージョンを上げずに行ってよい。
//
// ## 連結順序
//   pbr_common.wgsl（distribution_ggx 等）より **後**であること（shade_light が依存する）。
//   surface.wgsl の **直後**・lighting_eval.wgsl の **直前** に置く（既存の全連結リストで統一）。
//   その直後に、モデル ID を振り分ける `shade_surface` の実装を 1 本だけ連結する:
//     - アセット未指定時 : shading_dispatch.wgsl（既定＝モデル 0 固定）
//     - アセット指定時   : Rust が生成する同名関数（switch でモデル ID を振り分ける）
//
// ## 依存
//   - pbr_common.wgsl : PI / distribution_ggx / geometry_smith / fresnel_schlick
// ============================================================

/// シェーディング契約のバージョン。アセットの `// @shading_contract N` と一致必須。
/// 破壊的変更（フィールド削除・改名・意味変更／標準ライブラリのシグネチャ変更）でのみ上げる。
const SHADING_CONTRACT_VERSION: u32 = 1u;

// ── アセットへ渡す面の情報 ────────────────────────────────────

/// シェーディングモデル（アセット）へ渡す、G-Buffer 復元済みの面の情報。
///
/// ## なぜ `Surface`（surface.wgsl）をそのまま渡さないのか
/// `Surface` は**エンジン内部の作業用構造体**であり、アセットに見せる必要のない
/// 内部フィールドを含む:
///   - `shadow_mask` / `shadow_mask_valid` : RT ソフト影マスクのスロット配列。ライトループが
///     影係数を求めるための中間データで、影は `LightSample.color` に織り込み済み。
///   - `screen_gi`                         : SSGI のスクリーン入力。アンビエント項専用。
///   - `alpha`                             : 合成（ブレンド）側の値でライティングには無関係。
/// これらを契約に含めると、(1) アセットが内部実装（マスクのスロット数など）に依存できてしまい、
/// エンジン側のリファクタが破壊的変更になる、(2) 契約バージョンを上げる頻度が跳ね上がる。
/// よって **ライティングに必要な物性＋情報系だけを写したフラットな構造体**を別に定義し、
/// `Surface` → `ShadingSurface` の写し替えを lighting_eval.wgsl 側の責務とする。
///
/// フィールドの順序と名前は契約の一部である（変更＝メジャーバージョンアップ）。
struct ShadingSurface {
    /// ワールド座標。位置依存のプロシージャル模様・距離フェード等に使える。
    world_pos:     vec3<f32>,

    /// シェーディング法線 N（ワールド空間・正規化済み・法線マップ適用後）。BRDF はこれを使う。
    normal:        vec3<f32>,

    /// 幾何法線 Ng（ワールド空間・正規化済み）。三角形の面そのものの向き（フラット）。
    /// エンジンは直接光の光漏れゲートに使う。アセットではファセット表現・輪郭判定などに使える。
    geo_normal:    vec3<f32>,

    /// 補間頂点法線 Nv（ワールド空間・正規化済み・法線マップ適用前）。
    /// 三角形をまたいで滑らかで、かつ法線マップの高周波な擾乱を含まない 3 本目の法線。
    vertex_normal: vec3<f32>,

    /// 表面→カメラ方向 V（正規化済み）。
    view_dir:      vec3<f32>,

    /// アルベド（ベースカラー rgb・リニア）。頂点カラー・ベースカラー係数は畳み込み済み。
    base_color:    vec3<f32>,

    /// ラフネス（0..1・clamp 済み）。
    roughness:     f32,

    /// メタリック（0=誘電体 / 1=金属）。
    metallic:      f32,

    /// アンビエントオクルージョン（0..1）。エンジンは環境光項にのみ乗算する。
    occlusion:     f32,

    /// エミッシブ（自己発光・リニア HDR）。エンジンがライト評価の最後に加算する。
    emissive:      vec3<f32>,

    /// 拡散透過（0..1。葉・布・紙の逆光透け。`Surface.diffuse_transmission` の写し）。
    transmission:  f32,

    /// マテリアルの自由回線（0..1）。濡れ・ダメージ・経年劣化など、意味はマテリアルが決める。
    user_data:     f32,

    /// アクタ単位のセマンティックタグ（0..15。0 = タグ無し）。
    render_tag:    u32,

    /// シェーディングモデル ID（0..3。0 = エンジン標準 PBR）。
    shading_model: u32,

    /// フラグメント座標（`@builtin(position).xy` ＝ ピクセル座標）。
    /// ディザ・ハッチング・ノイズの種として使う。
    frag_coord:    vec2<f32>,
}

// ── アセットへ渡す 1 ライト分の光 ─────────────────────────────

/// エンジンが計算済みの「1 ライト分の光」。
///
/// ## 重要な前提（アセットは減衰を再計算してはならない）
/// `color` には **color × intensity × 距離減衰 × スポット円錐（rect は前面判定）× 影係数**が
/// **すべて織り込み済み**である。アセット側で距離減衰や円錐減衰を再計算すると二重適用になる。
/// アセットがすべきことは「この放射輝度に対する BRDF（反射率）を掛けて返す」ことだけである。
///
/// フィールドの順序と名前は契約の一部である（変更＝メジャーバージョンアップ）。
struct LightSample {
    /// 表面→光源方向 L（正規化済み）。
    direction:         vec3<f32>,

    /// 実効放射輝度（減衰・円錐・**影まで**織り込み済み）。通常はこれを使う。
    color:             vec3<f32>,

    /// 影を掛ける**前**の放射輝度（減衰・円錐までは織り込み済み）。
    /// 逆光透け（拡散透過）のように「影を無視したい」表現のための回線。
    color_unshadowed:  vec3<f32>,

    /// 光源までの距離。平行光は方向のみで距離を持たないため、大きな定数が入る
    /// （light_common.wgsl の RT_DIR_TMAX。距離フェードの分母として安全に使える）。
    distance:          f32,

    /// ライト種別。値は light_common.wgsl の `LIGHT_KIND_*` と同一
    /// （0=directional / 1=point / 2=spot / 3=rect）。
    kind:              u32,
}

// ── 標準ライブラリ（アセットが使ってよい純関数） ──────────────

/// シーン HDR ターゲット（Rgba16Float ＝ IEEE half）が保持できる有限値の上限。
/// これを超える値は保存時に +Inf へ丸まり、以降のブルーム・トーンマップを汚染するため、
/// `shading_nan_guard` の上限クランプ値として使う（マジックナンバーを避けるための定数化）。
const SHADING_RADIANCE_MAX: f32 = 65504.0;

/// Rec.709（sRGB 原色）の輝度係数。`shading_luminance` が使う。
const SHADING_LUMA_REC709: vec3<f32> = vec3<f32>(0.2126, 0.7152, 0.0722);

/// sRGB 伝達関数の線形区間の傾き（リニア→sRGB）。
const SHADING_SRGB_LINEAR_SCALE: f32 = 12.92;
/// sRGB 伝達関数のガンマ区間の指数（リニア→sRGB。1/2.4）。
const SHADING_SRGB_GAMMA_INV: f32 = 1.0 / 2.4;
/// sRGB 伝達関数のガンマ区間のスケール係数。
const SHADING_SRGB_GAMMA_SCALE: f32 = 1.055;
/// sRGB 伝達関数のガンマ区間のオフセット。
const SHADING_SRGB_GAMMA_OFFSET: f32 = 0.055;
/// リニア→sRGB で線形区間とガンマ区間を切り替えるリニア側のしきい値。
const SHADING_SRGB_LINEAR_THRESHOLD: f32 = 0.0031308;
/// sRGB→リニアで線形区間とガンマ区間を切り替える sRGB 側のしきい値。
const SHADING_SRGB_ENCODED_THRESHOLD: f32 = 0.04045;

/// 0..1 へのクランプ（スカラー）。
fn shading_saturate(x: f32) -> f32 {
    return clamp(x, 0.0, 1.0);
}

/// 0..1 へのクランプ（ベクトル・成分ごと）。
fn shading_saturate3(v: vec3<f32>) -> vec3<f32> {
    return clamp(v, vec3<f32>(0.0), vec3<f32>(1.0));
}

/// 非有限値・負値を除去して「安全な放射輝度」に正規化する（アセットの出力の最終防波堤）。
///
/// アセットは任意の式を書けるため、0 除算・log(0)・pow(負, 小数) などで NaN や ±Inf を
/// 返しうる。NaN/Inf は 1 ピクセルでもブルーム（ダウンサンプル平均）に混ざると画面全体を
/// 破壊するため、ここで確実に潰す。
///   - NaN（`x != x` が真）      → 0
///   - ±Inf（有限判定に落ちる）  → 0
///   - 負値                      → 0（放射輝度に負は無い）
///   - SHADING_RADIANCE_MAX 超過 → SHADING_RADIANCE_MAX（half float で +Inf 化するのを防ぐ）
///
/// **有限かつ非負かつ上限以下の入力に対しては恒等写像である**。
///
/// ## 掛ける対象はユーザー実装（モデル 1..3）だけ
/// エンジン標準 PBR（モデル 0）にはこの門を**掛けない**。モデル 0 の出力は理論上この条件を
/// 常に満たすので恒等写像のはずだが、上限クランプだけは極端な HDR スパイクで値を変え得る。
/// 「モデル 0 の経路はアセット導入前と完全に同値」という L3-a の設計要件に疑いを残さないため、
/// 既定ディスパッチ（shading_dispatch.wgsl）と生成ディスパッチの `default:` 腕は素通しにする。
fn shading_nan_guard(c: vec3<f32>) -> vec3<f32> {
    // NaN 判定は `x != x`（NaN は自分自身と等しくならない唯一の値）。
    let is_nan = vec3<bool>(c.x != c.x, c.y != c.y, c.z != c.z);
    let no_nan = select(c, vec3<f32>(0.0), is_nan);
    // 有限判定。NaN は上で 0 に潰れているので、ここに残る非有限値は ±Inf だけ。
    let is_finite = abs(no_nan) <= vec3<f32>(SHADING_RADIANCE_MAX);
    let finite = select(vec3<f32>(0.0), no_nan, is_finite);
    // 負値の除去（上限クランプは有限判定で既に満たされているが、意図を明示して二重に固定する）。
    return clamp(finite, vec3<f32>(0.0), vec3<f32>(SHADING_RADIANCE_MAX));
}

/// Rec.709 の相対輝度。トゥーンのしきい値判定・彩度操作などに使う。
fn shading_luminance(c: vec3<f32>) -> f32 {
    return dot(c, SHADING_LUMA_REC709);
}

/// リニア → sRGB（伝達関数の適用）。色相操作を「見た目の明るさ」空間で行いたいとき用。
/// 出力先のシーン HDR は**リニア**なので、変換した色はそのまま出力せず必ず戻すこと。
fn shading_linear_to_srgb(c: vec3<f32>) -> vec3<f32> {
    let lo = c * SHADING_SRGB_LINEAR_SCALE;
    let hi = SHADING_SRGB_GAMMA_SCALE * pow(max(c, vec3<f32>(0.0)), vec3<f32>(SHADING_SRGB_GAMMA_INV))
           - SHADING_SRGB_GAMMA_OFFSET;
    return select(hi, lo, c <= vec3<f32>(SHADING_SRGB_LINEAR_THRESHOLD));
}

/// sRGB → リニア（`shading_linear_to_srgb` の逆変換）。
fn shading_srgb_to_linear(c: vec3<f32>) -> vec3<f32> {
    let lo = c / SHADING_SRGB_LINEAR_SCALE;
    let hi = pow(max((c + SHADING_SRGB_GAMMA_OFFSET) / SHADING_SRGB_GAMMA_SCALE, vec3<f32>(0.0)),
                 vec3<f32>(1.0 / SHADING_SRGB_GAMMA_INV));
    return select(hi, lo, c <= vec3<f32>(SHADING_SRGB_ENCODED_THRESHOLD));
}

/// 階調の量子化（トゥーン／セルシェーディングの段数化）。
/// `steps` 段の階段関数にする。`steps <= 0`（ゼロ除算・負段数）は量子化せず素通しする。
fn shading_posterize(x: f32, steps: f32) -> f32 {
    if steps <= 0.0 {
        return x;
    }
    return floor(x * steps) / steps;
}

// ── エンジン標準 PBR（モデル 0・アセットでは上書き不可） ──────

/// 1 ライト分の Cook-Torrance BRDF を評価して放射輝度を返す。
///
/// - `L`        : 面から光源への方向（正規化済み）
/// - `radiance` : そのライトの実効放射輝度（color * intensity * 減衰）
///
/// 旧 lighting_eval.wgsl から本ファイルへ**そのまま移設**した（L3-a 第 1 段）。
/// 契約の「モデル 0」の実体であると同時に、アセットが標準 PBR を土台に加算・混合したいときの
/// 基礎ブロックでもあるため、バインディングを持たない本ファイルに置くのが正しい所属である。
/// 演算順序を含めて一切変更していない（既存出力との同値性の根拠）。
fn shade_light(
    N: vec3<f32>, V: vec3<f32>, L: vec3<f32>,
    albedo: vec3<f32>, F0: vec3<f32>, metallic: f32, roughness: f32,
    radiance: vec3<f32>,
) -> vec3<f32> {
    let H   = normalize(V + L);
    let ndl = max(dot(N, L), 0.0);
    let ndv = max(dot(N, V), 0.0001);
    let hdv = max(dot(H, V), 0.0);

    let D = distribution_ggx(N, H, roughness);
    let G = geometry_smith(N, V, L, roughness);
    let F = fresnel_schlick(hdv, F0);

    let kS = F;
    let kD = (vec3<f32>(1.0) - kS) * (1.0 - metallic);

    // 分母にクランプして除算ゼロを防止
    let specular = D * G * F / max(4.0 * ndv * ndl, 0.001);
    return (kD * albedo / PI + specular) * radiance * ndl;
}

/// 誘電体の垂直入射反射率（F0）。金属は base_color 自体を F0 として使う。
/// 値 0.04 は既存 lighting_eval.wgsl のものと同一（IOR 1.5 相当の一般的な誘電体）。
const SHADING_DIELECTRIC_F0: f32 = 0.04;

/// **モデル 0 ＝ エンジン標準 PBR**（Cook-Torrance）。
///
/// この関数は契約の土台であり、**アセットでは上書きできない**。理由は 2 つ:
///   1. エンジン内部（デファード合成・反射・GI など）が「標準 PBR とはこの式である」という
///      前提で組み立てられており、差し替えを許すと整合が取れない。
///   2. モデル ID が未定義・アセットのロード失敗時に必ず戻れる安全な既定が要る。
/// アセットが標準 PBR を土台にしたい場合は、この関数を**呼んで**結果に加算・混合すること。
fn shade_model_0(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    // 誘電体は F0 = 0.04、金属は base_color を F0 として使用（既存 lighting_eval.wgsl と同一）。
    let F0 = mix(vec3<f32>(SHADING_DIELECTRIC_F0), sf.base_color, sf.metallic);
    return shade_light(
        sf.normal, sf.view_dir, li.direction,
        sf.base_color, F0, sf.metallic, sf.roughness,
        li.color,
    );
}
