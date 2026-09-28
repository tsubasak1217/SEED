// ============================================================
//  primitive2d/tess_cache.rs — SEED.Draw の図形の三角形分割の使い回し（2 世代の表）
//
//  【なぜ要るか（2026-09-28。docs/app_platform_roadmap.md §3.9 の原因 2）】
//  SEED.Draw は毎フレーム図形を積み直す即時型で、グラフ（SEED.UI の LineChart・BarChart）はスクロール中も
//  毎フレーム同じ図形を積む。三角形分割（tessellate.rs）は図形の形だけの純関数なので、前のフレームと同じ形なら
//  結果をそのまま使える（UI の見本で「描画/UI/図形/三角形分割」が 1 フレームに約 57 回）。
//
//  【何が同じなら使い回すか（キー）＝何が変わったら作り直すか】
//  tessellate が読む値だけ（tessellate.rs の `tessellate` の本体と一致させること）:
//    図形の種別・塗り／輪郭・線の太さ・SRT（位置・回転・拡大）・追加のスカラ（extras）・点列・
//    見た目の拡張の有無（軽い三角形分割）・フェザーの幅（描画空間の単位。画面の 1 画素のフェザーは行列から決まる）
//  浮動小数はすべてビット列で比べる（-0.0 と 0.0 も別。値が 1 ビットでも違えば作り直す）。
//  色・グラデーション・レイヤー・座標空間（行列）はキーに入れない（三角形分割の後、毎フレームの頂点の射影で使う）。
//  → キーが同じなら、tessellate をもう一度呼んだ結果と 1 ビットも違わない。
//
//  【捨て方（2 世代）】Primitive2dRenderer::begin（フレームの頭）で世代を進め、前のフレームで使わなかった形を捨てる。
// ============================================================

use std::collections::HashMap;
use std::rc::Rc;

use crate::engine::core::fast_hash::{digest_u32, FastBuildHasher};

use super::offscreen_cull::mesh_bounds;
use super::queue::{PrimitiveCommand, PrimitiveStyle, Transform2d};
use super::tessellate::{tessellate, Mesh2d};

/// 覚える値: 三角形分割と、その頂点の外接矩形（画面の外の図形を積まない判定に使う。offscreen_cull.rs）。
#[derive(Debug)]
pub struct CachedShape {
    /// 三角形分割（描画空間。SRT 適用済み）。
    pub mesh: Mesh2d,
    /// 頂点の外接矩形（描画空間の [min_x, min_y, max_x, max_y]。頂点が無ければ None）。
    pub bounds: Option<[f32; 4]>,
}

impl CachedShape {
    /// 分割から作る（外接矩形はここで 1 回だけ求める）。
    fn new(mesh: Mesh2d) -> Self {
        let bounds = mesh_bounds(&mesh);
        Self { mesh, bounds }
    }
}

/// 図形の形のキー（ビット列の並び。上の【何が同じなら使い回すか】の値を固定の順に並べたもの）。
///
/// 並び: [種別, 塗り/輪郭, 拡張, フェザー, 太さ, SRT(5), extras(PRIM_EXTRA_FLOATS), 点の数, 点(x, y)...]
pub fn shape_key_bits(cmd: &PrimitiveCommand, feather: f32, out: &mut Vec<u32>) {
    // `..` を使わずに全欄を取り出す（PrimitiveCommand・PrimitiveStyle・Transform2d に欄を足すとここでビルドが止まり、
    // 三角形分割が読む値ならキーへ足す・読まない値なら `_` にする、を必ず決めさせる。古い分割を使い回す不具合を防ぐ）。
    // 三角形分割が読まない値: 座標空間（行列は射影で掛ける）・色とグラデーション（射影で掛ける）・レイヤー（並べ替え）・
    // 画面の 1 画素のフェザー（push が feather に直して渡すので、feather のほうをキーに入れる）。
    let PrimitiveCommand { kind, space: _, color: _, mode, thickness, layer: _, srt, extras, points, style } = cmd;
    let PrimitiveStyle { extended, pixel_feather: _, gradient: _ } = style;
    let Transform2d { position, rotation_deg, scale } = srt;
    out.clear();
    out.push(*kind as u32);
    out.push(*mode as u32);
    out.push(u32::from(*extended));
    out.push(feather.to_bits());
    out.push(thickness.to_bits());
    out.push(position[0].to_bits());
    out.push(position[1].to_bits());
    out.push(rotation_deg.to_bits());
    out.push(scale[0].to_bits());
    out.push(scale[1].to_bits());
    out.extend(extras.iter().map(|v| v.to_bits()));
    out.push(points.len() as u32);
    for p in points {
        out.push(p[0].to_bits());
        out.push(p[1].to_bits());
    }
}

