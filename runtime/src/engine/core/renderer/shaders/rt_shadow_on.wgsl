// ============================================================
// rt_shadow_on.wgsl  —  インラインレイトレ影 本体（RT 対応パイプライン用）
//
// RT 対応 GPU のパイプライン（mesh_rt.toml / skinned_mesh_rt.toml）に連結される。
// group 4 binding 6 に TLAS（acceleration_structure）を宣言し、フラグメントの
// ライトループから表面→ライト方向の遮蔽レイ（rayQuery）を飛ばす。
//
// 【バインドグループ】group 4（ライト binding0/1 ＋ シャドウ binding2〜5 と同居）
//   binding 6: acceleration_structure（TLAS。Rust: rt_shadow.rs / lighting.rs）
// max_bind_groups=5（group 0〜4）環境に適合（新グループを増やさず binding 追加のみ）。
//
// 関数シグネチャは rt_shadow_off.wgsl と一致させること。
//
// ============================================================
// ## 3 本の法線の役割分担（最重要・ここを崩すと影が破綻する）
//
//   N  (Surface.normal)        : 法線マップ適用後のシェーディング法線。**BRDF 専用**。
//                                影の計算では一切使わない（本ファイルへは渡ってこない）。
//   Nv (Surface.vertex_normal) : 補間頂点法線（法線マップ前）。三角形をまたいで**滑らか**。
//                                → 影の「減衰カーブ」を決める判定に使う:
//                                   ・シャドウターミネータのランプ（smoothstep）
//                                   ・レイ原点の固定量法線オフセット（押し出し方向）
//                                   ・円錐サンプルの地平線判定（dot(nv,dir)）
//   Ng (Surface.geo_normal)    : 画面微分 cross(dpdx,dpdy) 由来の**フラットな面法線**。
//                                三角形内で一定＝面境界で不連続。
//                                → 「レイ原点を自分の三角形の平面から確実に浮かせる」目的**だけ**:
//                                   ・原点の最小クリアランス（平面から浮かせる）
//                                Ng を**レイの向き**や**遮蔽判定**に使ってはならない（後述）。
//
// なぜ分けるか（退行の原因）: 以前は減衰判定（`dot(Ng,L) <= 0 → return 0.0` の硬い
// カットオフ／地平線カリング）にも Ng を使っていた。Ng は三角形ごとに一定なので、
// 滑らかな曲面（カーテンの折り目など）では隣り合う三角形で判定が 0/1 に飛び、
// **面境界に沿った硬い黒帯（ファセット状の影）**になった。判定を Nv に移すと、
// 判定値が面をまたいで連続に変化するため黒帯が消える。
//
// ## 地平線より下を向くサンプルの扱い（光漏れの再発防止・最重要）
// 円錐サンプル（およびターミネータ帯の中心方向）は、面の地平線より下＝面の裏側を
// 向くことがある。これを **Ng 方向へ「起こす（リフトする）」実装は光漏れを生む**:
//   厚みのない一枚布（カーテン）にライトが裏側からあたるとき、こちら側の面から見た
//   ライト方向は必ず面の裏側にある。リフトはそのレイを手前側（Ng 側）へ折り返すため、
//   レイは布を貫通せずに空へ逃げ、「遮蔽なし＝照らされている」と誤判定される。
//   折り目で Nv がわずかに光側を向くと、ターミネータのランプが 0 にならないため、
//   その誤判定が**点描状の光漏れ**として現れる（この症状の実際の原因だった）。
// 現実装は起こさない。**地平線より下を向くサンプルは遮蔽（0）として数える**:
//   - 幾何的に面の裏側から光は来ない。0 と数えるのは近似ではなく正しい可視性である。
//   - 分母は常に samples 本のままなので、「カリングで分母が暴れる＝ディザノイズ」も出ない。
//   - 判定に使う法線は**滑らかな Nv**（Ng だと三角形ごとに判定が飛び、黒帯が再発する）。
//
// ## ソフトシャドウ
// 面光源の見込み半径（cone_radius）に応じて円錐内へ複数レイを分散する。
//   cone_radius=0 のとき従来どおりハード 1 本へ分岐（コスト増ゼロ）。
//   cone_radius>0 のときは本数を適応化（RT_SHADOW_SAMPLES_MIN..MAX）する。
//   ライト種別ごとの cone_radius の求め方と上限クランプは lighting_eval.wgsl 側
//   （RT_SHADOW_MAX_CONE_RADIUS）。
//
// ## ノイズについて（ディザ状のまだら）
// サンプル方向はピクセルごとに IGN で回転させるため、遮蔽率の量子化がそのまま
// 空間ノイズになる。以下でノイズを抑える:
//   - サンプル数を cone_radius に応じて適応化（4〜16 本）
//   - cone_radius に上限（lighting_eval.wgsl）を設けて円錐の発散を止める
//   - **平均の母数（分母）をピクセル間で変えない**（常に samples 本）
// 旧実装は「地平線より下のサンプルを母数から除外」していたが、除外本数はピクセルごとの
// 回転に依存して変わるため、分母自体がピクセル間でばらつき、それ自体がノイズ源だった。
// 現実装は除外しない。地平線より下のサンプルは **0（遮蔽）として母数に数える**ので、
// 分母は常に samples で一定であり、かつ光漏れも起きない（上記参照）。
// ============================================================

/// group 4 binding 6: RT 影用 TLAS。
@group(4) @binding(6) var rt_accel: acceleration_structure;

