// ============================================================
//  renderer/water/mod.rs — 水面描画パス（Phase W1）
//
//  ## 役割（単一責任）
//  エンジン層が解決した `ResolvedWaterVolume` の配列を受け取り、
//  「1 ドローで全水面クアッドを描く」ためのリソース（ストレージバッファ・
//  屈折背景グラブ・BindGroup・パイプライン）を管理して描画する。
//
//  ## メッシュを持たない
//  水面は常に軸平行の矩形なので、頂点バッファは一切持たない。
//  `draw(0..6, 0..N)` の 1 ドローで N 個の水ボリュームを描き、
//  頂点位置は `vertex_index`、パラメータは `instance_index` からシェーダが引く。
//
//  ## 深度
//  本パスは深度アタッチメントを持たず（TOML `no_depth = true`）、
//  共有深度の DepthOnly ビューを **サンプルテクスチャとして** group1 に受け取る。
//  遮蔽判定（手動深度テスト）と水の厚み復元をシェーダ内で行う。
//  詳細は `shaders/water_surface.wgsl` の冒頭コメントを参照。
//
//  ## 波紋（Phase I2）
//  インタラクションフィールド（`renderer::interaction`）を group2 で読む。
//  **BindGroup は本モジュールが毎フレーム自前で作る**（場テクスチャは ping-pong で
//  毎フレーム入れ替わるため、そもそも作り直しが要る）。草のように
//  `create_field_sample_bind_group_layout` の共有レイアウトを使わないのは、
//  水面パイプラインが WGSL リフレクション（`RenderPipelineBuilder`）で組まれ、
//  リフレクションが uniform を VERTEX_FRAGMENT 可視にするため共有レイアウトと
//  構造的に一致しないから。**リフレクションが返した BGL をそのまま使う**のが正解で、
//  レイアウト定義の二重管理も起きない。
//
//  場がまだ構築されていないフレーム（水はあるがインタラクションソースも草も無い等）に
//  備えて、1×1 のゼロテクスチャとゼロ UBO のフォールバックを常時持つ。
//  ゼロ UBO は `inv_extent = 0` になるので、波紋サンプルは常に窓外扱い＝影響ゼロになる。
//
//  ## 屈折の背景（自前グラブ）
//  `RefractPyramid` はブラーミップ鎖まで作るため水面には過剰。
//  ここでは「シーン HDR をフル解像度 1 ミップへコピーするだけ」の専用テクスチャを持つ。
//  **水ボリュームが 1 つも無いフレームではテクスチャ確保もコピーも行わない**（コスト 0）。
// ============================================================

pub mod params;

pub use params::{WaterParams, WATER_MAX_VOLUMES, WATER_QUAD_VERTEX_COUNT};

use std::collections::HashMap;

use crate::engine::water::{
    ResolvedWaterVolume, ShoreFieldSet, SHORE_FIELD_MAX_LAYERS, SHORE_FIELD_RESOLUTION,
};
use super::{DEPTH_FORMAT, pipeline_config::RenderPipelineBuilder};

/// ショアフィールド配列テクスチャのフォーマット（Phase W1.5）。
///
/// インタラクションフィールドと同じ Rgba16Float。理由も同じで、
/// **フィルタ可能な浮動小数フォーマットが core WebGPU ではこれ**だから
/// （Rgba32Float はオプション機能 `float32-filterable` を要求する）。
/// 岸波は距離場をバイリニア補間して位相を作るので、フィルタ不可だと
/// テクセル境界で位相が段付き、縞が見える。
///
/// 精度: f16 は 256m 付近で刻み 0.25m だが、岸波の振幅は沖で 0 へ落ちるため
/// 精度が要るのは岸近傍（数 m）だけで、そこでは刻みが 1mm 未満になる。
pub const SHORE_FIELD_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;

