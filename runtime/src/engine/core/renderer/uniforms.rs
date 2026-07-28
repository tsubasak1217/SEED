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
/// WGSL レイアウト（計 304 bytes。Phase D3: G-Buffer デファード化 Phase A で
/// inv_view_proj（160→224）、速度バッファ（第2層）で prev_view_proj（224→288）、
/// Play レターボックス対応で viewport（288→304）を追加）:
/// | オフセット | フィールド      | サイズ |
/// |-----------|----------------|--------|
/// |   0       | view_proj      |  64    |
/// |  64       | view           |  64    |
/// | 128       | position       |  12    |
/// | 140       | time           |   4    |  ← 旧 _pad（W0 で転用。サイズ不変）
/// | 144       | resolution     |   8    |
/// | 152       | _pad2          |   8    |
/// | 160       | inv_view_proj  |  64    |
/// | 224       | prev_view_proj |  64    |
/// | 288       | viewport       |  16    |
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct CameraUniform {
    pub view_proj:  [[f32; 4]; 4],
    pub view:       [[f32; 4]; 4],
    /// ワールド空間でのカメラ位置（スペキュラ計算用）
    pub position:   [f32; 3],
    /// ゲーム内累計時間（秒）。`Clock::anim_time`（＝ スクリプトの `SEED.Time.ElapsedTime`）。
    ///
    /// 【なぜここか】L3 シェーディング契約の `ShadingSurface.time`（`shading_contract.wgsl`）へ
    /// 供給するための時間入力。ライティング段（フォワード = `shader_common.wgsl` /
    /// デファード = `deferred_lighting.wgsl`）が既にバインドしている唯一の共通 uniform が
    /// `CameraUniform` であり、`position: vec3` の直後には WGSL の 16 byte アライン規則により
    /// **必ず 4 byte の死に領域**が生じる。そこを名前付きの規約として転用したので、
    /// バッファサイズ（304 byte）もバインドグループレイアウトも一切変わらない
    /// （`ModelUniform.normal_matrix` 4 列目の転用と同じ考え方）。
    ///
    /// 【進む条件】Play かつ非ポーズのときだけ進む（Edit・ポーズでは停止）。
    /// エンジン全体のアニメーション時刻の規約に従う（草の揺れと同一の値）。
    ///
    /// 【書き込み側】メインカメラは `frame_renderer.rs` が `ctx.anim_time` を入れる。
    /// シャドウ・ギズモなど**ライティングを行わないパス**のカメラは 0.0 でよい。
    pub time:       f32,
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

    /// **前フレームの** ViewProjection 行列（速度バッファ＝モーションベクタ生成用）。
    ///
    /// G-Buffer の頂点シェーダが「今フレームのワールド座標を **前フレームの** カメラで
    /// 再投影した位置」を求めるのに使う。今フレームのクリップ座標との差がそのまま
    /// スクリーンスペースの移動量（モーションベクタ）になる。
    ///
    /// 【境界条件】初回フレーム・Play⇄Edit 切替・シーンロード直後・カメラテレポート時は
    /// **前フレームが存在しない／連続でない** ため、呼び出し側が `prev_view_proj = view_proj`
    /// を入れる（＝静的ジオメトリの速度が厳密に 0 になる）。この「prev=curr 初期化」が
    /// 速度爆発を防ぐ単一の仕組みであり、シェーダ側には一切の分岐を置かない。
    /// 実装は `frame_renderer.rs` の `velocity_prev_view_proj`（`Option` の None＝リセット）。
    ///
    /// 値は他のフィールドと同じ規約（列優先アップロード＝ `.transpose().data`）。
    pub prev_view_proj: [[f32; 4]; 4],

    /// **フレームバッファ基準**のビューポート矩形 `(x, y, width, height)`。
    ///
    /// Play のレターボックス／ピラーボックス時に `set_viewport` する矩形そのもの。
    /// 黒帯が無い場合（Edit モード・スケーリング無し）は `(0, 0, RT幅, RT高さ)`。
    ///
    /// 【なぜ必要か】`@builtin(position)`（frag_coord）は**フレームバッファ画素座標**だが、
    /// NDC `[-1,1]` が写像されるのは**ビューポート矩形だけ**である。したがって
    /// 「深度 → NDC → ワールド座標」の復元では、frag_coord をレンダーターゲット全面ではなく
    /// **この矩形**で正規化しなければならない。RT 全面で正規化すると、黒帯があるフレームで
    /// 復元ワールド座標が水平方向へ数メートル滑り、RT 影のレイ原点が地形内部へ潜って
    /// 画面が黒く塗り潰される（Play 限定の黒い染みの原因）。
    ///
    /// 【resolution との違い】`resolution` は RT 全面のサイズで、**テクスチャのアドレッシング**
    /// （AO / SSGI / シャドウマスクは RT 全面で生成される）に使う。用途を混同しないこと。
    ///
    /// クラスタライティングが同じ理由で `ClusterParams` に矩形を持っているのと同じ規約
    /// （`cluster_common.wgsl` のタイル境界の注記を参照）。
    pub viewport: [f32; 4],
}

