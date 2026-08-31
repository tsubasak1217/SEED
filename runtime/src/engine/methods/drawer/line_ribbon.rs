// ============================================================
//  line_ribbon.rs — ポリライン → カメラ向きリボンの頂点展開
//
//  LineRendererComponent（釣り糸・ロープ・軌跡）の描画実体。
//  ワールド空間の点列を「常にカメラを向く帯（ビルボードリボン）」の
//  三角形頂点列へ展開する **純関数** をここに置く。
//
//  【なぜ CPU 展開なのか】
//  ギズモの太線（gizmo_line.wgsl）は太さを **px** で指定するスクリーン空間展開で、
//  「遠くの線も同じ太さに見える」デバッグ表示向けの挙動になっている。
//  一方ゲーム内の釣り糸・ロープは **ワールド単位の太さ**（遠ければ細く見える）が
//  必要なので、専用のリボン展開が要る。頂点数は数百点 × 数本と小さいため、
//  専用 WGSL を増やさず CPU で展開して既存の unlit（ColorVertex）パイプラインへ
//  流すのが、実装量・検証コストともに最小になる。
//
//  【展開方式】
//  各セグメント（p[i] → p[i+1]）を 2 三角形のクワッドにする。
//  ある端点 p でのオフセット方向は
//      side = normalize(cross(seg_dir, view_dir)) * (width / 2)
//  （seg_dir = セグメント方向、view_dir = カメラ → p の向き）。
//  これは「セグメント方向にも視線方向にも直交する」ため、リボンの面は常に
//  カメラを向き、線はどの角度から見ても width の幅を保つ。
//
//  ジョイント（折れ点）はマイター処理せず、セグメントごとに独立して展開する
//  （＝隣接クワッドが端点で軽く重なる）。細い糸・ロープでは折れ角が緩く、
//  重なりは視認できない。マイターは頂点数と分岐を増やすだけなので採らない。
// ============================================================

use super::uniforms::ColorVertex;

/// セグメント方向と視線方向がほぼ平行（線がカメラ正面を向く）とみなす閾値。
///
/// 外積の長さがこの値未満だとオフセット方向が数値的に不定になるため、
/// フォールバック軸へ切り替える。1e-4 は「約 0.006 度以内の平行」に相当する。
const DEGENERATE_CROSS_EPS: f32 = 1e-4;

/// 点が重なっている（セグメント長が実質 0）とみなす閾値（ワールド単位）。
const DEGENERATE_SEGMENT_EPS: f32 = 1e-6;

/// 1 セグメントを展開したときの頂点数（2 三角形 = 1 クワッド）。
pub const VERTS_PER_SEGMENT: usize = 6;

// ─── ベクトル小道具（このモジュール内専用）──────────────────

/// 差ベクトル a - b。
#[inline]
fn sub3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}

/// 外積 a × b。
#[inline]
fn cross3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}

/// ベクトルの長さ。
#[inline]
fn length3(v: [f32; 3]) -> f32 {
    (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt()
}

/// スカラー倍。
#[inline]
fn scale3(v: [f32; 3], s: f32) -> [f32; 3] {
    [v[0] * s, v[1] * s, v[2] * s]
}

/// 加算 a + b。
#[inline]
fn add3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}

/// 正規化。長さが閾値未満なら None（呼び出し側でフォールバックする）。
#[inline]
fn normalize3(v: [f32; 3], eps: f32) -> Option<[f32; 3]> {
    let len = length3(v);
    if len < eps {
        None
    } else {
        Some(scale3(v, 1.0 / len))
    }
}

// ─── リボン展開 ───────────────────────────────────────────────