/// group 4 binding 14: TLAS インスタンス順の平均アルベド（.rgb）＋パック済み α/transmission（.a）。
/// 色付き影（半透明レイヤー越しの光を染める）で、半透明ヒットの custom_data を添字に引く。
/// GI compute（binding4）と RT リフレクション（別 group）が読むのと同一バッファ（rt_shadow.rs 所有）。
/// - `.rgb`: 生の平均アルベド。**GI/反射が「生アルベド」として読む共有値。意味を変えてはならない**。
/// - `.a`  : 色付き影専用。α（base_color_factor.a）と transmission を各 8bit 固定小数で相乗り
///           （rt_shadow.rs::pack_shadow_alpha_transmission と対。GI/反射は .a を読まないため不干渉）。
@group(4) @binding(14) var<storage, read> rt_shadow_albedo: array<vec4<f32>>;

// ─── 定数 ────────────────────────────────────────────────────

/// レイ最小距離。自己交差を避けるための下限（法線オフセットと併用）。
const RT_SHADOW_TMIN: f32 = 0.001;

/// 自己交差防止の原点オフセット量（ワールド単位）。押し出しは**補間法線 Nv 方向**の固定量。
/// Nv は面をまたいで滑らかなので、オフセット量も滑らかに変化する（＝バイアス由来の
/// 不連続＝黒帯が出ない）。Nv は必ず Ng と同じ半球にある（surface_gather.wgsl が
/// faceforward 済み）ため、この押し出しは常に三角形の平面から「上」へ向かう。
///
/// 【スロープスケールを廃止した理由（光漏れの再発防止・最重要）】
/// 以前はオフセットを NORMAL_BIAS / max(dot(Nv,L), 下限 0.1) と除算でスケールし、グレージング
/// 入射（面すれすれの光）ほど押し出し量を増やしていた。しかし下限 0.1 では最大 0.02/0.1 =
/// 0.2 ワールド単位（Sponza はメートル単位なので 20cm）まで膨らむ。カーテンの折り目どうしの
/// 間隔は数 cm しかないため、レイ原点が**隣の布ヒダを突き抜けた位置**へ飛び、本来遮蔽する
/// はずの布がレイの後方に落ちて当たらなくなる ⇒ 幾何的に光を向いた折り目のフチだけが光る
/// リム状の光漏れになった。よってスロープスケールは廃止し、オフセットは固定 2 項
/// （NORMAL_BIAS + GEO_CLEARANCE）だけにする。総オフセットは最大でも 0.02 + 0.005 =
/// 0.025 ワールド単位（2.5cm）に静的に収まり、薄い布のヒダ間隔を越えない。
///
/// 【グレージング角の自己交差アクネを廃止後も許容できる根拠（現在の防御構成）】
///   (a) 幾何ゲート（lighting_eval.wgsl の geo_gate）が幾何的に裏を向いた面を落とすため、
///       グレージングで自己交差しうる面の大半は影の計算に入る前に寄与が消える。
///   (b) 下のシャドウターミネータランプ（dot(nv,l) < BAND の帯を smoothstep で減光）が、
///       浅い入射角＝アクネの出やすい帯の寄与をそもそも大きく落とす。
/// この 2 段でグレージング時のアクネは実用上問題にならない範囲へ抑えられている。
const RT_SHADOW_NORMAL_BIAS: f32 = 0.02;

/// 幾何法線 Ng 方向の**最小クリアランス**（ワールド単位）。
/// Nv 方向の押し出しだけでは、Nv が三角形の平面から大きく傾いている（＝曲率の高い箇所）とき
/// 平面からの実効クリアランスが dot(Nv,Ng) 倍に落ちる。フラットな Ng 方向へ固定量を足すことで
/// 「その三角形の平面から必ずこの距離だけ浮いている」ことを保証する（自己交差＝真っ黒の防止）。
/// 値 0.005 の根拠: RT_SHADOW_TMIN(0.001) の 5 倍。tmin による打ち切りに埋もれず、かつ
/// RT_SHADOW_NORMAL_BIAS(0.02) の 1/4 なので接触影（接地点の影）を痩せさせない。
const RT_SHADOW_GEO_CLEARANCE: f32 = 0.005;

/// 影のインスタンスカリングマスク。TLAS 側インスタンスマスクとの AND が 0 の
/// インスタンスは素通りする（＝影を落とさない）。
/// 不透明ビットのみを立て、Blend / Mask マテリアルを影のオクルーダから除外する。
/// Rust 側 `rt_shadow.rs::RT_MASK_OPAQUE` と値を一致させること
/// （rt_shadow.rs のユニットテスト `wgsl_cull_mask_matches_rust_mask` が両者の一致を検証する）。
const RT_SHADOW_CULL_MASK: u32 = 0x01u;

/// 半透明レイヤー（Blend/Mask）のインスタンスマスク（色付き影の第 2 クエリ専用）。
/// Rust 側 `rt_shadow.rs::RT_MASK_NON_OPAQUE`（0x02）と一致させること。
/// 不透明の遮蔽（0x01）で影が確定していないピクセルだけ、このマスクで半透明レイヤーを
/// 追加トレースし、ガラスの透過色を影に乗せる。
const RT_TRANSLUCENT_CULL_MASK: u32 = 0x02u;

/// 色付き影の透過色を累積する最大ヒット数（tmin を先へ進めながら再トレースする回数上限）。
/// inline ray query は最近ヒットしか返さないため、重なった半透明レイヤーを N 枚まで貫く。
///
/// 経路ごとに 2 値を分ける（半影品質とコストの綱引き）:
/// - CENTER（ハード影の中心 1 本）: 4 枚。中心 1 本しか飛ばさないのでコスト余裕があり、
///   ガラスが数枚重なっても色が乗る。これ以上は稀かつコスト線形増のため打ち切る。
/// - SAMPLE（ソフト影の円錐サンプルごと）: 2 枚。ソフト影は最大 RT_SHADOW_SAMPLES_MAX(16) 本 ×
///   最大 4 灯（半解像度マスク）で走るため、貫通数を各サンプルで掛けると最悪レイ数が跳ね上がる。
///   実測上「半透明レイヤーが 3 枚以上重なる」画素は希少で、そこに貫通数を割くより、遮蔽されて
///   いない全サンプルで tint を平均して**半影に色の減衰を付ける**方が見た目の効きが大きい。
///   ゆえにサンプルごとは 2 枚に抑え、最悪コスト（16×2）を有界に保つ。
/// ループの静的上限は両者の大きい方（CENTER）で縛り、SAMPLE 経路は早期 break で打ち切る。
const RT_TRANSLUCENT_MAX_HITS_CENTER: u32 = 4u;
const RT_TRANSLUCENT_MAX_HITS_SAMPLE: u32 = 2u;

