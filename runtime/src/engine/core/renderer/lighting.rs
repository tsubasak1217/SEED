// ============================================================
//  lighting.rs — ライト GPU バッファとライト構造体
//
//  シーンの LightComponent を毎フレーム収集して GPU の storage buffer
//  （ライト配列）と uniform（ライト数メタ）へアップロードするための
//  型・バッファ管理を提供する。
//
//  実際のシェーディングは shader_fragment.wgsl 内のライトループが行う。
//  本モジュールは「CPU 側のライトデータ ↔ GPU バッファ」の橋渡しに徹する。
//
//  【バインドグループ】group 4（mesh / skinned_mesh パイプライン共通）
//  デバイスの max_bind_groups=5（group 0〜4）環境に対応するため、
//  Phase R2 のシャドウ資源も group 4 へ同居させた複合レイアウトになっている。
//    binding 0: array<GpuLight>（storage, read）
//    binding 1: LightMeta（uniform, ライト数）
//    binding 2: CSM 深度配列（texture_depth_2d_array, ShadowResources 所有）
//    binding 3: スポット深度配列（texture_depth_2d_array, ShadowResources 所有）
//    binding 4: 比較サンプラー（LessEqual, ShadowResources 所有）
//    binding 5: ShadowMatrices UBO（ShadowResources 所有）
//    binding 6: TLAS（RT 影バリアントのみ。RtShadowResources 所有）
//    binding 7: array<ClusterCell>（storage, read。ClusterResources 所有・Phase C1）
//    binding 8: array<u32> クラスタライトインデックス（storage, read。同上）
//    binding 9: ClusterParams（uniform。同上。**BG ごとに差し替わる**）
//  シェーダの宣言（cluster_common.wgsl / light_common.wgsl / shadow.wgsl の group 4）と
//  厳密に一致させること。
// ============================================================

use wgpu::util::DeviceExt;

use super::shadow::ShadowResources;

// ─── LightingPass ────────────────────────────────────────────

/// ライティングを行う描画パスの種別（＝**どのカメラのパスか**）。
///
/// group 4（ライト＋シャドウ＋クラスタの複合 BindGroup）の中には
/// **カメラ固有の資源**が 2 つ含まれる:
///   - シャドウ資源（binding 2/5）: CSM のカスケードはカメラ視錐台にフィットさせる。
///   - クラスタ資源（binding 7〜9）: フロクセル分割は near/far/fov/ビューポート依存。
/// そのためパスごとに正しい BindGroup を選ぶ必要がある。誤って別カメラ用の BG を
/// 渡すと「そのパスのライティングが、別のカメラの向きで変わる」という追跡困難な
/// バグになる（実際に発生した）。
///
/// 呼び出し側が BindGroup を直に触れないよう、LightBuffer の BG フィールドは private とし、
/// **本 enum で「どのカメラのパスか」を名指しさせる**ことで取り違えを防ぐ。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LightingPass {
    /// メインカメラのパス（Edit=デバッグカメラ / Play=ゲームカメラ）。
    /// クラスタ有効・メインカメラ基準の CSM を使う。
    MainCamera,
    /// エディタのカメラプレビュー（選択中ゲームカメラの小窓）のパス。
    /// クラスタ無効（線形走査）・**プレビューカメラ基準の CSM** を使う。
    CameraPreview,
}

/// GPU に送れるライトの最大数。
///
/// storage buffer を固定容量で確保し、毎フレーム有効なライトのみ書き込む。
///
/// Phase C1（Clustered Lighting）で 64 → 1024 へ引き上げた。フラグメントは
/// 全ライトではなく「自分のクラスタのライトリスト ＋ 全平行光」しか走査しないため、
/// 灯数を増やしてもフラグメント負荷は増えない（増えるのはクラスタ構築 compute の
/// コストと storage buffer の容量 = 1024 × 96B ≒ 96KB だけ）。
/// 上限を更に上げる場合は renderer/clustered.rs の MAX_LIGHTS_PER_CLUSTER
/// （1 クラスタが保持できる灯数）も併せて検討すること。
pub const MAX_LIGHTS: usize = 1024;

// ─── ライト種別コード ─────────────────────────────────────────
// LightKind::to_code() およびシェーダの LIGHT_KIND_* 定数と一致させること。