/// 水面描画に必要な GPU リソース一式と描画手続き。
pub struct WaterRenderer {
    /// 水面パイプライン（頂点バッファ無し・深度アタッチメント無し）。
    pipeline: wgpu::RenderPipeline,
    /// group1（パラメータ配列＋背景＋サンプラー＋深度）の BindGroupLayout。
    params_bgl: wgpu::BindGroupLayout,
    /// group2（インタラクションフィールド）の BindGroupLayout（リフレクション由来）。
    field_bgl: wgpu::BindGroupLayout,
    /// 場が無いフレーム用のフォールバック（1×1 ゼロテクスチャ）のビュー。
    fallback_field_view: wgpu::TextureView,
    /// フォールバック用のゼロ UBO（`inv_extent = 0` ＝ 常に窓外＝波紋ゼロ）。
    fallback_field_uniform: wgpu::Buffer,
    /// ID パス用パイプライン（同じクアッドを ID バッファへ描く。エディタのピッキング）。
    id_pipeline: wgpu::RenderPipeline,
    /// ID パス group1（パラメータ配列のみ）の BindGroupLayout。
    id_params_bgl: wgpu::BindGroupLayout,
    /// 屈折背景サンプラー（線形・ClampToEdge）。
    sampler: wgpu::Sampler,
    /// 屈折背景グラブのフォーマット（シーン HDR と一致必須。copy_texture_to_texture の要件）。
    hdr_format: wgpu::TextureFormat,

    /// 水パラメータのストレージバッファ（必要に応じて容量を拡張する）。
    params_buf: Option<wgpu::Buffer>,
    /// `params_buf` の容量（要素数）。
    params_capacity: usize,

    /// 屈折背景グラブ（シーン HDR のフル解像度 1 ミップコピー）。
    grab_tex: Option<wgpu::Texture>,
    grab_view: Option<wgpu::TextureView>,
    /// グラブの現在サイズ（サーフェスサイズ追従）。
    grab_width: u32,
    grab_height: u32,

    /// このフレームの group1 BindGroup（深度ビューがフレーム依存のため毎フレーム作り直す）。
    frame_bind_group: Option<wgpu::BindGroup>,
    /// このフレームの group2 BindGroup（波紋の場。ping-pong で毎フレーム変わる）。
    frame_field_bind_group: Option<wgpu::BindGroup>,
    /// このフレームの ID パス group1 BindGroup（パラメータバッファのみ）。
    /// バッファは容量拡張で作り直され得るので、`frame_bind_group` と同じく毎フレーム作る。
    id_bind_group: Option<wgpu::BindGroup>,
    /// このフレームで描くインスタンス数（= 水ボリューム数、上限クランプ後）。
    instance_count: u32,

    // ─── ショアフィールド（岸波。Phase W1.5）─────────────────────
    /// 水域ごとの岸情報を積んだ配列テクスチャ（レイヤ = 水域）。
    /// **岸波を使う水域が 1 つも無いフレームでは確保しない**（VRAM もアップロードも 0）。
    shore_tex: Option<wgpu::Texture>,
    /// 上のビュー（BindGroup 用）。
    shore_view: Option<wgpu::TextureView>,
    /// 岸波が無いフレーム用のフォールバック（1×1×1 のゼロ配列テクスチャ）のビュー。
    /// パラメータ側のレイヤ番号が負なのでシェーダは触らないが、
    /// BindGroup には常に何かを挿す必要があるため常備する。
    fallback_shore_view: wgpu::TextureView,
    /// ショアフィールド用サンプラー（線形・ClampToEdge）。
    shore_sampler: wgpu::Sampler,
    /// レイヤ番号 → 最後にアップロードした revision。
    /// `ShoreFieldSet` 側の revision と一致していればアップロードを飛ばす。
    shore_uploaded: HashMap<u32, u64>,
    /// 上限超過の警告を出したか（毎フレームのログ氾濫を防ぐため 1 回だけ出す）。
    warned_overflow: bool,
}

