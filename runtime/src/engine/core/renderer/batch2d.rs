// ============================================================
//  batch2d.rs — 2D クワッド系の汎用インスタンシングバッチャ（Phase R6）
//
//  「同一パイプライン＋同一テクスチャ＋連続描画順」のスプライトを 1 ドローコールへ
//  束ねる軽量ヘルパー。旧実装（sprite_drawer の prepare_sprites_from_mats/draw_sprites）は
//  スプライト 1 枚ごとに uniform buffer と BindGroup を毎フレーム新規生成し、
//  draw(0..6, 0..1) をループしていた（UI ヘビー時に致命的な CPU/割当コスト）。
//
//  本モジュールは:
//   - クアッド 1 枚（SpritePipeline.unit_quad_vbuf, 6 頂点）を全スプライトで共有し、
//     モデル行列・カラーを per-instance の頂点属性（永続インスタンスバッファ）で供給する。
//   - インスタンスバッファは永続化し毎フレーム write_buffer で書き込む
//     （容量不足時のみ倍々成長で再確保）。BindGroup の毎フレーム生成は撤廃。
//   - 描画順（レイヤーソート済み）は一切変更せず、テクスチャが切り替わる境界だけで
//     バッチを区切る（＝見た目不変が最優先）。
//
//  【形と塗り（W2-4）】SpriteComponent の形と塗りの欄（角丸・楕円・弧・縁・グラデーション・9 スライス・影）を
//  使うスプライトと、角丸・楕円の切り抜きの中のスプライトは、別のパイプライン（sprite_shape.wgsl）で描く。
//  そのアイテムは形のインスタンス（ShapeInstance）とパラメータ（ストレージバッファ）へ積み、バッチの種類を分ける
//  （`SpriteBatch::shape`）。**欄がすべて既定で形の切り抜きも受けないアイテムは従来とまったく同じ経路**
//  （同じ SpriteInstance・同じバッチ・同じ描画コマンド列）で描く＝既定の見た目は画素単位で変わらない。
//  9 スライスは 1 枚の四角形のままシェーダーで UV を写す（ui_shape/nine_slice.rs）。
//
//  TODO(将来課題): テクスチャ配列／アトラスで異なるテクスチャも 1 バッチへ統合すれば
//  ドローコールをさらに削減できる（現行のテクスチャ管理を大改造しないため未実施）。
// ============================================================

use crate::engine::components::CanvasDrawZone;
use crate::engine::core::renderer::pipeline::{SpriteOutlinePipeline, SpritePipeline};
use crate::engine::core::renderer::sprite_skin::SkinnedSpriteDraw;
use crate::engine::core::renderer::ui_clip::UiClipId;
use crate::engine::core::renderer::ui_shape::{
    build_shape_instances, ClipSdf, ShapeInstance, ShapeParamsGpu, SpriteStyleDraw, SHAPE_INSTANCE_SIZE,
    SHAPE_PARAMS_SIZE,
};
use crate::engine::methods::drawer::GpuSpriteTexture;
use std::sync::Arc;

// ============================================================
//  SpriteDrawItem — 収集フェーズが積む 1 描画アイテム
// ============================================================

/// 2D 描画アイテム 1 件（矩形スプライトとスキンメッシュの共通表現）。
///
/// `mesh` が `None` ならユニットクワッド（従来の SpriteComponent）、
/// `Some` なら `.sprite_mesh` の変形済み頂点＋インデックスで描く。
/// **両者は同じリストに並んで同じ規則（ゾーン → レイヤー）でソートされる**ので、
/// 矩形スプライトとスキンスプライトの前後関係が正しく解決される。
pub struct SpriteDrawItem {
    /// GPU モデル行列（列優先）。
    pub model: [[f32; 4]; 4],
    /// RGBA カラー乗算。
    pub color: [f32; 4],
    /// テクスチャ。None = 白フォールバック。
    pub tex: Option<Arc<GpuSpriteTexture>>,
    /// スキンメッシュ描画のハンドル。None = ユニットクワッド描画。
    pub mesh: Option<Arc<SkinnedSpriteDraw>>,
    /// 描画ゾーン（背景／前面）。
    pub zone: CanvasDrawZone,
    /// 描画優先度レイヤー（大きいほど手前）。
    pub layer: i32,
    /// 切り抜きの領域の番号（renderer/ui_clip.rs の表の添字。None = 切り抜かない）。
    /// 2D キャンバスの収集だけが付ける（W2-0 の試作。それ以外の経路は常に None）。
    pub clip: Option<crate::engine::core::renderer::ui_clip::UiClipId>,
    /// 形と塗り（W2-4。欄がすべて既定のスプライト・スキンメッシュ・インライン画像・アウトラインは None）。
    /// Some のアイテムは形と塗りのパイプラインで描く。
    pub style: Option<Box<SpriteStyleDraw>>,
}