/// `CameraUniform.viewport` のバイトオフセット（部分書き込み用）。
///
/// ビューポート矩形は「実サーフェスへのクランプ」がカメラ行列の確定より後になるため、
/// 確定後にこのオフセットだけを上書きする（`CameraBuffer::update_viewport`）。
pub const CAMERA_UNIFORM_VIEWPORT_OFFSET: u64 = std::mem::offset_of!(CameraUniform, viewport) as u64;

impl CameraUniform {
    pub fn identity() -> Self {
        let id = [
            [1.0, 0.0, 0.0, 0.0f32],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        Self { view_proj: id, view: id, position: [0.0; 3], time: 0.0,
               resolution: [1280.0, 720.0], _pad2: [0.0; 2], inv_view_proj: id,
               // 前フレーム行列は「prev=curr」で初期化する（速度 0）。
               prev_view_proj: id,
               // ビューポート矩形の既定は「RT 全面」＝黒帯なし（従来挙動と同値）。
               viewport: [0.0, 0.0, 1280.0, 720.0] }
    }
}

// ── モデル変換 (Group 1, Binding 0) ──────────────────────────

/// `normal_matrix` のうち「インスタンス拡張スロット」に転用する列の添字。
///
/// 【なぜ 4 列目が空いているのか】
/// 法線変換は必ず方向ベクトル（w=0）に対して行われる:
///   `normal_matrix * vec4(n, 0.0)` = col0*n.x + col1*n.y + col2*n.z + col3***0.0**
/// つまり 4 列目は数学的に結果へ一切寄与しない。実際、`normal_matrix` を参照する
/// 全 WGSL（shader_static_vertex / shader_skinned_vertex / outline / meshlet_cull）は
/// 例外なく w=0 のベクトルを掛けており、4 列目を読む箇所は存在しない。
///
/// 【なぜここへ詰めるのか】
/// `ModelUniform` は 6 個の WGSL ファイルで再宣言され、かつ全インスタンス × 全メッシュノード
/// ぶんが毎フレーム GPU へ転送される（描画帯域の主要因の 1 つ）。フィールドを 1 つ足すと
/// 128→144 byte（+12.5%）の帯域増と 6 ファイルのレイアウト同期が必要になる。
/// 既に「常にゼロが掛かる」死に領域が 16 byte あるので、そこを**名前付きの規約**として
/// 転用すれば帯域増加ゼロ・レイアウト変更ゼロで per-instance データを追加できる。
pub const INSTANCE_EXTRAS_COLUMN: usize = 3;

/// インスタンス拡張スロット内で `render_tag`（セマンティックタグ）を置く行。
/// WGSL 側は `u_model.normal_matrix[3][0]` で読む（shader_common.wgsl の定数と一致必須）。
pub const INSTANCE_EXTRAS_ROW_RENDER_TAG: usize = 0;

/// ノードのモデル変換行列 + 法線行列（＋インスタンス拡張スロット）。
///
/// WGSL レイアウト（計 128 bytes・不変）:
/// | オフセット | フィールド     | サイズ |
/// |-----------|---------------|--------|
/// |   0       | model         |  64    |
/// |  64       | normal_matrix |  64    |  ← うち末尾 16 byte（4 列目）は拡張スロット
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ModelUniform {
    /// モデル行列（ローカル空間→ワールド空間）
    pub model:         [[f32; 4]; 4],
    /// 法線変換行列 = transpose(inverse(model)) の 3x3 部分を 4x4 に拡張。
    ///
    /// **4 列目（`INSTANCE_EXTRAS_COLUMN`）は法線変換には使われない拡張スロット**であり、
    /// per-instance の付随データを載せる（現在は行 0 に `render_tag`）。
    /// 書き込みは `set_render_tag` 経由に限ること（生で触らない）。
    pub normal_matrix: [[f32; 4]; 4],
}

impl ModelUniform {
    /// 恒等変換のモデル uniform。
    ///
    /// **`model` と `normal_matrix` で 4 列目の意味が異なるため、同じ配列を共有してはならない。**
    /// - `model` は WGSL 側で `m * vec4(pos, 1.0)` と点（w=1）に掛けられる純粋な変換行列で、
    ///   4 列目は平行移動列。恒等行列としては `[0,0,0,1]` でなければならない。
    ///   ここを `[0,0,0,0]` にすると変換結果の w が 0 になり、点が「無限遠の方向ベクトル」に
    ///   化けて、カメラの平行移動が効かない（=画面に張り付く）描画になる。
    /// - `normal_matrix` は必ず w=0 の方向ベクトルに掛けられるため 4 列目は死に領域で、
    ///   インスタンス拡張スロット（`INSTANCE_EXTRAS_COLUMN`）として 0 初期化する
    ///   （タグ無し＝`RENDER_TAG_NONE` と一致）。
    pub fn identity() -> Self {
        Self {
            model: [
                [1.0, 0.0, 0.0, 0.0f32],
                [0.0, 1.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 1.0], // 平行移動列（w=1 必須）
            ],
            normal_matrix: [
                [1.0, 0.0, 0.0, 0.0f32],
                [0.0, 1.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 0.0], // インスタンス拡張スロット（既定ゼロ）
            ],
        }
    }

