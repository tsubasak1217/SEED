// ============================================================
//  canvas_text_bounds.rs — TextComponent の表示枠（キャンバス px）マップ
//
//  【役割】
//  2D 編集でテキストを「掴める・枠が出る」ようにするための寸法供給層。
//  `CanvasTextRenderer`（＝描画に使うフォント実体）でテキストを実測し、
//  Text スロット entity → ローカル境界矩形 の対応表を作る。
//
//  【なぜ表にするのか】
//  実測にはフォントレジストリ（`&mut CanvasTextRenderer`）が要る一方、
//  ピック走査（pick_2d）・枠描画（canvas_collect）はシーンを不変借用したまま走る。
//  借用の衝突を避けるため、走査に入る**前に**一度だけ測って表を渡す。
//  こうすると測定は 1 フレーム/1 ピックあたり 1 回で済み、走査側は表引きだけになる。
//
//  【座標系】
//  値はアクターのキャンバスローカル px（原点 = アクター位置、X 右・Y 下）。
//
//  【pivot の扱い（重要）】
//  枠あり（`box_width > 0`）のテキストは pivot が Sprite と同じ意味を持つ。
//  描画側は「pivot 無しの行列 + グリフ座標の平行移動」で実現しているので、
//  ここで返す矩形も**同じ平行移動を適用済み**にし、
//  `zero_pivot = true` を立てて「行列は `to_mesh_mat4_no_pivot` を使え」と伝える。
//  枠なしは従来どおり（pivot は行列側でも無効なので何もしない）。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{CanvasTransform, ComponentKind, TextComponent};
use crate::engine::core::font::text_layout::{TextLayoutSpec, TextLocalBox};
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;

use super::App;

/// 1 つの Text スロットの実測結果。
#[derive(Clone, Copy, Debug)]
pub(super) struct TextBounds {
    /// ローカル境界矩形（枠ありのときは pivot ぶんの平行移動を**適用済み**）。
    pub local: TextLocalBox,
    /// 変換行列に pivot を効かせてはいけないか（＝枠モードか）。
    ///
    /// `true` の呼び出し側は `CanvasTransform::to_mesh_mat4_no_pivot` を使うこと。
    pub zero_pivot: bool,
}

/// Text スロット entity → テキストの実測結果。
pub(super) type TextBoundsMap = HashMap<Entity, TextBounds>;

/// 実測に必要な 1 スロットぶんのパラメータ（シーン借用を閉じるための中間表現）。
struct TextMeasureReq {
    slot_entity: Entity,
    content: String,
    font_path: String,
    /// レイアウト条件（サイズ・整列・縁取り・枠・折り返し）。
    spec: TextLayoutSpec,
    /// 所属アクターの正規化ピボット（枠ありのときだけ効く）。
    pivot: [f32; 2],
}

impl App {
    /// アクティブ世界線の全 TextComponent スロットを実測し、境界矩形の表を返す。
    ///
    /// テキスト描画器（`canvas_text`）が未初期化の場合は空の表を返す
    /// （＝テキストはピックも枠表示もされない。描画もされていない状態と一致する）。
    pub(super) fn build_text_bounds_map(&mut self) -> TextBoundsMap {
        // ── 1. パラメータ収集（ここでシーンの不変借用は閉じる）──
        let mut reqs: Vec<TextMeasureReq> = Vec::new();
        if let Some(scene) = self.scene.as_ref() {
            let wl = self.active_world_line;
            for actor in scene.actors.iter().filter(|a| a.world_line == wl) {
                collect_text_reqs(actor, &scene.world, &mut reqs);
            }
        }
        // ── 2. 実測（フォントレジストリの可変借用が要る）──
        let mut map = TextBoundsMap::new();
        let Some(renderer) = self.canvas_text.as_mut() else {
            return map;
        };
        for r in reqs {
            let Some((bx, pivot_size)) = renderer.resolve_bounds(&r.content, &r.spec, &r.font_path)
            else {
                continue;
            };
            // 枠ありのみ pivot を矩形へ焼き込む（描画側の平行移動と同じ式）。
            let (dx, dy) = if r.spec.has_box() {
                (-r.pivot[0] * pivot_size[0], -r.pivot[1] * pivot_size[1])
            } else {
                (0.0, 0.0)
            };
            map.insert(
                r.slot_entity,
                TextBounds {
                    local: TextLocalBox {
                        min: [bx.min[0] + dx, bx.min[1] + dy],
                        max: [bx.max[0] + dx, bx.max[1] + dy],
                    },
                    zero_pivot: r.spec.has_box(),
                },
            );
        }
        map
    }
}

/// アクターとその子孫の Text スロットを再帰的に集める。
///
/// 無効スロット（`enabled = false`）も集める。エディタでは非表示のものも
/// 選択できる規約（`PickFilter2d::EDITOR_SELECT`）に合わせ、
/// 「表示中のものだけ拾う」判定は走査側のフィルタに任せる。
fn collect_text_reqs(
    actor: &Actor,
    world: &crate::engine::ecs::World,
    out: &mut Vec<TextMeasureReq>,
) {
    // pivot はアクターのルートに直付けされた CanvasTransform が持つ。
    let pivot = world
        .get::<CanvasTransform>(actor.entity)
        .map(|ct| ct.pivot)
        .unwrap_or([0.0, 0.0]);
    for slot in actor.slots() {
        if slot.kind != ComponentKind::Text {
            continue;
        }
        let Some(tc) = world.get::<TextComponent>(slot.entity) else {
            continue;
        };
        out.push(TextMeasureReq {
            slot_entity: slot.entity,
            content: tc.content.clone(),
            font_path: tc.font_path.clone(),
            spec: TextLayoutSpec {
                font_size: tc.font_size,
                line_spacing: tc.line_spacing,
                align: tc.align,
                vertical_align: tc.vertical_align,
                outline_width: tc.outline_width,
                box_width: tc.box_width,
                box_height: tc.box_height,
                wrap: tc.wrap,
            },
            pivot,
        });
    }
    for child in actor.children() {
        collect_text_reqs(child, world, out);
    }
}