// ============================================================
//  per-instance データ
// ============================================================

/// スプライト 1 インスタンス分の GPU データ（80 bytes）。
///
/// sprite.wgsl / sprite_outline.wgsl の per-instance 頂点属性
/// location 2〜5（model の 4 列）＋ location 6（color）に 1:1 対応する。
/// WGSL mat4x4<f32> は列優先のため、model は [[col0], [col1], [col2], [col3]] で格納する。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct SpriteInstance {
    /// モデル行列（列優先 4×4 = 64 bytes）
    pub model: [[f32; 4]; 4],
    /// RGBA カラー乗算（16 bytes）
    pub color: [f32; 4],
}

/// SpriteInstance 1 件の GPU バイト数。
/// pipeline_config.rs の "sprite_instance" 頂点レイアウト array_stride と一致必須。
pub const SPRITE_INSTANCE_SIZE: u64 = std::mem::size_of::<SpriteInstance>() as u64;

/// インスタンスストリームの初期容量（インスタンス数）。
const INITIAL_INSTANCE_CAPACITY: u32 = 256;
/// 形のインスタンス・パラメータの初期容量（要素数。形と塗りを使う UI は少ないので小さく始める）。
const INITIAL_SHAPE_CAPACITY: u32 = 16;
/// 容量不足時の成長率（倍々）。
const INSTANCE_GROWTH_FACTOR: u32 = 2;

// ============================================================
//  バッチ（テクスチャ境界で区切った 1 ドローコール分）
// ============================================================

/// テクスチャ境界で区切られた 1 バッチ（= 1 ドローコール）。
pub struct SpriteBatch {
    /// このバッチのテクスチャ。None は白フォールバック（アウトラインは常に None）。
    pub tex: Option<Arc<GpuSpriteTexture>>,
    /// 所属インスタンスバッファ先頭からのインスタンス番号。
    pub base: u32,
    /// インスタンス数。
    pub count: u32,
    /// スキンメッシュ描画のハンドル。
    /// `Some` のバッチは常に count=1（メッシュごとに頂点／インデックスが違うため
    /// 融合できない）。`None` のバッチだけがユニットクワッドで融合される。
    pub mesh: Option<Arc<SkinnedSpriteDraw>>,
    /// 形と塗りのバッチか（W2-4）。true なら `base`・`count` は形のインスタンスの番号で、
    /// 形と塗りのパイプラインで描く。
    pub shape: bool,
}

/// 1 回の push で得られるバッチ列。
/// 描画順は入力（ソート済み）順を厳密に保ち、連続同一テクスチャのみ融合する。
pub struct SpriteBatchList {
    pub batches: Vec<SpriteBatch>,
}

impl SpriteBatchList {
    /// 空（＝描画するバッチが無い）か。
    pub fn is_empty(&self) -> bool {
        self.batches.is_empty()
    }
}

/// 2 つのテクスチャオプションが同一バッチに属せるか（両 None または同一 Arc）。
fn same_tex(a: &Option<Arc<GpuSpriteTexture>>, b: &Option<Arc<GpuSpriteTexture>>) -> bool {
    match (a, b) {
        (None, None) => true,
        (Some(x), Some(y)) => Arc::ptr_eq(x, y),
        _ => false,
    }
}

// ============================================================
//  SpriteScratch — 1 フレーム分の CPU の積み上げ（GPU に触れない。単体テストの対象）
// ============================================================

/// 容量の成長（倍々。最低 1 から）【純関数】。
fn grown_capacity(current: u32, needed: u32) -> u32 {
    let mut cap = current.max(1);
    while cap < needed {
        cap = cap.saturating_mul(INSTANCE_GROWTH_FACTOR);
    }
    cap
}