    /// モデル行列から生成する（法線行列は自動計算・拡張スロットはゼロ）。
    pub fn from_matrix(model: [[f32; 4]; 4]) -> Self {
        let nm = normal_matrix_from_model(&model);
        Self { model, normal_matrix: nm }
    }

    /// インスタンス拡張スロットへセマンティックタグ（`render_tag`）を書き込む。
    ///
    /// 値は「整数を素直に float 化したもの」。頂点シェーダが `@interpolate(flat)` で
    /// フラグメントへ渡し、G-Buffer 書き込み時に `pack_surface_id` でパックされる。
    #[inline]
    pub fn set_render_tag(&mut self, render_tag: u8) {
        self.normal_matrix[INSTANCE_EXTRAS_COLUMN][INSTANCE_EXTRAS_ROW_RENDER_TAG] =
            (render_tag & super::surface_id::RENDER_TAG_MASK) as f32;
    }
}

/// 法線変換行列 = cofactor(M_3x3) / det = transpose(inverse(M_3x3))
///
/// 戻り値の 4 列目は必ずゼロで返す（インスタンス拡張スロットの初期状態）。
/// 特異行列フォールバックでも 3x3 部分だけをコピーし、4 列目は汚さない。
fn normal_matrix_from_model(m: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let (a, b, c) = (m[0][0], m[0][1], m[0][2]);
    let (d, e, f) = (m[1][0], m[1][1], m[1][2]);
    let (g, h, i) = (m[2][0], m[2][1], m[2][2]);

    let det = a*(e*i - f*h) - b*(d*i - f*g) + c*(d*h - e*g);
    if det.abs() < 1e-6 {
        // 特異行列フォールバック: 元の 3x3 をそのまま使う（従来動作）。
        // ただし 4 列目は拡張スロットなので、元行列の平行移動列を持ち込まずゼロで返す
        // （法線変換は w=0 で行われるため、この違いは法線の計算結果に影響しない）。
        return [
            [m[0][0], m[0][1], m[0][2], m[0][3]],
            [m[1][0], m[1][1], m[1][2], m[1][3]],
            [m[2][0], m[2][1], m[2][2], m[2][3]],
            [0.0, 0.0, 0.0, 0.0],
        ];
    }
    let inv = 1.0 / det;

    [
        [ (e*i-f*h)*inv, -(d*i-f*g)*inv,  (d*h-e*g)*inv, 0.0],
        [-(b*i-c*h)*inv,  (a*i-c*g)*inv, -(a*h-b*g)*inv, 0.0],
        [ (b*f-c*e)*inv, -(a*f-c*d)*inv,  (a*e-b*d)*inv, 0.0],
        // 4 列目 = インスタンス拡張スロット（既定ゼロ）。法線変換には寄与しない。
        [0.0, 0.0, 0.0, 0.0],
    ]
}

// ── 前フレームのモデル変換 (G-Buffer パス専用 Group 4, Binding 0) ──────────

/// **前フレームの**モデル行列（速度バッファ＝モーションベクタ生成用）。
///
/// WGSL レイアウト（計 64 bytes・`shaders/velocity_common.wgsl` の `PrevModelUniform` と一致必須）:
/// | オフセット | フィールド   | サイズ |
/// |-----------|-------------|--------|
/// |   0       | prev_model  |  64    |
///
/// ## なぜ `ModelUniform` を拡張せず別バッファにするのか（設計判断）
/// `ModelUniform`（128 byte）は 6 個の WGSL ファイルが再宣言する ABI であり、かつ
/// **全インスタンス × 全メッシュノードぶんが毎フレーム GPU へ転送される**。ここへ
/// `prev_model` を足すと 128→192 byte（+50%）になり、速度を一切必要としないパス
/// （深度プリパス・ID パス・アウトライン・シャドウ・フォワード）まで巻き添えで
/// 帯域が増える。`normal_matrix` の 4 列目（インスタンス拡張スロット）は 16 byte しか
/// 空いておらず 4x4 行列は入らない。
///
/// そこで **G-Buffer パスだけが bind する group 4 の別ストレージバッファ**に分離した。
/// 1 エントリ 64 byte（`ModelUniform` の半分）で済み、レイアウト変更も他パスへの
/// 波及もゼロになる。配列添字は現行インスタンスバッファと**完全に同じ compact 添字**
/// （`@builtin(instance_index)`）であり、`InstancedModelBatch::update` が同一ループで
/// 両方を同順に詰めることで対応付けを構造的に保証する
/// （LOD バケットの入れ替わりでスロットがズレる＝速度爆発を原理的に起こさない）。
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct PrevModelUniform {
    /// 前フレームのモデル行列（ローカル空間→ワールド空間・列優先）。
    ///
    /// 「前フレームが無い」場合（初回・新規可視・バッチ再生成・LOD チャンク差し替え）は
    /// **今フレームと同じ行列**が入る（＝そのインスタンスの速度はカメラ由来ぶんのみになり、
    /// 決して爆発しない）。この初期化規約は `InstancedModelBatch::update` が担う。
    pub prev_model: [[f32; 4]; 4],
}

impl PrevModelUniform {
    /// 恒等変換の前フレーム行列（`ModelUniform::identity().model` と同一＝真の恒等行列）。
    pub fn identity() -> Self {
        Self {
            prev_model: [
                [1.0, 0.0, 0.0, 0.0f32],
                [0.0, 1.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 1.0], // 平行移動列（w=1 必須）
            ],
        }
    }

