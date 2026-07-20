// ============================================================
//  grass_gbuffer.rs — プロシージャル草の GPU インスタンシング パイプライン
//
//  ## 役割（単一責任）
//  「草インスタンス配列を G-Buffer へ焼く」ためのパイプライン・GPU リソース保持・
//  描画ヘルパーだけを持つ。
//  **どこに何本生やすか**（散布データの生成・地形との連携・IPC）は
//  engine/terrain/scatter/ の責務であり、本ファイルは一切関知しない。
//  本ファイルは「インスタンス配列 + パラメータ」を受け取る API だけを公開する。
//
//  ## 頂点バッファを持たない設計
//  草の葉 1 枚は「縦に分割したクワッドの帯」でしかない。この形状は
//  `@builtin(vertex_index)` から完全に手続き的に生成できるため、頂点バッファも
//  インデックスバッファも作らない（VRAM 帯域とアップロードコストが丸ごと消える）。
//  描画は `rp.draw(0..GRASS_MAX_VERTS_PER_BLADE, 0..count)` の 1 コールのみ。
//  分割数が最大未満のインスタンスでは余った頂点をシェーダ側で面積 0 に潰す
//  （縮退。詳細は grass_gbuffer.wgsl の `grass_degenerate_vertex` を参照）。
//
//  ## バインドグループ
//    group0 = camera  … MeshPipeline の BGL を借りる（gbuffer.rs / terrain_gbuffer.rs と同じ方針）
//    group1 = 草インスタンス（本ファイルが定義する専用レイアウト）
//      binding 0: storage(read) array<GrassInstance>
//      binding 1: uniform       GrassUniform
//
//  ## 時刻の運び方（設計判断）
//  風のアニメーションには経過時間が要るが、`CameraUniform` は 22 本のパイプラインが
//  共有しているため 1 バイトも触れない。時刻は本ファイル専用の `GrassUniformGpu` に
//  持たせ、毎フレーム 4 バイトだけ部分更新する（`update_time`）。
// ============================================================

use bytemuck::{Pod, Zeroable};
use wgpu::util::DeviceExt;

use super::pipeline::{get_shader_source, MeshPipeline};

// ============================================================
//  形状定数（WGSL grass_gbuffer.wgsl と一致必須）
// ============================================================

/// 1 枚の草平面を縦に分割する最大セグメント数。WGSL と一致必須。
///
/// `runtime/src/engine/terrain/scatter/props.rs`（散布データ側・別担当が作成予定）
/// にも同名定数が置かれる予定であり、そちらとも一致必須。
pub const GRASS_MAX_SEGMENTS: u32 = 8;

/// 1 セグメント（クワッド）= 三角形 2 枚 = 6 頂点。WGSL と一致必須。
pub const GRASS_VERTS_PER_SEGMENT: u32 = 6;

/// 1 株が持てる平面の最大枚数（十字配置 = 2 枚）。WGSL と一致必須。
pub const GRASS_MAX_PLANES: u32 = 2;

/// 1 株ぶんの固定頂点数（＝ draw() へ渡す頂点数）。WGSL と一致必須。
pub const GRASS_MAX_VERTS_PER_BLADE: u32 =
    GRASS_MAX_SEGMENTS * GRASS_VERTS_PER_SEGMENT * GRASS_MAX_PLANES;

// ============================================================
//  バインディング番号・レイアウト定数
// ============================================================

/// group1 のバインディング番号。WGSL 側 @binding と一致必須。
const BINDING_INSTANCES: u32 = 0;
const BINDING_UNIFORM:   u32 = 1;

/// `GrassInstanceGpu` の想定バイト数（std430。テストで固定する）。
const GRASS_INSTANCE_BYTES: usize = 48;

/// `GrassUniformGpu` 内の `time` フィールドのバイトオフセット。
///
/// `update_time` が 4 バイトだけ部分更新するために使う。構造体の並びを変えたら
/// ここも直すこと（テスト `grass_uniform_time_offset_is_correct` が実測値と照合する）。
const GRASS_UNIFORM_TIME_OFFSET: wgpu::BufferAddress = 52;

/// インスタンスが 0 本のときに確保するダミー要素数。
///
/// wgpu はサイズ 0 のバッファ生成でパニックするため、空でも必ず 1 要素は確保する。
/// `count` は 0 のままなので描画には一切使われない（`draw_grass` が早期 return する）。
const GRASS_EMPTY_DUMMY_ELEMENTS: usize = 1;

// ============================================================
//  GPU データレイアウト（WGSL と一致必須）
// ============================================================

