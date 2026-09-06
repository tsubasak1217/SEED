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
//  スプライトと同じ `to_mesh_mat4` チェーンでキャンバス空間へ写して使う。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{ComponentKind, TextComponent};
use crate::engine::core::font::text_layout::TextLocalBox;
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;

use super::App;

/// Text スロット entity → テキストのローカル境界矩形。
pub(super) type TextBoundsMap = HashMap<Entity, TextLocalBox>;

/// 実測に必要な 1 スロットぶんのパラメータ（シーン借用を閉じるための中間表現）。
struct TextMeasureReq {
    slot_entity: Entity,
    content: String,
    font_size: f32,
    line_spacing: f32,
    align: crate::engine::components::TextAlign,
    vertical_align: crate::engine::components::TextVerticalAlign,
    font_path: String,
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
            if let Some(bx) = renderer.measure_text_box(
                &r.content,
                r.font_size,
                r.line_spacing,
                r.align,
                r.vertical_align,
                &r.font_path,
            ) {
                map.insert(r.slot_entity, bx);
            }
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
            font_size: tc.font_size,
            line_spacing: tc.line_spacing,
            align: tc.align,
            vertical_align: tc.vertical_align,
            font_path: tc.font_path.clone(),
        });
    }
    for child in actor.children() {
        collect_text_reqs(child, world, out);
    }
}