impl WaterRenderer {
    /// パイプラインを構築する（テクスチャ・バッファは `prepare` で遅延確保）。
    ///
    /// `hdr_format` はシーン HDR のフォーマット（`HDR_FORMAT`）。
    /// カラーターゲットと屈折背景グラブの両方に使う。
    pub fn new(
        device:     &wgpu::Device,
        hdr_format: wgpu::TextureFormat,
        cache:      Option<&wgpu::PipelineCache>,
    ) -> Self {
        // 自己完結のシェーダリゾルバ（連結は water_surface.wgsl 1 本のみ。
        // shader_common.wgsl は連結しない＝マテリアル group を要求しないため）。
        let (pipeline, mut bgls) = RenderPipelineBuilder::new(
            device,
            include_str!("../pipelines/water_surface.toml"),
            hdr_format,
            DEPTH_FORMAT,
        )
        .with_label("Water Surface")
        .with_cache(cache)
        .build(|name: &str| -> &'static str {
            match name {
                "water_surface.wgsl" => include_str!("../shaders/water_surface.wgsl"),
                other => panic!("water: unknown shader source: {other}"),
            }
        });
        // bgls は group 番号順（0 = カメラ, 1 = 水リソース, 2 = インタラクションフィールド）。
        // group1 と group2 を保持する（**remove は添字がずれるので大きい方から取る**）。
        assert!(bgls.len() >= 3,
            "water_surface.wgsl は group0(カメラ)/group1(水リソース)/group2(波紋の場) を宣言すること");
        let field_bgl  = bgls.remove(2);
        let params_bgl = bgls.remove(1);

