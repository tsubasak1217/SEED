// ============================================================
//  line_renderer_ops.rs — LineRendererComponent のインスペクタ編集と
//                          シーン走査 → リボン頂点への収集
//
//  ・handle_set_line_renderer_field: インスペクタ（C#）からの
//    SET_LINE_RENDERER_FIELD IPC を受けてフィールドを更新する
//    （AudioComponent / LightComponent と同流儀）。
//  ・collect_line_ribbons: シーンの LineRenderer スロットを走査し、
//    点列をワールド空間へ写してカメラ向きリボン頂点へ展開する。
//
//  【描画ロジックはここに置かない】
//  頂点展開そのものは methods/drawer/line_ribbon.rs の純関数
//  （expand_polyline_ribbon）が持つ。ここは「シーンから何を拾って
//  どの空間へ写すか」だけを担当する。
//
//  【座標系】
//  SEED の Transform はワールド空間で保持され、親子行列合成は存在しない
//  （core/transform_sync.rs 参照）。したがって local_space=true の点列は
//  「そのアクター自身の Transform 行列」を掛けるだけでワールドになる。
// ============================================================

use crate::engine::components::{ComponentKind, LineRendererComponent, Transform};
use crate::engine::ecs::World;
use crate::engine::methods::drawer::{expand_polyline_ribbon, ColorVertex};
use crate::engine::structs::objects::Actor;

use super::App;

// ─── 値の下限 ─────────────────────────────────────────────────

/// 線の太さの下限（ワールド単位）。0 以下は「描かない」を意味するため、
/// インスペクタからの入力はここでクランプして負値を弾く。
const MIN_LINE_WIDTH: f32 = 0.0;

/// 色成分の下限・上限（RGBA。リニア色だが UI からは 0..1 を想定）。
const COLOR_MIN: f32 = 0.0;
/// 色成分の上限。
const COLOR_MAX: f32 = 1.0;

/// 色文字列のフィールド数（"r,g,b,a"）。
const COLOR_COMPONENTS: usize = 4;

// ─── インスペクタからのフィールド編集 ─────────────────────────

impl App {
    /// インスペクタからの LineRendererComponent フィールド更新
    /// （SET_LINE_RENDERER_FIELD IPC）。
    ///
    /// key: width / color / local_space / depth_test / visible。
    /// color は "r,g,b,a" 形式。points はスクリプト駆動が前提のため
    /// インスペクタからは編集しない（件数表示のみ）。
    /// 不正な key・value は無視する。
    pub(super) fn handle_set_line_renderer_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        key: &str,
        value: &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_light_field と同流儀）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::LineRenderer)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(lr) = scene.world.get_mut::<LineRendererComponent>(entity) else {
            return;
        };

        // key ごとに値を解釈して反映する（パース失敗は無視）。
        match key {
            "width" => {
                if let Ok(v) = value.parse::<f32>() {
                    lr.width = v.max(MIN_LINE_WIDTH);
                }
            }
            "color" => {
                // "r,g,b,a"（リニア）をパースする。要素数が違えば無視。
                let parts: Vec<&str> = value.split(',').collect();
                if parts.len() == COLOR_COMPONENTS {
                    let mut rgba = [0.0f32; COLOR_COMPONENTS];
                    let mut ok = true;
                    for (dst, src) in rgba.iter_mut().zip(parts.iter()) {
                        match src.trim().parse::<f32>() {
                            Ok(v) => *dst = v.clamp(COLOR_MIN, COLOR_MAX),
                            Err(_) => ok = false,
                        }
                    }
                    if ok {
                        lr.color = rgba;
                    }
                }
            }
            // bool 系はインスペクタから "1"/"0" で届く。
            "local_space" => lr.local_space = value == "1",
            "depth_test" => lr.depth_test = value == "1",
            "visible" => lr.visible = value == "1",
            _ => {}
        }
    }
}

// ─── シーン走査 → リボン頂点収集 ──────────────────────────────

/// 1 フレーム分の LineRenderer 描画頂点。深度テストの有無で 2 本に分ける。
///
/// 深度あり／なしは別パイプラインになるため、頂点段階で仕分けしておくと
/// 描画は最大 2 ドローコールで済む（線の本数に比例しない）。
#[derive(Default)]
pub(crate) struct LineRibbonVertices {
    /// depth_test = true の線（不透明物に隠れる）。
    pub depth_tested: Vec<ColorVertex>,
    /// depth_test = false の線（常に最前面）。
    pub always_on_top: Vec<ColorVertex>,
}

impl LineRibbonVertices {
    /// 描画すべき頂点が 1 つも無いか。
    pub fn is_empty(&self) -> bool {
        self.depth_tested.is_empty() && self.always_on_top.is_empty()
    }
}

