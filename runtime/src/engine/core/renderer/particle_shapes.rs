// ============================================================
//  particle_shapes.rs — パーティクル 1 粒の組込みメッシュ（頂点＝位置のみ）
//
//  拡張 GPU パーティクルの「形状（ParticleShape）」に対応する単位メッシュを
//  CPU 側で用意し、GPU の頂点／インデックスバッファへ 1 度だけ確保して全エミッタで
//  共有する（ShapeMeshCache）。頂点属性は位置（vec3）のみ。描画シェーダ
//  （particle_draw.wgsl）が instance_index で粒子を読み、頂点をビルボード化
//  （Point）または軸角回転＋スケール＋平行移動（Sphere/Box/Plane/Model）する。
//
//  【設計方針】
//   - 組込み形状（Point/Sphere/Box/Plane）は本ファイルの const/生成関数で定義する
//     （データドリブン：頂点データの差し替えだけで形状を増やせる）。
//   - Model 形状は既存ローダ（loader::load_model）で読んだ先頭プリミティブの
//     位置のみを流用する。頂点数が MAX_PARTICLE_MODEL_VERTS を超えたら警告し、
//     呼び出し側（particle_system）が Point へフォールバックする。
//   - すべて単位サイズ（±0.5 / 半径 0.5）で定義し、実サイズは描画シェーダ側の
//     scale_curve × size 倍率で決める（マジックナンバー禁止・スケールの一元化）。
//
//  ※ 位置のみの頂点なので stride=12（vec3<f32>）。パイプラインの頂点バッファ
//    レイアウト（pipeline.rs）と一致させること。
// ============================================================

use bytemuck::{Pod, Zeroable};
use wgpu::util::DeviceExt;

use crate::engine::components::particle_emitter_component::MAX_PARTICLE_MODEL_VERTS;

// ─── 定数（単位メッシュの寸法）────────────────────────────────
// マジックナンバー禁止：単位メッシュの基準寸法を名前付き定数にする。

/// 単位メッシュの半径（Box/Plane の half extent、Sphere の半径）。
/// 実サイズは描画時の scale_curve × size 倍率で決めるため、基準は 0.5 固定。
const UNIT_HALF: f32 = 0.5;

// ─── 位置のみ頂点（GPU 頂点バッファ要素・12 バイト）──────────

/// パーティクルメッシュの頂点（位置のみ）。stride=12。
///
/// 描画シェーダは @location(0) の vec3 として読む。パイプラインの
/// VertexBufferLayout（pipeline.rs）と厳密に一致させること。
#[repr(C)]
#[derive(Copy, Clone, Pod, Zeroable)]
pub struct ShapeVertex {
    /// ローカル位置（単位メッシュ空間）。
    pub pos: [f32; 3],
}

impl ShapeVertex {
    /// 頂点バッファレイアウト（位置 vec3 @location(0)）。pipeline.rs と共有する。
    pub const LAYOUT: wgpu::VertexBufferLayout<'static> = wgpu::VertexBufferLayout {
        array_stride: std::mem::size_of::<ShapeVertex>() as wgpu::BufferAddress,
        step_mode: wgpu::VertexStepMode::Vertex,
        attributes: &[wgpu::VertexAttribute {
            format: wgpu::VertexFormat::Float32x3,
            offset: 0,
            shader_location: 0,
        }],
    };
}

// ─── GPU 上の 1 メッシュ（頂点＋インデックス）───────────────

/// GPU 上に確保した 1 個の形状メッシュ（全エミッタで共有）。
pub struct ShapeMesh {
    /// 位置頂点バッファ（VERTEX）。
    pub vbuf: wgpu::Buffer,
    /// インデックスバッファ（INDEX, u32）。
    pub ibuf: wgpu::Buffer,
    /// インデックス数（draw_indexed の範囲）。
    pub index_count: u32,
}

impl ShapeMesh {
    /// 位置配列とインデックス配列から GPU メッシュを生成する。
    fn from_data(
        device: &wgpu::Device,
        label: &str,
        positions: &[[f32; 3]],
        indices: &[u32],
    ) -> Self {
        // 位置のみ頂点へ詰め替える（bytemuck で bytes 化するため repr(C) の ShapeVertex を使う）。
        let verts: Vec<ShapeVertex> = positions.iter().map(|&p| ShapeVertex { pos: p }).collect();
        let vbuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some(label),
            contents: bytemuck::cast_slice(&verts),
            usage: wgpu::BufferUsages::VERTEX,
        });
        let ibuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some(label),
            contents: bytemuck::cast_slice(indices),
            usage: wgpu::BufferUsages::INDEX,
        });
        Self {
            vbuf,
            ibuf,
            index_count: indices.len() as u32,
        }
    }
}

// ─── 組込みメッシュのデータ生成 ──────────────────────────────