        // ── 波紋の場が無いフレーム用のフォールバック ──────────────────
        // 1×1 のゼロテクスチャ（場と同じ Rgba16Float）とゼロ UBO。
        // ゼロ UBO は inv_extent = 0 なので、波紋サンプルの UV は常に 0 に潰れ、
        // 高さも勾配も 0＝W1 と同じ見た目になる（分岐を増やさずに無効化できる）。
        let fallback_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Water Ripple Fallback"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          super::interaction::INTERACTION_FIELD_FORMAT,
            usage:           wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats:    &[],
        });
        let fallback_field_view = fallback_tex.create_view(&wgpu::TextureViewDescriptor::default());
        let fallback_field_uniform = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Water Ripple Fallback Uniform"),
            size:  std::mem::size_of::<super::interaction::InteractionFieldUniformGpu>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            // 内容を明示的にゼロで埋めるため map 済みで作る（未初期化メモリを読ませない）。
            mapped_at_creation: true,
        });
        fallback_field_uniform.slice(..).get_mapped_range_mut().fill(0u8);
        fallback_field_uniform.unmap();

        // ── ID パス用パイプライン（エディタのピッキング）─────────────────────
        // 同じクアッド生成・同じパラメータバッファを使い、出力先だけが ID バッファ。
        // color_format は TOML 側で Rgba32Float を明示しているため、ここで渡す
        // hdr_format は使われない（ビルダ引数の体裁を合わせるためだけに渡す）。
        let (id_pipeline, mut id_bgls) = RenderPipelineBuilder::new(
            device,
            include_str!("../pipelines/water_id.toml"),
            hdr_format,
            DEPTH_FORMAT,
        )
        .with_label("Water Id")
        .with_cache(cache)
        .build(|name: &str| -> &'static str {
            match name {
                "water_id.wgsl" => include_str!("../shaders/water_id.wgsl"),
                other => panic!("water: unknown shader source: {other}"),
            }
        });
        assert!(id_bgls.len() >= 2, "water_id.wgsl は group0(カメラ)/group1(水パラメータ) を宣言すること");
        let id_params_bgl = id_bgls.remove(1);

        // ── ショアフィールドが無いフレーム用のフォールバック（Phase W1.5）──
        // 1×1×1 のゼロ配列テクスチャ。パラメータのレイヤ番号が負のときシェーダは
        // サンプルしないので中身は問われないが、BindGroup には必ず何か要る。
        let fallback_shore = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Water Shore Fallback"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          SHORE_FIELD_FORMAT,
            usage:           wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats:    &[],
        });
        let fallback_shore_view = fallback_shore.create_view(&wgpu::TextureViewDescriptor {
            label:      Some("Water Shore Fallback View"),
            dimension:  Some(wgpu::TextureViewDimension::D2Array),
            ..Default::default()
        });
        let shore_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Water Shore Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Water Scene Grab Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        Self {
            pipeline,
            params_bgl,
            field_bgl,
            fallback_field_view,
            fallback_field_uniform,
            id_pipeline,
            id_params_bgl,
            sampler,
            hdr_format,
            params_buf:       None,
            params_capacity:  0,
            grab_tex:         None,
            grab_view:        None,
            grab_width:       0,
            grab_height:      0,
            frame_bind_group: None,
            frame_field_bind_group: None,
            id_bind_group:    None,
            instance_count:   0,
            warned_overflow:  false,
            shore_tex:        None,
            shore_view:       None,
            fallback_shore_view,
            shore_sampler,
            shore_uploaded:   HashMap::new(),
        }
    }

    /// ショアフィールド（Phase W1.5）を GPU の配列テクスチャへ反映する。
    ///
    /// 焼き直された（revision が変わった）レイヤだけを `write_texture` する。
    /// 岸波を使う水域が 1 つも無ければテクスチャの確保すら行わない。
    fn sync_shore_fields(
        &mut self,
        device: &wgpu::Device,
        queue:  &wgpu::Queue,
        shore:  &ShoreFieldSet,
    ) {
        if shore.is_empty() {
            // 岸波を使う水域が消えたらテクスチャごと解放する（4MB を握り続けない）。
            self.shore_tex = None;
            self.shore_view = None;
            self.shore_uploaded.clear();
            return;
        }
        // ── 配列テクスチャの遅延確保（レイヤ数は上限固定。増減で作り直さない）──
        let res = SHORE_FIELD_RESOLUTION as u32;
        if self.shore_tex.is_none() {
            let tex = device.create_texture(&wgpu::TextureDescriptor {
                label:           Some("Water Shore Field"),
                size: wgpu::Extent3d {
                    width:                 res,
                    height:                res,
                    depth_or_array_layers: SHORE_FIELD_MAX_LAYERS as u32,
                },
                mip_level_count: 1,
                sample_count:    1,
                dimension:       wgpu::TextureDimension::D2,
                format:          SHORE_FIELD_FORMAT,
                usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                view_formats:    &[],
            });
            self.shore_view = Some(tex.create_view(&wgpu::TextureViewDescriptor {
                label:     Some("Water Shore Field View"),
                dimension: Some(wgpu::TextureViewDimension::D2Array),
                ..Default::default()
            }));
            self.shore_tex = Some(tex);
            // 新しいテクスチャは中身が未定義なので、アップロード済み記録を捨てて焼き直す。
            self.shore_uploaded.clear();
        }
        let tex = self.shore_tex.as_ref().expect("water: shore texture 未確保");

        // ── 変化したレイヤだけアップロード ──
        //    f32×4 → f16×4 へ詰め替える（フィルタ可能フォーマットの制約。上の定数コメント参照）。
        for (_id, entry) in shore.iter() {
            if self.shore_uploaded.get(&entry.layer) == Some(&entry.revision) {
                continue;
            }
            let mut halfs: Vec<half::f16> = Vec::with_capacity(entry.texels.len() * 4);
            for t in &entry.texels {
                halfs.push(half::f16::from_f32(t[0]));
                halfs.push(half::f16::from_f32(t[1]));
                halfs.push(half::f16::from_f32(t[2]));
                halfs.push(half::f16::from_f32(t[3]));
            }
            /// Rgba16Float の 1 テクセルのバイト数（4 チャネル × f16）。
            const SHORE_TEXEL_BYTES: u32 = 8;
            queue.write_texture(
                wgpu::ImageCopyTexture {
                    texture:   tex,
                    mip_level: 0,
                    origin:    wgpu::Origin3d { x: 0, y: 0, z: entry.layer },
                    aspect:    wgpu::TextureAspect::All,
                },
                bytemuck::cast_slice(&halfs),
                wgpu::ImageDataLayout {
                    offset:         0,
                    bytes_per_row:  Some(res * SHORE_TEXEL_BYTES),
                    rows_per_image: Some(res),
                },
                wgpu::Extent3d { width: res, height: res, depth_or_array_layers: 1 },
            );
            self.shore_uploaded.insert(entry.layer, entry.revision);
        }
    }

    /// このフレームの水面描画を準備する。
    ///
    /// 戻り値 `false` は「描くものが無い」の意味で、呼び出し側は
    /// グラブコピーも水面パスもスキップすること（リソース確保も行われない＝コスト 0）。
    ///
    /// `depth_view` は共有深度の **DepthOnly ビュー**（`RenderFrame::depth_only_view_r`）。
    /// 本パスは深度アタッチメントを持たず、これをサンプルして手動深度テストを行うため、
    /// BindGroup 生成に必要（フレーム依存なので毎フレーム作り直す）。
    ///
    /// `id_base` はエディタのピッキング ID 空間のベースオフセット（`canvas_id_offset`）。
    /// ここでアップロードするパラメータに raw アクタ ID を埋めておき、後段の ID パス
    /// （`draw_id`）がそのまま書き出す。
    ///
    /// `field` は波紋を書くインタラクションフィールド（Phase I2）。`None` の
    /// フレームはゼロのフォールバックがバインドされ、波紋の寄与が完全に消える
    /// （水面の見た目は W1 と同一になる）。
    ///
    /// `shore` は岸波のショアフィールド集合（Phase W1.5）。焼かれていない水域は
    /// パラメータのレイヤ番号が負になり、シェーダが岸波を完全にスキップする。
    #[allow(clippy::too_many_arguments)]
    pub fn prepare(
        &mut self,
        device:     &wgpu::Device,
        queue:      &wgpu::Queue,
        volumes:    &[ResolvedWaterVolume],
        camera_pos: [f32; 3],
        width:      u32,
        height:     u32,
        depth_view: &wgpu::TextureView,
        id_base:    u32,
        field:      Option<&super::interaction::InteractionFieldRenderer>,
        shore:      &ShoreFieldSet,
    ) -> bool {
        self.instance_count         = 0;
        self.frame_bind_group       = None;
        self.frame_field_bind_group = None;
        self.id_bind_group          = None;
        if volumes.is_empty() {
            return false;
        }

        // 上限超過は切り捨て（警告は 1 回だけ）。
        let count = volumes.len().min(WATER_MAX_VOLUMES);
        if volumes.len() > WATER_MAX_VOLUMES && !self.warned_overflow {
            self.warned_overflow = true;
            eprintln!(
                "[water] 水ボリュームが上限 {} 個を超えたため {} 個を切り捨てます",
                WATER_MAX_VOLUMES,
                volumes.len() - WATER_MAX_VOLUMES,
            );
        }

        // ── ショアフィールド（岸波。Phase W1.5）を GPU へ反映する ──
        //    パラメータ生成より先に行う（レイヤ番号を確定させてから params へ詰めるため）。
        self.sync_shore_fields(device, queue, shore);

        // ── パラメータ配列を作ってアップロード ──
        let gpu: Vec<WaterParams> = volumes[..count]
            .iter()
            .map(|v| WaterParams::from_resolved(
                v, camera_pos, id_base, shore.get(v.actor_dfs_id)))
            .collect();

        if self.params_buf.is_none() || self.params_capacity < count {
            let capacity = count.max(1);
            self.params_buf = Some(device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("Water Params Storage"),
                size:  (capacity * std::mem::size_of::<WaterParams>()) as u64,
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            }));
            self.params_capacity = capacity;
        }
        let params_buf = self.params_buf.as_ref().expect("water: params buffer 未確保");
        queue.write_buffer(params_buf, 0, bytemuck::cast_slice(&gpu));

        // ── 屈折背景グラブをサーフェスサイズへ追従確保 ──
        let w = width.max(1);
        let h = height.max(1);
        if self.grab_tex.is_none() || self.grab_width != w || self.grab_height != h {
            let tex = device.create_texture(&wgpu::TextureDescriptor {
                label:           Some("Water Scene Grab"),
                size:            wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
                mip_level_count: 1,
                sample_count:    1,
                dimension:       wgpu::TextureDimension::D2,
                format:          self.hdr_format,
                // サンプル（屈折の背景）＋ シーン HDR からのコピー先。
                usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                view_formats:    &[],
            });
            self.grab_view   = Some(tex.create_view(&wgpu::TextureViewDescriptor::default()));
            self.grab_tex    = Some(tex);
            self.grab_width  = w;
            self.grab_height = h;
        }
        let grab_view = self.grab_view.as_ref().expect("water: grab view 未確保");

        // ── group1 BindGroup（深度ビューがフレーム依存のため毎フレーム生成）──
        self.frame_bind_group = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Resources BG"),
            layout:  &self.params_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: params_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(grab_view) },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::Sampler(&self.sampler) },
                wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::TextureView(depth_view) },
                // ショアフィールド（Phase W1.5）。未確保のフレームは 1×1 のフォールバック。
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: wgpu::BindingResource::TextureView(
                        self.shore_view.as_ref().unwrap_or(&self.fallback_shore_view)),
                },
                wgpu::BindGroupEntry {
                    binding: 5,
                    resource: wgpu::BindingResource::Sampler(&self.shore_sampler),
                },
            ],
        }));

        // ── group2 BindGroup（波紋の場。ping-pong で毎フレーム入れ替わる）──────
        // 場が未構築のフレームはゼロのフォールバックを挿し、シェーダ側の分岐を増やさない。
        let (field_view, field_sampler_ref, field_uniform) = match field {
            Some(f) => (f.field_view(), Some(f.field_sampler()), f.field_uniform_buffer()),
            None    => (&self.fallback_field_view, None, &self.fallback_field_uniform),
        };
        let fallback_sampler = &self.sampler;
        self.frame_field_bind_group = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Ripple Field BG"),
            layout:  &self.field_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0, resource: wgpu::BindingResource::TextureView(field_view) },
                wgpu::BindGroupEntry {
                    binding: 1,
                    // 場のサンプラーが無いときは屈折背景用（線形・ClampToEdge）を流用する。
                    // どちらも同じフィルタ設定なので、見た目にも検証上も等価。
                    resource: wgpu::BindingResource::Sampler(
                        field_sampler_ref.unwrap_or(fallback_sampler)) },
                wgpu::BindGroupEntry {
                    binding: 2, resource: field_uniform.as_entire_binding() },
            ],
        }));

        // ── ID パス用 group1（パラメータ配列のみ）─────────────────────────
        // ID パスは Edit / ポーズ中しか描かれないが、BindGroup 生成は 1 バインディングだけで
        // 極めて軽いため、描くかどうかを判定せずここで作っておく（呼び出し側の分岐を増やさない）。
        self.id_bind_group = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Id Params BG"),
            layout:  &self.id_params_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: params_buf.as_entire_binding() },
            ],
        }));

        self.instance_count = count as u32;
        true
    }

    /// シーン HDR を屈折背景グラブへコピーする（水面パスの **直前・レンダーパス外**で呼ぶ）。
    ///
    /// メインパス・WBOIT 合成の後に呼ぶことで、スカイボックスも既存半透明も
    /// 屈折の背景に含まれる。`prepare` が `true` を返したフレームでのみ呼ぶこと。
    pub fn record_grab(&self, encoder: &mut wgpu::CommandEncoder, scene_hdr_tex: &wgpu::Texture) {
        let Some(tex) = self.grab_tex.as_ref() else { return; };
        encoder.copy_texture_to_texture(
            wgpu::ImageCopyTexture {
                texture:   scene_hdr_tex,
                mip_level: 0,
                origin:    wgpu::Origin3d::ZERO,
                aspect:    wgpu::TextureAspect::All,
            },
            wgpu::ImageCopyTexture {
                texture:   tex,
                mip_level: 0,
                origin:    wgpu::Origin3d::ZERO,
                aspect:    wgpu::TextureAspect::All,
            },
            wgpu::Extent3d {
                width:                self.grab_width,
                height:               self.grab_height,
                depth_or_array_layers: 1,
            },
        );
    }

    /// 水面パス内で全水ボリュームを 1 ドローで描く。
    /// `prepare` が `true` を返したフレームでのみ呼ぶこと。
    pub fn draw<'p>(&'p self, pass: &mut wgpu::RenderPass<'p>, camera_bg: &'p wgpu::BindGroup) {
        let Some(bg) = self.frame_bind_group.as_ref() else { return; };
        let Some(field_bg) = self.frame_field_bind_group.as_ref() else { return; };
        if self.instance_count == 0 {
            return;
        }
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, camera_bg, &[]);
        pass.set_bind_group(1, bg, &[]);
        pass.set_bind_group(2, field_bg, &[]);
        // 頂点バッファ無し: 6 頂点 × N インスタンス（= 水ボリューム数）。
        pass.draw(0..WATER_QUAD_VERTEX_COUNT, 0..self.instance_count);
    }

    /// ID パス内で全水面クアッドを 1 ドローで描く（エディタのピッキング用）。
    ///
    /// `prepare` が `true` を返したフレームでのみ呼ぶこと。
    /// 呼ぶ位置は「3D モデルの ID 描画より後・ギズモアイコンより前」を推奨する:
    ///   ・モデルより後 … 水面下のモデルの上に水面が上書きされ、水面が選択される
    ///   ・ギズモより前 … 同深度で競合したときはギズモ（編集ハンドル）を優先できる
    /// 深度テストはパイプライン側（LessEqual・書き込み無し）が担うので、
    /// 「水面より手前の物体をクリックしたらそちらが選択される」は描画順に依存しない。
    pub fn draw_id<'p>(&'p self, pass: &mut wgpu::RenderPass<'p>, camera_bg: &'p wgpu::BindGroup) {
        let Some(bg) = self.id_bind_group.as_ref() else { return; };
        if self.instance_count == 0 {
            return;
        }
        pass.set_pipeline(&self.id_pipeline);
        pass.set_bind_group(0, camera_bg, &[]);
        pass.set_bind_group(1, bg, &[]);
        pass.draw(0..WATER_QUAD_VERTEX_COUNT, 0..self.instance_count);
    }
}