/// 草 1 株ぶんの散布データ（GPU）。
///
/// WGSL の `GrassInstance` と一致必須（std430 / 48 バイト）。
/// `vec3<f32>` は align 16 なので、`pos`+`yaw` / `normal`+`scale` の順で
/// ちょうど vec4 に詰まる（パディング穴が生じない並び）。
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default)]
pub struct GrassInstanceGpu {
    /// 株の根元のワールド座標。
    pub pos:    [f32; 3],
    /// 平面の向き（Y 軸まわり、ラジアン）。
    pub yaw:    f32,
    /// 生えている面の法線（ワールド）。草の「上方向」になる。
    pub normal: [f32; 3],
    /// 株ごとの大きさ倍率（高さ・幅の双方に掛かる）。
    pub scale:  f32,
    /// 株ごとの疑似乱数の種（風の位相・色ゆらぎに使う）。
    pub seed:   u32,
    /// 16 バイト境界へ揃えるためのパディング（GPU では未使用）。
    pub _pad:   [u32; 3],
}

/// 草の種別ごとの見た目・風パラメータ（GPU uniform）。
///
/// WGSL の `GrassUniform` と一致必須（80 バイト = 16 の倍数）。
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default)]
pub struct GrassUniformGpu {
    /// 根元の色（リニア）。
    pub color_bottom: [f32; 3],
    /// 根元の葉幅（ワールド単位）。
    pub width:        f32,
    /// 穂先の色（リニア）。
    pub color_top:    [f32; 3],
    /// 葉の長さ（ワールド単位。instance.scale が掛かる）。
    pub height:       f32,

    // ── 風 ──
    /// 基本揺れの振幅（曲げ角のラジアン相当）。
    pub wind_strength:  f32,
    /// 基本揺れの時間周波数。
    pub wind_speed:     f32,
    /// 位置による位相差の空間周波数（大きいほど細かい波が走る）。
    pub wind_frequency: f32,
    /// 突風の振幅。
    pub gust_strength:  f32,
    /// 突風の時間周波数（`wind_speed` より低くすること）。
    pub gust_speed:     f32,
    /// 経過時間（秒）。`update_time` がここだけを毎フレーム書き換える。
    pub time:           f32,
    /// 風とは無関係な静的な垂れ（曲げ角のラジアン相当）。
    pub bend:           f32,
    /// G-Buffer へ書く roughness。
    pub roughness:      f32,

    /// 実際に使う縦分割数（1..=`GRASS_MAX_SEGMENTS`。シェーダ側でも clamp する）。
    pub segments:         u32,
    /// 実際に使う平面枚数（1 または 2）。
    pub cross_planes:     u32,
    /// 穂先アルファカットアウトの閾値。0 以下でカットアウト無効。
    pub tip_alpha_cutoff: f32,
    /// 陰影用法線を地表法線へ寄せる割合（0..1）。
    ///
    /// 0 = 真の幾何法線をそのまま使う、1 = 完全に地表法線へ置き換える。
    /// 板ポリの葉の幾何法線は水平を向くため、そのまま使うと高い位置の太陽に対して
    /// N·L ≒ 0 となり草が真っ黒に落ちる。それを防ぐための寄せ量である
    /// （詳細は grass_gbuffer.wgsl の `fs_grass` 内「法線の地表寄せ」節）。
    ///
    /// 本フィールドは **旧 `_pad` を潰して置いた**ものであり、構造体サイズは
    /// 80 バイトのまま変わらない（`time` のバイトオフセットにも影響しない）。
    pub normal_up_blend:  f32,
}

// ============================================================
//  GrassGBufferPipeline — 草描画パイプライン
// ============================================================

/// 草描画パイプライン（G-Buffer 書き込み）。cull なし・深度 Less/書き込みあり。
///
/// 頂点バッファを持たないため `vertex.buffers` は空。
/// `instance_bgl`（group1）は本ファイルが定義し、`GrassInstanceBuffer` がこれを使って
/// バインドグループを作る。
pub struct GrassGBufferPipeline {
    /// 描画パイプライン本体。
    pub pipe: wgpu::RenderPipeline,
    /// group1（インスタンス storage + パラメータ uniform）のバインドグループレイアウト。
    pub instance_bgl: wgpu::BindGroupLayout,
}