    /// 現行の `ModelUniform` から「前フレーム＝今フレーム」の値を作る（速度 0 の初期化）。
    #[inline]
    pub fn from_current(m: &ModelUniform) -> Self {
        Self { prev_model: m.model }
    }
}

// ── マテリアル (Group 2, Binding 0) ──────────────────────────

/// PBR マテリアルパラメータ。
///
/// WGSL レイアウト（計 96 bytes。ガラス表現で 64→80、サーフェス情報系で 80→96 へ拡張）:
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
/// | 76        | ignore_vertex_color | 4   |  ← 旧 _pad2 を転用（頂点カラー乗算スキップ, 0/1）
/// | 80        | user_data         |   4    |  ← 汎用ユーザーデータ回線（0..1, G-Buffer RT2.a へ）
/// | 84        | shading_model     |   4    |  ← シェーディングモデル ID（0=DefaultPBR, RT3.a へ）
/// | 88        | _pad3             |   4    |  ← 予約（16 byte 境界合わせ。次の追加はここを転用する）
/// | 92        | _pad4             |   4    |  ← 予約（同上）
///
/// 【80→96 へ増えた理由と影響】
/// 旧レイアウトは 80 byte を 1 バイトも余さず使い切っており（過去 4 回の追加は全て
/// _pad の転用で吸収してきた）、これ以上フィールドを足す余地が無かった。
/// uniform 構造体のサイズは 16 の倍数である必要があるため 80→96 とし、
/// 同時に将来のための予備 8 byte（_pad3/_pad4）を確保した。
/// マテリアル uniform は**マテリアルごとに 1 個**しか存在しない（インスタンス数に比例しない）ため、
/// 16 byte の増加はメモリ・帯域ともに実質無視できる。
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
    /// 頂点カラー無視トグル（旧 _pad2, offset 76 を転用）。0=乗算する（従来動作）、
    /// 1=頂点カラー（in.color）の乗算をスキップして base_color_factor（×テクスチャ）を
    /// そのまま使う。カメラプレビューの地形描画専用: 地形メッシュの頂点カラーはレイヤ
    /// ブレンド重み（成分ごとに 0..1・アルファ＝スロット3重みで大抵 0）であり、通常の
    /// 頂点カラーとして乗算すると RGB がレイヤ重みで着色され、かつアルファがほぼ 0 になって
    /// プレビュー合成（AlphaBlending ブリット）で透けて消える。1 にすると中立な白アルベド・
    /// アルファ 1 の簡易表示になる。surface_gather.wgsl のベースカラー採取 1 箇所が参照する。
    /// 旧レイアウトでは全ビット 0（_pad2=0.0）＝この値 0（無視しない）と一致し後方互換。
    pub ignore_vertex_color: u32,