// ============================================================
//  テスト（WGSL 静的検証）
// ============================================================
#[cfg(test)]
mod tests {
    /// 水面シェーダを naga で parse + validate する（GPU デバイス不要）。
    /// TOML の shader_sources は water_surface.wgsl 単体なので、連結順の考慮は不要。
    #[test]
    fn water_surface_shader_parses_and_validates() {
        let src = include_str!("../shaders/water_surface.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("water_surface.wgsl WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("water_surface.wgsl WGSL validate 失敗: {e:?}"));
    }

    /// TOML のエントリポイント名がシェーダの実装と一致すること
    /// （不一致はパイプライン生成時のランタイム失敗になるため、静的に照合しておく）。
    #[test]
    fn water_surface_toml_entries_match_shader() {
        let toml_src = include_str!("../pipelines/water_surface.toml");
        let wgsl_src = include_str!("../shaders/water_surface.wgsl");
        assert!(toml_src.contains("vertex_entry    = \"vs_water\""));
        assert!(toml_src.contains("fragment_entry  = \"fs_water\""));
        assert!(wgsl_src.contains("fn vs_water("));
        assert!(wgsl_src.contains("fn fs_water("));
        // 深度アタッチメントを持たない前提（手動深度テスト）を TOML 側でも保証する。
        assert!(toml_src.contains("no_depth        = true"));
    }