/// 環境光（アンビエント）の既定色（白）。従来のハードコード値と同一の見た目を維持する。
pub const DEFAULT_AMBIENT_COLOR: [f32; 3] = [1.0, 1.0, 1.0];
/// 環境光（アンビエント）の既定強度。旧シェーダの `vec3(0.05)` と一致（0 で完全な暗闇）。
pub const DEFAULT_AMBIENT_INTENSITY: f32 = 0.05;

/// 平行光
pub const LIGHT_KIND_DIRECTIONAL: u32 = 0;
/// 点光源
pub const LIGHT_KIND_POINT: u32 = 1;
/// スポット光
pub const LIGHT_KIND_SPOT: u32 = 2;
/// 矩形エリアライト
pub const LIGHT_KIND_RECT: u32 = 3;

// ─── GpuLight ────────────────────────────────────────────────

/// GPU の storage buffer に格納する 1 ライト分のデータ（96 bytes）。
///
/// WGSL storage の std430 相当アライメント（vec3 は 16 バイト境界）に合わせ、
/// 明示的なパディングフィールドで詰める。bytemuck::Pod のため隙間バイトを残さない。
///
/// | offset | field            | size |
/// |--------|------------------|------|
/// |   0    | color            |  12  |
/// |  12    | intensity        |   4  |
/// |  16    | position         |  12  |
/// |  28    | range            |   4  |
/// |  32    | direction        |  12  |
/// |  44    | kind (u32)       |   4  |
/// |  48    | inner_cos        |   4  |
/// |  52    | outer_cos        |   4  |
/// |  56    | rect_half_width  |   4  |
/// |  60    | rect_half_height |   4  |
/// |  64    | rect_right       |  12  |
/// |  76    | shadow_index     |   4  |
/// |  80    | rect_up          |  12  |
/// |  92    | soft_radius      |   4  |
/// 合計 96（16 の倍数 → array stride も 96）
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GpuLight {
    /// 光の色（リニア RGB）。強度は color とは別に intensity で持つ。
    pub color:            [f32; 3],
    /// 光の強度（color に乗算する係数）。
    pub intensity:        f32,
    /// ワールド位置（point/spot/rect で使用。directional では未使用）。
    pub position:         [f32; 3],
    /// 減衰距離（point/spot。この距離付近で消灯）。
    pub range:            f32,
    /// 照射方向（光が進む向き＝Transform::forward()）。
    /// シェーダは L = -direction（面から光源への向き）として使う。
    pub direction:        [f32; 3],
    /// 種別コード（LIGHT_KIND_*）。
    pub kind:             u32,
    /// スポット内側コーンの cos（この cos より大きい＝内側は全光量）。
    pub inner_cos:        f32,
    /// スポット外側コーンの cos（この cos で 0 まで減衰）。
    pub outer_cos:        f32,
    /// rect の半幅（rect_right 方向）。
    pub rect_half_width:  f32,
    /// rect の半高（rect_up 方向）。
    pub rect_half_height: f32,
    /// rect の右方向ベクトル（面の横軸、正規化）。
    pub rect_right:       [f32; 3],
    /// 影スロット（Phase R2）。-1 = 影なし。
    /// 方向光: 0 = CSM 有効（影付き方向光は最大 1 灯）。
    /// スポット: 0..MAX_SHADOW_SPOTS-1 = スポットシャドウ配列のレイヤ番号。
    /// シェーダ（shadow.wgsl / shader_fragment.wgsl）が f32→i32 で判定する。
    pub shadow_index:     f32,
    /// rect の上方向ベクトル（面の縦軸、正規化）。
    pub rect_up:          [f32; 3],
    /// ソフト影の見込み半径（Phase R8 ソフトシャドウ）。0 = ハードシャドウ。
    /// directional: tan(角径) の無次元スロープ（collect_gpu_lights で度→tan 変換済み）。
    /// point/spot/rect: 光源のワールド半径（シェーダで radius/距離＝見込み角に換算）。
    /// 旧レイアウトの _pad1（offset 92）を再利用するため 96 バイトは不変。
    pub soft_radius:      f32,
}

impl GpuLight {
    /// ゼロ値（未使用スロットの埋め草）。
    pub fn zeroed() -> Self { bytemuck::Zeroable::zeroed() }

