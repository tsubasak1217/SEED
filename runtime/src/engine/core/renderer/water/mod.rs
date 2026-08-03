// ============================================================
//  renderer/water/mod.rs — 水面描画パス（Phase W1）
//
//  ## 役割（単一責任）
//  エンジン層が解決した `ResolvedWaterVolume` の配列を受け取り、
//  水面を描くためのリソース（ストレージバッファ・屈折背景グラブ・
//  BindGroup・パイプライン）を管理して描画する。
//
//  ## メッシュを持たない
//  水面の形状はパラメータから完全に決まるので、頂点バッファは一切持たない。
//  頂点位置は `vertex_index`（格子セルと角）、パラメータは `instance_index` から
//  シェーダが引く。形状生成の実体は `shaders/water_height_field.wgsl` にあり、
//  水面パスと ID パスが**同じ関数**を呼ぶ（Phase W5.1）。
//
//  ## 2 バケット・2 ドロー（Phase W5.1）
//  頂点変位のために 1 インスタンスを格子へ分割した結果、1 インスタンスあたりの
//  頂点数がクアッド（Ocean/Region）と川リボンで桁違いになった。
//  1 ドローに頂点数は 1 つしか指定できないため、パラメータ配列を
//  **[クアッド…, 川…] の順**に並べ、`instance_index` のレンジを分けて 2 回描く。
//  分割数の決め方（頂点予算・放射状ワープ）は `tessellation.rs` が正典。
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
pub mod tessellation;

pub use params::{
    WaterParams, WATER_MAX_INSTANCES, WATER_MAX_VOLUMES,
};

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