    /// 水面 ID パスシェーダを naga で parse + validate する（GPU デバイス不要）。
    #[test]
    fn water_id_shader_parses_and_validates() {
        let src = include_str!("../shaders/water_id.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("water_id.wgsl WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("water_id.wgsl WGSL validate 失敗: {e:?}"));
    }

    /// ID パス TOML のエントリポイント名と深度設定がシェーダ／ID パスの前提と一致すること。
    #[test]
    fn water_id_toml_entries_match_shader() {
        let toml_src = include_str!("../pipelines/water_id.toml");
        let wgsl_src = include_str!("../shaders/water_id.wgsl");
        assert!(toml_src.contains("vertex_entry    = \"vs_water_id\""));
        assert!(toml_src.contains("fragment_entry  = \"fs_water_id\""));
        assert!(wgsl_src.contains("fn vs_water_id("));
        assert!(wgsl_src.contains("fn fs_water_id("));
        // ID パスは深度アタッチメントを持つ（手前の物体が優先して選択される）前提。
        assert!(!toml_src.contains("no_depth"));
        assert!(toml_src.contains("depth_compare   = \"LessEqual\""));
        assert!(toml_src.contains("depth_write     = false"));
        // 出力先は ID バッファ（Rgba32Float）。
        assert!(toml_src.contains("color_format    = \"Rgba32Float\""));
    }