/// 1 つの振り分け先の中身（要約が同じだった形の並び。普通は 1 つ）。
type Bucket = Vec<(Vec<u32>, Rc<CachedShape>)>;

/// 三角形分割の使い回しの表（2 世代）。
#[derive(Default)]
pub struct TessellationCache {
    /// 今の世代（このフレームで使った・作った形）。要約は digest_u32 で作ったものなので、表は速いハッシュで引く。
    current: HashMap<u64, Bucket, FastBuildHasher>,
    /// 前の世代（前のフレームで使った形）。
    previous: HashMap<u64, Bucket, FastBuildHasher>,
    /// キーを組む作業領域（図形ごとに確保し直さない）。
    scratch: Vec<u32>,
    /// 使い回した回数（診断とテスト用）。
    hits: u64,
    /// 作った回数（同上）。
    misses: u64,
}

impl TessellationCache {
    /// 空の表を作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// 図形の三角形分割と外接矩形を返す（同じ形を前のフレームか今のフレームで分割していれば、それを使う）。
    pub fn tessellate(&mut self, cmd: &PrimitiveCommand, feather: f32) -> Rc<CachedShape> {
        let mut key = std::mem::take(&mut self.scratch);
        shape_key_bits(cmd, feather, &mut key);
        let digest = digest_u32(&key);
        let found = if let Some(mesh) = self.current.get(&digest).and_then(|b| find(b, &key)) {
            Some(mesh)
        } else if let Some(bucket) = self.previous.get_mut(&digest) {
            // 前の世代にあれば今の世代へ移す
            bucket.iter().position(|(k, _)| *k == key).map(|pos| {
                let entry = bucket.swap_remove(pos);
                let mesh = Rc::clone(&entry.1);
                self.current.entry(digest).or_default().push(entry);
                mesh
            })
        } else {
            None
        };
        let mesh = match found {
            Some(mesh) => {
                self.hits += 1;
                mesh
            }
            None => {
                self.misses += 1;
                let mesh = Rc::new(CachedShape::new(tessellate(cmd, feather)));
                self.current.entry(digest).or_default().push((key.clone(), Rc::clone(&mesh)));
                mesh
            }
        };
        self.scratch = key;
        mesh
    }

    /// 世代を進める（フレームの頭に 1 回）。前のフレームで使わなかった形を捨てる。
    pub fn advance_generation(&mut self) {
        self.previous = std::mem::take(&mut self.current);
    }

    /// 使い回した回数と作った回数（診断とテスト用）。
    pub fn stats(&self) -> (u64, u64) {
        (self.hits, self.misses)
    }

    /// 覚えている形の数（2 世代の合計。テスト用）。
    pub fn len(&self) -> usize {
        self.current.values().map(Vec::len).sum::<usize>() + self.previous.values().map(Vec::len).sum::<usize>()
    }
}