impl GrassGBufferPipeline {
    /// 草 G-Buffer パイプラインを構築する。
    ///
    /// group0（カメラ）は `mesh_pipeline` の BGL を借りる（gbuffer.rs と同じ方針＝
    /// wgpu の BindGroupLayout 構造的等価性に依拠する既存慣例）。
    /// `color_targets` には `gbuffer_color_targets()` の 4 枚をそのまま渡すこと。
    pub fn new(
        device:        &wgpu::Device,
        mesh_pipeline: &MeshPipeline,
        df:            wgpu::TextureFormat,
        cache:         Option<&wgpu::PipelineCache>,
        color_targets: &[Option<wgpu::ColorTargetState>],
    ) -> Self {
        const LABEL: &str = "grass_gbuffer";

        let instance_bgl = create_instance_bind_group_layout(device);

        // ── シェーダモジュール（自己完結。連結しない）──
        //   grass_gbuffer.wgsl は shader_common.wgsl を取り込まず CameraUniform を
        //   自前宣言している（deferred_lighting.wgsl と同じ先例）。
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some(LABEL),
            source: wgpu::ShaderSource::Wgsl(get_shader_source(GRASS_SHADER_NAME).into()),
        });

        // ── パイプラインレイアウト（group0 = 借用カメラ / group1 = 自前）──
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some(LABEL),
            bind_group_layouts: &[&mesh_pipeline.camera_bgl, &instance_bgl],
            push_constant_ranges: &[],
        });

        // ── 深度: 通常の G-Buffer パスと同一（Less・書き込みあり）──
        //   草は不透明（アルファはカットアウトのみ）なので深度書き込みして問題ない。
        let depth_stencil = wgpu::DepthStencilState {
            format:              df,
            depth_write_enabled: true,
            depth_compare:       wgpu::CompareFunction::Less,
            stencil:             wgpu::StencilState::default(),
            bias:                wgpu::DepthBiasState::default(),
        };

        let pipe = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some(LABEL),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_grass"),
                // 頂点バッファなし（すべて vertex_index から生成する）。
                buffers:             &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:              &shader,
                entry_point:         Some("fs_grass"),
                targets:             color_targets,
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology:   wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                // 草の葉は板ポリなので両面描画する（cull None）。
                // 裏面の法線はフラグメントシェーダが front_facing で反転する。
                cull_mode:  None,
                ..Default::default()
            },
            depth_stencil: Some(depth_stencil),
            multisample:   wgpu::MultisampleState::default(),
            multiview:     None,
            cache,
        });

        Self { pipe, instance_bgl }
    }
}

/// 草シェーダの登録名（pipeline.rs::get_shader_source のキーと一致必須）。
const GRASS_SHADER_NAME: &str = "grass_gbuffer.wgsl";

/// group1（草インスタンス）のバインドグループレイアウトを作る。
///
/// 内訳: read-only storage バッファ 1 本（インスタンス配列）+ uniform 1 本（パラメータ）。
/// storage は頂点ステージからのみ読むが、uniform は色・roughness・カットアウト閾値を
/// フラグメントでも使うため VERTEX | FRAGMENT で可視にする。
fn create_instance_bind_group_layout(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("grass_instance_bgl"),
        entries: &[
            // インスタンス配列（read-only storage）。可変長なので uniform ではなく storage。
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_INSTANCES,
                visibility: wgpu::ShaderStages::VERTEX,
                ty: wgpu::BindingType::Buffer {
                    ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                count: None,
            },
            // 見た目・風パラメータ（uniform）。
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_UNIFORM,
                visibility: wgpu::ShaderStages::VERTEX | wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty:                 wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                count: None,
            },
        ],
    })
}

// ============================================================
//  GrassInstanceBuffer — 1 プロップ種別ぶんの GPU リソース
// ============================================================

/// 1 プロップ種別ぶんの草インスタンス GPU リソース（バッファ＋バインドグループ）。
///
/// `capacity` はバッファに確保済みの要素数、`count` は実際に描く本数。
/// 本数が減っただけならバッファは作り直さず、`count` を減らすだけで済ませる
/// （毎フレーム再確保しないための容量管理）。
pub struct GrassInstanceBuffer {
    /// インスタンス配列（storage）。
    buffer: wgpu::Buffer,
    /// 見た目・風パラメータ（uniform）。
    uniform_buffer: wgpu::Buffer,
    /// group1 のバインドグループ。
    bind_group: wgpu::BindGroup,
    /// `buffer` に確保済みの要素数（>= 1。空でもダミー 1 要素を持つ）。
    capacity: usize,
    /// 実際に描画するインスタンス本数。
    count: u32,
}