/// 水シェーダのソース解決（パイプライン生成とテストが共有する唯一の窓口）。
///
/// 形状・高さ場は `water_height_field.wgsl` に切り出してあり、
/// 水面パスと ID パスの両方がこれを**先頭に**連結する（Phase W5.1）。
/// 連結順は TOML の `shader_sources` が正典で、WGSL は宣言前の識別子を
/// 参照できないため共有モジュールが必ず先でなければならない。
/// （`pub(super)`）コースティクス生成パス（Phase W5.3）も同じ高さ場モジュールを連結するため、
/// renderer モジュール内から参照できるようにしてある。**実体はここが唯一**で、
/// `caustics.rs` のリゾルバは `water_height_field.wgsl` をここへ委譲する。
pub(super) fn resolve_water_shader(name: &str) -> &'static str {
    match name {
        "water_height_field.wgsl" => include_str!("../shaders/water_height_field.wgsl"),
        "water_surface.wgsl"      => include_str!("../shaders/water_surface.wgsl"),
        "water_id.wgsl"           => include_str!("../shaders/water_id.wgsl"),
        other => panic!("water: unknown shader source: {other}"),
    }
}

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
    /// ID パス用パイプライン（同じ格子を ID バッファへ描く。エディタのピッキング）。
    id_pipeline: wgpu::RenderPipeline,
    /// ID パス group1（パラメータ配列＋ショアフィールド）の BindGroupLayout。
    ///
    /// **水面パスの group1 とは別物**である。ID パスは屈折の背景・シーン深度
    /// （binding1〜3）を宣言しないため、リフレクションが返す BGL の構成が違う。
    id_params_bgl: wgpu::BindGroupLayout,
    /// ID パス group2（波紋の場）の BindGroupLayout（頂点変位で読む。Phase W5.1）。
    id_field_bgl: wgpu::BindGroupLayout,
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
    /// このフレームの ID パス group1 BindGroup（パラメータ＋ショアフィールド）。
    /// バッファは容量拡張で作り直され得るので、`frame_bind_group` と同じく毎フレーム作る。
    id_bind_group: Option<wgpu::BindGroup>,
    /// このフレームの ID パス group2 BindGroup（波紋の場。ping-pong で毎フレーム変わる）。
    id_field_bind_group: Option<wgpu::BindGroup>,

    // ─── このフレームの描画バケット（Phase W5.1）────────────────
    //
    // 格子分割を入れたことで「1 インスタンスの頂点数」がクアッドと川で大きく違う。
    // 1 ドローに頂点数は 1 つしか指定できないため、**パラメータ配列を
    // [クアッド…, 川…] の順に並べ、2 回に分けて描く**。
    // 混ぜたまま最大値で描くと、川のインスタンス（数百本になり得る）まで
    // クアッドぶんの頂点を空回しすることになる。
    /// クアッド（Ocean / Region）インスタンス数。配列の先頭から連続。
    quad_count: u32,
    /// 川リボンのインスタンス数。配列の `quad_count` 番目から連続。
    river_count: u32,
    /// クアッド 1 インスタンスの頂点数（格子の分割数から決まる）。
    quad_vertex_count: u32,
    /// 川リボン 1 インスタンスの頂点数。
    river_vertex_count: u32,

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
        // 連結は「共有モジュール + 水面本体」の 2 本（TOML の shader_sources が正典）。
        // shader_common.wgsl は連結しない＝マテリアル group を要求しないため。
        let (pipeline, mut bgls) = RenderPipelineBuilder::new(
            device,
            include_str!("../pipelines/water_surface.toml"),
            hdr_format,
            DEPTH_FORMAT,
        )
        .with_label("Water Surface")
        .with_cache(cache)
        .build(resolve_water_shader);
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
        .build(resolve_water_shader);
        assert!(id_bgls.len() >= 3,
            "water_id.wgsl は group0(カメラ)/group1(水パラメータ＋岸波)/group2(波紋の場) を宣言すること");
        // **remove は添字がずれるので大きい方から取る**（水面パス側と同じ理由）。
        let id_field_bgl  = id_bgls.remove(2);
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
            id_field_bgl,
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
            id_field_bind_group: None,
            quad_count:         0,
            river_count:        0,
            quad_vertex_count:  0,
            river_vertex_count: 0,
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
        self.quad_count             = 0;
        self.river_count            = 0;
        self.quad_vertex_count      = 0;
        self.river_vertex_count     = 0;
        self.frame_bind_group       = None;
        self.frame_field_bind_group = None;
        self.id_bind_group          = None;
        self.id_field_bind_group    = None;
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

        // ── パラメータ配列（＝描画インスタンス列）を作ってアップロード ──
        //    W1〜W1.5 は「1 水ボリューム = 1 インスタンス」だったが、
        //    川（W4）は折れ線の 1 分割ごとに 1 インスタンス（リボンのクアッド）を出す。
        //
        //    **Phase W5.1 以降はクアッドと川を別バケットへ分ける**。格子分割によって
        //    1 インスタンスの頂点数が両者で桁違いになり、1 ドローの頂点数（1 つだけ）を
        //    共有できなくなったためである。配列は [クアッド…, 川…] の順に連結し、
        //    2 回のドローが `instance_index` のレンジで自分のぶんだけを描く。
        let mut quads:  Vec<WaterParams> = Vec::with_capacity(count);
        let mut rivers: Vec<WaterParams> = Vec::new();
        // 各バケットが「欲しがる」分割数（後で頂点予算へ収める）。
        let mut quad_div_want  = 1u32;
        let mut river_div_want = (1u32, 1u32);
        for v in &volumes[..count] {
            match v.river.as_ref() {
                // 川: 折れ線の隣り合うノード対ごとにリボンの 1 コマを積む。
                Some(river) => {
                    for w in river.nodes.windows(2) {
                        if quads.len() + rivers.len() >= WATER_MAX_INSTANCES { break; }
                        rivers.push(WaterParams::from_river_segment(
                            v, &w[0], &w[1], river.half_width, river.flow_speed, id_base));
                        // 幅方向は半幅から、長さ方向は区間の実長から刻み数を決める。
                        let seg_len = distance_xyz(w[0].pos, w[1].pos);
                        let want = tessellation::river_desired_divisions(river.half_width, seg_len);
                        river_div_want = (river_div_want.0.max(want.0), river_div_want.1.max(want.1));
                    }
                }
                // Ocean / Region: 1 個の軸平行クアッド（を格子へ刻んだもの）。
                None => {
                    if quads.len() + rivers.len() >= WATER_MAX_INSTANCES { break; }
                    let p = WaterParams::from_resolved(
                        v, camera_pos, id_base, shore.get(v.actor_dfs_id));
                    quad_div_want = quad_div_want.max(tessellation::quad_desired_divisions(
                        matches!(v.kind, crate::engine::components::water_volume_component::WaterVolumeKind::Ocean),
                        p.half_extent[0].max(p.half_extent[2]),
                    ));
                    quads.push(p);
                }
            }
            if quads.len() + rivers.len() >= WATER_MAX_INSTANCES {
                if !self.warned_overflow {
                    self.warned_overflow = true;
                    eprintln!(
                        "[water] 描画インスタンスが上限 {WATER_MAX_INSTANCES} 個に達したため \
                         以降を切り捨てます（川の分割が多すぎる可能性があります）",
                    );
                }
                break;
            }
        }
        // 川の制御点が足りない等で 1 インスタンスも出なければ、描くものが無い。
        if quads.is_empty() && rivers.is_empty() {
            return false;
        }

        // ── 格子分割数を頂点予算へ収めて全インスタンスへ焼き込む（Phase W5.1）──
        //    バケット内は同じ分割数でなければならない（1 ドローの頂点数は 1 つだけ）。
        let quad_div = tessellation::fit_quad_divisions(quad_div_want, quads.len());
        for p in quads.iter_mut() {
            p.set_grid_divisions(quad_div, quad_div);
        }
        let (river_div_a, river_div_l) =
            tessellation::fit_river_divisions(river_div_want, rivers.len());
        for p in rivers.iter_mut() {
            p.set_grid_divisions(river_div_a, river_div_l);
        }
        self.quad_count         = quads.len()  as u32;
        self.river_count        = rivers.len() as u32;
        self.quad_vertex_count  = tessellation::grid_vertex_count(quad_div, quad_div);
        self.river_vertex_count = tessellation::grid_vertex_count(river_div_a, river_div_l);

        // 2 バケットを 1 本の配列へ連結する（先頭 quad_count 個がクアッド）。
        let mut gpu = quads;
        gpu.extend(rivers);
        let count = gpu.len();

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

        // ── ID パス用 group1／group2 ─────────────────────────────────────
        // Phase W5.1 で ID パスも頂点変位を掛けるようになったため、岸波（group1
        // binding4/5）と波紋の場（group2）を**頂点段から**読む。水面パスと同じ
        // リソースを挿すが、**レイアウトは別物**（ID パスは屈折の背景・深度を宣言しない）
        // なので BindGroup も別に作る必要がある。
        // ID パスは Edit / ポーズ中しか描かれないが、生成コストは無視できるので
        // 描くかどうかを判定せずここで作っておく（呼び出し側の分岐を増やさない）。
        self.id_bind_group = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Id Params BG"),
            layout:  &self.id_params_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: params_buf.as_entire_binding() },
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
        self.id_field_bind_group = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Id Ripple Field BG"),
            layout:  &self.id_field_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0, resource: wgpu::BindingResource::TextureView(field_view) },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(
                        field_sampler_ref.unwrap_or(fallback_sampler)) },
                wgpu::BindGroupEntry {
                    binding: 2, resource: field_uniform.as_entire_binding() },
            ],
        }));

        debug_assert_eq!(count, (self.quad_count + self.river_count) as usize,
            "パラメータ配列の長さとバケットのインスタンス数が食い違っている");
        true
    }

    // ── 他パスへのリソース公開（Phase W5.3）────────────────────────
    //
    // コースティクス生成パス（`renderer::caustics`）は、水面パスと**同じ高さ場**を
    // 評価するために group1/group2 の中身（水パラメータ配列・ショアフィールド・波紋の場）を
    // 必要とする。ただし **BindGroup オブジェクトそのものは使い回せない**:
    // パイプラインごとに WGSL リフレクションが返す BindGroupLayout が違う（宣言している
    // binding の集合が違う）ため、レイアウト非互換で bind に失敗する。
    // ID パスが既に同じ理由で専用の BindGroup を作っているのと同じ事情である。
    // よって「リソースだけを公開し、BindGroup は各パスが自分のレイアウトで組む」。
    // 公開の流儀は `InteractionFieldRenderer` のアクセサ（`field_view()` 等）に合わせる。

    /// 水パラメータのストレージバッファ（`prepare` が `true` を返したフレームでのみ `Some`）。
    pub fn params_buffer(&self) -> Option<&wgpu::Buffer> {
        self.params_buf.as_ref()
    }

    /// ショアフィールド配列テクスチャのビュー（未確保のフレームは 1×1 のフォールバック）。
    pub fn shore_texture_view(&self) -> &wgpu::TextureView {
        self.shore_view.as_ref().unwrap_or(&self.fallback_shore_view)
    }

    /// ショアフィールド用サンプラー（線形・ClampToEdge）。
    pub fn shore_sampler(&self) -> &wgpu::Sampler {
        &self.shore_sampler
    }

    /// 汎用フォールバックサンプラー（線形・ClampToEdge）。
    /// 波紋の場が未構築のフレームで group2 のサンプラースロットを埋めるために使う。
    pub fn fallback_sampler(&self) -> &wgpu::Sampler {
        &self.sampler
    }

    /// 波紋の場が無いフレーム用の 1×1 ゼロテクスチャのビュー。
    pub fn fallback_field_view(&self) -> &wgpu::TextureView {
        &self.fallback_field_view
    }

    /// 波紋の場が無いフレーム用のゼロ UBO（`inv_extent = 0` ＝ 常に窓外＝波紋ゼロ）。
    pub fn fallback_field_uniform(&self) -> &wgpu::Buffer {
        &self.fallback_field_uniform
    }

    /// このフレームのクアッド（Ocean / Region）インスタンス数。
    ///
    /// パラメータ配列は `[クアッド…, 川…]` の順なので、コースティクスの水域探索は
    /// **先頭からこの個数だけ**を走査すればよい（川リボンは対象外）。
    pub fn quad_count(&self) -> u32 {
        self.quad_count
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

    /// クアッドバケットと川バケットを 2 ドローで描く（両パス共通の手続き）。
    ///
    /// 頂点バッファは無く、1 インスタンスあたりの頂点数は格子の分割数で決まる。
    /// 川はクアッドより桁違いに少ない頂点数なので、**バケットごとに頂点数を変えて**
    /// 描かないと、川のインスタンス数ぶんだけ無駄な頂点シェーダ起動が積み上がる。
    ///
    /// `instance_index` は firstInstance を含む絶対値なので、川バケットの
    /// レンジ `quad_count..` はそのままパラメータ配列の添字として使える。
    fn draw_buckets(&self, pass: &mut wgpu::RenderPass<'_>) {
        if self.quad_count > 0 && self.quad_vertex_count > 0 {
            pass.draw(0..self.quad_vertex_count, 0..self.quad_count);
        }
        if self.river_count > 0 && self.river_vertex_count > 0 {
            pass.draw(
                0..self.river_vertex_count,
                self.quad_count..(self.quad_count + self.river_count),
            );
        }
    }

    /// 水面パス内で全水ボリュームを描く。
    /// `prepare` が `true` を返したフレームでのみ呼ぶこと。
    pub fn draw<'p>(&'p self, pass: &mut wgpu::RenderPass<'p>, camera_bg: &'p wgpu::BindGroup) {
        let Some(bg) = self.frame_bind_group.as_ref() else { return; };
        let Some(field_bg) = self.frame_field_bind_group.as_ref() else { return; };
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, camera_bg, &[]);
        pass.set_bind_group(1, bg, &[]);
        pass.set_bind_group(2, field_bg, &[]);
        self.draw_buckets(pass);
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
        let Some(field_bg) = self.id_field_bind_group.as_ref() else { return; };
        pass.set_pipeline(&self.id_pipeline);
        pass.set_bind_group(0, camera_bg, &[]);
        pass.set_bind_group(1, bg, &[]);
        // 頂点変位（Phase W5.1）で波紋の場も頂点段から読むため、group2 が要る。
        pass.set_bind_group(2, field_bg, &[]);
        self.draw_buckets(pass);
    }
}