/// アクターツリーを走査し、全 LineRenderer スロットをリボン頂点へ展開する。
///
/// `camera_pos` はリボンの向きを決めるカメラのワールド座標。
/// Play / Edit の区別はしない（エディタの編集中も線が見える＝ゲーム内オブジェクト
/// としての正しい振る舞い。ギズモのようなエディタ専用表示ではない）。
pub(crate) fn collect_line_ribbons(
    actors: &[Actor],
    world: &World,
    wl: u32,
    camera_pos: [f32; 3],
) -> LineRibbonVertices {
    let mut out = LineRibbonVertices::default();
    collect_recursive(actors, world, wl, camera_pos, &mut out);
    out
}

/// `collect_line_ribbons` の再帰実装。
///
/// 非アクティブなアクターはサブツリーごと除外し、enabled=false のスロットも飛ばす
/// （他のコンポーネント収集＝collect_gpu_lights と同じ扱い）。
fn collect_recursive(
    actors: &[Actor],
    world: &World,
    wl: u32,
    camera_pos: [f32; 3],
    out: &mut LineRibbonVertices,
) {
    for actor in actors {
        if actor.world_line != wl || !actor.active {
            continue;
        }

        for slot in actor.slots() {
            if slot.kind != ComponentKind::LineRenderer || !slot.enabled {
                continue;
            }
            let Some(lr) = world.get::<LineRendererComponent>(slot.entity) else {
                continue;
            };
            if !lr.visible || lr.width <= 0.0 || lr.points.len() < 2 {
                continue;
            }

            // 点列をワールド空間へ写す。local_space=false ならそのまま使う
            // （余計なコピーを避けるため Cow 相当の分岐にする）。
            let world_points: Vec<[f32; 3]> = if lr.local_space {
                let m = world
                    .get::<Transform>(actor.entity)
                    .map(|t| t.to_mat4())
                    .unwrap_or(IDENTITY_MAT4);
                lr.points.iter().map(|p| transform_point(m, *p)).collect()
            } else {
                lr.points.clone()
            };

            let dst = if lr.depth_test {
                &mut out.depth_tested
            } else {
                &mut out.always_on_top
            };
            expand_polyline_ribbon(&world_points, lr.width, lr.color, camera_pos, dst);
        }

        // 子アクターを再帰処理（アクティブなアクターのみ到達する）。
        collect_recursive(actor.children(), world, wl, camera_pos, out);
    }
}

/// 単位行列（Transform を持たないアクター向けの中立値）。
const IDENTITY_MAT4: [[f32; 4]; 4] = [
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
];