    /// 平行光を構築する。
    ///
    /// `direction` は光が進む向き（正規化済みを想定）。
    pub fn directional(direction: [f32; 3], color: [f32; 3], intensity: f32) -> Self {
        Self {
            color,
            intensity,
            position:         [0.0; 3],
            range:            0.0,
            direction:        normalize(direction),
            kind:             LIGHT_KIND_DIRECTIONAL,
            inner_cos:        0.0,
            outer_cos:        0.0,
            rect_half_width:  0.0,
            rect_half_height: 0.0,
            rect_right:       [0.0; 3],
            shadow_index:     -1.0,
            rect_up:          [0.0; 3],
            soft_radius:      0.0,
        }
    }

    /// 点光源を構築する。
    pub fn point(position: [f32; 3], color: [f32; 3], intensity: f32, range: f32) -> Self {
        Self {
            color,
            intensity,
            position,
            range:            range.max(1e-3),
            direction:        [0.0, 0.0, 1.0],
            kind:             LIGHT_KIND_POINT,
            inner_cos:        0.0,
            outer_cos:        0.0,
            rect_half_width:  0.0,
            rect_half_height: 0.0,
            rect_right:       [0.0; 3],
            shadow_index:     -1.0,
            rect_up:          [0.0; 3],
            soft_radius:      0.0,
        }
    }

    /// スポット光を構築する。
    ///
    /// `inner_deg`/`outer_deg` は半角（コーン中心軸からの角度）で、
    /// inner ≤ outer を保証して cos に変換する。
    pub fn spot(
        position:  [f32; 3],
        direction: [f32; 3],
        color:     [f32; 3],
        intensity: f32,
        range:     f32,
        inner_deg: f32,
        outer_deg: f32,
    ) -> Self {
        // 内側は外側以下に丸める（逆転していると減衰式が破綻するため）。
        let outer = outer_deg.max(0.0);
        let inner = inner_deg.clamp(0.0, outer);
        Self {
            color,
            intensity,
            position,
            range:            range.max(1e-3),
            direction:        normalize(direction),
            kind:             LIGHT_KIND_SPOT,
            inner_cos:        inner.to_radians().cos(),
            outer_cos:        outer.to_radians().cos(),
            rect_half_width:  0.0,
            rect_half_height: 0.0,
            rect_right:       [0.0; 3],
            shadow_index:     -1.0,
            rect_up:          [0.0; 3],
            soft_radius:      0.0,
        }
    }

    /// 矩形エリアライトを構築する。
    ///
    /// `direction` は面の法線（光が進む向き）、`right`/`up` は面の横・縦軸。
    pub fn rect(
        position:  [f32; 3],
        direction: [f32; 3],
        right:     [f32; 3],
        up:        [f32; 3],
        color:     [f32; 3],
        intensity: f32,
        range:     f32,
        width:     f32,
        height:    f32,
    ) -> Self {
        Self {
            color,
            intensity,
            position,
            range:            range.max(1e-3),
            direction:        normalize(direction),
            kind:             LIGHT_KIND_RECT,
            inner_cos:        0.0,
            outer_cos:        0.0,
            rect_half_width:  (width * 0.5).max(1e-4),
            rect_half_height: (height * 0.5).max(1e-4),
            rect_right:       normalize(right),
            shadow_index:     -1.0,
            rect_up:          normalize(up),
            soft_radius:      0.0,
        }
    }
}

/// ベクトルを正規化する（長さ 0 は [0,0,1] にフォールバック）。
fn normalize(v: [f32; 3]) -> [f32; 3] {
    let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if len < 1e-6 { [0.0, 0.0, 1.0] } else { [v[0] / len, v[1] / len, v[2] / len] }
}

// ─── LightMeta ───────────────────────────────────────────────