/// 1 フレーム分の CPU の積み上げ（従来のクワッド・形のインスタンス・形のパラメータ）。
///
/// `push_items` がアイテムを経路ごとに振り分けてバッチ列を返す。GPU への書き込みは `InstanceStream::upload`。
#[derive(Default)]
pub struct SpriteScratch {
    /// 従来のスプライト（ユニットクワッド・スキンメッシュ）のインスタンス。
    pub quads: Vec<SpriteInstance>,
    /// 形と塗りのインスタンス。
    pub shapes: Vec<ShapeInstance>,
    /// 形と塗りのパラメータ（ストレージバッファの中身）。
    pub params: Vec<ShapeParamsGpu>,
    /// 1 アイテム分の形のインスタンスの作業場所（影 → 本体）。
    work: Vec<([[f32; 4]; 4], ShapeParamsGpu)>,
}

impl SpriteScratch {
    /// フレーム開始: 積み上げを空にする（確保した容量は使い回す）。
    pub fn clear(&mut self) {
        self.quads.clear();
        self.shapes.clear();
        self.params.clear();
    }

    /// アイテムを積み、テクスチャ・経路の境界で区切ったバッチ列を返す。
    ///
    /// 入力順は一切変えない。次の 2 つだけが形と塗りの経路に入る（残りは従来とまったく同じ経路）:
    ///   - 形と塗りを持つアイテム（`style` が Some）
    ///   - スキンメッシュでなく、`clip_of` が形のある切り抜き（角丸・楕円）を返すアイテム
    ///
    /// # 引数
    /// * `items`   - 描画順のアイテム
    /// * `clip_of` - 切り抜きの番号 → いちばん内側の形のある切り抜き（無ければ None）
    pub fn push_items<I>(
        &mut self,
        items: I,
        clip_of: &dyn Fn(Option<UiClipId>) -> Option<ClipSdf>,
    ) -> SpriteBatchList
    where
        I: IntoIterator<Item = SpriteDrawItem>,
    {
        let mut batches: Vec<SpriteBatch> = Vec::new();
        for item in items {
            // スキンメッシュは形の切り抜きを受けない（外接矩形の scissor だけ）
            let clip_sdf = if item.mesh.is_none() { clip_of(item.clip) } else { None };
            if item.style.is_some() || clip_sdf.is_some() {
                self.push_shape_item(&item, clip_sdf.as_ref(), &mut batches);
                continue;
            }
            // ── 従来の経路（W2-4 以前とまったく同じ）──
            let idx = self.quads.len() as u32;
            self.quads.push(SpriteInstance {
                model: item.model,
                color: item.color,
            });
            // 融合条件: 直前バッチが「クワッド描画」かつ同一テクスチャのときだけ。
            // スキンメッシュは頂点／インデックスバッファがバッチごとに違うため
            // 常に単独バッチ（count=1）になる。
            let mergeable = item.mesh.is_none();
            match batches.last_mut() {
                Some(b) if mergeable && !b.shape && b.mesh.is_none() && same_tex(&b.tex, &item.tex) => {
                    b.count += 1
                }
                _ => batches.push(SpriteBatch {
                    tex: item.tex,
                    base: idx,
                    count: 1,
                    mesh: item.mesh,
                    shape: false,
                }),
            }
        }
        SpriteBatchList { batches }
    }

    /// 形と塗りのアイテム 1 つを積む（影 → 本体の最大 2 インスタンス。同じテクスチャなら直前の形のバッチへ融合）。
    fn push_shape_item(&mut self, item: &SpriteDrawItem, clip: Option<&ClipSdf>, batches: &mut Vec<SpriteBatch>) {
        let tex_size = item.tex.as_ref().map(|t| [t.width, t.height]);
        self.work.clear();
        build_shape_instances(&item.model, item.color, item.style.as_deref(), tex_size, clip, &mut self.work);
        for (model, params) in self.work.drain(..) {
            let params_index = self.params.len() as u32;
            self.params.push(params);
            let idx = self.shapes.len() as u32;
            self.shapes.push(ShapeInstance { model, params: params_index, _pad: [0; 3] });
            match batches.last_mut() {
                Some(b) if b.shape && same_tex(&b.tex, &item.tex) => b.count += 1,
                _ => batches.push(SpriteBatch {
                    tex: item.tex.clone(),
                    base: idx,
                    count: 1,
                    mesh: None,
                    shape: true,
                }),
            }
        }
    }
}