/// Point（ビルボード）用の単位クアッド。xy 平面の ±0.5、z=0。
///
/// 描画シェーダ（billboard）は頂点 xy をカメラ右／上ベクトルで展開し、
/// UV は pos.xy+0.5（0..1）で得る。両面表示（cull None）。
fn point_quad() -> (Vec<[f32; 3]>, Vec<u32>) {
    let h = UNIT_HALF;
    let positions = vec![
        [-h, -h, 0.0], // 0 左下
        [h, -h, 0.0],  // 1 右下
        [h, h, 0.0],   // 2 右上
        [-h, h, 0.0],  // 3 左上
    ];
    // 2 三角形（CCW）。
    let indices = vec![0, 1, 2, 0, 2, 3];
    (positions, indices)
}

/// Plane（両面クアッド）用の単位平面。xy 平面の ±0.5。
///
/// cull None のため 1 枚のクアッドで両面表示になる（v1：厚みなし・法線なし）。
fn plane_mesh() -> (Vec<[f32; 3]>, Vec<u32>) {
    // Point と同一データだが、描画上は mesh モード（3D 回転される）として扱う。
    point_quad()
}

/// Box（単位立方体）用のメッシュ。±0.5 の 8 頂点・12 三角形。
///
/// 位置のみ（面ごとの法線・UV は持たない。UV は mesh モードで中心固定）。
fn box_mesh() -> (Vec<[f32; 3]>, Vec<u32>) {
    let h = UNIT_HALF;
    // 8 コーナー（-x/-y/-z を 0 として bit で並べる）。
    let positions = vec![
        [-h, -h, -h], // 0
        [h, -h, -h],  // 1
        [h, h, -h],   // 2
        [-h, h, -h],  // 3
        [-h, -h, h],  // 4
        [h, -h, h],   // 5
        [h, h, h],    // 6
        [-h, h, h],   // 7
    ];
    // 6 面 × 2 三角形（CCW 外向き）。cull None なので巻き向きは描画に影響しないが一応外向きで定義。
    let indices = vec![
        // -z 面
        0, 2, 1, 0, 3, 2, // +z 面
        4, 5, 6, 4, 6, 7, // -x 面
        0, 4, 7, 0, 7, 3, // +x 面
        1, 2, 6, 1, 6, 5, // -y 面
        0, 1, 5, 0, 5, 4, // +y 面
        3, 7, 6, 3, 6, 2,
    ];
    (positions, indices)
}

/// Sphere（低ポリ icosphere）用のメッシュ。正二十面体（12 頂点・20 三角形）を半径 0.5 へ正規化。
///
/// v1 は細分割なし（十分に低ポリで軽量）。より滑らかにしたい場合は本関数で
/// 1 段細分割（42 頂点/80 三角形）へ拡張できる（データドリブン）。
fn icosphere_mesh() -> (Vec<[f32; 3]>, Vec<u32>) {
    // 黄金比 φ。正二十面体の頂点座標に使う。
    let phi = (1.0 + 5.0_f32.sqrt()) * 0.5;
    // 未正規化の 12 頂点（±1, ±φ の 3 平面矩形）。
    let raw: [[f32; 3]; 12] = [
        [-1.0, phi, 0.0],
        [1.0, phi, 0.0],
        [-1.0, -phi, 0.0],
        [1.0, -phi, 0.0],
        [0.0, -1.0, phi],
        [0.0, 1.0, phi],
        [0.0, -1.0, -phi],
        [0.0, 1.0, -phi],
        [phi, 0.0, -1.0],
        [phi, 0.0, 1.0],
        [-phi, 0.0, -1.0],
        [-phi, 0.0, 1.0],
    ];
    // 各頂点を単位球面へ正規化し、半径 0.5 へスケールする。
    let positions: Vec<[f32; 3]> = raw
        .iter()
        .map(|v| {
            let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
            [
                v[0] / len * UNIT_HALF,
                v[1] / len * UNIT_HALF,
                v[2] / len * UNIT_HALF,
            ]
        })
        .collect();
    // 20 三角形（標準的な正二十面体のインデックス）。
    let indices = vec![
        0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7,
        1, 8, 3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9,
        8, 1,
    ];
    (positions, indices)
}

// ─── 形状メッシュキャッシュ（全エミッタ共有）──────────────────

/// 組込み形状メッシュ＋モデルメッシュのキャッシュ（ParticleSystem が 1 つ保持）。
///
/// 組込み 4 種はデバイス取得後に 1 度だけ生成し、Model はパスごとに遅延ロードして
/// HashMap に保持する（ロード失敗・頂点数超過は None＝Point フォールバック）。
pub struct ShapeMeshCache {
    /// Point（ビルボード）用クアッド。
    point: ShapeMesh,
    /// Sphere（icosphere）。
    sphere: ShapeMesh,
    /// Box（立方体）。
    cube: ShapeMesh,
    /// Plane（両面クアッド）。
    plane: ShapeMesh,
    /// Model パス → メッシュ（None はロード失敗／頂点数超過で Point 代替）。
    models: std::collections::HashMap<String, Option<ShapeMesh>>,
}