/// 再トレース時に tmin を「直前ヒットの t＋この量」へ進める微小オフセット（同一面の再ヒット防止）。
/// RT_SHADOW_TMIN と同オーダー。ヒット面の厚みより十分小さく、次のレイヤーは取りこぼさない。
const RT_TRANSLUCENT_T_STEP: f32 = 0.002;

/// 色付き影のパック定数（Rust の rt_shadow.rs::pack_shadow_alpha_transmission と対）。
/// rt_shadow_albedo[.a] へ α（base_color_factor.a）と transmission を各 8bit 固定小数で相乗りさせる。
/// .rgb は GI/反射が読む生アルベドなので触れず、GI が読まない .a にだけ 2 値を詰めている。
///   パック: a_q=round(α*QUANT), t_q=round(tr*QUANT), .a = a_q*RADIX + t_q（最大 65535＝f32 整数域内）
///   デコード（下記 rt_trace_translucent_tint）: a_q=floor(.a/RADIX), t_q=.a - a_q*RADIX
/// 値は Rust 側定数と一致させること（rt_shadow.rs のユニットテスト shadow_pack_roundtrip_and_transmittance が担保）。
/// 1 チャンネルの量子化段数（8bit）。Rust の rt_shadow.rs::SHADOW_PACK_QUANT と一致させること。
const SHADOW_PACK_QUANT: f32 = 255.0;
/// α を上位バイトへ寄せる基数（t_q と衝突しない）。Rust の SHADOW_PACK_RADIX と一致させること。
const SHADOW_PACK_RADIX: f32 = 256.0;

/// ソフトシャドウの最小サンプル数（cone_radius > 0 のときに必ず飛ばす遮蔽レイの本数）。
/// 4 本＝遮蔽率が 5 段階に量子化される。細いペナンブラ（cone_radius が小さい）ならこの
/// 粗さは影の境界の数ピクセルに閉じるため知覚されない。
const RT_SHADOW_SAMPLES_MIN: u32 = 4u;

/// ソフトシャドウの最大サンプル数（適応サンプルの上限）。
/// ループ回数を必ずこの定数で縛ること（cone_radius がどれだけ大きくても 1 ライトあたりの
/// レイ本数がこれを超えない＝最悪コストが静的に決まる）。
/// 16 本＝遮蔽率が 17 段階になり、IGN 回転と合わせればディザが視認しづらい程度に細かい。
/// これ以上増やしてもノイズの低減は √N でしか効かず、コストは線形に増える。
const RT_SHADOW_SAMPLES_MAX: u32 = 16u;

/// 適応サンプル数の傾き: 「サンプル 1 本が受け持つ cone_radius の幅」。
/// samples = clamp(ceil(cone_radius / この値), MIN, MAX) とすることで、ペナンブラが
/// 広い（＝1 サンプルあたりの担当立体角が大きい＝量子化が目立つ）ほど本数を増やす。
///
/// 値 0.03125 の根拠: lighting_eval.wgsl の RT_SHADOW_MAX_CONE_RADIUS(=0.5) を
/// RT_SHADOW_SAMPLES_MAX(=16) で割った値。すなわち「クランプ上限まで広がった円錐で
/// ちょうど最大サンプル数に到達する」ように傾きを決めている。3 定数の整合は Rust 側の
/// ユニットテスト（rt_shadow.rs::wgsl_soft_shadow_constants_are_consistent）が担保する。
const RT_SHADOW_CONE_RADIUS_PER_SAMPLE: f32 = 0.03125;

/// シャドウターミネータのランプ幅（**Nv 基準**の dot(Nv,L) の帯幅）。
/// dot(Nv,L) が 0（幾何的に光に背を向ける）→ この値（十分に光を受ける）にかけて
/// 遮蔽率を smoothstep で 0 → 1 に上げる。硬いカットオフ（<=0 で即 0.0）の置き換え。
///
/// なぜ 0 に落とす必要があるのか（＝ランプの終端を 0 にする根拠）:
///   法線マップで N が持ち上がると、幾何的には光に背を向けている面でも BRDF の
///   ndl = dot(N,L) が正になり、**壁の裏側から光が漏れる**。これを止められるのは影の係数だけ
///   （ndl は N 由来なので止まらない）。ゆえに遮蔽率は 0 まで落とす。ただし**滑らかに**落とす。
///
/// 値 0.15 の根拠: cos = 0.15 → 光の入射角 ≈ 81.4°。この帯の中では BRDF の ndl も 0.15 以下、
/// すなわち最大照度の 15% 未満しか光を受けない。そこを滑らかに減光しても「面が急に暗くなる」
/// 帯としては知覚されない。逆にこれ以上広げると、正当に照らされている領域まで影の係数で
/// 減光してしまい、全体が暗く沈む。
///
/// ## サンプルの地平線判定との二重適用にならない根拠
/// この帯は **円錐の中心方向 L** の関数（dot(Nv,L)）、地平線判定は **各サンプル方向** の関数。
/// 役割が違い、効く領域もほぼ重ならない:
///   - ハード影（cone_radius = 0）: サンプルは L 一本のみで dot(Nv,L) > 0 が保証済み
///     （関数冒頭の早期 return）。地平線判定は一度も発火しない ⇒ ランプ単独。
///   - dot(Nv,L) >= BAND（＝ランプ = 1.0、正当に照らされる領域）: ランプは減光しない。
///     ここで円錐の一部が地平線より下に落ちるのは、**光源の円盤の一部が本当に地平線の下に
///     沈んでいる**からで、その分の減光は物理的に正しい可視性 ⇒ 過剰な暗化ではない。
///   - 両方が 1 未満になるのは dot(Nv,L) ∈ (0, 0.15) の帯だけ。この帯は BRDF の ndl 自体が
///     0.15 未満（最大照度の 15% 未満）で、そもそもほぼ黒い領域である。ここでランプが
///     必要なのは「法線マップで N が持ち上がり ndl が正になる」ケースを潰すため（上記）で、
///     地平線判定だけでは 0 に落ちきらない（dot(Nv,L) → +0 でも円錐の半分は地平線の上に残る）。
///     ゆえに両者は重複ではなく、それぞれ別の抜け道を塞いでいる。
const RT_SHADOW_TERMINATOR_BAND_COS: f32 = 0.15;

