// ============================================================
// surface.wgsl  —  シェーディング対象の「面の情報」（Surface）
//
// ## 役割（単一責任）
// シェーディング（ライト評価）に必要な面の属性だけを保持する**データ定義専用**ファイル。
// バインディング（uniform / texture）も関数も宣言しない。これは意図的な制約である:
//
//   将来の Deferred（G-Buffer）化では
//     - ジオメトリパス : VertexOutput + マテリアル（group 2）→ Surface を作って G-Buffer へ焼く
//     - ライティングパス: G-Buffer テクスチャ → Surface を復元 → evaluate_lighting
//   の 2 パスへ分かれる。ライティングパスはマテリアルのテクスチャ群（group 2）を
//   一切バインドしない。もし Surface 構造体とマテリアルサンプリング関数を同じ
//   WGSL ファイルに同居させると、ライティングパスがこのファイルを連結した時点で
//   group 2 のバインディングがリフレクション（pipeline_config::reflect_bgls は
//   module.global_variables を無条件に走査する）に載り、使わない BindGroup を
//   要求する破綻したパイプラインレイアウトになる。
//   ゆえに「構造体（本ファイル）」と「採取（surface_gather.wgsl）」を分離する。
//
// ## G-Buffer との対応
// 本構造体のフィールドは、そのまま G-Buffer に焼く内容の候補である。
// 逆に「G-Buffer に載らない値」（頂点カラー・UV）はここには持たない:
//   - uv0 / uv1  : テクスチャ採取のためだけに使う。採取が終われば不要 → Surface に載せない。
//   - 頂点カラー : ベースカラーへ乗算されるだけ（base_color *= in.color）なので、
//                  採取段で albedo / alpha へ畳み込む（G-Buffer にも畳み込んだ結果が載る）。
//   - world_tan / world_bitan : 法線マップを world 空間 N へ変換するためだけに使う。
//                  変換後の N を持てば十分 → Surface に載せない。
// ============================================================

/// RT ソフト影マスクのスロット数（＝Surface.shadow_mask のレイヤ数）。
/// Rust shadow_mask::RT_SHADOW_MASK_LIGHTS / shadow_mask.wgsl SHADOW_MASK_LAYERS と一致必須
/// （rt_shadow.rs のユニットテストが 3 者の一致を担保する）。
const SURFACE_SHADOW_MASK_SLOTS: u32 = 4u;

// ── サーフェス識別情報のビット規約（Rust renderer::surface_id と一致必須）────
//
// セマンティックタグ（4bit）とシェーディングモデル ID（2bit）を 1 個の float へ
// パックして G-Buffer RT3.a（Rgba16Float）へ焼く。half float は整数 2048 まで
// 誤差ゼロで表現できるため、最大値 63 のパック値は完全に無損失で往復する。
// 値の意味・格納先の設計理由は Rust 側 surface_id.rs のモジュールコメントを正典とする。

/// セマンティックタグのビット幅（Rust RENDER_TAG_BITS と一致必須）。
const RENDER_TAG_BITS: u32 = 4u;
/// セマンティックタグの値マスク。
const RENDER_TAG_MASK: u32 = 15u;
/// シェーディングモデル ID の値マスク（シフト前）。
const SHADING_MODEL_MASK: u32 = 3u;
/// シェーディングモデル ID のシフト量（タグの直上）。
const SHADING_MODEL_SHIFT: u32 = RENDER_TAG_BITS;
/// 既定のシェーディングモデル＝標準 PBR。
const SHADING_MODEL_DEFAULT_PBR: u32 = 0u;

/// render_tag と shading_model を G-Buffer RT3.a 用の 1 個の float へパックする。
/// マスクを掛けるので範囲外の入力でもビットが隣へ漏れない。
fn pack_surface_id(render_tag: u32, shading_model: u32) -> f32 {
    let tag = render_tag & RENDER_TAG_MASK;
    let sm  = (shading_model & SHADING_MODEL_MASK) << SHADING_MODEL_SHIFT;
    return f32(tag | sm);
}

/// `pack_surface_id` の逆変換。x = render_tag / y = shading_model。
/// half float の丸めに備えて round してから整数化する。
fn unpack_surface_id(packed: f32) -> vec2<u32> {
    let v   = u32(round(max(packed, 0.0)));
    let tag = v & RENDER_TAG_MASK;
    let sm  = (v >> SHADING_MODEL_SHIFT) & SHADING_MODEL_MASK;
    return vec2<u32>(tag, sm);
}

