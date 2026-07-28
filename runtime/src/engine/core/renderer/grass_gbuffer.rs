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
//    group2 = 瞬発インタラクションフィールド（Phase I1）
//      renderer::interaction::create_field_sample_bind_group_layout が定義する共有レイアウト。
//      BindGroup 本体は `InteractionFieldRenderer` が持ち、草は**読むだけ**。
//      場は常に存在する（ソース 0 個でもゼロ埋めテクスチャが返る）ので、
//      描画側は「場が無いフレーム」を分岐で扱わなくてよい。
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

/// 草インスタンス 1 本あたりの storage stride（バイト）。
///
/// 単一 storage バインドに収まる最大本数（`grass_max_instances_for_limit`）の分母。
/// `GrassInstanceGpu` の実サイズを使うので、構造体を変えても自動追従する
/// （テスト `grass_instance_is_48_bytes` が `GRASS_INSTANCE_BYTES` と一致を固定する）。
const GRASS_INSTANCE_STRIDE: usize = std::mem::size_of::<GrassInstanceGpu>();

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

/// リサイズ時に確保する容量倍率（必要本数の何倍を確保するか）。
///
/// 【snatch lock 再帰対策の要】バッファの再確保は旧バッファの drop を伴い、
/// その遅延破棄がフレーム末尾 submit（snatch read lock 保持）中に処理されると
/// write lock を再帰取得してパニックする。ブラシで 1 本ずつ増える度に丁度の
/// サイズで確保し直すと毎ストローク drop が発生してしまうため、余裕を持たせて
/// 確保し、再確保（＝drop）の頻度そのものを構造的に下げる。縮小はしない
/// （`update` は容量が足りていれば絶対に再確保しない）。
const GRASS_CAPACITY_GROWTH_FACTOR: usize = 2;

/// リサイズ時に確保する最小容量（サイズ 0 バッファ生成の回避）。
const GRASS_MIN_CAPACITY: usize = 1;

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
        // group2（インタラクションフィールド）は共有レイアウト。BindGroup を持つ
        // InteractionFieldRenderer 側も同じ関数で作るため、wgpu の BindGroupLayout
        // 構造的等価性でそのままバインドできる（カメラ BGL 借用と同じ既存慣例）。
        let interaction_bgl = super::interaction::create_field_sample_bind_group_layout(
            device, wgpu::ShaderStages::VERTEX,
        );

        // ── シェーダモジュール（velocity_math.wgsl だけを前置連結）──
        //   grass_gbuffer.wgsl は shader_common.wgsl を取り込まず CameraUniform を
        //   自前宣言している（deferred_lighting.wgsl と同じ先例）。
        //   速度バッファ（モーションベクタ）の計算式だけは velocity_math.wgsl と共有する
        //   （**バインディングを持たない純関数だけのファイル**なので、連結しても草の
        //    バインドグループ構成＝group0/1 は 1 ビットも変わらない。前フレームの
        //    インスタンス行列を持つ velocity_common.wgsl は連結しない＝草は静的扱い）。
        let combined: String = GRASS_SHADER_SOURCES.iter()
            .map(|n| get_shader_source(n))
            .collect::<Vec<_>>()
            .join("
");
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some(LABEL),
            source: wgpu::ShaderSource::Wgsl(combined.into()),
        });

        // ── パイプラインレイアウト（group0 = 借用カメラ / group1 = 自前）──
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some(LABEL),
            bind_group_layouts: &[&mesh_pipeline.camera_bgl, &instance_bgl, &interaction_bgl],
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

/// 草パイプラインの連結ソース（naga 検証テストと一致させること）。
/// velocity_math.wgsl はバインディングを持たない純関数のみのファイル。
const GRASS_SHADER_SOURCES: [&str; 2] = ["velocity_math.wgsl", GRASS_SHADER_NAME];

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
//  単一 storage バインド上限のガード（草インスタンス爆発対策）
//
//  【背景】草インスタンスバッファはプロップ種別ごとに 1 本の storage 配列で、
//    バインドグループには `as_entire_binding()`（＝バッファ全域）で渡す。したがって
//    バインド範囲＝バッファ全サイズであり、これが wgpu の
//    `max_storage_buffer_binding_size`（既定 128MB）を超えると、バインドグループ生成
//    時点で検証エラーになり即パニックする。16×16 チャンク（768 チャンク）へ高密度散布
//    すると 1 プロップで約 400 万本に達し、400万 × 48B ≒ 192MB で上限を突破していた。
//
//  【対策】総本数を「単一バインドに収まる最大本数」でクランプする。バッファ容量も
//    この上限で頭打ちにするため、確保するバッファサイズが 128MB を超えることは
//    構造的に起こらない（前回のメッシュレット cmd バッファ防御と同方針）。
//    超過分（＝最大本数を超えた末尾）は描画しない。呼び出し側（rebuild_grass_gpu）は
//    バッファへ詰める前にチャンク順で切り詰めるため、切り捨てられるのは
//    ソート末尾（＝最も座標の大きい）チャンク群である。
// ============================================================