impl GrassInstanceBuffer {
    /// インスタンス配列とパラメータから GPU リソースを作る。
    ///
    /// `instances` が空でもパニックしない（ダミー 1 要素を確保し `count = 0` にする。
    /// wgpu はサイズ 0 のバッファ生成でパニックするため）。
    pub fn new(
        device:   &wgpu::Device,
        pipeline: &GrassGBufferPipeline,
        instances: &[GrassInstanceGpu],
        uniform:   GrassUniformGpu,
    ) -> Self {
        let (buffer, capacity) = create_instance_buffer(device, instances);

        let uniform_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("grass_uniform"),
            contents: bytemuck::bytes_of(&uniform),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        let bind_group = create_instance_bind_group(device, pipeline, &buffer, &uniform_buffer);

        Self {
            buffer,
            uniform_buffer,
            bind_group,
            capacity,
            count: instances.len() as u32,
        }
    }

    /// インスタンス配列とパラメータを更新する。
    ///
    /// 容量が足りていれば `queue.write_buffer` で中身だけ差し替える（バインドグループは
    /// 作り直さない＝毎フレーム呼んでも安い）。容量不足なら再確保し、バッファが変わる
    /// のでバインドグループも作り直す。
    pub fn update(
        &mut self,
        device:    &wgpu::Device,
        queue:     &wgpu::Queue,
        pipeline:  &GrassGBufferPipeline,
        instances: &[GrassInstanceGpu],
        uniform:   GrassUniformGpu,
    ) {
        // ── パラメータは常に全上書き（80 バイトなので分岐する価値がない）──
        queue.write_buffer(&self.uniform_buffer, 0, bytemuck::bytes_of(&uniform));

        if instances.len() > self.capacity {
            // ── 容量不足 → 再確保（バッファが変わるのでバインドグループも再作成）──
            let (buffer, capacity) = create_instance_buffer(device, instances);
            self.bind_group =
                create_instance_bind_group(device, pipeline, &buffer, &self.uniform_buffer);
            self.buffer   = buffer;
            self.capacity = capacity;
        } else if !instances.is_empty() {
            // ── 容量内 → 中身だけ書き換える ──
            queue.write_buffer(&self.buffer, 0, bytemuck::cast_slice(instances));
        }
        // instances が空のときはバッファへ書かない（write_buffer にサイズ 0 を渡さない）。
        // ダミー要素の中身は残るが count = 0 なので一切参照されない。
        self.count = instances.len() as u32;
    }

    /// 時刻だけを更新する軽量パス（風のアニメーション用）。
    ///
    /// uniform 全体（80 バイト）ではなく `time` の 4 バイトだけを部分更新する。
    /// 毎フレーム全プロップ種別ぶん呼ばれる想定のため、転送量を最小化している。
    pub fn update_time(&self, queue: &wgpu::Queue, time: f32) {
        queue.write_buffer(
            &self.uniform_buffer,
            GRASS_UNIFORM_TIME_OFFSET,
            bytemuck::bytes_of(&time),
        );
    }

    /// 実際に描画するインスタンス本数。
    pub fn count(&self) -> u32 {
        self.count
    }
}

/// インスタンス storage バッファを作る（空配列でもパニックしない）。
///
/// 戻り値は (バッファ, 確保した要素数)。
/// wgpu はサイズ 0 のバッファ生成でパニックするため、空配列のときは
/// ゼロ初期化したダミー 1 要素を確保する（`count` 側が 0 なので描画には使われない）。
fn create_instance_buffer(
    device:    &wgpu::Device,
    instances: &[GrassInstanceGpu],
) -> (wgpu::Buffer, usize) {
    let dummy: [GrassInstanceGpu; GRASS_EMPTY_DUMMY_ELEMENTS] = Default::default();
    let data: &[GrassInstanceGpu] = if instances.is_empty() { &dummy } else { instances };

    let buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label:    Some("grass_instances"),
        contents: bytemuck::cast_slice(data),
        usage:    wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
    });
    (buffer, data.len())
}

/// group1 のバインドグループを作る。
fn create_instance_bind_group(
    device:   &wgpu::Device,
    pipeline: &GrassGBufferPipeline,
    instances: &wgpu::Buffer,
    uniform:   &wgpu::Buffer,
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label:  Some("grass_instance_bg"),
        layout: &pipeline.instance_bgl,
        entries: &[
            wgpu::BindGroupEntry {
                binding:  BINDING_INSTANCES,
                resource: instances.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding:  BINDING_UNIFORM,
                resource: uniform.as_entire_binding(),
            },
        ],
    })
}

// ============================================================
//  描画
// ============================================================