/// 行優先 4x4 行列で点（w=1）を変換する。
///
/// `Transform::to_mat4()` は行優先（GPU 慣習）で `m[row][col]`、
/// 平行移動が `m[0..3][3]` に入る形なので、行ベクトルではなく列ベクトル規約で掛ける。
#[inline]
fn transform_point(m: [[f32; 4]; 4], p: [f32; 3]) -> [f32; 3] {
    [
        m[0][0] * p[0] + m[0][1] * p[1] + m[0][2] * p[2] + m[0][3],
        m[1][0] * p[0] + m[1][1] * p[1] + m[1][2] * p[2] + m[1][3],
        m[2][0] * p[0] + m[2][1] * p[1] + m[2][2] * p[2] + m[2][3],
    ]
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::methods::drawer::line_ribbon::VERTS_PER_SEGMENT;

    /// `transform_point` が Transform の平行移動・スケール・回転を正しく適用すること
    /// （local_space=true の点列がワールドへ写る経路の検証）。
    #[test]
    fn transform_point_applies_translation_and_scale() {
        let tf = Transform {
            position: [10.0, 20.0, 30.0],
            rotation: [0.0, 0.0, 0.0],
            scale: [2.0, 3.0, 4.0],
        };
        let p = transform_point(tf.to_mat4(), [1.0, 1.0, 1.0]);
        assert!((p[0] - 12.0).abs() < 1e-5);
        assert!((p[1] - 23.0).abs() < 1e-5);
        assert!((p[2] - 34.0).abs() < 1e-5);
    }

    /// 回転 90 度（Y 軸）で +X の点が +Z 側／-Z 側のどちらであれ、
    /// 原点からの距離が保たれること（回転行列が正しく掛かっている証拠）。
    #[test]
    fn transform_point_preserves_length_under_rotation() {
        let tf = Transform {
            position: [0.0, 0.0, 0.0],
            rotation: [0.0, 90.0, 0.0],
            scale: [1.0, 1.0, 1.0],
        };
        let p = transform_point(tf.to_mat4(), [1.0, 0.0, 0.0]);
        let len = (p[0] * p[0] + p[1] * p[1] + p[2] * p[2]).sqrt();
        assert!((len - 1.0).abs() < 1e-5, "回転で長さは変わらない: {len}");
        assert!(p[0].abs() < 1e-5, "+X は 90 度回転で X 成分がほぼ 0 になる");
    }

    /// 単位行列では点がそのまま返ること（Transform を持たないアクターの経路）。
    #[test]
    fn identity_matrix_is_neutral() {
        let p = transform_point(IDENTITY_MAT4, [1.5, -2.5, 3.5]);
        assert_eq!(p, [1.5, -2.5, 3.5]);
    }

    // ── カテナリー（釣り糸のたわみ）式の契約テスト ────────────────
    //
    // 実装本体は C# 側 `scripting/src/Api/LineHelper.cs` の
    // `LineHelper.Catenary`（純 C#。エンジンへの FFI は無い）にある。
    // C# 側にテストプロジェクトが無いため、**同一の式**をここに写して
    // 「端点一致」「中央のたわみ量」「左右対称」という契約を Rust 側で検証する。
    // 形状定数はどちらも 2.0。片方だけ変更したらこのテストが落ちるよう、
    // 値と式を C# の実装と一字一句そろえてあること。

    /// カテナリー形状定数（C# 側 `LineHelper.CatenaryShape` と同値）。
    const CATENARY_SHAPE: f64 = 2.0;

    /// `LineHelper.Catenary` と同じ式で点列を作る（検証用の写し）。
    fn catenary_points(
        start: [f32; 3],
        end: [f32; 3],
        slack: f32,
        segments: usize,
    ) -> Vec<[f32; 3]> {
        let sag = slack.max(0.0);
        let cosh_a = CATENARY_SHAPE.cosh();
        let denom = cosh_a - 1.0;
        let mut out = Vec::with_capacity(segments + 1);
        for i in 0..=segments {
            if i == 0 {
                out.push(start);
                continue;
            }
            if i == segments {
                out.push(end);
                continue;
            }
            let t = i as f32 / segments as f32;
            let base = [
                start[0] + (end[0] - start[0]) * t,
                start[1] + (end[1] - start[1]) * t,
                start[2] + (end[2] - start[2]) * t,
            ];
            let shaped =
                ((CATENARY_SHAPE * (2.0 * t as f64 - 1.0)).cosh() - cosh_a) / denom;
            out.push([base[0], base[1] + (shaped * sag as f64) as f32, base[2]]);
        }
        out
    }

    /// 端点が厳密に始点・終点と一致し、点数が segments + 1 になること。
    #[test]
    fn catenary_endpoints_match_exactly() {
        let start = [0.0, 5.0, 0.0];
        let end = [4.0, 3.0, 1.0];
        for segments in [1usize, 2, 8, 33] {
            let pts = catenary_points(start, end, 1.5, segments);
            assert_eq!(pts.len(), segments + 1, "点数は segments + 1");
            assert_eq!(pts[0], start, "始点は厳密一致（竿先から浮かない）");
            assert_eq!(pts[segments], end, "終点は厳密一致（ウキから浮かない）");
        }
    }

    /// 中央のたわみがちょうど slack ぶん下がること（正規化係数が -1 になる）。
    #[test]
    fn catenary_mid_sag_equals_slack() {
        let slack = 2.0f32;
        // 水平な糸（両端の高さが同じ）で中点の落差を測る。
        let pts = catenary_points([0.0, 10.0, 0.0], [8.0, 10.0, 0.0], slack, 8);
        let mid = pts[4];
        assert!(
            (mid[1] - (10.0 - slack)).abs() < 1e-4,
            "中央は slack ぶん下がること: {}",
            mid[1]
        );
    }

    /// 左右対称であること（t と 1-t のたわみが等しい）。
    #[test]
    fn catenary_is_symmetric() {
        let segments = 10usize;
        let pts = catenary_points([0.0, 0.0, 0.0], [10.0, 0.0, 0.0], 1.0, segments);
        for i in 0..=segments {
            let j = segments - i;
            assert!(
                (pts[i][1] - pts[j][1]).abs() < 1e-5,
                "t={i} と t={j} のたわみが一致すること"
            );
        }
    }

    /// slack = 0（および負値）では直線になること。
    #[test]
    fn catenary_without_slack_is_straight() {
        for slack in [0.0f32, -3.0] {
            let pts = catenary_points([0.0, 0.0, 0.0], [6.0, 0.0, 0.0], slack, 6);
            for (i, p) in pts.iter().enumerate() {
                assert!(p[1].abs() < 1e-6, "slack={slack} なら直線: index {i}");
            }
        }
    }

    /// 深度あり／なしの仕分けバッファが独立していること（`is_empty` の判定含む）。
    #[test]
    fn ribbon_vertices_split_by_depth_flag() {
        let mut v = LineRibbonVertices::default();
        assert!(v.is_empty());
        expand_polyline_ribbon(
            &[[0.0, 0.0, 0.0], [1.0, 0.0, 0.0]],
            0.1,
            [1.0; 4],
            [0.0, 0.0, -1.0],
            &mut v.always_on_top,
        );
        assert!(!v.is_empty());
        assert!(v.depth_tested.is_empty(), "深度ありバッファは触られていない");
        assert_eq!(v.always_on_top.len(), VERTS_PER_SEGMENT);
    }
}