/// 円周率（本ファイルで自己完結させる）。
const RT_SHADOW_PI: f32 = 3.14159265359;
/// 黄金角（ラジアン）。Vogel ディスク分布のサンプル間角度。
const RT_SHADOW_GOLDEN_ANGLE: f32 = 2.39996323;

// ─── 遮蔽判定 ────────────────────────────────────────────────

/// RT 影が有効か。RT パイプラインでは LightMeta.rt_shadows で実行時分岐する。
fn rt_shadow_enabled() -> bool {
    return u_light_meta.rt_shadows != 0u;
}

/// 単一の遮蔽レイを飛ばして遮蔽率を返す（1.0=非遮蔽 / 0.0=遮蔽）。
/// 不透明ジオメトリのみを対象とし、最初のヒットで打ち切る。
fn rt_trace_occlusion(o: vec3<f32>, dir: vec3<f32>, tmax: f32) -> f32 {
    var desc: RayDesc;
    // 最初のヒットで打ち切り（影は「何かに当たったか」だけが必要）。
    desc.flags     = RAY_FLAG_TERMINATE_ON_FIRST_HIT;
    desc.cull_mask = RT_SHADOW_CULL_MASK; // 不透明インスタンスのみを対象（Blend/Mask は素通り）
    desc.tmin      = RT_SHADOW_TMIN;
    desc.tmax      = max(tmax, RT_SHADOW_TMIN);
    desc.origin    = o;
    desc.dir       = dir;

    var rq: ray_query;
    rayQueryInitialize(&rq, rt_accel, desc);
    rayQueryProceed(&rq);
    let hit = rayQueryGetCommittedIntersection(&rq);

    // ヒットあり（TRIANGLE 等）＝遮蔽。ヒットなし（NONE）＝照射。
    if hit.kind != RAY_QUERY_INTERSECTION_NONE {
        return 0.0;
    }
    return 1.0;
}

// ─── 拡散透過の厚み減衰（Beer-Lambert）＋遮蔽 ──────────────────
//
// 拡散透過（逆光透け, lighting_eval.wgsl の逆光項）は薄物（葉・布）向けに「影・幾何ゲート
// 非適用」で実装したが、厚い不透明物体（石のルーク等）に付けると (1) 厚み減衰が無く前面全体が
// 一様に発光して「薄い紙の塔」に見え、(2) 自身の厚い胴で自己遮蔽されている部分まで明るくなる。
// これを是正するため、シェーディング点→光源方向へ 1 本レイを飛ばし、不透明ジオメトリの内部を
// 通過した積算距離に Beer-Lambert 則 exp(-σ·thickness) を掛ける係数を供給する（下記関数）。
// 別の不透明遮蔽物も内部区間として厚みに積まれるため、遮蔽部が明るくならない（(2) の解消）。

/// 拡散透過の厚み減衰（Beer-Lambert）の吸収係数 σ [1/ワールド単位]。
/// exp(-σ·thickness) を逆光透け項へ掛ける。thickness は光源レイが不透明内部を通過した積算距離。
///
/// 【σ の決め方（このシーンのスケール感）】ワールド単位はメートル（Sponza 基準・同シーン内に配置）。
///   RenderTest.scene のチェスセット（ABeautifulGame を約 3 倍スケールで配置）の駒は厚み数 cm〜十数 cm。
///   σ=12 のとき:  2cm→exp(-0.24)=0.79（ほぼ非減衰） / 8cm→exp(-0.96)=0.38 /
///                 30cm→exp(-3.6)=0.027（ほぼ 0）。
///   ⇒「数 cm はほぼ非減衰・30cm 超でほぼ 0」のオーダーに収まる。薄い葉・布（厚み <1cm・片面）は
///     ほぼ 1.0＝従来どおり透け、厚いルークは中心ほど暗い厚み勾配が付いて一様発光にならない。
/// 【将来のマテリアルパラメータ化余地】KHR_materials_volume の attenuationDistance 相当を持たせるなら
///   σ = 1/attenuationDistance をマテリアルから供給する。現状は全マテリアル共通の名前付き定数。
const DT_THICKNESS_SIGMA: f32 = 12.0;

/// 内部通過距離を積算する front→back ペアの最大数（コスト有界・マジックナンバー禁止）。
/// 凸物体なら 1 ペアで足りるが、王冠状のルーク上部など複数の中実区間を跨ぐ形状のため 4 ペアまで積む。
/// これを超える区間は無視する（既に十分厚く exp(-σ·t)≈0 に達しており打ち切っても見た目に影響しない）。
const DT_MAX_INTERIOR_PAIRS: u32 = 4u;