/// ライト配列のメタ情報 uniform（32 bytes）。
///
/// count / rt_shadows / view_mode / ambient_* が意味を持ち、_pad は 16 バイト境界のためのパディング。
/// WGSL 側 LightMeta（light_common.wgsl）と厳密に一致させること:
///   先頭スカラー 4 つ（16B）→ ambient_color: vec3（16B 境界, offset 16）→ ambient_intensity（offset 28）。
///
/// | offset | field             | size |
/// |--------|-------------------|------|
/// |   0    | count             |   4  |
/// |   4    | rt_shadows        |   4  |
/// |   8    | view_mode         |   4  |
/// |  12    | _pad              |   4  |
/// |  16    | ambient_color     |  12  |
/// |  28    | ambient_intensity |   4  |
/// 合計 32
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct LightMeta {
    /// 有効なライト数（シェーダはこの数だけループする）。
    pub count:      u32,
    /// インラインレイトレ影の有効フラグ（Phase R8, 0=無効/1=有効）。
    /// RT 対応 GPU でのみ RT パイプラインが読む。有効時は全ライト種で遮蔽レイを飛ばし、
    /// 無効時（および RT 非対応パイプライン）は従来のシャドウマップ経路を使う。
    pub rt_shadows: u32,
    /// エディタのシーンビュー表示モード（0=Lit / 1=Unlit / 2=Wireframe。SceneViewMode::to_code）。
    /// 0 以外のとき evaluate_lighting はライティングを行わずアルベド＋エミッシブを返す。
    /// メインカメラ用 LightMeta にのみ非 0 を書き込み、プレビュー用は常に 0（＝Lit）にすることで、
    /// アンリット／ワイヤをエディタのシーンビュー（デバッグカメラ）だけに限定する。
    pub view_mode:  u32,
    /// アライメント用パディング（ambient_color を 16 バイト境界へ揃える）。
    pub _pad:       u32,
    /// 環境光の色（リニア RGB, Phase R1.5）。既定は白。
    pub ambient_color: [f32; 3],
    /// 環境光の強度（Phase R1.5）。既定 0.05（従来のハードコード値）。0 で完全な暗闇。
    pub ambient_intensity: f32,
}

// ─── LightBuffer ─────────────────────────────────────────────

/// ライト用 GPU バッファ一式（storage 配列 + メタ uniform + bind group）。
///
/// 容量 MAX_LIGHTS の storage buffer を確保し、毎フレーム `update()` で
/// 有効ライトのみを書き込み、メタにライト数を書く。
pub struct LightBuffer {
    /// array<GpuLight>（storage, read）。容量 MAX_LIGHTS 固定。
    /// クラスタ構築 compute（clustered.rs）も同じバッファを group 0 で読む。
    lights_buffer: wgpu::Buffer,
    /// **メインカメラ用** LightMeta（uniform）。view_mode に実際の表示モードが入り得る。
    /// メインカメラで描く全パス（不透明・距離ソート透明・WBOIT・RT・ギズモアイコン）が読む。
    meta_buffer_main:    wgpu::Buffer,
    /// **カメラプレビュー用** LightMeta（uniform）。view_mode は常に 0（＝Lit）。
    /// count / rt_shadows / ambient は main と同値を書くが、view_mode だけは 0 に固定することで、
    /// エディタのシーンビューがアンリット／ワイヤでもプレビュー小窓は常にライティング表示になる。
    meta_buffer_preview: wgpu::Buffer,
    /// group 4 の複合 bind group（binding 0 = lights, 1 = meta,
    /// 2 = CSM 深度配列, 3 = スポット深度配列, 4 = 比較サンプラー, 5 = シャドウ行列 UBO,
    /// 7 = クラスタグリッド, 8 = クラスタライトインデックス, 9 = クラスタパラメータ）。
    /// ライトバッファもシャドウ資源もクラスタバッファも生成後不変のため、
    /// 起動時 1 回だけ生成して使い回す（中身の更新は queue.write_buffer /
    /// 深度パス描画 / compute で行われ、BG 再生成は不要）。
    ///
    /// **メインカメラ用**: クラスタ有効（binding 9 = メインカメラ用 ClusterParams）＋
    /// メインカメラ基準の CSM（binding 2/5 = ShadowResources 本体）。
    /// メインカメラで描く全パス（不透明・距離ソート透明・WBOIT・ギズモアイコン）で使う。
    ///
    /// 取り違え防止のため private。参照は `bind_group(LightingPass)` 経由で行う。
    bind_group_main: wgpu::BindGroup,
    /// **カメラプレビュー用**の group 4 複合 bind group。
    ///
    /// メインカメラ用と異なるのは「カメラ固有の資源」だけ:
    ///   - binding 9: enabled=0 固定の ClusterParams
    ///     → フラグメントはクラスタを一切参照せず、従来どおり全ライトを線形走査する。
    ///       クラスタは near/far/fov/ビューポート依存＝カメラ固有のため、メインカメラ
    ///       基準のクラスタをプレビューで使うとライトが飛ぶ／暗くなる。
    ///   - binding 2/5: **プレビューカメラ基準の CSM**（ShadowResources のプレビュー実体）
    ///     → CSM のカスケードはカメラ視錐台にフィットさせるためカメラ固有。メインカメラ
    ///       （Edit ではデバッグカメラ）基準の CSM をプレビューで使うと、プレビューの影が
    ///       デバッグカメラの向きで変わってしまう。
    /// ライト配列・メタ・スポット以外の共有資源（binding 0/1/3/4/7/8）はメイン用と共有する。
    ///
    /// 取り違え防止のため private。参照は `bind_group(LightingPass)` 経由で行う。
    bind_group_preview: wgpu::BindGroup,
}