/// G-Buffer パスへ草を 1 バッチ描画する。
///
/// 頂点バッファもインデックスバッファも設定しない（すべて `vertex_index` から生成）。
/// 頂点数は常に `GRASS_MAX_VERTS_PER_BLADE` 固定で、余りはシェーダ側で面積 0 に潰す。
///
/// 呼び出し側は本関数の前に G-Buffer レンダーパスを開始しておくこと
/// （深度・MRT 4 枚は通常の G-Buffer パスと共通）。
pub fn draw_grass<'pass>(
    rp:        &mut wgpu::RenderPass<'pass>,
    pipeline:  &'pass GrassGBufferPipeline,
    buf:       &'pass GrassInstanceBuffer,
    camera_bg: &'pass wgpu::BindGroup,
) {
    // 0 本のときは何もしない（インスタンス数 0 の draw は無駄なだけでなく、
    // ダミー要素しか無いバッファを触ることになるため明示的に弾く）。
    if buf.count == 0 {
        return;
    }
    rp.set_pipeline(&pipeline.pipe);
    rp.set_bind_group(0, camera_bg, &[]);
    rp.set_bind_group(1, &buf.bind_group, &[]);
    rp.draw(0..GRASS_MAX_VERTS_PER_BLADE, 0..buf.count);
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 草シェーダのソース（自己完結。連結不要）。
    fn shader_src() -> &'static str {
        include_str!("shaders/grass_gbuffer.wgsl")
    }

    /// 草 G-Buffer WGSL を naga で parse + validate する。
    ///
    /// GPU を回せない環境ではこれが唯一の実効的な検証手段であり、最重要のテスト。
    /// 連結は行わない（grass_gbuffer.wgsl は単体でモジュールになる）。
    #[test]
    fn grass_gbuffer_shader_parses_and_validates() {
        let src = shader_src();
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("[grass_gbuffer] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[grass_gbuffer] WGSL validate 失敗: {e:?}"));
    }

    /// `get_shader_source` から草シェーダが引けること（pipeline.rs への登録漏れ検出）。
    #[test]
    fn grass_shader_is_registered_in_resolver() {
        assert_eq!(
            get_shader_source(GRASS_SHADER_NAME),
            shader_src(),
            "pipeline.rs::get_shader_source に grass_gbuffer.wgsl が登録されていない"
        );
    }

    /// インスタンス構造体が std430 の 48 バイトであること。
    /// ズレると GPU が隣の株のデータを読む（静かな描画バグ）。
    #[test]
    fn grass_instance_is_48_bytes() {
        assert_eq!(
            std::mem::size_of::<GrassInstanceGpu>(),
            GRASS_INSTANCE_BYTES,
            "GrassInstanceGpu は 48 バイト（WGSL GrassInstance と一致必須）"
        );
    }

    /// uniform 構造体が 16 バイト境界へ揃っていること（std140 の要求）。
    #[test]
    fn grass_uniform_is_16byte_aligned() {
        let size = std::mem::size_of::<GrassUniformGpu>();
        assert_eq!(size % 16, 0, "GrassUniformGpu は 16 の倍数バイトであること（実測 {size}）");
    }

    /// `update_time` が書き換えるオフセットが実際の `time` フィールド位置と一致すること。
    /// ここがズレると「roughness が時間で暴れる」ような分かりにくいバグになる。
    #[test]
    fn grass_uniform_time_offset_is_correct() {
        let u = GrassUniformGpu::default();
        let base = &u as *const _ as usize;
        let actual = (&u.time as *const f32 as usize) - base;
        assert_eq!(
            actual as wgpu::BufferAddress, GRASS_UNIFORM_TIME_OFFSET,
            "GRASS_UNIFORM_TIME_OFFSET が time の実オフセットと不一致"
        );
    }

    /// Rust 側の形状定数が WGSL 側と一致すること（文字列一致で検証する。
    /// terrain_gbuffer.rs の TERRAIN_BLEND_SLOTS 検証と同じ先例に倣う）。
    #[test]
    fn grass_shape_constants_match_shader() {
        let src = shader_src();
        for expected in [
            format!("const GRASS_MAX_SEGMENTS: u32 = {GRASS_MAX_SEGMENTS}u;"),
            format!("const GRASS_VERTS_PER_SEGMENT: u32 = {GRASS_VERTS_PER_SEGMENT}u;"),
            format!("const GRASS_MAX_PLANES: u32 = {GRASS_MAX_PLANES}u;"),
        ] {
            assert!(
                src.contains(&expected),
                "grass_gbuffer.wgsl に `{expected}` が無い（Rust 側定数と不一致）"
            );
        }
        // 派生定数は式で書かれているため、値そのものも固定しておく。
        assert_eq!(GRASS_MAX_VERTS_PER_BLADE, 96, "1 株ぶんの頂点数は 8*6*2=96");
    }
}