/// 厚み積算トレースの静的ループ上限（= 2×ペア数。1 ペアあたり front と back で最大 2 ヒット）。
/// 片面ジオメトリや順序の乱れで未ペアのヒットが混じっても、1px あたりのレイ本数がこの定数を
/// 超えないことを保証する（最悪コストが静的に決まる）。
const DT_TRACE_MAX_HITS: u32 = 8u;

/// レイ原点を -L 方向（光源と反対＝逆光側）へ後退させる微小量 [ワールド単位]。
/// シェーディング点 P は透過物体の**逆光面**の上にある。P からそのまま +L へ飛ばすと自分の入射面
/// （近面）が t≈0 に落ちて tmin で切り捨てられ、最初のヒットが**出射面（back）**になってペアが崩れる
/// （＝厚み 0 と誤判定して前面が発光する＝直そうとしている症状そのもの）。原点を -L 側へ僅かに
/// 後退させると近面が t≈backoff の front ヒットとして確実に拾え、front→back ペアが成立する。
/// 逆光透けは back=-dot(N,L) が大きい（＝-L が N とよく揃う）ほど強く効くので、-L 後退はまさに効きが
/// 強い所ほど良く近面をクリアする（自己整合）。厚みは exit_t-enter_t で相殺され backoff 自体は積算厚みに
/// 含まれない（原点固定・両 t は同一原点基準）。値 0.02 は影の RT_SHADOW_NORMAL_BIAS と同オーダー。
const DT_ORIGIN_BACKOFF: f32 = 0.02;

/// 同一面の再ヒット回避のため、次レイの tmin を「直前ヒット t＋この量」へ進める微小オフセット。
/// RT_SHADOW_TMIN と同オーダー。ヒット面の厚みより十分小さく、次の界面を取りこぼさない。
const DT_TRACE_T_STEP: f32 = 0.002;

/// 拡散透過（逆光透け）の厚み減衰係数を返す（1.0=非減衰＝従来動作 / 0..1=厚みで減衰）。
/// シェーディング点 P から光源方向 L へ 1 本のレイを進め、不透明ジオメトリ（0x01）の内部を通過した
/// 積算距離に Beer-Lambert 則 exp(-σ·thickness) を適用する。lighting_eval.wgsl の逆光項へ乗じる。
/// - 不透明ヒットを front（入射）→ back（出射）でペア化し、(back_t - front_t) を厚みへ加算する。
/// - 片面ジオメトリ（葉・布）は back が続かない＝厚み 0＝減衰なし（薄物は従来どおり透ける／正しい）。
/// - 半透明（0x02）は cull_mask で除外＝素通し（色付き影と役割が被るため。ファイル上部の規約）。
/// - 別の不透明遮蔽物も内部区間として厚みに積算＝遮蔽部が明るくならない（ユーザー報告②の解消）。
///
/// 引数:
///   p    : シェーディング点のワールド座標（逆光面上）。
///   l    : 面→光源方向（正規化済み）。
///   tmax : レイ最大距離（directional=大定数 RT_DIR_TMAX / 局所光=光源距離。既存影レイと同流儀）。
fn rt_diffuse_transmission_factor(p: vec3<f32>, l: vec3<f32>, tmax: f32) -> f32 {
    // 原点を逆光側（-L）へ微小後退（近面を front ヒットとして確実に拾うため。定数コメント参照）。
    let o = p - l * DT_ORIGIN_BACKOFF;

    var thickness = 0.0;    // 不透明内部の積算通過距離（ワールド単位）
    var enter_t   = -1.0;   // 直近の未ペア front ヒットの t（-1=保留なし）
    var pairs     = 0u;     // 成立した front→back ペア数
    var tmin      = RT_SHADOW_TMIN;

    // ループ上限は必ず定数 DT_TRACE_MAX_HITS で縛る（未ペアのヒットが続いても静的上限を超えない）。
    for (var i: u32 = 0u; i < DT_TRACE_MAX_HITS; i = i + 1u) {
        var desc: RayDesc;
        desc.flags     = RAY_FLAG_NONE;       // 最近ヒットを取り、次反復で tmin を先へ進める（TERMINATE なし）。
        desc.cull_mask = RT_SHADOW_CULL_MASK; // 不透明のみ（半透明 0x02 は素通し）。
        desc.tmin      = tmin;
        desc.tmax      = max(tmax, RT_SHADOW_TMIN);
        desc.origin    = o;
        desc.dir       = l;

        var rq: ray_query;
        rayQueryInitialize(&rq, rt_accel, desc);
        rayQueryProceed(&rq);
        let hit = rayQueryGetCommittedIntersection(&rq);
        if hit.kind == RAY_QUERY_INTERSECTION_NONE {
            break; // これ以上の不透明ヒットは無い。
        }

        if hit.front_face {
            // 入射面（中実へ入る）。直前の未ペア front は上書きする（連続 front の保険）。
            enter_t = hit.t;
        } else {
            // 出射面（中実から出る）。直前に front があればペア成立＝その区間長を厚みへ加算。
            if enter_t >= 0.0 {
                thickness += max(hit.t - enter_t, 0.0);
                enter_t = -1.0;
                pairs = pairs + 1u;
                if pairs >= DT_MAX_INTERIOR_PAIRS {
                    break; // 十分厚い（exp(-σ·t)≈0）。以降は打ち切ってコストを有界に保つ。
                }
            }
            // front を伴わない back（片面ジオメトリの裏／原点が中実内から始まった等）は無視する。
        }

        // 次のレイ: tmin を直前ヒットの少し先へ進める（同一面の再ヒット回避）。
        tmin = hit.t + DT_TRACE_T_STEP;
    }

    // Beer-Lambert。厚み 0（薄物・非交差）なら exp(0)=1.0＝従来動作へ縮退する。
    return exp(-DT_THICKNESS_SIGMA * thickness);
}