    /// 2 つの水シェーダの `WaterParams` は同一レイアウトでなければならない
    /// （同じストレージバッファを別々の struct 宣言で読むため、ズレると全パラメータが壊れる）。
    #[test]
    fn water_params_struct_fields_match_between_shaders() {
        /// WGSL ソースから `struct WaterParams { ... }` のフィールド名列を抜き出す。
        fn field_names(src: &str) -> Vec<String> {
            let body = src
                .split_once("struct WaterParams {")
                .expect("struct WaterParams が見つからない")
                .1
                .split_once('}')
                .expect("struct WaterParams の終端が見つからない")
                .0;
            body.lines()
                .map(|l| l.trim())
                // コメント行・空行を除外し、"name: type," の name だけを取る
                .filter(|l| !l.is_empty() && !l.starts_with("//"))
                .filter_map(|l| l.split_once(':').map(|(n, _)| n.trim().to_string()))
                .collect()
        }
        let surface = field_names(include_str!("../shaders/water_surface.wgsl"));
        let id      = field_names(include_str!("../shaders/water_id.wgsl"));
        assert_eq!(surface, id,
            "water_surface.wgsl と water_id.wgsl の WaterParams フィールド順が食い違っている");
        assert_eq!(surface.len(), 11,
            "Rust 側 WaterParams（vec4 11 本）と本数を揃えること");
    }