// ============================================================
//  InstanceStream — 成長する永続インスタンスバッファ（1 チャンネル）
// ============================================================

/// 描画に使う 1 チャンネルの GPU バッファの組（レンダーパスの記録の前に clone して 'rp で渡す）。
#[derive(Clone)]
pub struct SpriteStreamGpu {
    /// 従来のスプライトのインスタンスバッファ。
    pub quad: Arc<wgpu::Buffer>,
    /// 形と塗りのインスタンスバッファ。
    pub shape: Arc<wgpu::Buffer>,
    /// 形と塗りのパラメータ（group 2）のバインドグループ。
    pub params_bind_group: Arc<wgpu::BindGroup>,
}

/// 1 チャンネル分の永続インスタンスバッファ。
///
/// 1 フレームの使い方: `begin()` → `push()` を必要回 → `upload()` → `buffer()`。
/// `push()` は CPU 側 scratch へ積み、`upload()` で 1 度だけ write_buffer する
/// （容量不足時のみ倍々成長で再確保）。
pub struct InstanceStream {
    buffer: Arc<wgpu::Buffer>,
    /// 収容可能インスタンス数。
    capacity: u32,
    /// 形と塗りのインスタンスバッファ（W2-4）。
    shape_buffer: Arc<wgpu::Buffer>,
    /// 形のインスタンスバッファの収容数。
    shape_capacity: u32,
    /// 形と塗りのパラメータのストレージバッファ（W2-4）。
    params_buffer: Arc<wgpu::Buffer>,
    /// パラメータの収容数。
    params_capacity: u32,
    /// パラメータのバインドグループのレイアウト（形と塗りのパイプラインの group 2 と同じもの）。
    params_bgl: wgpu::BindGroupLayout,
    /// パラメータのバインドグループ（パラメータのバッファを作り直したら作り直す）。
    params_bind_group: Arc<wgpu::BindGroup>,
    /// このフレーム分の CPU 積み上げ。
    scratch: SpriteScratch,
    label: &'static str,
}

impl InstanceStream {
    /// 初期容量でバッファを確保する。
    ///
    /// # 引数
    /// * `params_bgl` - 形と塗りのパイプラインの group 2 のレイアウト（`SpriteShapePipeline::params_bgl`）
    fn new(device: &wgpu::Device, label: &'static str, params_bgl: &wgpu::BindGroupLayout) -> Self {
        let buffer = Arc::new(Self::alloc(
            device,
            INITIAL_INSTANCE_CAPACITY,
            SPRITE_INSTANCE_SIZE,
            label,
            wgpu::BufferUsages::VERTEX,
        ));
        let shape_buffer = Arc::new(Self::alloc(
            device,
            INITIAL_SHAPE_CAPACITY,
            SHAPE_INSTANCE_SIZE,
            label,
            wgpu::BufferUsages::VERTEX,
        ));
        let params_buffer = Arc::new(Self::alloc(
            device,
            INITIAL_SHAPE_CAPACITY,
            SHAPE_PARAMS_SIZE,
            label,
            wgpu::BufferUsages::STORAGE,
        ));
        let params_bind_group = Arc::new(Self::params_bind_group(device, params_bgl, &params_buffer, label));
        Self {
            buffer,
            capacity: INITIAL_INSTANCE_CAPACITY,
            shape_buffer,
            shape_capacity: INITIAL_SHAPE_CAPACITY,
            params_buffer,
            params_capacity: INITIAL_SHAPE_CAPACITY,
            params_bgl: params_bgl.clone(),
            params_bind_group,
            scratch: SpriteScratch::default(),
            label,
        }
    }

    /// 指定容量の `usage`|COPY_DST バッファを確保する。
    fn alloc(
        device: &wgpu::Device,
        capacity: u32,
        stride: u64,
        label: &'static str,
        usage: wgpu::BufferUsages,
    ) -> wgpu::Buffer {
        device.create_buffer(&wgpu::BufferDescriptor {
            label: Some(label),
            size: capacity as u64 * stride,
            usage: usage | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        })
    }