    /// 汎用ユーザーデータ回線（0..1, offset 80）。
    ///
    /// エンジンは意味を一切定めない「自由回線」で、マテリアルが濡れ具合・ダメージ量・
    /// 経年劣化など好きな意味を載せられる。G-Buffer RT2.a（Rgba8Unorm＝8bit）へ焼かれ、
    /// 将来の合成（第 3 層）が `Surface.user_data` として読む。
    /// 既定 0.0＝旧データ・未設定マテリアルと一致（後方互換）。
    pub user_data:          f32,

    /// シェーディングモデル ID（offset 84）。0 = DefaultPBR（現状これのみ実装）。
    ///
    /// 将来のトゥーン／異方性ヘア等をライティング段で分岐させるための識別子。
    /// G-Buffer RT3.a へ `render_tag` と一緒にビットパックされる
    /// （規約は renderer::surface_id）。既定 0＝DefaultPBR。
    pub shading_model:      u32,

    /// 予約（16 byte 境界合わせ）。次にマテリアルへ値を足すときはここを転用し、
    /// 構造体サイズを 96 のまま維持すること。
    pub _pad3:              u32,
    /// 予約（同上）。
    pub _pad4:              u32,
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

    /// 深度→ワールド座標を復元する全シェーダが、ビューポート矩形を宣言していること。
    ///
    /// 【何を守っているか】NDC `[-1,1]` はレンダーターゲット全面ではなく **set_viewport した
    /// 矩形**へ写像される。Play のレターボックス／ピラーボックスでこれを無視して
    /// `frag_coord / resolution` で正規化すると、復元ワールド座標が水平方向へ数メートル
    /// 滑り、RT 影のレイ原点が地形内部へ潜って画面が黒く塗り潰される。
    /// CameraUniform を切り詰めて宣言し直す改変でこの規約が静かに外れるのを防ぐため、
    /// 「viewport フィールドの宣言」と「素の resolution 正規化が残っていないこと」を固定する。
    #[test]
    fn world_pos_reconstruction_shaders_declare_viewport() {
        // (シェーダ名, ソース) — 深度からワールド座標を復元するフルスクリーンパスのすべて。
        let shaders: [(&str, &str); 5] = [
            ("deferred_lighting.wgsl", include_str!("shaders/deferred_lighting.wgsl")),
            ("shadow_mask.wgsl",       include_str!("shaders/shadow_mask.wgsl")),
            ("ao_common.wgsl",         include_str!("shaders/ao_common.wgsl")),
            ("ssgi_common.wgsl",       include_str!("shaders/ssgi_common.wgsl")),
            ("reflection_common.wgsl", include_str!("shaders/reflection_common.wgsl")),
        ];
        for (name, src) in shaders {
            assert!(
                src.contains("viewport:"),
                "{name} の CameraUniform に viewport が宣言されていない                 （深度→ワールド復元がレターボックスでズレる）"
            );
            // NDC は必ずビューポート相対 UV（uv_vp）から作ること。
            // RT 全面基準の `uv` から直接 NDC を組み立てる旧式が残っていないかを見る
            // （`uv` そのものは AO / SSGI / マスクのサンプリングに今も使うので存在してよい）。
            assert!(
                !src.contains("vec3<f32>(uv.x * 2.0 - 1.0"),
                "{name} が RT 全面基準の uv から直接 NDC を作っている                 （ビューポート相対の uv_vp から作ること）"
            );
        }
    }

