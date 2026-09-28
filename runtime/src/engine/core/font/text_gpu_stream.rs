// ============================================================
//  font/text_gpu_stream.rs — キャンバスのテキストの頂点・添字を使い回しの GPU バッファへ送る
//
//  【なぜ要るか（2026-09-28。docs/app_platform_roadmap.md §3.9 の原因 2）】
//  以前はゾーン（背景・前面・3D キャンバス）ごと・フレームごとに `create_buffer_init` で頂点と添字のバッファを
//  新しく作って捨てていた（build_gpu_batch）。ここではバッファを持ち続け、足りなくなったときだけ倍の大きさで
//  作り直し、中身は `queue.write_buffer` で書き換える（スプライトの InstanceStream・SEED.Draw の Primitive2dRenderer と同じ形）。
//
//  【使い方の約束（守らないと前のフレームの字が出る・字が欠ける）】
//    1. フレームの頭に CPU のバッチを空にする（CanvasTextRenderer::begin_frame）
//    2. 全ゾーンの字を 1 本の CPU のバッチへ積む（区間はバッチの頭からの添字の番号）
//    3. 全ゾーンを積み終えてから、描画を記録する**前**に 1 回だけ `upload` する
//       （作り直したバッファを、記録済みのパスが古いほうで読むのを避ける。書き込みは次の submit の前に効く）
//    4. 描画は `buffers` の組と区間で行う（区間が送った添字の数を超えたら描かない）
// ============================================================

use super::TextBatch;
use super::pipeline::TextVertex;

/// 頂点のバッファの最初の大きさ（頂点の数。1 字 4 頂点なので 1024 字ぶん）。
const INITIAL_VERTEX_CAPACITY: u64 = 4096;
/// 添字のバッファの最初の大きさ（添字の数。1 字 6 添字）。
const INITIAL_INDEX_CAPACITY: u64 = 6144;
/// 足りなくなったときに何倍へ広げるか。
const CAPACITY_GROWTH_FACTOR: u64 = 2;

/// 使い回しのテキストの GPU バッファ（頂点・添字）。
#[derive(Default)]
pub struct TextGpuStream {
    /// 頂点のバッファ（まだ作っていなければ None）。
    vertex_buf: Option<wgpu::Buffer>,
    /// 添字のバッファ。
    index_buf: Option<wgpu::Buffer>,
    /// 頂点のバッファの大きさ（頂点の数）。
    vertex_capacity: u64,
    /// 添字のバッファの大きさ（添字の数）。
    index_capacity: u64,
    /// 今のフレームで送った添字の数（区間の検査に使う。送っていなければ 0）。
    uploaded_indices: u32,
}

impl TextGpuStream {
    /// 空のストリーム（バッファは最初の `upload` で作る）。
    pub fn new() -> Self {
        Self::default()
    }

    /// バッチの中身を GPU へ送る（容量が足りなければ倍々で作り直す）。空のバッチなら何も送らない。
    pub fn upload(&mut self, device: &wgpu::Device, queue: &wgpu::Queue, batch: &TextBatch) {
        self.uploaded_indices = 0;
        if batch.vertices.is_empty() || batch.indices.is_empty() {
            return;
        }
        let need_vertices = batch.vertices.len() as u64;
        if self.vertex_buf.is_none() || self.vertex_capacity < need_vertices {
            self.vertex_capacity = grown_capacity(self.vertex_capacity, INITIAL_VERTEX_CAPACITY, need_vertices);
            self.vertex_buf = Some(device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("Canvas Text Vertex Stream"),
                size: self.vertex_capacity * std::mem::size_of::<TextVertex>() as u64,
                usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            }));
        }
        let need_indices = batch.indices.len() as u64;
        if self.index_buf.is_none() || self.index_capacity < need_indices {
            self.index_capacity = grown_capacity(self.index_capacity, INITIAL_INDEX_CAPACITY, need_indices);
            self.index_buf = Some(device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("Canvas Text Index Stream"),
                size: self.index_capacity * std::mem::size_of::<u32>() as u64,
                usage: wgpu::BufferUsages::INDEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            }));
        }
        if let (Some(vb), Some(ib)) = (&self.vertex_buf, &self.index_buf) {
            queue.write_buffer(vb, 0, bytemuck::cast_slice(&batch.vertices));
            queue.write_buffer(ib, 0, bytemuck::cast_slice(&batch.indices));
            self.uploaded_indices = batch.indices.len() as u32;
        }
    }

    /// 描画に使うバッファの組と、今のフレームで送った添字の数（送っていなければ None）。
    pub fn buffers(&self) -> Option<(&wgpu::Buffer, &wgpu::Buffer, u32)> {
        if self.uploaded_indices == 0 {
            return None;
        }
        Some((self.vertex_buf.as_ref()?, self.index_buf.as_ref()?, self.uploaded_indices))
    }
}

/// 必要な数を満たすまで倍々に広げた容量【純関数】（今の容量が最初の大きさより小さければ最初の大きさから）。
fn grown_capacity(current: u64, initial: u64, need: u64) -> u64 {
    let mut capacity = current.max(initial);
    while capacity < need {
        capacity *= CAPACITY_GROWTH_FACTOR;
    }
    capacity
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 足りていれば今の容量のまま、足りなければ倍々（最初は既定の大きさから）。
    #[test]
    fn capacity_grows_by_doubling() {
        assert_eq!(grown_capacity(0, INITIAL_VERTEX_CAPACITY, 10), INITIAL_VERTEX_CAPACITY);
        assert_eq!(grown_capacity(0, INITIAL_VERTEX_CAPACITY, INITIAL_VERTEX_CAPACITY + 1), INITIAL_VERTEX_CAPACITY * 2);
        assert_eq!(grown_capacity(8192, INITIAL_VERTEX_CAPACITY, 8192), 8192);
        assert_eq!(grown_capacity(8192, INITIAL_VERTEX_CAPACITY, 30000), 32768);
    }

    /// 送っていないストリームは描かない（区間の検査の前提）。
    #[test]
    fn nothing_uploaded_means_no_buffers() {
        assert!(TextGpuStream::new().buffers().is_none());
    }
}