    /// パラメータのバインドグループを作る。
    fn params_bind_group(
        device: &wgpu::Device,
        layout: &wgpu::BindGroupLayout,
        buffer: &wgpu::Buffer,
        label: &'static str,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some(label),
            layout,
            entries: &[wgpu::BindGroupEntry { binding: 0, resource: buffer.as_entire_binding() }],
        })
    }

    /// フレーム開始: 積み上げをクリアする（バッファ本体は再利用）。
    pub fn begin(&mut self) {
        self.scratch.clear();
    }

    /// items（model, color, tex）を追記し、テクスチャ境界で区切ったバッチ列を返す（切り抜きの形は見ない）。
    ///
    /// 入力順は一切変更しない（連続する同一テクスチャのみ 1 バッチへ融合する）。
    /// 返される base は「このストリームのバッファ先頭からの」絶対インスタンス番号のため、
    /// 同一フレーム内で push を複数回呼んでも整合する（scratch は連続配置）。
    pub fn push<I>(&mut self, items: I) -> SpriteBatchList
    where
        I: IntoIterator<Item = SpriteDrawItem>,
    {
        self.scratch.push_items(items, &|_| None)
    }

    /// `push` と同じだが、角丸・楕円の切り抜き（`clip_of`）の中のアイテムを形と塗りの経路へ回す（W2-4）。
    pub fn push_clipped<I>(
        &mut self,
        items: I,
        clip_of: &dyn Fn(Option<UiClipId>) -> Option<ClipSdf>,
    ) -> SpriteBatchList
    where
        I: IntoIterator<Item = SpriteDrawItem>,
    {
        self.scratch.push_items(items, clip_of)
    }

    /// scratch を GPU バッファへ書き込む。容量不足なら倍々成長で再確保する。
    pub fn upload(&mut self, device: &wgpu::Device, queue: &wgpu::Queue) {
        // ── 従来のスプライト ──
        let needed = self.scratch.quads.len() as u32;
        if needed > 0 {
            if needed > self.capacity {
                let cap = grown_capacity(self.capacity, needed);
                self.buffer = Arc::new(Self::alloc(device, cap, SPRITE_INSTANCE_SIZE, self.label, wgpu::BufferUsages::VERTEX));
                self.capacity = cap;
            }
            queue.write_buffer(&self.buffer, 0, bytemuck::cast_slice(&self.scratch.quads));
        }
        // ── 形と塗り（使うフレームだけ書く）──
        let shapes = self.scratch.shapes.len() as u32;
        if shapes > 0 {
            if shapes > self.shape_capacity {
                let cap = grown_capacity(self.shape_capacity, shapes);
                self.shape_buffer =
                    Arc::new(Self::alloc(device, cap, SHAPE_INSTANCE_SIZE, self.label, wgpu::BufferUsages::VERTEX));
                self.shape_capacity = cap;
            }
            queue.write_buffer(&self.shape_buffer, 0, bytemuck::cast_slice(&self.scratch.shapes));
        }
        let params = self.scratch.params.len() as u32;
        if params > 0 {
            if params > self.params_capacity {
                let cap = grown_capacity(self.params_capacity, params);
                self.params_buffer =
                    Arc::new(Self::alloc(device, cap, SHAPE_PARAMS_SIZE, self.label, wgpu::BufferUsages::STORAGE));
                self.params_capacity = cap;
                self.params_bind_group =
                    Arc::new(Self::params_bind_group(device, &self.params_bgl, &self.params_buffer, self.label));
            }
            queue.write_buffer(&self.params_buffer, 0, bytemuck::cast_slice(&self.scratch.params));
        }
    }

    /// 現フレームで使う GPU バッファの組への共有ハンドル。
    /// `upload()` 後に呼ぶこと（再確保後の最新バッファを返す）。
    /// レンダーパス記録前にローカルへ clone しておけば、パスの 'rp ライフタイムで
    /// `&handle` を渡せる（RefCell の Ref を跨いで保持する必要がない）。
    pub fn buffer(&self) -> SpriteStreamGpu {
        SpriteStreamGpu {
            quad: self.buffer.clone(),
            shape: self.shape_buffer.clone(),
            params_bind_group: self.params_bind_group.clone(),
        }
    }
}