/// ある端点でのリボン半幅オフセットベクトルを求める。
///
/// `seg_dir` はセグメント方向（正規化済み）、`point` は端点のワールド座標、
/// `camera_pos` はカメラのワールド座標、`half_width` は width/2。
///
/// 戻り値は「セグメント方向・視線方向の両方に直交し、長さ = half_width」のベクトル。
/// 線がカメラを正面から向いている（seg_dir ∥ view_dir）縮退時は、seg_dir に直交する
/// 任意の軸へフォールバックする（この向きから見ると線は点に潰れて見えるため、
/// どの向きに広げても見た目は変わらない。破綻せず幅を保つことだけが要件）。
fn side_offset(
    seg_dir: [f32; 3],
    point: [f32; 3],
    camera_pos: [f32; 3],
    half_width: f32,
) -> [f32; 3] {
    let view_dir = sub3(point, camera_pos);
    let raw = cross3(seg_dir, view_dir);
    if let Some(n) = normalize3(raw, DEGENERATE_CROSS_EPS) {
        return scale3(n, half_width);
    }
    // 縮退フォールバック: seg_dir と最も平行でない基本軸との外積を取る。
    // seg_dir の絶対値最小成分の軸を選ぶと、必ず十分に非平行な軸になる。
    let ax = seg_dir[0].abs();
    let ay = seg_dir[1].abs();
    let az = seg_dir[2].abs();
    let axis = if ax <= ay && ax <= az {
        [1.0, 0.0, 0.0]
    } else if ay <= az {
        [0.0, 1.0, 0.0]
    } else {
        [0.0, 0.0, 1.0]
    };
    // seg_dir は正規化済み・axis とは非平行なので外積は必ず有限長になる。
    let n = normalize3(cross3(seg_dir, axis), DEGENERATE_SEGMENT_EPS)
        .unwrap_or([1.0, 0.0, 0.0]);
    scale3(n, half_width)
}