    /// CameraUniform が Rust/WGSL 双方で 304 バイト（160→224 で inv_view_proj、
    /// 224→288 で prev_view_proj、288→304 で viewport を追加）であることを固定する。ズレると
    /// shader_common.wgsl / deferred_lighting.wgsl / grass_gbuffer.wgsl の
    /// CameraUniform 定義との対応が崩れ、GPU が誤ったバイトを読む（静かな描画バグ）。
    #[test]
    fn camera_uniform_size_is_304_bytes() {
        assert_eq!(std::mem::size_of::<CameraUniform>(), 304);
        // viewport の位置も固定する（部分書き込みのオフセット規約）。
        assert_eq!(CAMERA_UNIFORM_VIEWPORT_OFFSET, 288);
        // prev_view_proj は末尾 64 バイト（offset 224）。
        let c = CameraUniform::identity();
        let base = &c as *const _ as usize;
        assert_eq!(&c.prev_view_proj as *const _ as usize - base, 224,
                   "prev_view_proj は offset 224");
    }

    /// PrevModelUniform が 64 バイト（mat4x4 ちょうど）であること。
    /// velocity_common.wgsl の `array<PrevModelUniform>` はこのストライドで読む。
    #[test]
    fn prev_model_uniform_size_is_64_bytes() {
        assert_eq!(std::mem::size_of::<PrevModelUniform>(), 64, "PrevModelUniform は 64 バイト");
    }

