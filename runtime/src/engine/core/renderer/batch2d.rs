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
//  TODO(将来課題): テクスチャ配列／アトラスで異なるテクスチャも 1 バッチへ統合すれば
//  ドローコールをさらに削減できる（現行のテクスチャ管理を大改造しないため未実施）。
//  9-slice スプライトも本バッチャの対象外（別ジオメトリのため TODO）。
// ============================================================

use crate::engine::components::CanvasDrawZone;
use crate::engine::core::renderer::pipeline::{SpriteOutlinePipeline, SpritePipeline};
use crate::engine::core::renderer::sprite_skin::SkinnedSpriteDraw;
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
//  InstanceStream — 成長する永続インスタンスバッファ（1 チャンネル）
// ============================================================

/// 1 チャンネル分の永続インスタンスバッファ。
///
/// 1 フレームの使い方: `begin()` → `push()` を必要回 → `upload()` → `buffer()`。
/// `push()` は CPU 側 scratch へ積み、`upload()` で 1 度だけ write_buffer する
/// （容量不足時のみ倍々成長で再確保）。
pub struct InstanceStream {
    buffer: Arc<wgpu::Buffer>,
    /// 収容可能インスタンス数。
    capacity: u32,
    /// このフレーム分の CPU 積み上げ。
    scratch: Vec<SpriteInstance>,
    label: &'static str,
}

impl InstanceStream {
    /// 初期容量でバッファを確保する。
    fn new(device: &wgpu::Device, label: &'static str) -> Self {
        let buffer = Arc::new(Self::alloc(device, INITIAL_INSTANCE_CAPACITY, label));
        Self {
            buffer,
            capacity: INITIAL_INSTANCE_CAPACITY,
            scratch: Vec::new(),
            label,
        }
    }

    /// 指定容量の VERTEX|COPY_DST バッファを確保する。
    fn alloc(device: &wgpu::Device, capacity: u32, label: &'static str) -> wgpu::Buffer {
        device.create_buffer(&wgpu::BufferDescriptor {
            label: Some(label),
            size: capacity as u64 * SPRITE_INSTANCE_SIZE,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        })
    }

    /// フレーム開始: 積み上げをクリアする（バッファ本体は再利用）。
    pub fn begin(&mut self) {
        self.scratch.clear();
    }

    /// items（model, color, tex）を追記し、テクスチャ境界で区切ったバッチ列を返す。
    ///
    /// 入力順は一切変更しない（連続する同一テクスチャのみ 1 バッチへ融合する）。
    /// 返される base は「このストリームのバッファ先頭からの」絶対インスタンス番号のため、
    /// 同一フレーム内で push を複数回呼んでも整合する（scratch は連続配置）。
    pub fn push<I>(&mut self, items: I) -> SpriteBatchList
    where
        I: IntoIterator<Item = SpriteDrawItem>,
    {
        let mut batches: Vec<SpriteBatch> = Vec::new();
        for item in items {
            let idx = self.scratch.len() as u32;
            self.scratch.push(SpriteInstance {
                model: item.model,
                color: item.color,
            });
            // 融合条件: 直前バッチが「クワッド描画」かつ同一テクスチャのときだけ。
            // スキンメッシュは頂点／インデックスバッファがバッチごとに違うため
            // 常に単独バッチ（count=1）になる。
            let mergeable = item.mesh.is_none();
            match batches.last_mut() {
                Some(b) if mergeable && b.mesh.is_none() && same_tex(&b.tex, &item.tex) => {
                    b.count += 1
                }
                _ => batches.push(SpriteBatch {
                    tex: item.tex,
                    base: idx,
                    count: 1,
                    mesh: item.mesh,
                }),
            }
        }
        SpriteBatchList { batches }
    }

    /// scratch を GPU バッファへ書き込む。容量不足なら倍々成長で再確保する。
    pub fn upload(&mut self, device: &wgpu::Device, queue: &wgpu::Queue) {
        let needed = self.scratch.len() as u32;
        if needed == 0 {
            return;
        }
        if needed > self.capacity {
            // 収容できるまで倍々に増やす（最低 1 から開始）。
            let mut cap = self.capacity.max(1);
            while cap < needed {
                cap = cap.saturating_mul(INSTANCE_GROWTH_FACTOR);
            }
            self.buffer = Arc::new(Self::alloc(device, cap, self.label));
            self.capacity = cap;
        }
        queue.write_buffer(&self.buffer, 0, bytemuck::cast_slice(&self.scratch));
    }

    /// 現フレームで使う GPU バッファへの共有ハンドル。
    /// `upload()` 後に呼ぶこと（再確保後の最新バッファを返す）。
    /// レンダーパス記録前にローカルへ clone しておけば、パスの 'rp ライフタイムで
    /// `&*handle` を渡せる（RefCell の Ref を跨いで保持する必要がない）。
    pub fn buffer(&self) -> Arc<wgpu::Buffer> {
        self.buffer.clone()
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
    pub fn new(device: &wgpu::Device) -> Self {
        Self {
            main: InstanceStream::new(device, "Sprite Instance Buf (main)"),
            preview: InstanceStream::new(device, "Sprite Instance Buf (preview)"),
        }
    }
}

// ============================================================
//  描画関数
// ============================================================

/// テクスチャ付きスプライトバッチを描画する（同一テクスチャの連続 = 1 ドローコール）。
///
/// - `camera_bg`: group 0（mesh パイプラインと同一レイアウト）
/// - `inst_buf`:  当該ストリームの永続インスタンスバッファ（`InstanceStream::buffer()`）
/// - `list`:      当該描画順の `SpriteBatchList`
pub fn draw_sprite_batches<'rp>(
    pass: &mut wgpu::RenderPass<'rp>,
    pipeline: &'rp SpritePipeline,
    camera_bg: &'rp wgpu::BindGroup,
    inst_buf: &'rp wgpu::Buffer,
    list: &'rp SpriteBatchList,
) {
    if list.batches.is_empty() {
        return;
    }

    pass.set_pipeline(&pipeline.pipeline);
    pass.set_bind_group(0, camera_bg, &[]);
    // slot0 の既定はユニットクワッド。スキンメッシュのバッチだけ差し替える。
    pass.set_vertex_buffer(0, pipeline.unit_quad_vbuf.slice(..));
    // 直前のバッチがメッシュだったか（＝ slot0 をクワッドへ戻す必要があるか）。
    let mut slot0_is_mesh = false;

    for b in &list.batches {
        // slot1: このバッチのインスタンス範囲だけをスライスして束ねる
        let start = b.base as u64 * SPRITE_INSTANCE_SIZE;
        let end = start + b.count as u64 * SPRITE_INSTANCE_SIZE;
        pass.set_vertex_buffer(1, inst_buf.slice(start..end));
        // group 1: テクスチャ（未設定なら白フォールバック）
        let tex_bg = b
            .tex
            .as_ref()
            .map(|t| &t.bind_group)
            .unwrap_or(&pipeline.white_fallback_bg);
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

        for (name, src) in [("sprite", sprite), ("sprite_outline", outline)] {
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
}