// ============================================================
//  SpriteBatcher — DrawContext が保持する 2 チャンネルのバッチャ
// ============================================================

/// スプライトバッチャ本体（DrawContext が RefCell で保持し、フレーム内で内部可変）。
///
/// 独立した 2 ストリームを持つ:
///   - `main`:    メインパス／キャンバスオーバーレイパスの全スプライト＋選択アウトライン。
///   - `preview`: カメラプレビューパスのスプライト。
///
/// 分離する理由: プレビューパスはメインのスプライト収集より前に記録される。
/// 単一バッファを共有すると、メイン収集時の容量不足による再確保が、
/// 記録済みプレビューコマンドの参照バッファを無効化してしまう。
/// チャンネルを分ければ各ストリームは自分のパス記録前に upload/確定でき、この危険が無い。
pub struct SpriteBatcher {
    pub main: InstanceStream,
    pub preview: InstanceStream,
}

impl SpriteBatcher {
    /// 2 ストリームを初期容量で確保する。
    ///
    /// # 引数
    /// * `params_bgl` - 形と塗りのパイプラインの group 2 のレイアウト（W2-4）
    pub fn new(device: &wgpu::Device, params_bgl: &wgpu::BindGroupLayout) -> Self {
        Self {
            main: InstanceStream::new(device, "Sprite Instance Buf (main)", params_bgl),
            preview: InstanceStream::new(device, "Sprite Instance Buf (preview)", params_bgl),
        }
    }
}

// ============================================================
//  描画関数
// ============================================================

/// テクスチャ付きスプライトバッチを描画する（同一テクスチャの連続 = 1 ドローコール）。
///
/// - `camera_bg`: group 0（mesh パイプラインと同一レイアウト）
/// - `gpu`:       当該ストリームの GPU バッファの組（`InstanceStream::buffer()`）
/// - `list`:      当該描画順の `SpriteBatchList`
///
/// 形と塗りのバッチ（W2-4）は形と塗りのパイプラインへ切り替えて描く。形と塗りのバッチが無い列は、
/// 従来とまったく同じ描画コマンド列になる（パイプライン → group 0 → slot0 → バッチごとの slot1・group 1・draw）。
pub fn draw_sprite_batches<'rp>(
    pass: &mut wgpu::RenderPass<'rp>,
    pipeline: &'rp SpritePipeline,
    camera_bg: &'rp wgpu::BindGroup,
    gpu: &'rp SpriteStreamGpu,
    list: &'rp SpriteBatchList,
) {
    if list.batches.is_empty() {
        return;
    }

    // 今張っているパイプライン（None = まだ。Some(true) = 形と塗り）
    let mut current_shape: Option<bool> = None;
    // 直前のバッチがメッシュだったか（＝ slot0 をクワッドへ戻す必要があるか）。
    let mut slot0_is_mesh = false;

    for b in &list.batches {
        // group 1: テクスチャ（未設定なら白フォールバック）
        let tex_bg = b
            .tex
            .as_ref()
            .map(|t| &t.bind_group)
            .unwrap_or(&pipeline.white_fallback_bg);

        if b.shape {
            // ── 形と塗り ──
            if current_shape != Some(true) {
                pass.set_pipeline(&pipeline.shape.pipeline);
                pass.set_bind_group(0, camera_bg, &[]);
                pass.set_bind_group(2, &*gpu.params_bind_group, &[]);
                pass.set_vertex_buffer(0, pipeline.unit_quad_vbuf.slice(..));
                slot0_is_mesh = false;
                current_shape = Some(true);
            }
            let start = b.base as u64 * SHAPE_INSTANCE_SIZE;
            let end = start + b.count as u64 * SHAPE_INSTANCE_SIZE;
            pass.set_vertex_buffer(1, gpu.shape.slice(start..end));
            pass.set_bind_group(1, tex_bg, &[]);
            pass.draw(0..6, 0..b.count);
            continue;
        }

        // ── 従来のスプライト ──
        if current_shape != Some(false) {
            pass.set_pipeline(&pipeline.pipeline);
            pass.set_bind_group(0, camera_bg, &[]);
            // slot0 の既定はユニットクワッド。スキンメッシュのバッチだけ差し替える。
            pass.set_vertex_buffer(0, pipeline.unit_quad_vbuf.slice(..));
            slot0_is_mesh = false;
            current_shape = Some(false);
        }
        // slot1: このバッチのインスタンス範囲だけをスライスして束ねる
        let start = b.base as u64 * SPRITE_INSTANCE_SIZE;
        let end = start + b.count as u64 * SPRITE_INSTANCE_SIZE;
        pass.set_vertex_buffer(1, gpu.quad.slice(start..end));
        pass.set_bind_group(1, tex_bg, &[]);

        match &b.mesh {
            // スキンメッシュ: 変形済み頂点（sprite_vertex レイアウト）＋インデックス描画
            Some(m) => {
                pass.set_vertex_buffer(0, m.vertex_buffer.slice(..));
                pass.set_index_buffer(m.index_buffer.slice(..), wgpu::IndexFormat::Uint32);
                pass.draw_indexed(0..m.index_count, 0, 0..b.count);
                slot0_is_mesh = true;
            }
            // 従来のユニットクワッド 6 頂点 × count インスタンス
            None => {
                if slot0_is_mesh {
                    pass.set_vertex_buffer(0, pipeline.unit_quad_vbuf.slice(..));
                    slot0_is_mesh = false;
                }
                pass.draw(0..6, 0..b.count);
            }
        }
    }
}