impl LightBuffer {
    /// ライトバッファ一式と group 4 複合 bind group（メインカメラ用／プレビュー用の 2 本）を生成する。
    ///
    /// 2 本の違いは**カメラ固有の資源だけ**（シャドウ実体とクラスタパラメータ）。
    /// ライト storage / メタ / クラスタバッファ本体は共有する。
    ///
    /// - `bgl`:            mesh パイプラインの group 4 レイアウト（ライト＋シャドウ＋クラスタ複合）。
    /// - `shadow_main`:    メインカメラ基準の CSM を持つシャドウ資源（binding 2〜5）。
    /// - `shadow_preview`: カメラプレビュー基準の CSM を持つシャドウ資源（binding 2〜5）。
    /// - `clusters`:       クラスタ資源（binding 7〜9 のグリッド・インデックス・パラメータを供給）。
    pub fn new(
        device:         &wgpu::Device,
        bgl:            &wgpu::BindGroupLayout,
        shadow_main:    &ShadowResources,
        shadow_preview: &ShadowResources,
        clusters:       &super::clustered::ClusterResources,
    ) -> Self {
        // storage 配列は最初から MAX_LIGHTS 分ゼロ確保する（実行時サイズ変更を避ける）。
        let init_lights = vec![GpuLight::zeroed(); MAX_LIGHTS];
        let lights_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Lights Storage Buffer"),
            contents: bytemuck::cast_slice(&init_lights),
            usage:    wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        });

        // メインカメラ用・プレビュー用の 2 本の LightMeta を確保する。
        // 初期値は両方 view_mode=0（Lit）。以降 update() で毎フレーム書き換える。
        let init_meta = LightMeta {
            count:             0,
            rt_shadows:        0,
            view_mode:         0,
            _pad:              0,
            ambient_color:     DEFAULT_AMBIENT_COLOR,
            ambient_intensity: DEFAULT_AMBIENT_INTENSITY,
        };
        let meta_buffer_main = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Light Meta Uniform (main camera)"),
            contents: bytemuck::bytes_of(&init_meta),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        let meta_buffer_preview = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Light Meta Uniform (camera preview, view_mode=0 固定)"),
            contents: bytemuck::bytes_of(&init_meta),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        // group 4 複合 BG（ライト binding 0/1 ＋ シャドウ binding 2〜5 ＋ クラスタ binding 7〜9）。
        // shadow.wgsl / light_common.wgsl の group 4 宣言と一致させること。
        //
        // メインカメラ用とプレビュー用で異なるのは「カメラ固有の資源」だけ:
        //   binding 2/5 = シャドウ実体（CSM はカメラ視錐台にフィット）
        //   binding 9   = ClusterParams（フロクセル分割は near/far/fov/ビューポート依存）
        // それ以外（ライト配列・メタ・スポット深度・サンプラー・クラスタバッファ本体）は共有する。
        // meta 引数はパスごとに差し替える（main = meta_buffer_main / preview = meta_buffer_preview）。
        // これにより view_mode（アンリット／ワイヤ）をメインカメラのパスだけに効かせられる。
        let make_bg = |label: &str, shadow: &ShadowResources, params: &wgpu::Buffer, meta: &wgpu::Buffer| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label:   Some(label),
                layout:  bgl,
                entries: &[
                    wgpu::BindGroupEntry { binding: 0, resource: lights_buffer.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 1, resource: meta.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(&shadow.dir_array_view) },
                    wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::TextureView(&shadow.spot_array_view) },
                    wgpu::BindGroupEntry { binding: 4, resource: wgpu::BindingResource::Sampler(&shadow.sampler) },
                    wgpu::BindGroupEntry { binding: 5, resource: shadow.ubo.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 7, resource: clusters.grid_buffer().as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 8, resource: clusters.indices_buffer().as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 9, resource: params.as_entire_binding() },
                ],
            })
        };
        let bind_group_main = make_bg(
            "Lights+Shadow+Cluster BG (group 4, main camera)",
            shadow_main,
            clusters.params_buffer(),
            &meta_buffer_main,
        );
        let bind_group_preview = make_bg(
            "Lights+Shadow+Cluster BG (group 4, camera preview: cluster disabled + preview CSM)",
            shadow_preview,
            clusters.params_disabled_buffer(),
            &meta_buffer_preview,
        );

        Self { lights_buffer, meta_buffer_main, meta_buffer_preview, bind_group_main, bind_group_preview }
    }

    /// そのパス（＝そのカメラ）で bind すべき group 4 複合 BindGroup を返す。
    ///
    /// 呼び出し側は「どのカメラのパスか」を名指しするだけでよく、
    /// カメラ固有資源（CSM・クラスタ）の組み合わせを間違えられない。
    /// RT 影パイプラインのメインパスだけは TLAS 入りの別 BG（`create_rt_bind_group`）を使う。
    pub fn bind_group(&self, pass: LightingPass) -> &wgpu::BindGroup {
        match pass {
            LightingPass::MainCamera    => &self.bind_group_main,
            LightingPass::CameraPreview => &self.bind_group_preview,
        }
    }

    /// ライト storage buffer への参照（クラスタ構築 compute の BindGroup 生成に使う）。
    pub fn lights_buffer(&self) -> &wgpu::Buffer { &self.lights_buffer }

    /// 有効ライト配列を GPU へアップロードする（MAX_LIGHTS を超える分は切り捨て）。
    ///
    /// storage 配列の未使用スロットは前フレームの値が残るが、meta.count で
    /// ループ範囲を制限するためシェーディングには影響しない。
    ///
    /// - `rt_shadows`: このフレームでインラインレイトレ影を使うか（Phase R8）。
    ///   RT パイプラインのフラグメントはこの値でシャドウマップ/RT を実行時分岐する。
    /// - `view_mode`: エディタのシーンビュー表示モードコード（SceneViewMode::to_code。
    ///   0=Lit / 1=Unlit / 2=Wireframe）。**メインカメラ用 LightMeta にのみ**書き込み、
    ///   プレビュー用は常に 0（Lit）に固定する。呼び出し側は Play 中や非 Edit では 0 を渡すこと。
    /// - `ambient_color` / `ambient_intensity`: 環境光（Phase R1.5）。
    ///   フラグメントの `ambient = ambient_color * ambient_intensity * albedo * ao`。
    pub fn update(
        &self,
        queue:             &wgpu::Queue,
        lights:            &[GpuLight],
        rt_shadows:        bool,
        view_mode:         u32,
        ambient_color:     [f32; 3],
        ambient_intensity: f32,
    ) {
        let count = lights.len().min(MAX_LIGHTS);
        if count > 0 {
            queue.write_buffer(&self.lights_buffer, 0, bytemuck::cast_slice(&lights[..count]));
        }
        // メインカメラ用: view_mode をそのまま反映（アンリット／ワイヤが効く）。
        let meta_main = LightMeta {
            count:      count as u32,
            rt_shadows: if rt_shadows { 1 } else { 0 },
            view_mode,
            _pad:       0,
            ambient_color,
            ambient_intensity,
        };
        queue.write_buffer(&self.meta_buffer_main, 0, bytemuck::bytes_of(&meta_main));
        // プレビュー用: view_mode を 0（Lit）に固定。それ以外は main と同値。
        let meta_preview = LightMeta { view_mode: 0, ..meta_main };
        queue.write_buffer(&self.meta_buffer_preview, 0, bytemuck::bytes_of(&meta_preview));
    }

    /// RT 影用の group 4 複合 BindGroup を生成する（Phase R8, RT 対応時のみ）。
    ///
    /// 通常の bind group（binding 0〜5 ＋ クラスタ 7〜9）に TLAS（binding 6）を加えたもの。
    /// ライトバッファ・シャドウ資源・クラスタ資源は通常 BG と同一のものを共有し、TLAS のみ追加する。
    /// `rt_lights_bgl` は mesh_rt パイプライン由来（acceleration_structure を含む group 4）。
    ///
    /// RT パイプラインを使うのはメインカメラのパスだけなので、クラスタは常に有効側
    /// （params_buffer）を差す（カメラプレビューは RT を使わない＝R8 からの方針）。
    pub fn create_rt_bind_group(
        &self,
        device:        &wgpu::Device,
        rt_lights_bgl: &wgpu::BindGroupLayout,
        shadow:        &ShadowResources,
        clusters:      &super::clustered::ClusterResources,
        tlas:          &wgpu::Tlas,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Lights+Shadow+Cluster+TLAS BG (group 4, RT)"),
            layout:  rt_lights_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: self.lights_buffer.as_entire_binding() },
                // RT パイプラインはメインカメラのパス専用のため、メインカメラ用 LightMeta を使う
                // （view_mode がアンリット時はフラグメントが早期リターンし、RT レイは飛ばさない）。
                wgpu::BindGroupEntry { binding: 1, resource: self.meta_buffer_main.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(&shadow.dir_array_view) },
                wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::TextureView(&shadow.spot_array_view) },
                wgpu::BindGroupEntry { binding: 4, resource: wgpu::BindingResource::Sampler(&shadow.sampler) },
                wgpu::BindGroupEntry { binding: 5, resource: shadow.ubo.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 6, resource: wgpu::BindingResource::AccelerationStructure(tlas) },
                wgpu::BindGroupEntry { binding: 7, resource: clusters.grid_buffer().as_entire_binding() },
                wgpu::BindGroupEntry { binding: 8, resource: clusters.indices_buffer().as_entire_binding() },
                wgpu::BindGroupEntry { binding: 9, resource: clusters.params_buffer().as_entire_binding() },
            ],
        })
    }
}