/// 指定バイト上限に、草インスタンスが何本収まるかを返す（最低 1 本）。
///
/// `limit_bytes` は `max_storage_buffer_binding_size`（バイト）を想定する。
/// 0 本になる（＝stride が上限より大きい）ことは現実にはないが、サイズ 0 バッファ生成の
/// パニックを避けるため下限 1 を返す。`const fn` なのでテストで純粋に検証できる。
pub const fn grass_max_instances_for_limit(limit_bytes: u64) -> usize {
    let n = (limit_bytes / GRASS_INSTANCE_STRIDE as u64) as usize;
    if n == 0 { 1 } else { n }
}

/// この device で草インスタンス storage の単一バインドに収まる最大本数。
///
/// `device.limits().max_storage_buffer_binding_size` から求める。バインドは
/// `as_entire_binding()`（バッファ全域）なので、この本数を超えるバッファを作ると
/// バインドグループ生成でパニックする。描画側はこの値で総本数を頭打ちにすること。
pub fn max_grass_instances(device: &wgpu::Device) -> usize {
    grass_max_instances_for_limit(device.limits().max_storage_buffer_binding_size as u64)
}

/// インスタンス配列と span 列を、単一バインド上限 `max` 本以内へ切り詰める。
///
/// `instances` はチャンク座標順（`rebuild_grass_gpu` が詰めた順）に並んでいる前提で、
/// 末尾（＝座標の大きいチャンク）から捨てる。`spans` は各チャンクの連続区間
/// `[first, first+count)` を持ち first 昇順なので、`max` を跨ぐ span は count を詰め、
/// それ以降の span は丸ごと捨てて、span とバッファ内容の整合を保つ
/// （ずれると `draw_grass_culled` が範囲外を描いてしまう）。
///
/// 戻り値は切り捨てた本数（0 なら無切り捨て）。
pub fn clamp_instances_and_spans(
    instances: &mut Vec<GrassInstanceGpu>,
    spans:     &mut Vec<GrassChunkSpan>,
    max:       usize,
) -> usize {
    if instances.len() <= max {
        return 0;
    }
    let dropped = instances.len() - max;
    instances.truncate(max);

    // span を max 以内へ整える。first 昇順なので、first >= max を見つけたら以降は全て範囲外。
    let mut kept: Vec<GrassChunkSpan> = Vec::with_capacity(spans.len());
    for span in spans.iter() {
        let first = span.first as usize;
        if first >= max {
            break;
        }
        let mut s = *span;
        let end = first + span.count as usize;
        if end > max {
            // max を跨ぐ span は描ける範囲だけに詰める。
            s.count = (max - first) as u32;
        }
        kept.push(s);
    }
    *spans = kept;
    dropped
}

/// `GrassInstanceBuffer::new` 用の最終防衛クランプ（device 上限から max を求めて切り詰める）。
fn clamp_new_instances<'a>(
    device:    &wgpu::Device,
    instances: &'a [GrassInstanceGpu],
) -> &'a [GrassInstanceGpu] {
    clamp_update_instances(instances, max_grass_instances(device))
}

/// スライスを `max` 本以内へ切り詰める（超過時のみ警告）。切り詰めが起きるのは、
/// rebuild 側の事前クランプを通らない異常経路だけなので、起きたら警告を出す。
fn clamp_update_instances(instances: &[GrassInstanceGpu], max: usize) -> &[GrassInstanceGpu] {
    if instances.len() > max {
        eprintln!(
            "[SEED grass] 草インスタンス {} 本が単一 storage バインド上限 {} 本を超過。\
             {} 本へクランプして描画します（パニック回避）。",
            instances.len(), max, max
        );
        &instances[..max]
    } else {
        instances
    }
}

// ============================================================
//  GrassInstanceBuffer — 1 プロップ種別ぶんの GPU リソース
// ============================================================