    /// 水面シェーダが岸波（Phase W1.5）を実装していること。
    ///
    /// リファクタで group1 の配列テクスチャや合成関数が落ちても、静かに
    /// 「岸波が出ない」になるだけで気づけないため、文字列で押さえる。
    #[test]
    fn water_shader_implements_shore_waves() {
        let src = include_str!("../shaders/water_surface.wgsl");
        assert!(src.contains("@group(1) @binding(4) var t_shore: texture_2d_array<f32>;"),
            "ショアフィールド（group1 binding4）の宣言が消えている");
        assert!(src.contains("fn water_shore_height("),
            "岸波の高さ関数が消えている（W5.1 が頂点段で呼ぶ前提の関数）");
        assert!(src.contains("fn water_shore_gradient("), "岸波の勾配関数が消えている");
        assert!(src.contains("fn water_shore_foam("),     "砕け泡・打ち上げが消えている");
    }

    /// **W5.1（頂点変位）の合流点**である合成高さ／勾配関数が存在すること。
    ///
    /// この 2 つは「フラグメントの法線と頂点変位が同じ高さ場を見る」ための唯一の窓口であり、
    /// 個別ソースを直接足す実装へ戻すと W5.1 で必ず食い違う。契約として固定する。
    #[test]
    fn water_shader_exposes_combined_height_field() {
        let src = include_str!("../shaders/water_surface.wgsl");
        assert!(src.contains("fn water_surface_height(p: WaterParams, world_xz: vec2<f32>, t: f32) -> f32"),
            "合成高さ関数（W5.1 が頂点段で呼ぶ）のシグネチャが変わっている");
        assert!(src.contains("fn water_surface_gradient(p: WaterParams, world_xz: vec2<f32>, t: f32) -> vec2<f32>"),
            "合成勾配関数のシグネチャが変わっている");
        // フラグメントは必ず合成関数経由で勾配を得ること（個別ソースの直接加算に戻さない）。
        assert!(src.contains("water_surface_gradient(p, in.world_pos.xz, u_camera.time)"),
            "fs_water が合成勾配関数を使っていない");
    }
}