// ─── レイアウト検証テスト ──────────────────────────────────────
//
// GpuLight / LightMeta の repr(C) レイアウトは light_common.wgsl の WGSL 構造体と
// バイト単位で一致していなければならない（不一致は静かに描画バグを生む）。
// 変更時に気づけるよう、サイズ・オフセットを固定値で検証する。
#[cfg(test)]
mod layout_tests {
    use super::*;
    use std::mem::{size_of, offset_of};

    /// GpuLight は 96 バイト（array stride も 96）。soft_radius は旧 _pad1 の offset 92 を再利用。
    #[test]
    fn gpu_light_layout() {
        assert_eq!(size_of::<GpuLight>(), 96, "GpuLight は 96 バイト（WGSL stride と一致）");
        assert_eq!(offset_of!(GpuLight, shadow_index), 76);
        assert_eq!(offset_of!(GpuLight, rect_up),      80);
        assert_eq!(offset_of!(GpuLight, soft_radius),  92);
    }

    /// LightMeta は 32 バイト。view_mode は offset 8（旧 _pad[0] を再利用）、
    /// ambient_color は 16 バイト境界（offset 16）、ambient_intensity は offset 28。
    /// WGSL 側 light_common.wgsl の LightMeta（count/rt_shadows/view_mode/_pad2/ambient_*）と一致。
    #[test]
    fn light_meta_layout() {
        assert_eq!(size_of::<LightMeta>(), 32, "LightMeta は 32 バイト（WGSL uniform と一致）");
        assert_eq!(offset_of!(LightMeta, count),             0);
        assert_eq!(offset_of!(LightMeta, rt_shadows),        4);
        assert_eq!(offset_of!(LightMeta, view_mode),         8);
        assert_eq!(offset_of!(LightMeta, ambient_color),     16);
        assert_eq!(offset_of!(LightMeta, ambient_intensity), 28);
    }
}