/// 振り分け先からキーの形を探す。
fn find(bucket: &Bucket, key: &[u32]) -> Option<Rc<CachedShape>> {
    bucket.iter().find(|(k, _)| k.as_slice() == key).map(|(_, mesh)| Rc::clone(mesh))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::renderer::primitive2d::queue::{
        PrimitiveDrawMode, PrimitiveKind, PrimitiveStyle, Transform2d,
    };

    /// 折れ線の図形（グラフの線に相当）。
    fn polyline(points: Vec<[f32; 2]>) -> PrimitiveCommand {
        PrimitiveCommand {
            kind: PrimitiveKind::Polyline,
            space: None,
            color: [1.0, 1.0, 1.0, 1.0],
            mode: PrimitiveDrawMode::Outline,
            thickness: 2.0,
            layer: 0,
            srt: Transform2d::IDENTITY,
            extras: [0.0; crate::engine::core::renderer::primitive2d::PRIM_EXTRA_FLOATS],
            points,
            style: PrimitiveStyle::default(),
        }
    }

    fn meshes_equal(a: &Mesh2d, b: &Mesh2d) -> bool {
        a.idx == b.idx
            && a.verts.len() == b.verts.len()
            && a.verts.iter().zip(&b.verts).all(|(x, y)| {
                x.pos[0].to_bits() == y.pos[0].to_bits()
                    && x.pos[1].to_bits() == y.pos[1].to_bits()
                    && x.alpha.to_bits() == y.alpha.to_bits()
            })
    }

    /// 同じ形は 2 回目から分割しない。結果は tessellate を直接呼んだものとビット単位で同じ。
    #[test]
    fn same_shape_is_reused_and_identical() {
        let mut cache = TessellationCache::new();
        let cmd = polyline(vec![[0.0, 0.0], [10.0, 5.0], [20.0, -3.0], [30.0, 8.0]]);
        let a = cache.tessellate(&cmd, 1.0);
        cache.advance_generation();
        let b = cache.tessellate(&cmd, 1.0);
        assert!(Rc::ptr_eq(&a, &b), "前のフレームの分割を使う");
        assert!(meshes_equal(&b.mesh, &tessellate(&cmd, 1.0)));
        assert_eq!(b.bounds, crate::engine::core::renderer::primitive2d::offscreen_cull::mesh_bounds(&b.mesh), "外接矩形も覚える");
        assert_eq!(cache.stats(), (1, 1));
    }

    /// 形に効く値のどれかが違えば作り直し、色・レイヤー・座標空間だけの違いは使い回す。
    #[test]
    fn shape_inputs_invalidate_but_paint_inputs_do_not() {
        let base = polyline(vec![[0.0, 0.0], [10.0, 5.0], [20.0, -3.0]]);
        let mut cache = TessellationCache::new();
        let first = cache.tessellate(&base, 1.0);

        let mut variants = Vec::new();
        let mut v = base.clone();
        v.points[1][1] = 5.5;
        variants.push(v);
        let mut v = base.clone();
        v.thickness = 3.0;
        variants.push(v);
        let mut v = base.clone();
        v.mode = PrimitiveDrawMode::Fill;
        variants.push(v);
        let mut v = base.clone();
        v.srt.rotation_deg = 15.0;
        variants.push(v);
        let mut v = base.clone();
        v.extras[0] = 1.0;
        variants.push(v);
        let mut v = base.clone();
        v.style.extended = true;
        variants.push(v);
        let mut v = base.clone();
        v.points.push([40.0, 0.0]);
        variants.push(v);
        for (i, v) in variants.iter().enumerate() {
            let mesh = cache.tessellate(v, 1.0);
            assert!(!Rc::ptr_eq(&mesh, &first), "変種 {i} が古い分割を使った");
            assert!(meshes_equal(&mesh.mesh, &tessellate(v, 1.0)), "変種 {i}");
        }
        // フェザーの幅が違えば別
        assert!(!Rc::ptr_eq(&cache.tessellate(&base, 0.5), &first));

        // 色・レイヤー・座標空間は三角形分割に効かない（使い回す）
        let mut paint = base.clone();
        paint.color = [1.0, 0.0, 0.0, 1.0];
        paint.layer = 7;
        paint.space = Some(crate::engine::ecs::Entity::from_raw(3, 0));
        assert!(Rc::ptr_eq(&cache.tessellate(&paint, 1.0), &first));
    }

    /// 前のフレームで使わなかった形は 2 世代で消える。
    #[test]
    fn unused_shapes_are_dropped() {
        let mut cache = TessellationCache::new();
        cache.tessellate(&polyline(vec![[0.0, 0.0], [1.0, 1.0]]), 1.0);
        cache.advance_generation();
        cache.advance_generation();
        assert_eq!(cache.len(), 0);
    }
}