/// 2 点間のワールド距離（川リボン 1 区間の実長を求めるための小道具）。
fn distance_xyz(a: [f32; 3], b: [f32; 3]) -> f32 {
    let (dx, dy, dz) = (b[0] - a[0], b[1] - a[1], b[2] - a[2]);
    (dx * dx + dy * dy + dz * dz).sqrt()
}

// ============================================================
//  テスト（WGSL 静的検証）
// ============================================================
#[cfg(test)]
mod tests {
    use super::resolve_water_shader;

    /// TOML の `shader_sources` 順に連結した「実際にコンパイルされる WGSL」を返す。
    ///
    /// Phase W5.1 で形状・高さ場を `water_height_field.wgsl` へ切り出したため、
    /// **1 ファイルだけを検証しても意味が無くなった**（片方だけ見ると宣言が欠けて見える）。
    /// 連結順の正典は TOML なので、テストもそこから組み立てる。
    fn joined(toml_src: &str) -> String {
        let cfg: crate::engine::core::renderer::pipeline_config::PipelineConfig =
            toml::from_str(toml_src).expect("パイプライン TOML が読めない");
        cfg.shader_sources.iter()
            .map(|n| resolve_water_shader(n))
            .collect::<Vec<_>>()
            .join("
")
    }

    /// 水面パスへ実際に渡される WGSL（共有モジュール＋水面本体）。
    fn surface_src() -> String { joined(include_str!("../pipelines/water_surface.toml")) }

    /// ID パスへ実際に渡される WGSL（共有モジュール＋ID 本体）。
    fn id_src() -> String { joined(include_str!("../pipelines/water_id.toml")) }

    /// 水面シェーダ（連結後）を naga で parse + validate する（GPU デバイス不要）。
    #[test]
    fn water_surface_shader_parses_and_validates() {
        let src = surface_src();
        let module = naga::front::wgsl::parse_str(&src)
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
        let wgsl_src = surface_src();
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
        let src = id_src();
        let module = naga::front::wgsl::parse_str(&src)
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
        let wgsl_src = id_src();
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
    /// （同じストレージバッファを読むため、ズレると全パラメータが壊れる）。
    ///
    /// Phase W5.1 で宣言そのものを共有モジュールへ 1 本化したので、両者が
    /// 一致するのは**構造上あたりまえ**になった。それでも残してあるのは
    /// 「片方のシェーダが独自の `WaterParams` を再宣言する」退行を検出するため
    /// （再宣言すると連結時に重複定義でコンパイルが落ちるか、あるいは
    ///   共有モジュールを外して片方だけ独自定義に戻すという形で静かに壊れる）。
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
        let surface = field_names(&surface_src());
        let id      = field_names(&id_src());
        assert_eq!(surface, id,
            "water_surface.wgsl と water_id.wgsl の WaterParams フィールド順が食い違っている");
        // Phase W5.3（水中コースティクス）で `caustics` を足して 17 本になった。
        assert_eq!(surface.len(), 17,
            "Rust 側 WaterParams（vec4 17 本）と本数を揃えること");
    }

    /// WGSL 側 `WaterParams` の **naga 実測サイズ**が Rust 側と一致すること。
    ///
    /// フィールド名の一致（上のテスト）だけでは、型を取り違えた／vec3 を混ぜたといった
    /// 「名前は同じだがオフセットがずれる」退行を検出できない。ストレージバッファは
    /// 配列ストライドがずれた瞬間に**全水域のパラメータが総崩れ**になるため、
    /// バイト数そのものを契約として固定する（Phase W5.3 で `caustics` を足した際に追加）。
    #[test]
    fn wgsl_water_params_size_matches_rust() {
        let src = resolve_water_shader("water_height_field.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .expect("water_height_field.wgsl の parse に失敗");
        let (handle, _) = module.types.iter()
            .find(|(_, t)| t.name.as_deref() == Some("WaterParams"))
            .expect("WGSL に struct WaterParams が見つかりません");
        let mut layouter = naga::proc::Layouter::default();
        layouter.update(module.to_ctx()).expect("naga Layouter の計算に失敗");
        assert_eq!(
            layouter[handle].size as usize,
            std::mem::size_of::<super::WaterParams>(),
            "WGSL の WaterParams サイズが Rust 側と一致しません（配列ストライドが壊れる）",
        );
    }

    /// 川（Phase W4）の描画経路が両シェーダに生きていること。
    ///
    /// リボン生成が落ちても「川が平らなクアッドとして描かれる」だけで
    /// コンパイルは通ってしまうため、文字列で押さえる。
    #[test]
    fn water_shaders_implement_river_ribbon() {
        let surface = surface_src();
        let id      = id_src();
        assert!(surface.contains("fn water_river_vertex("),
            "水面シェーダのリボン頂点生成が消えている");
        assert!(id.contains("fn water_river_vertex("),
            "ID パスのリボン頂点生成が消えている（＝川がクリックできなくなる）");
        // 流れ（2 位相ブレンド）の消費経路
        assert!(surface.contains("fn water_flow_offsets("), "流れの位相オフセットが消えている");
        assert!(surface.contains("water_flow_wave_gradient(p, world_xz, flow_dir, t)"),
            "合成勾配が流れ付きの解析波を使っていない");
        // インスタンス種別のしきい値は両シェーダで一致していること
        assert!(surface.contains("const WATER_INSTANCE_RIVER_MIN: f32 = 0.5;"));
        assert!(id.contains("const WATER_INSTANCE_RIVER_MIN: f32 = 0.5;"));
    }

    /// 解析波のタイル感対策（Phase W6.1）が生きていること。
    ///
    /// 層数・包絡はどちらも「消しても絵は出る（ただし繰り返しが見える）」変更なので、
    /// リファクタで静かに失われないよう契約として固定する。
    #[test]
    fn water_shader_breaks_wave_tiling() {
        let src = surface_src();
        assert!(src.contains("const WAVE_LAYER_COUNT: u32 = 6u;"),
            "解析波の層数が 6 でなくなっている（少ないと繰り返しが目視できる）");
        assert!(src.contains("fn water_wave_envelope("),
            "低周波の空間包絡が消えている（＝波の密度が一様になり反復感が戻る）");
        // 包絡は高さ・勾配の両方へ同じ係数で掛かること（片方だけだと法線と変位が食い違う）。
        assert!(src.contains("grad = grad * water_wave_envelope(q, scale, speed, t);"),
            "勾配へ包絡が掛かっていない");
        assert!(src.contains("return h * water_wave_envelope(q, scale, speed, t);"),
            "高さへ包絡が掛かっていない");
        // 周波数比は無理数（有理数比だと最小公倍数で必ず繰り返す）。
        assert!(src.contains("const WAVE_FREQ_MUL_1: f32 = 1.618034;"),
            "層の周波数比が無理数でなくなっている");
    }

    /// 解析波の**体感の強さ**が旧 4 層から変わっていないこと（Phase W6.1）。
    ///
    /// 層の増減や振幅倍率の手直しは「タイル感は消えたが波が強く／弱くなった」という
    /// 見た目の退行を静かに起こす。位相が互いに独立なので、目に入る法線の傾きは
    /// 各層の寄与 A·f の**二乗和**（RMS）に比例する。シェーダ定数から実測して固定する。
    #[test]
    fn wave_amplitude_normalization_is_preserved() {
        /// WGSL ソースから `const <名前>: f32 = <値>;` を読む。
        fn constant(src: &str, name: &str) -> f32 {
            let needle = format!("const {name}: f32 = ");
            let rest = src.split_once(&needle)
                .unwrap_or_else(|| panic!("定数 {name} が見つからない")).1;
            rest.split_once(';')
                .expect("定数の終端 ; が無い").0
                .trim().parse::<f32>()
                .unwrap_or_else(|e| panic!("定数 {name} が数値として読めない: {e}"))
        }
        let src = surface_src();
        let mut sum_sq = 0.0f32;
        for i in 0..6 {
            let a = constant(&src, &format!("WAVE_AMP_MUL_{i}"));
            let f = constant(&src, &format!("WAVE_FREQ_MUL_{i}"));
            sum_sq += (a * f) * (a * f);
        }
        // 旧 4 層（振幅 1/0.5/0.25/0.125 × 周波数 1/2.13/3.71/5.29）の二乗和。
        const LEGACY_SUM_SQ: f32 = 3.431_73;
        let ratio = sum_sq / LEGACY_SUM_SQ;
        assert!((ratio - 1.0).abs() < 0.02,
            "波の体感強度が旧 4 層から {:.1}% ずれている（二乗和 {sum_sq} / 旧 {LEGACY_SUM_SQ}）",
            (ratio - 1.0) * 100.0);
    }

    /// 波の方位角（Phase W6.3）の回転経路が生きていること。
    #[test]
    fn water_shader_rotates_waves_by_axis() {
        let src = surface_src();
        assert!(src.contains("fn water_wave_rotate_point("),   "サンプル位置の逆回転が消えている");
        assert!(src.contains("fn water_wave_rotate_gradient("), "勾配の戻し回転が消えている");
        assert!(src.contains("fn water_wave_axis("),            "回転軸の取得が消えている");
        // 勾配は必ず戻し回転を通すこと（通し忘れると法線だけ回らずに陰影が破綻する）。
        assert!(src.contains("return water_wave_rotate_gradient(grad, axis);"),
            "解析波の勾配がワールド系へ戻されていない");
    }

    /// 川の流れの向きが**関節タンジェントの補間値**であること（Phase W6.2）。
    ///
    /// 区間の弦へ戻すと、区間境界で流れが跳んでリボンの継ぎ目が見える。
    /// 頂点出力が `flat` になっていないことも併せて押さえる（flat だと区間内で一定になり、
    /// 隣接インスタンスと値が食い違って継ぎ目が復活する）。
    #[test]
    fn water_shader_uses_joint_tangent_for_flow() {
        let src = surface_src();
        assert!(src.contains("@location(2) flow_dir: vec2<f32>,"),
            "流れの向きの varying が無い（または flat になっている）");
        // Phase W5.1 で幅／長さ方向へ格子分割したため、端点の select から
        // **区間内の線形補間（mix）** へ変わった。上流端 s=0・下流端 s=1 で
        // 従来と同じ値になるので、隣接インスタンスが関節で一致する性質は保たれる。
        assert!(src.contains("v.flow_dir = mix(p.river_tangent.xy, p.river_tangent.zw, s);"),
            "頂点が関節タンジェントを補間していない");
        assert!(src.contains("fn water_flow_dir(p: WaterParams, hint: vec2<f32>)"),
            "流れの向きが補間値を受け取らない実装に戻っている");
    }

    /// 水面シェーダが岸波（Phase W1.5）を実装していること。
    ///
    /// リファクタで group1 の配列テクスチャや合成関数が落ちても、静かに
    /// 「岸波が出ない」になるだけで気づけないため、文字列で押さえる。
    #[test]
    fn water_shader_implements_shore_waves() {
        let src = surface_src();
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
        let src = surface_src();
        assert!(src.contains("fn water_surface_height(")
             && src.contains("p: WaterParams, world_xz: vec2<f32>, flow_dir: vec2<f32>, t: f32,"),
            "合成高さ関数（W5.1 が頂点段で呼ぶ）のシグネチャが変わっている");
        assert!(src.contains("fn water_surface_gradient(")
             && src.contains(") -> vec2<f32> {"),
            "合成勾配関数のシグネチャが変わっている");
        // フラグメントは必ず合成関数経由で勾配を得ること（個別ソースの直接加算に戻さない）。
        assert!(src.contains("water_surface_gradient(p, in.world_pos.xz, in.flow_dir, u_camera.time)"),
            "fs_water が合成勾配関数を使っていない");
    }

    /// **水中から見上げた水面**（Phase W7）の合成が実装されていること。
    ///
    /// 3 点（水中判定・法線反転・厚み 0 の素通し・泡の抑制）はどれ 1 つ欠けても
    /// 「水中から外が見えない」「水中で全面が白い泡になる」に戻るため、契約として固定する。
    #[test]
    fn water_shader_handles_underwater_view() {
        let src = surface_src();
        assert!(src.contains("let underwater = u_camera.position.y < in.world_pos.y;"),
            "水中判定（カメラが水面より下か）が消えている");
        assert!(src.contains("let n = select(n_up, -n_up, underwater);"),
            "視線側を向く法線の反転が消えている（フレネルが逆向きに効かなくなる）");
        assert!(src.contains("if (underwater) {") && src.contains("thickness = 0.0;"),
            "水中では厚み 0（素通し）にする分岐が消えている＝外の景色が見えなくなる");
        assert!(src.contains("let foam_gate = select(1.0, 0.0, underwater);"),
            "泡の抑制ゲートが消えている＝水中で岸フォームが全面に出る");
    }

    /// **水面パスは半透明より前**に記録されること（Phase W7）。
    ///
    /// 水面は `blend = Replace`（背景をシェーダ内で合成し alpha=1 を出す）で描くため、
    /// 半透明の後に描くと「水面より手前の半透明」を上書きして消してしまう。
    /// パスの順序はコードの並びだけが根拠なので、ここで文字列の出現順として固定する。
    #[test]
    fn water_pass_is_recorded_before_transparency() {
        let src = include_str!("../../app_base/app/frame_renderer.rs");
        let water = src.find("// ── 水面パス（Phase W7")
            .expect("水面パスの節が見つからない（コメント見出しを変えたら本テストも更新する）");
        // 距離ソート半透明の節は「カメラプレビュー」と「メインパス」の 2 箇所にある。
        // 水面を描くのはメインパスだけなので、**最後の出現**（メインパス側）と比較する。
        let sorted = src.rfind("// ── 半透明の距離ソート描画（Phase R5）")
            .expect("距離ソート半透明の節が見つからない");
        let wboit = src.find("// ── WBOIT 透明描画（Phase R5")
            .expect("WBOIT の節が見つからない");
        assert!(water < sorted,
            "水面パスが距離ソート半透明より後にある（手前の半透明を上書きしてしまう）");
        assert!(water < wboit,
            "水面パスが WBOIT 合成より後にある（手前の半透明を上書きしてしまう）");
    }

    // ─── Phase W5.1（頂点変位の大波）の契約 ────────────────────────

    /// **両パスが同一の頂点生成関数を呼ぶ**こと。
    ///
    /// ここが崩れると「見えている波をクリックできない」（ID パスだけ平面のまま）
    /// という、実行しないと気づけない不具合になる。
    #[test]
    fn both_passes_share_the_same_vertex_generator() {
        let surface = surface_src();
        let id      = id_src();
        // 実体は共有モジュールに 1 つだけ存在すること。
        assert_eq!(surface.matches("fn water_grid_vertex(").count(), 1,
            "頂点生成関数が複数定義されている（＝二重実装が復活している）");
        assert_eq!(id.matches("fn water_grid_vertex(").count(), 1);
        // 両パスの頂点シェーダがそれを呼ぶこと。
        assert!(surface.contains("let v = water_grid_vertex(p, vi, u_camera.time);"),
            "vs_water が共有の頂点生成を呼んでいない");
        assert!(id.contains("let v = water_grid_vertex(p, vi, u_camera.time);"),
            "vs_water_id が共有の頂点生成を呼んでいない（ピック形状が平面に戻る）");
        // 変位は合成高さ場（W1.5 で用意した合流点）を通すこと。
        assert!(surface.contains("water_surface_height(p, v.world.xz, v.flow_dir, t) * gain"),
            "頂点変位が合成高さ場を使っていない（法線と食い違う）");
    }

    /// 頂点段のテクスチャサンプルが成立する構成であること。
    ///
    /// ① 頂点シェーダには暗黙 LOD が無いので、サンプルは必ず `textureSampleLevel`。
    /// ② リフレクションは既定でテクスチャを FRAGMENT 可視にするため、
    ///    TOML で group1/2 を VERTEX 可視へ広げていないとパイプライン生成が落ちる。
    #[test]
    fn vertex_stage_texture_access_is_declared() {
        for toml_src in [
            include_str!("../pipelines/water_surface.toml"),
            include_str!("../pipelines/water_id.toml"),
        ] {
            assert!(toml_src.contains("vertex_visible_groups = [1, 2]"),
                "頂点段から読む group が VERTEX 可視になっていない（パイプライン生成が失敗する）");
            assert!(toml_src.contains("water_height_field.wgsl"),
                "形状・高さ場の共有モジュールが連結されていない");
        }
        let shared = resolve_water_shader("water_height_field.wgsl");
        assert!(!shared.contains("textureSample("),
            "頂点段では暗黙 LOD のサンプル（textureSample）を使えない");
        assert!(shared.contains("textureSampleLevel(t_interaction")
             && shared.contains("textureSampleLevel(t_shore"),
            "波紋・岸波のサンプルが明示 LOD でなくなっている");
    }

    /// 格子の分割定数が Rust 側と WGSL 側で一致していること。
    ///
    /// 片方だけ変えると「格子の一部が描かれない」「近傍だけ極端に細かい」という
    /// 見た目の破綻になるが、コンパイルは通ってしまうため文字列で押さえる。
    #[test]
    fn grid_constants_match_between_rust_and_wgsl() {
        use super::tessellation::*;
        let shared = resolve_water_shader("water_height_field.wgsl");
        assert!(shared.contains(&format!(
            "const WATER_CELL_VERTEX_COUNT: u32 = {WATER_CELL_VERTEX_COUNT}u;")),
            "セルあたり頂点数が食い違っている");
        assert!(shared.contains(&format!(
            "const WATER_GRID_WARP_NEAR: f32 = {WATER_GRID_WARP_NEAR:?};")),
            "放射状ワープの線形係数が食い違っている");
        assert!(shared.contains(&format!(
            "const WATER_GRID_WARP_EXP: f32 = {WATER_GRID_WARP_EXP:?};")),
            "放射状ワープの指数が食い違っている");
        // 格子パラメータは整数演算から作ること（亀裂が出ない根拠。tessellation.rs 参照）。
        assert!(shared.contains("let line = vec2<f32>(f32(ix) + c.x, f32(iz) + c.y);"),
            "格子頂点が「セル添字＋角」の整数演算で作られていない（亀裂の原因になる）");
    }

    /// 遠景では頂点変位が 0 へ落ちること（＝格子が粗い所は従来どおり平面）。
    ///
    /// これが無いと、カメラ追従の巨大セルが波の中を泳いで水面全体がちらつく。
    #[test]
    fn displacement_is_gated_by_grid_resolution() {
        let shared = resolve_water_shader("water_height_field.wgsl");
        assert!(shared.contains("fn water_displacement_gain("),
            "変位ゲートが消えている（遠景の巨大セルがちらつく）");
        assert!(shared.contains("let gain = water_displacement_gain(cell, p.wave.y);"),
            "頂点生成がゲートを通していない");
    }

    /// **GPU 実機でのパイプライン生成**が通ること（`--ignored` で実行する検証用）。
    ///
    /// naga の静的検証（上のテスト群）は WGSL の妥当性しか見ない。**バインドの
    /// ステージ可視性**（`vertex_visible_groups` を入れ忘れると
    /// 「頂点段から見えない binding を使っている」でパイプライン生成が落ちる）は
    /// wgpu が実機のデバイス上でしか検証しないため、ここだけは実 GPU が要る。
    /// CI や GPU 無しの環境で落ちないよう既定では走らせない。
    ///
    /// 実行: `cargo test water::tests::water_pipelines_build_on_gpu -- --ignored --nocapture`
    #[test]
    #[ignore = "実 GPU が必要。--ignored で実行する"]
    fn water_pipelines_build_on_gpu() {
        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor::default());
        let Ok(adapter) = pollster::block_on(instance.request_adapter(
            &wgpu::RequestAdapterOptions::default())) else {
            eprintln!("[water] GPU アダプタが見つからないため検証をスキップ");
            return;
        };
        let (device, _queue) = pollster::block_on(adapter.request_device(
            &wgpu::DeviceDescriptor::default()))
            .expect("デバイス生成に失敗");
        // 生成そのものが検証（失敗すれば wgpu がパニック／エラーを出す）。
        let _renderer = super::WaterRenderer::new(
            &device, wgpu::TextureFormat::Rgba16Float, None);
        let _ = device.poll(wgpu::PollType::Wait);
    }
}