/// ワールド空間のポリラインを、カメラを向くリボンの三角形頂点列へ展開して `out` へ追記する。
///
/// - `points`     : ワールド座標の点列（2 点未満なら何も追加しない）
/// - `width`      : リボンの幅（ワールド単位）。0 以下なら何も追加しない
/// - `color`      : 全頂点に付ける色（RGBA・リニア）
/// - `camera_pos` : カメラのワールド座標（リボンの向きを決める）
/// - `out`        : 追記先。既存内容は消さない（複数の線を 1 バッファへ束ねられる）
///
/// 追記される頂点数は `VERTS_PER_SEGMENT × (有効セグメント数)`。
/// 長さ 0 のセグメント（同じ点が連続）はスキップするため、点数から一意に決まるとは限らない。
/// トポロジは TriangleList、カリングは None を前提とする（裏表どちらからでも見える）。
pub fn expand_polyline_ribbon(
    points: &[[f32; 3]],
    width: f32,
    color: [f32; 4],
    camera_pos: [f32; 3],
    out: &mut Vec<ColorVertex>,
) {
    if points.len() < 2 || width <= 0.0 {
        return;
    }
    let half_width = width * 0.5;
    out.reserve((points.len() - 1) * VERTS_PER_SEGMENT);

    for pair in points.windows(2) {
        let (a, b) = (pair[0], pair[1]);
        // 長さ 0 のセグメントは方向が定まらないので飛ばす。
        let Some(seg_dir) = normalize3(sub3(b, a), DEGENERATE_SEGMENT_EPS) else {
            continue;
        };

        // 端点ごとに視線方向が違うため、オフセットも端点ごとに計算する
        // （長いセグメントを至近距離で見たときの捩れを防ぐ）。
        let off_a = side_offset(seg_dir, a, camera_pos, half_width);
        let off_b = side_offset(seg_dir, b, camera_pos, half_width);

        let a_neg = sub3(a, off_a);
        let a_pos = add3(a, off_a);
        let b_neg = sub3(b, off_b);
        let b_pos = add3(b, off_b);

        // 2 三角形（a-, a+, b-） (b-, a+, b+)。build_thick と同じ頂点並び。
        let v = |position: [f32; 3]| ColorVertex { position, color };
        out.extend_from_slice(&[
            v(a_neg),
            v(a_pos),
            v(b_neg),
            v(b_neg),
            v(a_pos),
            v(b_pos),
        ]);
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 内積（テスト内の直交性検証用）。
    fn dot3(a: [f32; 3], b: [f32; 3]) -> f32 {
        a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
    }

    /// 点が 2 点未満・幅が 0 以下のときは何も追加しないこと。
    #[test]
    fn degenerate_inputs_emit_nothing() {
        let mut out = Vec::new();
        expand_polyline_ribbon(&[], 1.0, [1.0; 4], [0.0; 3], &mut out);
        assert!(out.is_empty(), "空の点列は何も出さない");

        expand_polyline_ribbon(&[[0.0; 3]], 1.0, [1.0; 4], [0.0; 3], &mut out);
        assert!(out.is_empty(), "1 点だけでは線にならない");

        expand_polyline_ribbon(
            &[[0.0; 3], [1.0, 0.0, 0.0]],
            0.0,
            [1.0; 4],
            [0.0; 3],
            &mut out,
        );
        assert!(out.is_empty(), "幅 0 は何も出さない");

        expand_polyline_ribbon(
            &[[0.0; 3], [1.0, 0.0, 0.0]],
            -1.0,
            [1.0; 4],
            [0.0; 3],
            &mut out,
        );
        assert!(out.is_empty(), "負の幅は何も出さない");
    }

    /// セグメント数に比例した頂点数（1 セグメント = 6 頂点）が出ること。
    #[test]
    fn vertex_count_matches_segment_count() {
        let camera = [0.0, 0.0, -10.0];
        for n in 2..=8usize {
            let points: Vec<[f32; 3]> = (0..n).map(|i| [i as f32, 0.0, 0.0]).collect();
            let mut out = Vec::new();
            expand_polyline_ribbon(&points, 0.5, [1.0; 4], camera, &mut out);
            assert_eq!(
                out.len(),
                (n - 1) * VERTS_PER_SEGMENT,
                "{n} 点なら {} セグメント分の頂点が出ること",
                n - 1
            );
        }
    }

    /// 長さ 0 のセグメント（同一点の連続）はスキップされること。
    #[test]
    fn zero_length_segments_are_skipped() {
        let points = [[0.0, 0.0, 0.0], [0.0, 0.0, 0.0], [1.0, 0.0, 0.0]];
        let mut out = Vec::new();
        expand_polyline_ribbon(&points, 0.5, [1.0; 4], [0.0, 0.0, -5.0], &mut out);
        assert_eq!(
            out.len(),
            VERTS_PER_SEGMENT,
            "有効セグメントは 1 本だけ（重複点の区間は捨てる）"
        );
    }

    /// リボンの幅がワールド単位の `width` に一致し、
    /// 幅方向がセグメント方向・視線方向の両方に直交すること。
    #[test]
    fn ribbon_width_and_orientation_are_correct() {
        let width = 0.4;
        let camera = [0.0, 5.0, -3.0];
        // 斜めのセグメント（軸平行だと偶然の直交で検証が甘くなる）。
        let a = [1.0, 2.0, 3.0];
        let b = [4.0, -1.0, 5.0];
        let mut out = Vec::new();
        expand_polyline_ribbon(&[a, b], width, [1.0; 4], camera, &mut out);
        assert_eq!(out.len(), VERTS_PER_SEGMENT);

        // 頂点 0 = a-side、頂点 1 = a+side。両者の差が幅ベクトル。
        let a_neg = out[0].position;
        let a_pos = out[1].position;
        let span = sub3(a_pos, a_neg);
        assert!(
            (length3(span) - width).abs() < 1e-5,
            "幅は width と一致すること: {} vs {width}",
            length3(span)
        );

        // 中点が元の点 a に戻ること（±half_width の対称展開）。
        let mid = scale3(add3(a_neg, a_pos), 0.5);
        for i in 0..3 {
            assert!((mid[i] - a[i]).abs() < 1e-5, "リボンは元の点を中心にする");
        }

        // 幅方向はセグメント方向・視線方向の両方と直交する。
        let seg_dir = normalize3(sub3(b, a), DEGENERATE_SEGMENT_EPS).unwrap();
        let view_dir = normalize3(sub3(a, camera), DEGENERATE_SEGMENT_EPS).unwrap();
        let n = normalize3(span, DEGENERATE_SEGMENT_EPS).unwrap();
        assert!(dot3(n, seg_dir).abs() < 1e-5, "セグメント方向と直交すること");
        assert!(dot3(n, view_dir).abs() < 1e-5, "視線方向と直交すること");
    }

    /// 視線とセグメントが平行（線がカメラを正面から向く）でも
    /// 破綻せず、幅を保った頂点が出ること。
    #[test]
    fn parallel_to_view_does_not_degenerate() {
        let width = 0.25;
        // カメラは原点、線は +Z へ真っ直ぐ = 視線と完全に平行。
        let camera = [0.0, 0.0, 0.0];
        let points = [[0.0, 0.0, 1.0], [0.0, 0.0, 5.0]];
        let mut out = Vec::new();
        expand_polyline_ribbon(&points, width, [1.0; 4], camera, &mut out);
        assert_eq!(out.len(), VERTS_PER_SEGMENT);

        let span = sub3(out[1].position, out[0].position);
        assert!(
            (length3(span) - width).abs() < 1e-5,
            "縮退時もワールド幅を保つこと"
        );
        assert!(
            span.iter().all(|c| c.is_finite()),
            "NaN / Inf を出さないこと"
        );
    }

    // ── パイプライン定義（TOML）の検証 ────────────────────────
    //
    // GPU を必要とせずに TOML の綴り・値だけを検証する。パイプライン生成は
    // 実機起動時にしか走らないため、キー名や列挙値の打ち間違いは
    // このテストが唯一の防御線になる（ビルドは通ってしまう）。

    use crate::engine::core::renderer::pipeline_config::PipelineConfig;

    /// リボン用 TOML（深度あり）が読め、意図どおりの設定になっていること。
    #[test]
    fn ribbon_depth_pipeline_toml_is_valid() {
        let src = include_str!("../../core/renderer/pipelines/line_ribbon_depth.toml");
        let cfg: PipelineConfig = toml::from_str(src).expect("TOML として読めること");
        assert_eq!(cfg.shader_sources, vec!["unlit.wgsl".to_string()]);
        assert_eq!(cfg.vertex_slots, vec!["color_vertex".to_string()]);
        assert_eq!(cfg.topology, "TriangleList", "リボンは三角形リスト");
        assert_eq!(cfg.cull_mode, "None", "裏表どちらからも見えること");
        assert_eq!(cfg.depth_compare, "LessEqual", "不透明物に隠れること");
        assert!(!cfg.depth_write, "線は深度を書かない");
        assert_eq!(cfg.blend, "AlphaBlending", "半透明の線を出せること");
    }

    /// リボン用 TOML（深度なし）が読め、深度比較だけが違うこと。
    #[test]
    fn ribbon_nodepth_pipeline_toml_is_valid() {
        let src = include_str!("../../core/renderer/pipelines/line_ribbon_nodepth.toml");
        let cfg: PipelineConfig = toml::from_str(src).expect("TOML として読めること");
        assert_eq!(cfg.shader_sources, vec!["unlit.wgsl".to_string()]);
        assert_eq!(cfg.vertex_slots, vec!["color_vertex".to_string()]);
        assert_eq!(cfg.topology, "TriangleList");
        assert_eq!(cfg.depth_compare, "Always", "遮蔽を無視して常に最前面");
        assert!(!cfg.depth_write);
        assert_eq!(cfg.blend, "AlphaBlending");
    }

    /// 指定色が全頂点へ入ること、および `out` の既存内容を壊さないこと。
    #[test]
    fn appends_without_clearing_and_applies_color() {
        let color = [0.2, 0.4, 0.6, 0.8];
        let mut out = vec![ColorVertex {
            position: [9.0, 9.0, 9.0],
            color: [0.0; 4],
        }];
        expand_polyline_ribbon(
            &[[0.0; 3], [1.0, 0.0, 0.0]],
            0.1,
            color,
            [0.0, 0.0, -1.0],
            &mut out,
        );
        assert_eq!(out.len(), 1 + VERTS_PER_SEGMENT, "既存要素は保持される");
        assert_eq!(out[0].position, [9.0, 9.0, 9.0], "先頭の既存頂点は不変");
        for v in &out[1..] {
            assert_eq!(v.color, color, "追加頂点には指定色が入る");
        }
    }
}