// ── 半透明レイヤーの透過色 rt_trace_translucent_tint は【別ファイル（tint バリアント）】が供給する ──
//
// 色付き影（B3）で「平均アルベド」経路と「バインドレス＝ヒット点テクスチャ実サンプル＋Mask アルファ抜き」
// 経路を 1 本の rt_shadow_factor から差し替えられるよう、tint 関数はこのファイルから分離した:
//   - rt_shadow_tint_avg.wgsl      : 平均アルベド storage（rt_shadow_albedo, binding14）で染める従来経路。
//                                    forward（mesh_rt/skinned）・非バインドレスの deferred/shadow_mask が連結。
//   - rt_shadow_tint_bindless.wgsl : group3 のバインドレス資源（instance_table/UV/index/テクスチャ配列）で
//                                    ヒット点 UV のベースカラーを実サンプルし、Mask 面はテクスチャ α を
//                                    alpha_cutoff と比較して葉の形の影を落とす。deferred/shadow_mask の
//                                    バインドレス影バリアント（対応 GPU）が連結。
// どちらも `fn rt_trace_translucent_tint(o, dir, tmax, max_hits) -> vec3<f32>` の同一シグネチャで、
// rt_shadow_factor（下記）から前方参照で呼ばれる（WGSL はモジュール内の関数を宣言順に依存しない）。
// 連結時は本ファイルの後ろにいずれか 1 本だけを必ず並べること（両方／どちらも無しは重複定義／未定義になる）。

/// Interleaved Gradient Noise（Jimenez）。フラグメント座標から [0,1) の擬似乱数を返す。
/// 時間項を含まないため TAA 非前提でも時間的ちらつきが出ない（空間的にのみ変化する）。
fn rt_shadow_ign(p: vec2<f32>) -> f32 {
    return fract(52.9829189 * fract(dot(p, vec2<f32>(0.06711056, 0.00583715))));
}

/// ペナンブラの広さ（cone_radius）に応じた適応サンプル数を返す。
/// - cone_radius が小さい（＝影の境界が細い）ほど少なく、広いほど多く。
/// - 戻り値は必ず [RT_SHADOW_SAMPLES_MIN, RT_SHADOW_SAMPLES_MAX] に収まる。
/// 呼び出し側は cone_radius > 0 のときだけ使う（0 はハード 1 本経路）。
fn rt_shadow_sample_count(cone_radius: f32) -> u32 {
    // 1 本が受け持つ円錐幅を RT_SHADOW_CONE_RADIUS_PER_SAMPLE に保つ本数を求め、上下限で締める。
    let raw = ceil(cone_radius / RT_SHADOW_CONE_RADIUS_PER_SAMPLE);
    let n   = clamp(raw, f32(RT_SHADOW_SAMPLES_MIN), f32(RT_SHADOW_SAMPLES_MAX));
    return u32(n);
}

/// ベクトル v に直交する任意の単位ベクトルを 1 本作る（円錐サンプル基底の第 1 軸）。
fn rt_shadow_perp(v: vec3<f32>) -> vec3<f32> {
    // v と平行になりにくい軸を選んで外積を取る。
    let a = select(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), abs(v.x) < 0.9);
    return normalize(cross(a, v));
}