/// 草インスタンスバッファ内の「1 チャンク分の連続レンジ」＋そのワールド AABB。
///
/// チャンク単位フラスタム／距離カリング（Terrain T3 描画最適化）で使う。バッファは
/// チャンク座標順に詰めてあるため、各チャンクのインスタンスは `[first, first+count)`
/// の連続区間に収まる。描画時はこの区間だけを `draw(.., first..first+count)` で
/// 発行することで、可視チャンクぶんだけを描ける（バッファの詰め直しは不要）。
#[derive(Clone, Copy, Debug)]
pub struct GrassChunkSpan {
    /// このチャンクの草を包むワールド AABB 下端（プロップ高さ分のマージン込み）。
    pub aabb_min: [f32; 3],
    /// このチャンクの草を包むワールド AABB 上端（プロップ高さ分のマージン込み）。
    pub aabb_max: [f32; 3],
    /// バッファ先頭からのインスタンス開始添字。
    pub first: u32,
    /// このチャンクのインスタンス本数。
    pub count: u32,
}

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
    /// チャンク単位カリング用のレンジ表（バッファと同じチャンク順・連続区間）。
    ///
    /// 空のときはカリング情報が無い＝`draw_grass`（全描画）にフォールバックする。
    /// `rebuild_grass_gpu` がバッファ更新と同時に `set_spans` で差し替える。
    spans: Vec<GrassChunkSpan>,
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
        // 最終防衛: 単一バインド上限を超える本数はここで切り詰める。通常は呼び出し側
        // （rebuild_grass_gpu）が span と併せて既に切り詰めているので発火しないが、
        // どの経路から作られてもバッファが 128MB を超えてパニックしないための保険。
        let instances = clamp_new_instances(device, instances);
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
            spans: Vec::new(),
        }
    }

    /// チャンク単位カリング用のレンジ表を差し替える。
    ///
    /// `instances`（`update`／`new` に渡した配列）と同じチャンク順で、各チャンクの
    /// 連続区間とワールド AABB を記述する。バッファ本体の更新（`update`）と同じ
    /// タイミングで呼ぶこと（区間添字がバッファ内容とずれると別チャンクの草を描く）。
    pub fn set_spans(&mut self, spans: Vec<GrassChunkSpan>) {
        self.spans = spans;
    }

    /// インスタンス配列とパラメータを更新する。
    ///
    /// 容量が足りていれば `queue.write_buffer` で中身だけ差し替える（バインドグループは
    /// 作り直さない＝毎フレーム呼んでも安い）。容量不足なら**余裕を持たせて**再確保し、
    /// バッファが変わるのでバインドグループも作り直す。
    ///
    /// 【snatch lock 再帰の防止（最重要）】
    ///   再確保は旧バッファの drop を伴う。旧バッファは前フレームの submit が in-flight で
    ///   参照中のため、wgpu は即時破棄せず「遅延破棄キュー」へ積む。この遅延破棄を、
    ///   フレーム末尾の `queue.submit()`（snatch **read** lock 保持）が処理すると、破棄側は
    ///   snatch **write** lock を取りに行き「同一スレッドで snatch lock を再帰取得」して
    ///   パニックする（wgpu-core: resource.rs=破棄=write / global.rs=submit=read）。
    ///   本メソッドはフレーム冒頭（`begin_frame` より前・描画コマンド記録前）に呼ばれ、
    ///   この時点では read lock を誰も保持していない。そこで drop 直後に `poll(Wait)` して、
    ///   read lock 非保持のうちに遅延破棄を確定させる（散布モデル側 `rebuild_scatter_models_gpu`
    ///   の GpuModel／バッチ差し替えと同一の安全手順）。加えて容量に余裕を持たせることで
    ///   再確保（＝drop＝poll）の発生頻度そのものを構造的に下げている。
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

        // 最終防衛: 単一バインド上限を超える本数はここで切り詰める（new と同方針）。
        let max = max_grass_instances(device);
        let instances = clamp_update_instances(instances, max);

        if instances.len() > self.capacity {
            // ── 容量不足 → 余裕を持たせて再確保（バッファが変わるのでバインドグループも再作成）──
            //   丁度のサイズではなく GROWTH_FACTOR 倍を確保し、以後の増加では再確保しない
            //   （＝旧バッファ drop を発生させない）。縮小は一切しない。
            //   【上限クランプ】GROWTH_FACTOR 倍が単一バインド上限（max 本）を超えると、
            //   確保したバッファ全域をバインドした時点でパニックする。容量を max で頭打ち
            //   にして、バッファサイズが 128MB を超えないことを構造的に保証する。
            let new_capacity =
                (instances.len() * GRASS_CAPACITY_GROWTH_FACTOR).clamp(GRASS_MIN_CAPACITY, max);
            let buffer = create_sized_instance_buffer(device, new_capacity);
            // 実データを書き込む（余剰要素はゼロのまま。count で参照されない）。
            queue.write_buffer(&buffer, 0, bytemuck::cast_slice(instances));
            // ↓ この 2 つの代入で旧バインドグループと旧バッファが drop される。
            self.bind_group =
                create_instance_bind_group(device, pipeline, &buffer, &self.uniform_buffer);
            self.buffer   = buffer;
            self.capacity = new_capacity;
            // 旧バッファの後片付けを確定させる（散布モデル rebuild と同一の安全手順）。
            let _ = device.poll(wgpu::PollType::Wait);
        } else if !instances.is_empty() {
            // ── 容量内 → 中身だけ書き換える（drop は一切発生しない）──
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

/// 指定要素数ぶんの容量を持つインスタンス storage バッファを作る（中身は未初期化）。
///
/// `update` のリサイズ経路が「必要本数より多い容量」を確保するために使う
/// （`create_buffer_init` は contents ちょうどのサイズにしかできないため、容量に
/// 余裕を持たせるには `create_buffer` + 後続 `write_buffer` の 2 段構えにする）。
/// 余剰要素はゼロ初期化されないが、`count` が実本数までしか描かないため参照されない。
fn create_sized_instance_buffer(device: &wgpu::Device, capacity: usize) -> wgpu::Buffer {
    let cap = capacity.max(GRASS_MIN_CAPACITY);
    device.create_buffer(&wgpu::BufferDescriptor {
        label:              Some("grass_instances"),
        size:               (cap * std::mem::size_of::<GrassInstanceGpu>()) as wgpu::BufferAddress,
        usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    })
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
/// `field_bg` は瞬発インタラクションフィールドの group2
/// （`InteractionFieldRenderer::sample_bind_group()`）。常に有効な BindGroup を渡すこと。
pub fn draw_grass<'pass>(
    rp:        &mut wgpu::RenderPass<'pass>,
    pipeline:  &'pass GrassGBufferPipeline,
    buf:       &'pass GrassInstanceBuffer,
    camera_bg: &'pass wgpu::BindGroup,
    field_bg:  &'pass wgpu::BindGroup,
) {
    // 0 本のときは何もしない（インスタンス数 0 の draw は無駄なだけでなく、
    // ダミー要素しか無いバッファを触ることになるため明示的に弾く）。
    if buf.count == 0 {
        return;
    }
    rp.set_pipeline(&pipeline.pipe);
    rp.set_bind_group(0, camera_bg, &[]);
    rp.set_bind_group(1, &buf.bind_group, &[]);
    rp.set_bind_group(2, field_bg, &[]);
    rp.draw(0..GRASS_MAX_VERTS_PER_BLADE, 0..buf.count);
}

/// チャンク単位カリング＋遠景密度減衰付きで草を描画する（植生 LOD 第1段）。
///
/// `buf.spans` の各チャンク AABB を視錐台（`planes`）と距離（`cull_distance_sq`）で
/// テストし、可視チャンクだけを描く。さらに可視チャンクごとに最近点距離で
/// **先頭 kept 本だけ**を描く（遠いチャンクほど間引く＝`density_kept_count`）。
/// バッファは `rebuild_grass_gpu` がチャンク内でハッシュ順に並べてあるため、
/// 先頭プレフィクスは空間的に均一なサブセットになり、遠景を薄くしても穴が空かない。
///
/// 連続かつ**全密度**（kept==count）の可視チャンクは 1 回の draw へまとめて発行し、
/// draw コール数を抑える。間引かれたチャンク（kept<count）は `[first, first+kept)`
/// が次チャンク先頭と連続しないため自然に run が閉じ、そのプレフィクスだけが描かれる。
///
/// スパン情報が無い（`spans` が空）場合は従来どおり全描画へフォールバックする
/// （カリングメタデータ未設定でも描画が壊れないための安全弁）。
///
/// - `planes`: `extract_frustum_planes(view_proj)` で得たメインカメラの 6 平面。
/// - `camera_pos`: 距離カリング／密度減衰の基準（ワールド）。
/// - `cull_distance_sq`: これより遠い（最近点距離²）チャンクは描かない。
/// - `decay_near_sq` / `decay_mid_sq`: 密度減衰の帯境界（二乗距離）。
///   `near` 以内は全密度、`mid` 以内は 1/2、それ以遠は 1/4（`cull_distance_sq` まで）。
///
/// 戻り値は**実際に描画したインスタンス本数**（カリング＋密度減衰後）。呼び出し側が
/// `buf.count()`（全本数）と比べて削減量を計測ログへ出すために返す（決定的な数値）。
pub fn draw_grass_culled<'pass>(
    rp:               &mut wgpu::RenderPass<'pass>,
    pipeline:         &'pass GrassGBufferPipeline,
    buf:              &'pass GrassInstanceBuffer,
    camera_bg:        &'pass wgpu::BindGroup,
    // field_bg: 瞬発インタラクションフィールドの group2（常に有効な BindGroup を渡すこと）。
    field_bg:         &'pass wgpu::BindGroup,
    planes:           &[[f32; 4]; 6],
    camera_pos:       [f32; 3],
    cull_distance_sq: f32,
    decay_near_sq:    f32,
    decay_mid_sq:     f32,
) -> u32 {
    if buf.count == 0 {
        return 0;
    }
    // スパン未設定 → 従来の全描画（カリング情報が無いので棄却できない）。
    if buf.spans.is_empty() {
        draw_grass(rp, pipeline, buf, camera_bg, field_bg);
        return buf.count;
    }

    rp.set_pipeline(&pipeline.pipe);
    rp.set_bind_group(0, camera_bg, &[]);
    rp.set_bind_group(1, &buf.bind_group, &[]);
    rp.set_bind_group(2, field_bg, &[]);

    // 可視チャンクの連続区間を貯めて、途切れたところで 1 回 draw する。
    // run_end は「実際に描く上端」＝ `first + kept` を積む（間引きぶんは含めない）。
    let mut run_start: u32 = 0;
    let mut run_end:   u32 = 0; // 半開区間 [run_start, run_end)
    let mut run_open = false;
    // 実際に描いた本数（カリング＋密度減衰後）。計測ログ用に返す。
    let mut drawn: u32 = 0;

    for span in &buf.spans {
        if span.count == 0 {
            continue;
        }
        let dist_sq = super::gpu_resources::aabb_distance_sq(
            span.aabb_min, span.aabb_max, camera_pos,
        );
        let visible = !super::gpu_resources::aabb_outside_frustum(
            planes, span.aabb_min, span.aabb_max,
        ) && dist_sq <= cull_distance_sq;

        // 遠景密度減衰: 可視チャンクは距離に応じて先頭 kept 本だけ描く。
        let kept = if visible {
            super::gpu_resources::density_kept_count(
                span.count, dist_sq, decay_near_sq, decay_mid_sq,
            )
        } else {
            0
        };

        if kept > 0 {
            drawn += kept;
            let draw_end = span.first + kept;
            // 直前の run と連続するのは「直前が全密度で描き切り（run_end==span.first）」の
            // ときだけ。間引かれた直前チャンクは run_end < 次 first になり自然に途切れる。
            if run_open && span.first == run_end {
                run_end = draw_end;
            } else {
                if run_open {
                    rp.draw(0..GRASS_MAX_VERTS_PER_BLADE, run_start..run_end);
                }
                run_start = span.first;
                run_end   = draw_end;
                run_open  = true;
            }
        } else if run_open {
            // 不可視 or 全間引きで区切り → 溜まっていた区間を描いて閉じる。
            rp.draw(0..GRASS_MAX_VERTS_PER_BLADE, run_start..run_end);
            run_open = false;
        }
    }
    // 末尾に残った可視区間を描く。
    if run_open {
        rp.draw(0..GRASS_MAX_VERTS_PER_BLADE, run_start..run_end);
    }
    drawn
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 草シェーダの連結ソース（GRASS_SHADER_SOURCES と同順）。
    fn shader_src() -> String {
        [
            include_str!("shaders/velocity_math.wgsl"),
            include_str!("shaders/grass_gbuffer.wgsl"),
        ].join("
")
    }

    /// 草 G-Buffer WGSL を naga で parse + validate する。
    ///
    /// GPU を回せない環境ではこれが唯一の実効的な検証手段であり、最重要のテスト。
    /// 連結は行わない（grass_gbuffer.wgsl は単体でモジュールになる）。
    #[test]
    fn grass_gbuffer_shader_parses_and_validates() {
        let src = shader_src();
        let module = naga::front::wgsl::parse_str(&src)
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
            include_str!("shaders/grass_gbuffer.wgsl"),
            "pipeline.rs::get_shader_source に grass_gbuffer.wgsl が登録されていない"
        );
        // 速度計算の純関数ファイルも同じリゾルバから引けること。
        assert_eq!(
            get_shader_source("velocity_math.wgsl"),
            include_str!("shaders/velocity_math.wgsl"),
            "pipeline.rs::get_shader_source に velocity_math.wgsl が登録されていない"
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

    /// 128MB 上限から求まる最大本数が「上限 / stride の切り捨て」であること、
    /// かつその本数 × stride が上限を超えないこと（バッファが 128MB を超えない保証）。
    #[test]
    fn grass_max_instances_fits_under_limit() {
        // wgpu 既定の max_storage_buffer_binding_size。
        const LIMIT_128MB: u64 = 128 * 1024 * 1024; // 134217728
        let max = grass_max_instances_for_limit(LIMIT_128MB);
        // 128MB / 48 = 2796202.66… → 切り捨て 2796202。
        assert_eq!(max, 2_796_202, "128MB / 48B の切り捨て本数");
        // 最大本数ぶんのバッファは必ず上限以内（＝バインドでパニックしない）。
        assert!(
            (max as u64) * (GRASS_INSTANCE_STRIDE as u64) <= LIMIT_128MB,
            "max 本 × stride が上限を超えてはならない"
        );
        // 上限が stride 未満でも最低 1 本（サイズ 0 バッファ生成の回避）。
        assert_eq!(grass_max_instances_for_limit(0), 1);
        assert_eq!(grass_max_instances_for_limit(1), 1);
    }

    /// 上限内なら切り詰めが起きず、配列も span もそのままであること。
    #[test]
    fn clamp_is_noop_when_within_limit() {
        let mut inst = vec![GrassInstanceGpu::default(); 10];
        let mut spans = vec![
            GrassChunkSpan { aabb_min: [0.0; 3], aabb_max: [0.0; 3], first: 0, count: 5 },
            GrassChunkSpan { aabb_min: [0.0; 3], aabb_max: [0.0; 3], first: 5, count: 5 },
        ];
        let dropped = clamp_instances_and_spans(&mut inst, &mut spans, 100);
        assert_eq!(dropped, 0);
        assert_eq!(inst.len(), 10);
        assert_eq!(spans.len(), 2);
        assert_eq!(spans[1].count, 5);
    }

    /// 上限超過時: 配列は max へ、span は「跨ぐものは詰め・以降は捨て」で整合すること。
    /// これがずれると draw_grass_culled が範囲外インスタンスを描いてしまう。
    #[test]
    fn clamp_truncates_instances_and_spans_consistently() {
        // 3 チャンク（各 4 本）＝12 本を、max=7 へ切り詰める。
        let mut inst = vec![GrassInstanceGpu::default(); 12];
        let mut spans = vec![
            GrassChunkSpan { aabb_min: [0.0; 3], aabb_max: [0.0; 3], first: 0, count: 4 },
            GrassChunkSpan { aabb_min: [0.0; 3], aabb_max: [0.0; 3], first: 4, count: 4 }, // 4..8 が 7 を跨ぐ
            GrassChunkSpan { aabb_min: [0.0; 3], aabb_max: [0.0; 3], first: 8, count: 4 }, // 全範囲外
        ];
        let dropped = clamp_instances_and_spans(&mut inst, &mut spans, 7);
        assert_eq!(dropped, 5, "12 - 7 = 5 本を捨てる");
        assert_eq!(inst.len(), 7, "配列は max=7 本へ");
        assert_eq!(spans.len(), 2, "全範囲外の 3 つ目 span は消える");
        assert_eq!(spans[0].count, 4, "1 つ目はそのまま");
        assert_eq!(spans[1].first, 4);
        assert_eq!(spans[1].count, 3, "跨ぐ span は 4..7 の 3 本へ詰める");
        // どの span も max を超えない（描画範囲がバッファ容量内）。
        for s in &spans {
            assert!((s.first + s.count) as usize <= 7);
        }
    }
}