/// 選択スプライトのアウトライン（単色, テクスチャなし）バッチを描画する。
///
/// `sprite_outline.wgsl` がクリップ空間でコーナーを押し出すため、モデル行列は
/// 実スプライトと同じ。全インスタンスが tex=None のため通常 1 バッチに融合される。
/// ユニットクワッド頂点バッファは `sprite_pipeline` から供給する。
pub fn draw_sprite_outline_batches<'rp>(
    pass: &mut wgpu::RenderPass<'rp>,
    sprite_pipeline: &'rp SpritePipeline,
    outline_pipeline: &'rp SpriteOutlinePipeline,
    camera_bg: &'rp wgpu::BindGroup,
    inst_buf: &'rp wgpu::Buffer,
    list: &'rp SpriteBatchList,
) {
    if list.batches.is_empty() {
        return;
    }

    pass.set_pipeline(&outline_pipeline.pipeline);
    pass.set_vertex_buffer(0, sprite_pipeline.unit_quad_vbuf.slice(..));
    pass.set_bind_group(0, camera_bg, &[]);

    for b in &list.batches {
        let start = b.base as u64 * SPRITE_INSTANCE_SIZE;
        let end = start + b.count as u64 * SPRITE_INSTANCE_SIZE;
        pass.set_vertex_buffer(1, inst_buf.slice(start..end));
        // テクスチャ不要（group 1 なし、単色塗りつぶし）
        pass.draw(0..6, 0..b.count);
    }
}

// ============================================================
//  WGSL 静的検証（naga parse + validate）
// ============================================================
//
// スプライト／アウトラインの連結 WGSL を cargo test で parse+validate する。
// per-instance 頂点属性化（Phase R6）でシェーダを改修したため回帰防止に置く。
#[cfg(test)]
mod tests {
    #[test]
    fn sprite_shaders_parse_and_validate() {
        let sprite = include_str!("shaders/sprite.wgsl");
        let outline = include_str!("shaders/sprite_outline.wgsl");
        let shape = include_str!("shaders/sprite_shape.wgsl");

        for (name, src) in [("sprite", sprite), ("sprite_outline", outline), ("sprite_shape", shape)] {
            let module = naga::front::wgsl::parse_str(src)
                .unwrap_or_else(|e| panic!("[{name}] WGSL parse 失敗: {e:?}"));
            let mut validator = naga::valid::Validator::new(
                naga::valid::ValidationFlags::all(),
                naga::valid::Capabilities::empty(),
            );
            validator
                .validate(&module)
                .unwrap_or_else(|e| panic!("[{name}] WGSL validate 失敗: {e:?}"));
        }
    }