impl ShapeMeshCache {
    /// 組込み 4 メッシュを生成する（デバイス取得後に 1 度だけ呼ぶ）。
    pub fn new_builtins(device: &wgpu::Device) -> Self {
        let (pq, pqi) = point_quad();
        let (ic, ici) = icosphere_mesh();
        let (bx, bxi) = box_mesh();
        let (pl, pli) = plane_mesh();
        Self {
            point: ShapeMesh::from_data(device, "Particle Point Quad", &pq, &pqi),
            sphere: ShapeMesh::from_data(device, "Particle Icosphere", &ic, &ici),
            cube: ShapeMesh::from_data(device, "Particle Cube", &bx, &bxi),
            plane: ShapeMesh::from_data(device, "Particle Plane", &pl, &pli),
            models: std::collections::HashMap::new(),
        }
    }

    /// Point（ビルボード）メッシュを返す。
    pub fn point(&self) -> &ShapeMesh {
        &self.point
    }
    /// Sphere メッシュを返す。
    pub fn sphere(&self) -> &ShapeMesh {
        &self.sphere
    }
    /// Box メッシュを返す。
    pub fn cube(&self) -> &ShapeMesh {
        &self.cube
    }
    /// Plane メッシュを返す。
    pub fn plane(&self) -> &ShapeMesh {
        &self.plane
    }

    /// ロード済み Model メッシュを（ロードを試みずに）返す。
    ///
    /// draw 段（&self）で使う。sync_gpu 段で `model()` により必ずロード済みに
    /// なっているため、ここでのロードは不要（未ロード＝フォールバック済み）。
    pub fn model_cached(&self, path: &str) -> Option<&ShapeMesh> {
        self.models.get(path).and_then(|m| m.as_ref())
    }

    /// Model メッシュを遅延ロードして参照を返す。
    ///
    /// - キャッシュ済みならそれを返す（成功＝Some / 失敗・超過＝None）。
    /// - 未ロードなら loader::load_model で読み、先頭プリミティブの位置のみを流用する。
    ///   頂点数が MAX_PARTICLE_MODEL_VERTS を超える場合は警告して None を記録する。
    ///
    /// 戻り値 None は「Point へフォールバックせよ」を意味する（呼び出し側で shape_mode=0）。
    pub fn model(&mut self, device: &wgpu::Device, path: &str) -> Option<&ShapeMesh> {
        if !self.models.contains_key(path) {
            let loaded = Self::load_model_mesh(device, path);
            self.models.insert(path.to_string(), loaded);
        }
        self.models.get(path).and_then(|m| m.as_ref())
    }

    /// モデルファイルを読み、先頭プリミティブの位置のみで GPU メッシュを作る。
    ///
    /// 失敗（ロードエラー・プリミティブ無し・頂点数超過）は警告して None を返す。
    fn load_model_mesh(device: &wgpu::Device, path: &str) -> Option<ShapeMesh> {
        // 仮想パス（assets://）を実ファイルパスへ解決してからロードする
        // （他のモデル参照と同じ asset_fs::resolve を使う）。
        let fs_path = crate::engine::asset_fs::resolve(path);
        let model = match crate::engine::core::loader::load_model(&fs_path) {
            Ok(m) => m,
            Err(e) => {
                eprintln!(
                    "[SEED particle] モデル形状のロード失敗: path={path:?} err={e} → Point へフォールバック"
                );
                return None;
            }
        };
        // 先頭メッシュの先頭プリミティブを使う（位置のみ）。
        let prim = model.meshes.first().and_then(|m| m.primitives.first());
        let Some(prim) = prim else {
            eprintln!(
                "[SEED particle] モデルにプリミティブが無い: path={path:?} → Point へフォールバック"
            );
            return None;
        };
        // 頂点数上限チェック（GPU 頂点バッファの肥大・描画コスト抑制）。
        if prim.vertices.len() > MAX_PARTICLE_MODEL_VERTS {
            eprintln!(
                "[SEED particle] モデル頂点数 {} が上限 {} を超過: path={path:?} → Point へフォールバック",
                prim.vertices.len(),
                MAX_PARTICLE_MODEL_VERTS,
            );
            return None;
        }
        if prim.vertices.is_empty() || prim.indices.is_empty() {
            eprintln!(
                "[SEED particle] モデルの頂点／インデックスが空: path={path:?} → Point へフォールバック"
            );
            return None;
        }
        // 位置のみ抽出（法線・UV 等は使わない）。
        let positions: Vec<[f32; 3]> = prim.vertices.iter().map(|v| v.position).collect();
        Some(ShapeMesh::from_data(
            device,
            "Particle Model",
            &positions,
            &prim.indices,
        ))
    }
}