/// 表面からライト方向 l へ遮蔽レイを飛ばし、遮蔽率を返す。
/// - 戻り値: 1.0=完全照射 / 0.0=完全遮蔽 / 中間=ペナンブラ。
/// - `origin`     : 表面のワールド座標。
/// - `ng`         : **幾何法線**（フラットな面法線）。**レイ原点の最小クリアランス専用**。
///                  レイの向き・遮蔽判定には一切使わない（使うと光漏れ／黒帯が出る）。
/// - `nv`         : **補間頂点法線**（法線マップ前・滑らか）。減衰判定に使う
///                  （ターミネータランプ／レイ原点の法線オフセット方向／サンプルの地平線判定）。
/// - `l`          : 面から光源への方向（正規化済み）。
/// - `tmax`       : レイの最大距離。directional は大定数、局所光はライトまでの距離。
/// - `cone_radius`: 面光源の見込み半径（tan 相当）。0 でハード 1 本、>0 で円錐内 N サンプル平均。
/// - `frag_xy`    : フラグメント座標（サンプル回転のノイズ源。時間項なし）。
/// - `deterministic_tint`: ソフト経路（cone_radius>0）での**色付き tint の評価方法**を切り替える。
///     false（マスク経路 shadow_mask.wgsl）: 従来どおりサンプルごとに tint を評価して平均する。
///                生成直後はノイジーだが、後段の影マスク専用バイラテラルブラーが空間的に均すため、
///                tint にも半影の色勾配が乗る（高品質）。
///     true （インライン経路 lighting_eval.wgsl）: tint をサンプルごとに評価せず、中心方向 L の
///                決定的評価を 1 回だけ行って可視性平均へ一律に掛ける。バイラテラルで均されない
///                インライン経路では、per-sample tint の IGN 依存ノイズ（色付き斑点）が scene_hdr へ
///                直に焼かれてしまうため、それを断つ。tint のペナンブラ勾配は失われる（既知の品質差）。
///     可視性（スカラー）の Vogel ソフトサンプリングはどちらでも維持する（影の柔らかさは不変）。
///     ハード経路（cone_radius=0）はこのフラグの影響を受けない（元々中心 tint 1 本のため）。
///
/// ## 手順
/// 1. ターミネータ係数 = smoothstep(0, BAND, dot(nv, l))。0 なら即 0.0（レイも飛ばさない）。
///    硬いカットオフ（dot <= 0 で即 0）をやめ、滑らかな帯で 0 へ落とす。判定は**滑らかな nv**
///    なので、三角形の面境界に沿った黒帯（ファセット）が出ない。
/// 2. レイ原点 = origin + nv * (固定法線バイアス) + ng * (最小クリアランス)。両者とも固定量で、
///    総オフセットは最大でも NORMAL_BIAS + GEO_CLEARANCE = 0.025 ワールド単位に収まる
///    （スロープスケールは廃止。グレージングでオフセットが 20cm まで膨らんで薄い布のヒダ間隔を
///    越え、レイ原点が隣のヒダを突き抜けて遮蔽が外れる＝リム状の光漏れを防ぐため）。
///    前者が自己交差の主防御、後者は「その三角形の平面から必ず浮いている」ことを保証する。
/// 3. 円錐内へ Vogel ディスク分布でレイを分散する。地平線（dot(nv,dir) <= 0）より下を向く
///    サンプルは**レイを飛ばさず 0（遮蔽）として加算**する。方向を起こしてはならない
///    （＝一枚布の裏側へ抜けるべきレイが手前へ逃げ、光漏れになる。ファイル冒頭参照）。
///    サンプルは 1 本も捨てない（分母は常に samples ＝ ディザノイズが出ない）。
/// 4. 半透明レイヤーの透過色（RGB）を評価する。可視性（スカラー）はどちらの経路でも
///    Vogel サンプルの平均（ソフトな半影）で、tint の扱いだけを deterministic_tint で切り替える。
///    - ハード経路（cone_radius=0）: 中心方向 L の 1 本だけ透過色を評価（最大 CENTER=4 枚貫通）。
///    - ソフト経路（cone_radius>0）:
///        deterministic_tint == false（マスク経路）: 遮蔽されていない各サンプル方向 dir で透過色を
///          評価（最大 SAMPLE=2 枚貫通）し、遮蔽・地平線下は寄与 0 として母数 samples で平均。
///          掠めたサンプルだけ染まるため色にも半影が付く（後段バイラテラルがノイズを均す前提）。
///        deterministic_tint == true（インライン経路）: per-sample tint は評価せず、可視性平均に
///          中心方向 L の決定的 tint 1 評価（最大 CENTER=4 枚貫通）を一律に掛ける。IGN 依存の
///          色付き斑点ノイズを断つための縮退（バイラテラルが無い経路のため。上の引数解説参照）。
///    透過色は u_light_meta.translucency_rt に色付き影ビットが立っているときだけ評価する。
///    立っていなければ tint 評価自体をスキップし従来コストのまま（白い影）。
///
/// 戻り値は vec3<f32>（RGB 透過率）。呼び出し側 lighting_eval.wgsl は `radiance *= factor` を
/// 成分ごとの積として評価する（ガラス越しの光が色を帯びる）。
fn rt_shadow_factor(
    origin:      vec3<f32>,
    ng:          vec3<f32>,
    nv:          vec3<f32>,
    l:           vec3<f32>,
    tmax:        f32,
    cone_radius: f32,
    frag_xy:     vec2<f32>,
    deterministic_tint: bool,
) -> vec3<f32> {
    // ── 1. シャドウターミネータ（滑らかなランプ）────────────────
    // 幾何的な入射角は**補間法線**で測る。フラットな面法線で測ると面境界で不連続になる。
    let ndl_v = dot(nv, l);
    // 完全に光へ背を向けている（ランプの値も 0）。レイを飛ばす意味がないので即返す。
    if ndl_v <= 0.0 {
        return vec3<f32>(0.0, 0.0, 0.0);
    }
    // 0 → BAND で 0 → 1。dot が 0 に近づくほど連続的に遮蔽へ寄せる。
    let terminator = smoothstep(0.0, RT_SHADOW_TERMINATOR_BAND_COS, ndl_v);

    // ── 2. レイ原点（自己交差防止）──────────────────────────────
    // (a) 補間法線方向の固定オフセット（滑らか）。スロープスケール（除算）は廃止した:
    //     グレージングで最大 0.2 ワールド単位（20cm）まで膨らみ、薄い布のヒダ間隔（数 cm）を
    //     越えてレイ原点が隣のヒダを突き抜け、遮蔽が外れてリム状の光漏れになったため。
    // (b) 幾何法線方向の最小クリアランス（三角形の平面から必ず浮かせる）。
    // nv と ng は同じ半球（faceforward 済み）なので、両項とも必ず平面から上向きに効く。
    // 総オフセットは最大でも NORMAL_BIAS + GEO_CLEARANCE = 0.025 ワールド単位（2.5cm）に静的に収まる。
    let o = origin
          + nv * RT_SHADOW_NORMAL_BIAS
          + ng * RT_SHADOW_GEO_CLEARANCE;

    // ── 3+4. 不透明可視性 × 半透明透過色（RGB）を同時に求める ─────
    // 色付き影（RT-Translucency）が有効か。無効なら tint 評価を一切スキップし従来コストのまま。
    let colored = (u_light_meta.translucency_rt & TRANSLUCENCY_RT_COLORED_SHADOW) != 0u;

    if cone_radius <= 0.0 {
        // ── ハードシャドウ経路（面積ゼロ・1 本）: コスト増ゼロを維持 ──
        // 方向は l そのまま。ng でリフトしてはならない（薄い面の裏側へ抜けるべきレイが
        // 手前へ折り返され、光漏れになる）。dot(nv,l) > 0 は上の早期 return で保証済み。
        let vis = rt_trace_occlusion(o, l, tmax);
        // 不透明で完全遮蔽されていない（vis>0）ときだけ、中心方向 L の透過色を 1 本累積する
        // （最大 CENTER=4 枚貫通・コスト有界）。無効・遮蔽時は白のまま（従来どおり）。
        var tint = vec3<f32>(1.0, 1.0, 1.0);
        if colored && vis > 0.0 {
            tint = rt_trace_translucent_tint(o, l, tmax, RT_TRANSLUCENT_MAX_HITS_CENTER);
        }
        // スカラー可視性 × ターミネータ係数 × 透過色。
        return vec3<f32>(terminator * vis) * tint;
    }

    // ── ソフトシャドウ経路（円錐内 N サンプル平均）──
    // ペナンブラの広さに応じたサンプル本数（MIN..MAX に必ず収まる）。
    let samples = rt_shadow_sample_count(cone_radius);

    // 円錐サンプル用の直交基底（l に垂直な 2 軸）。
    let t = rt_shadow_perp(l);
    let b = cross(l, t);
    // フラグメントごとの回転（バンディング回避）。時間項を含まないため静止画で安定。
    let rot = rt_shadow_ign(frag_xy) * 2.0 * RT_SHADOW_PI;

    // 可視性（スカラー）は deterministic_tint に関わらず Vogel サンプルの平均で求める
    //（＝影の柔らかさ／半影はどちらの経路でも同一）。tint（色）の扱いだけを切り替える:
    //   ・マスク経路（deterministic_tint == false）: サンプルごとに tint を評価して tint_sum に累積。
    //     生成直後はノイジーだが後段の影マスク専用バイラテラルブラーが均すので、色にも半影勾配が乗る。
    //   ・インライン経路（deterministic_tint == true）: per-sample tint は評価しない（IGN 依存の
    //     色付き斑点ノイズを scene_hdr へ直に焼かないため）。ループ後に中心 L の tint 1 評価だけ掛ける。
    //     旧実装はインライン経路でもサンプルごとに tint を評価しており、バイラテラルで均されないまま
    //     scene_hdr へ焼かれて**赤い斑点ノイズ**（カーテンを掠めたサンプルだけ赤 tint）になっていた。
    var vis_sum  = 0.0;                       // 非遮蔽・地平線上サンプルの可視性の総和（両経路共通）
    var tint_sum = vec3<f32>(0.0, 0.0, 0.0);  // per-sample tint 累積（マスク経路のみ使用）

    // ループ上限は必ず定数 RT_SHADOW_SAMPLES_MAX で縛る（samples は clamp 済みだが、
    // 万一の異常値でも 1 ピクセルあたりのレイ本数が静的上限を超えないことを保証する）。
    for (var i: u32 = 0u; i < RT_SHADOW_SAMPLES_MAX; i = i + 1u) {
        if i >= samples {
            break;
        }
        // Vogel ディスク: 半径 √((i+0.5)/N)、角度 i*黄金角 + 回転。
        let fi    = f32(i) + 0.5;
        let r     = sqrt(fi / f32(samples));
        let theta = f32(i) * RT_SHADOW_GOLDEN_ANGLE + rot;
        let disk  = vec2<f32>(cos(theta), sin(theta)) * r * cone_radius;
        // l を円錐内へずらして正規化（見込み半径 cone_radius のディスクを張る）。
        let dir   = normalize(l + t * disk.x + b * disk.y);

        // 地平線判定（**滑らかな nv 基準**。ng だと三角形ごとに飛んでファセット状の黒帯になる）。
        // 面の裏側を向くサンプルは幾何的に光を受け取れない。寄与 0 として**加算せず**、
        // レイも飛ばさない（トレース 1 本ぶん速い）。捨てない＝分母は samples のまま一定。
        if dot(nv, dir) <= 0.0 {
            continue;
        }
        // 不透明遮蔽レイ（従来どおり）。当たれば寄与 0（可視性・tint とも加算しない）。
        let occ = rt_trace_occlusion(o, dir, tmax);
        if occ <= 0.0 {
            continue;
        }
        // 非遮蔽サンプル: スカラー可視性を 1 加算（両経路共通）。
        vis_sum += 1.0;
        // マスク経路のときだけ、そのサンプル方向で半透明透過色を評価して累積する
        //（最大 SAMPLE=2 枚貫通）。インライン経路は per-sample tint を評価しない＝色ノイズ源を断つ。
        if colored && !deterministic_tint {
            tint_sum += rt_trace_translucent_tint(o, dir, tmax, RT_TRANSLUCENT_MAX_HITS_SAMPLE);
        }
    }

    // 分母は常に samples（地平線より下・遮蔽のサンプルも寄与 0 として母数に含む）。
    // ピクセルごとの IGN 回転で分母が変動しない＝ディザ状のまだらノイズが出ない。
    let inv = 1.0 / f32(samples);

    if deterministic_tint {
        // ── インライン経路（バイラテラル無し）: tint を中心 L の決定的 1 評価へ縮退 ──
        // colored かつ可視サンプルが 1 本でもある（vis_sum>0）ときだけ中心 tint を評価し、
        // 可視性平均へ一律に掛ける（IGN 依存の per-pixel 色ノイズが乗らない）。
        // トレードオフ: tint のペナンブラ勾配が失われ、影のシルエットどおりの硬い色境界になる。
        var tint_c = vec3<f32>(1.0, 1.0, 1.0);
        if colored && vis_sum > 0.0 {
            tint_c = rt_trace_translucent_tint(o, l, tmax, RT_TRANSLUCENT_MAX_HITS_CENTER);
        }
        return vec3<f32>(vis_sum * inv) * terminator * tint_c;
    }

    // ── マスク経路: サンプルごとの tint 平均（後段バイラテラルで均され、色にも半影勾配が付く）──
    // 非 colored のときは tint_sum が 0 のままなので、スカラー可視性平均 vis_sum を使う
    //（＝従来の「白い影」を再現。tint_sum を使うと 0 になり真っ黒になってしまう）。
    if colored {
        return (tint_sum * inv) * terminator;
    }
    return vec3<f32>(vis_sum * inv) * terminator;
}