    /// `PrevModelUniform::from_current` が model 行列をそのまま複製し、
    /// 「前フレーム＝今フレーム（速度 0）」の初期化になること。
    #[test]
    fn prev_model_from_current_copies_model_matrix() {
        let m = ModelUniform::from_matrix([
            [2.0, 0.0, 0.0, 0.0f32],
            [0.0, 3.0, 0.0, 0.0],
            [0.0, 0.0, 4.0, 0.0],
            [5.0, 6.0, 7.0, 1.0],
        ]);
        assert_eq!(PrevModelUniform::from_current(&m).prev_model, m.model);
        // 恒等の平行移動列は w=1（ModelUniform::identity と同じ規約）。
        assert_eq!(PrevModelUniform::identity().prev_model[3], [0.0, 0.0, 0.0, 1.0]);
    }

    /// MaterialUniform が Rust/WGSL 双方で 96 バイト（ガラス表現で 64→80、情報系 2 値の追加で
    /// 80→96 へ拡張）であることと、主要フィールドのオフセットを固定する。ズレると
    /// shader_common.wgsl の MaterialUniform 定義との対応が崩れ、GPU が誤ったバイトを読む
    /// （静かな描画バグ）。
    #[test]
    fn material_uniform_layout_is_96_bytes() {
        assert_eq!(std::mem::size_of::<MaterialUniform>(), 96, "MaterialUniform は 96 バイト");
        // 代表オフセットの検証（base アドレスからのバイト差）。
        let m = MaterialUniform {
            base_color_factor: [0.0; 4], metallic_factor: 0.0, roughness_factor: 0.0,
            alpha_cutoff: 0.0, has_base_color_tex: 0, emissive_factor: [0.0; 3],
            has_normal_tex: 0, has_mr_tex: 0, has_occlusion_tex: 0, has_emissive_tex: 0,
            ior: 0.0, transmission: 0.0, mr_tex_ignore: 0, diffuse_transmission: 0.0,
            ignore_vertex_color: 0, user_data: 0.0, shading_model: 0, _pad3: 0, _pad4: 0,
        };
        let base = &m as *const _ as usize;
        let off = |p: *const f32| p as usize - base;
        assert_eq!(off(&m.ior),          60, "ior は offset 60");
        assert_eq!(off(&m.transmission), 64, "transmission は offset 64");
        assert_eq!(&m.mr_tex_ignore as *const u32 as usize - base, 68, "mr_tex_ignore は offset 68");
        assert_eq!(off(&m.diffuse_transmission), 72, "diffuse_transmission は offset 72");
        assert_eq!(&m.ignore_vertex_color as *const u32 as usize - base, 76, "ignore_vertex_color は offset 76");
        assert_eq!(off(&m.user_data),    80, "user_data は offset 80");
        assert_eq!(&m.shading_model as *const u32 as usize - base, 84, "shading_model は offset 84");
    }

    /// ModelUniform が 128 バイトのままであること（インスタンス拡張スロットは
    /// normal_matrix の 4 列目を転用しており、サイズを一切変えていない）。
    /// このサイズは 6 個の WGSL ファイルが再宣言している ABI であり、崩すと
    /// 全パスがバッファをズレて読む（静かな描画バグ）。
    #[test]
    fn model_uniform_size_is_128_bytes() {
        assert_eq!(std::mem::size_of::<ModelUniform>(), 128, "ModelUniform は 128 バイト");
    }