/// シェーディングに必要な面の情報一式。
///
/// ライト評価（evaluate_lighting）はこの構造体**だけ**を入力とする。
/// VertexOutput（補間属性）へ依存させないことで、フォワード（半透明）と
/// 将来の G-Buffer ライティングパスが同一のライト評価コードを共有できる。
struct Surface {
    /// ワールド座標。ライトベクトル L・視線ベクトル V・影レイの原点・
    /// カスケード選択用のビュー深度に使う。
    world_pos:   vec3<f32>,

    /// シェーディング法線 N（ワールド空間・正規化済み）。
    /// 法線マップ適用後、かつ front_facing による裏面反転済み。BRDF はこれを使う。
    normal:      vec3<f32>,

    /// 幾何法線 Ng（ワールド空間・正規化済み）。三角形の**面そのもの**の向き
    /// （画面微分 cross(dpdx, dpdy) 由来なので三角形内で一定＝フラット）。
    /// front_facing による裏面反転済み。
    ///
    /// 用途は **RT 影レイの自己交差回避に限る**:
    ///   - レイ原点を「その三角形の平面から確実に浮かせる」最小クリアランス方向
    ///   - レイ方向を「その三角形の平面より上」へ持ち上げる（地平線リフト）ときの基準
    /// BRDF にも、影の減衰カーブ（ターミネータ判定）にも使ってはならない。
    /// フラットゆえに三角形の境界で不連続に変化し、これを減衰の判定に使うと
    /// **面境界に沿った硬い黒帯（ファセット状の影）**が出る（実際に退行として発生した）。
    geo_normal:  vec3<f32>,

    /// 補間頂点法線 Nv（ワールド空間・正規化済み）。**法線マップ適用前**、
    /// front_facing による裏面反転済み。
    ///
    /// N（法線マップ後）と Ng（フラットな面法線）の中間にある「3 本目の法線」。
    /// 三角形をまたいで**滑らかに**変化し、かつ法線マップの高周波な擾乱を含まない。
    /// この性質から、RT 影の**減衰カーブの判定**（シャドウターミネータのランプ、
    /// レイ原点のスロープスケールバイアス）にはこれを使う。
    /// - Ng を使うとファセット状の黒帯が出る（上記）。
    /// - N を使うと法線マップのノイズが影の減衰にそのまま乗る＋接空間の傾きで
    ///   バイアスの実効クリアランスが落ちて自己遮蔽する。
    ///
    /// ## G-Buffer 化（Deferred）での扱い
    /// ライティングパスは補間属性を持たないため、この 3 本目をどう供給するかは選択が要る:
    ///   (a) G-Buffer に焼く: 8:8（または 16:16）の octahedral で 1 チャンネル追加する。
    ///       品質は完全に等価。コストは G-Buffer 帯域 +2〜4 byte/px。
    ///   (b) 焼かずに代用する: ライティングパスでは
    ///         Ng ← 深度バッファから復元したワールド座標の画面微分（標準的手法・追加コスト小）
    ///         Nv ← G-Buffer の N（法線マップ後）で代用
    ///       で済ませる。N は三角形をまたいで滑らかなのでファセットは出ない。
    ///       法線マップの擾乱が影の減衰へ乗る（＝バンプが影の境界に現れる）が、
    ///       多くのタイトルはこれを許容している（むしろ意図的に使う場合もある）。
    /// 現時点の推奨は (b)（G-Buffer を太らせない）。バンプ由来の影ノイズが問題化したら
    /// (a) へ切り替える。いずれにせよ **Ng を減衰判定に使う選択肢だけは無い**。
    vertex_normal: vec3<f32>,

    /// アルベド（ベースカラー rgb・リニア）。頂点カラー乗算済み。
    albedo:      vec3<f32>,

    /// アルファ（ベースカラー a）。頂点カラー乗算済み。
    /// 不透明パスでは出力アルファ、WBOIT パスでは重み計算に使う。
    /// （Deferred の不透明 G-Buffer では常に 1.0 相当となり実質未使用になる想定）
    alpha:       f32,

    /// メタリック（0=誘電体 / 1=金属）。テクスチャ・ファクタ適用済み。
    metallic:    f32,

    /// ラフネス（clamp 済み）。テクスチャ・ファクタ適用済み。
    roughness:   f32,