    /// per-instance 頂点レイアウト（sprite_instance）の array_stride と
    /// SpriteInstance の Rust 側サイズが一致することを保証する。
    #[test]
    fn sprite_instance_size_matches_layout() {
        assert_eq!(super::SPRITE_INSTANCE_SIZE, 80);
    }

    use super::*;
    use crate::engine::components::{SpriteComponent, SpriteShape};
    use crate::engine::core::renderer::ui_shape::{ClipSdf, UiClipShape};

    /// テスト用のアイテム（テクスチャなし・ユニットクワッド）。
    fn item(x: f32, style: Option<Box<SpriteStyleDraw>>, clip: Option<UiClipId>) -> SpriteDrawItem {
        SpriteDrawItem {
            model: [[10.0, 0.0, 0.0, 0.0], [0.0, 20.0, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0], [x, 5.0, 0.0, 1.0]],
            color: [0.1, 0.2, 0.3, 0.4],
            tex: None,
            mesh: None,
            zone: CanvasDrawZone::Foreground,
            layer: 0,
            clip,
            style,
        }
    }

    /// 既定の欄のスプライト（形と塗りなし・形の切り抜きなし）は従来と同じ頂点（SpriteInstance）・同じバッチになる。
    #[test]
    fn plain_items_take_the_legacy_path() {
        let mut scratch = SpriteScratch::default();
        let list = scratch.push_items(vec![item(1.0, None, None), item(2.0, None, Some(0))], &|_| None);
        assert!(scratch.shapes.is_empty() && scratch.params.is_empty());
        assert_eq!(scratch.quads.len(), 2);
        assert_eq!(scratch.quads[0].model, item(1.0, None, None).model);
        assert_eq!(scratch.quads[0].color, [0.1, 0.2, 0.3, 0.4]);
        // 同じテクスチャの連続は 1 バッチ（従来どおり）
        assert_eq!(list.batches.len(), 1);
        assert!(!list.batches[0].shape);
        assert_eq!((list.batches[0].base, list.batches[0].count), (0, 2));
        // 収集の側: 既定の欄のスプライトは形と塗りを運ばない
        let sc = SpriteComponent::default();
        assert!(SpriteStyleDraw::for_sprite(&sc, [10.0, 20.0], &item(0.0, None, None).model, 1.0).is_none());
    }

    /// 形のあるスプライトと、角丸・楕円の切り抜きの中のスプライトは形と塗りの経路。並びは入力順のまま。
    #[test]
    fn styled_and_round_clipped_items_take_the_shape_path() {
        let sc = SpriteComponent {
            shape: SpriteShape { corner_radii: [4.0; 4], ..SpriteShape::default() },
            ..SpriteComponent::default()
        };
        let styled = SpriteStyleDraw::for_sprite(&sc, [10.0, 20.0], &item(0.0, None, None).model, 1.0);
        let circle = ClipSdf::new(
            &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0], [0.0, 10.0, 0.0], [10.0, 10.0, 0.0]],
            &UiClipShape::ellipse([10.0, 10.0]),
        );
        let clip_of = move |id: Option<UiClipId>| if id == Some(7) { circle } else { None };
        let mut scratch = SpriteScratch::default();
        let list = scratch.push_items(
            vec![item(1.0, None, None), item(2.0, styled, None), item(3.0, None, Some(7)), item(4.0, None, None)],
            &clip_of,
        );
        assert_eq!(scratch.quads.len(), 2);
        assert_eq!(scratch.shapes.len(), 2);
        assert_eq!(scratch.params.len(), 2);
        let kinds: Vec<(bool, u32, u32)> = list.batches.iter().map(|b| (b.shape, b.base, b.count)).collect();
        assert_eq!(kinds, vec![(false, 0, 1), (true, 0, 2), (false, 1, 1)]);
        // 切り抜きだけのアイテムは行列を変えない
        assert_eq!(scratch.shapes[1].model, item(3.0, None, None).model);
        assert_eq!(scratch.shapes[1].params, 1);
    }

    /// 容量は倍々に伸びる。
    #[test]
    fn capacity_grows_by_doubling() {
        assert_eq!(grown_capacity(16, 17), 32);
        assert_eq!(grown_capacity(16, 100), 128);
        assert_eq!(grown_capacity(0, 1), 1);
    }
}