    /// `set_render_tag` が拡張スロット（4 列目・行 0）にだけ書き、
    /// 法線変換に使う 0..2 列を一切壊さないこと。
    #[test]
    fn set_render_tag_touches_only_the_extras_slot() {
        let model = [
            [2.0, 0.0, 0.0, 0.0f32],
            [0.0, 3.0, 0.0, 0.0],
            [0.0, 0.0, 4.0, 0.0],
            [5.0, 6.0, 7.0, 1.0],
        ];
        let before = ModelUniform::from_matrix(model);
        let mut after = before;
        after.set_render_tag(9);

        // 法線変換に使う 0..2 列は不変。
        for col in 0..INSTANCE_EXTRAS_COLUMN {
            assert_eq!(before.normal_matrix[col], after.normal_matrix[col], "col {col} が変化した");
        }
        // モデル行列も不変。
        assert_eq!(before.model, after.model);
        // 拡張スロットの該当行だけにタグが入る。
        assert_eq!(
            after.normal_matrix[INSTANCE_EXTRAS_COLUMN][INSTANCE_EXTRAS_ROW_RENDER_TAG],
            9.0
        );
    }

    /// タグはビット幅（4bit）でマスクされ、範囲外の値でも隣の情報を侵食しないこと。
    #[test]
    fn set_render_tag_masks_out_of_range_values() {
        let mut u = ModelUniform::identity();
        u.set_render_tag(0xFF);
        assert_eq!(
            u.normal_matrix[INSTANCE_EXTRAS_COLUMN][INSTANCE_EXTRAS_ROW_RENDER_TAG],
            super::super::surface_id::RENDER_TAG_MASK as f32
        );
    }

    /// `ModelUniform::identity()` の **model 行列** が真の恒等行列であること。
    ///
    /// 【この回帰テストが守るもの】
    /// `identity()` は unlit_line パイプラインの「恒等モデル bind group」
    /// （`create_identity_model_bg_for_unlit`）の中身であり、グリッド・コライダー枠・
    /// カメラ錐台など**全てのワールド固定ラインギズモ**がこの 1 個のバッファを共有する。
    /// WGSL 側（gizmo_line.wgsl）は `vp * m * vec4(pos, 1.0)` と点（w=1）に掛けるため、
    /// 平行移動列 m[3] が `[0,0,0,1]` でないと変換結果の w が 0 になり、
    /// 全頂点が「無限遠の方向ベクトル」に化けてカメラの平行移動が効かなくなる。
    /// 拡張スロット（normal_matrix の 4 列目）のゼロ初期化を model 側へ巻き添えで
    /// 適用してしまった過去の不具合を再発させないために固定する。
    #[test]
    fn identity_model_matrix_is_a_true_identity() {
        let u = ModelUniform::identity();
        assert_eq!(
            u.model,
            [
                [1.0, 0.0, 0.0, 0.0f32],
                [0.0, 1.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [0.0, 0.0, 0.0, 1.0],
            ],
            "model は真の恒等行列でなければならない（m[3][3]=1）"
        );
        // 一方、normal_matrix の 4 列目は拡張スロットなのでゼロ。
        assert_eq!(
            u.normal_matrix[INSTANCE_EXTRAS_COLUMN],
            [0.0, 0.0, 0.0, 0.0],
            "normal_matrix の拡張スロット列はゼロ初期化"
        );
    }

    /// 恒等モデル行列で点（w=1）を変換しても、位置と w が保存されること。
    ///
    /// GPU 側の `m * vec4(pos, 1.0)`（列優先 mat4x4）を CPU で再現して検証する。
    /// w が 1 のまま返らない行列は、続く view_proj で平行移動列が無効化され、
    /// 「カメラを動かしても画面上で動かない線」という症状になる。
    #[test]
    fn identity_model_preserves_points_and_w() {
        let m = ModelUniform::identity().model;
        let p = [12.5f32, -3.25, 7.0, 1.0];

        // 列優先での m * p（m[col][row] レイアウト）。
        let mut out = [0.0f32; 4];
        for row in 0..4 {
            out[row] = (0..4).map(|col| m[col][row] * p[col]).sum();
        }

        assert_eq!(out, p, "恒等行列は点をそのまま返す（w も 1 のまま）");
    }
}