    /// 拡散透過（diffuse_transmission, 0..1。葉・布・紙の逆光透け）。0.0=従来動作。
    /// evaluate_lighting のライトループが、面がライトに背を向けている側の逆光を
    /// radiance×saturate(-dot(N,L))×diffuse_transmission×albedo/PI で加算するのに使う。
    /// G-Buffer（deferred）では RT2.b に焼く（Rgba8Unorm 8bit で 0..1 に十分）。
    diffuse_transmission: f32,

    /// エミッシブ（自己発光・リニア HDR）。ライト評価の最後に加算する。
    emissive:    vec3<f32>,

    /// アンビエントオクルージョン（0..1）。環境光項にのみ乗算する。
    occlusion:   f32,

    /// フラグメント座標（@builtin(position).xy = ピクセル座標）。
    /// RT ソフトシャドウのサンプル方向を決める Interleaved Gradient Noise の
    /// 種として使う（ピクセルごとに異なる回転を与えるため）。
    /// G-Buffer には焼かない（ライティングパスでも同じピクセル座標が直接得られる）。
    frag_coord:  vec2<f32>,

    /// スクリーンスペース GI（SSGI）の間接放射照度と有効フラグ（GI_MODE_SSGI 時のみ意味を持つ）。
    ///   .rgb = このピクセルで拾った 1 バウンス間接光（ミスはフラットアンビエントで埋め済み）
    ///   .a   = 1.0 なら .rgb が有効（deferred ライティングパスが t_ssgi から供給した）、
    ///          0.0 なら無効（フォワードパス＝スクリーン入力が不透明前提のため使えない）。
    ///
    /// **フォワードパス（不透明メッシュ・半透明 WBOIT/距離ソート）はこのフィールドを設定しない**。
    /// WGSL の `var s: Surface;` はゼロ初期化されるため screen_gi=vec4(0) となり、.a=0＝無効。
    /// evaluate_gi_ambient は SSGI モードでも .a<=0 のフォワードではフラットアンビエントへ
    /// フォールバックする（半透明フォワードに SSGI を効かせない設計）。
    /// deferred ライティングパスだけが `s.screen_gi = vec4(ssgi_rgb, 1.0)` を設定する。
    screen_gi:   vec4<f32>,

    /// RT ソフト影マスク（Phase RT-Shadow-Denoise）。スロット i（0..SURFACE_SHADOW_MASK_SLOTS-1）の
    /// .rgb ＝そのライトのデノイズ済み遮蔽率（色付き影込みの透過率）。**deferred ライティングパスのみ**が
    /// 半解像度マスク（texture_2d_array）を各レイヤぶんサンプルして設定する。ライトループは
    /// light.shadow_mask_slot（>=0）が指すレイヤをここから引く（レイを飛ばさずデノイズ済み値を使う）。
    shadow_mask: array<vec4<f32>, SURFACE_SHADOW_MASK_SLOTS>,

    /// shadow_mask が有効か。1.0=deferred が設定済み（マスク経路を使える）／0.0=無効。
    ///
    /// **フォワード／WBOIT パスはこのフィールドを設定しない**（`var s: Surface;` のゼロ初期化で 0.0）。
    /// evaluate_lighting は shadow_mask_valid<=0 のときマスク対象ライトでもインライン rt_shadow_factor へ
    /// フォールバックする（マスクは deferred の全画面パスでしか焼けないため。screen_gi.a と同じ流儀）。
    shadow_mask_valid: f32,

    // ─── 情報系（ライティングには使わず、合成＝第 3 層のための素材）─────────

    /// アクタ単位のセマンティックタグ（0..15。0 = タグ無し）。
    /// 「このアクタは敵」「インタラクト可能」といった**意味**を 1 ピクセル単位で
    /// 引けるようにするための値。ジオメトリパスでは VertexOutput.render_tag 由来、
    /// ライティングパスでは G-Buffer RT3.a のアンパック由来。
    /// G-Buffer に載らないパス（フォワード半透明）ではゼロ初期化のまま＝タグ無し。
    render_tag:  u32,

    /// シェーディングモデル ID（0 = DefaultPBR）。マテリアル単位。
    /// 将来トゥーン等をライティング段で分岐させるための識別子で、現状は常に 0。
    /// 格納先は render_tag と同じ RT3.a（ビットパック）。
    shading_model: u32,

    /// 汎用ユーザーデータ（0..1）。マテリアルが自由な意味を載せる回線
    /// （濡れ・ダメージ・経年劣化など。エンジンは意味を定めない）。
    /// G-Buffer では RT2.a（Rgba8Unorm＝8bit・1/255 刻み）に焼く。
    user_data:   f32,
}
