// ============================================================
//  registry.rs — プロパティレジストリ（唯一の拡張点）
//
//  【役割】
//  (component, property) 文字列ペアを ECS World への型付き get/set へ橋渡しする。
//  新しくアニメーション可能なプロパティを増やす場合は、ここに 1 エントリ足すだけでよい。
//
//  【設計方針】
//  トレイトオブジェクトは使わず enum PropBinding + match で素直に実装する。
//  - resolve_binding: 未知の component/property は None（呼び出し側で警告してトラック無視）。
//  - apply_write: 値型はサンプル時に track.value_type で保証されるため、
//    ここでは束縛が期待する型のみ処理し、想定外は握りつぶす（クラッシュさせない）。
//
//  【instance_mats 同期】
//  actor_transform（3D の ModelComponent 描画）は instance_mats を描画ソースとするため、
//  Transform 書き換え後に対象アクターの全 Model スロットの instance_mats[0] を
//  再計算し、バッチを dirty 化する（apply_physics_transform と同一方針）。
//  canvas_transform / sprite は毎フレーム World から直接収集されるため同期不要。
// ============================================================

use crate::engine::components::{
    Transform, CanvasTransform, SpriteComponent, ModelComponent, ComponentKind,
};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::clip::{AnimValue, ValueType};

// ─── PropBinding ─────────────────────────────────────────────

/// アニメーション書き込み先の束縛（コンポーネント×プロパティ）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PropBinding {
    /// ルート Transform の位置（vec3）
    ActorTransformPosition,
    /// ルート Transform の回転（vec3 = YXZ オイラー角・度）
    ActorTransformRotation,
    /// ルート Transform のスケール（vec3）
    ActorTransformScale,
    /// ルート CanvasTransform の位置（vec2）
    CanvasTransformPosition,
    /// ルート CanvasTransform の回転（float = Z 回転・度）
    CanvasTransformRotation,
    /// ルート CanvasTransform のスケール（vec2）
    CanvasTransformScale,
    /// SpriteComponent のカラー（color = [f32;4]）— 最初の有効な Sprite スロット
    SpriteColor,
}

impl PropBinding {
    /// この束縛が期待する値型を返す（トラックの value_type 検証に使う）。
    pub fn expected_value_type(self) -> ValueType {
        match self {
            PropBinding::ActorTransformPosition
            | PropBinding::ActorTransformRotation
            | PropBinding::ActorTransformScale => ValueType::Vec3,
            PropBinding::CanvasTransformPosition
            | PropBinding::CanvasTransformScale => ValueType::Vec2,
            PropBinding::CanvasTransformRotation => ValueType::Float,
            PropBinding::SpriteColor => ValueType::Color,
        }
    }
}

/// (component, property) を束縛へ解決する。未知なら None。
///
/// P1 対応プロパティ:
/// - actor_transform: position / rotation / scale （すべて vec3）
/// - canvas_transform: position(vec2) / rotation(float) / scale(vec2)
/// - sprite: color(color)
pub fn resolve_binding(component: &str, property: &str) -> Option<PropBinding> {
    match (component, property) {
        ("actor_transform", "position")  => Some(PropBinding::ActorTransformPosition),
        ("actor_transform", "rotation")  => Some(PropBinding::ActorTransformRotation),
        ("actor_transform", "scale")     => Some(PropBinding::ActorTransformScale),
        ("canvas_transform", "position") => Some(PropBinding::CanvasTransformPosition),
        ("canvas_transform", "rotation") => Some(PropBinding::CanvasTransformRotation),
        ("canvas_transform", "scale")    => Some(PropBinding::CanvasTransformScale),
        ("sprite", "color")              => Some(PropBinding::SpriteColor),
        _ => None,
    }
}

// ─── 書き込み ────────────────────────────────────────────────

/// 束縛と値を対象アクターへ書き込む。
///
/// - `target`: 適用先アクター（actor_path 解決済み）。ルート Transform/CanvasTransform は
///   target.entity に、Sprite スロットは target.slots() のスロット entity に格納される。
/// - `world`: ECS World（`target` は scene.actors、`world` は scene.world と別フィールドのため同時借用可）。
pub fn apply_write(world: &mut World, target: &Actor, binding: PropBinding, value: &AnimValue) {
    match binding {
        // ── 3D Transform 系（書き込み後 instance_mats を同期）──
        PropBinding::ActorTransformPosition => {
            if let AnimValue::Vec3(v) = value {
                let changed = if let Some(tf) = world.get_mut::<Transform>(target.entity) {
                    tf.position = *v; true
                } else { false };
                if changed { sync_model_instance_mats(world, target); }
            }
        }
        PropBinding::ActorTransformRotation => {
            if let AnimValue::Vec3(v) = value {
                let changed = if let Some(tf) = world.get_mut::<Transform>(target.entity) {
                    tf.rotation = *v; true
                } else { false };
                if changed { sync_model_instance_mats(world, target); }
            }
        }
        PropBinding::ActorTransformScale => {
            if let AnimValue::Vec3(v) = value {
                let changed = if let Some(tf) = world.get_mut::<Transform>(target.entity) {
                    tf.scale = *v; true
                } else { false };
                if changed { sync_model_instance_mats(world, target); }
            }
        }
        // ── 2D CanvasTransform 系（同期不要）──
        PropBinding::CanvasTransformPosition => {
            if let AnimValue::Vec2(v) = value {
                if let Some(ct) = world.get_mut::<CanvasTransform>(target.entity) { ct.position = *v; }
            }
        }
        PropBinding::CanvasTransformRotation => {
            if let AnimValue::Float(v) = value {
                if let Some(ct) = world.get_mut::<CanvasTransform>(target.entity) { ct.rotation = *v; }
            }
        }
        PropBinding::CanvasTransformScale => {
            if let AnimValue::Vec2(v) = value {
                if let Some(ct) = world.get_mut::<CanvasTransform>(target.entity) { ct.scale = *v; }
            }
        }
        // ── Sprite カラー（同期不要）──
        PropBinding::SpriteColor => {
            if let AnimValue::Color(v) = value {
                // 最初の Sprite スロットへ書き込む
                if let Some(slot_entity) = target.slots().iter()
                    .find(|s| s.kind == ComponentKind::Sprite)
                    .map(|s| s.entity)
                {
                    if let Some(sp) = world.get_mut::<SpriteComponent>(slot_entity) { sp.color = *v; }
                }
            }
        }
    }
}

// ─── 読み取り（Edit プレビューの元値退避用）───────────────────

/// 束縛の現在値を対象アクターから読み取る。apply_write の逆方向。
///
/// Edit モードのアニメーションプレビュー（ANIM_PREVIEW）で、プレビュー適用前の
/// 元値を退避するために使う。値が読み取れない（対象コンポーネントが無い等）場合は None。
pub fn read_binding(world: &World, target: &Actor, binding: PropBinding) -> Option<AnimValue> {
    match binding {
        PropBinding::ActorTransformPosition =>
            world.get::<Transform>(target.entity).map(|tf| AnimValue::Vec3(tf.position)),
        PropBinding::ActorTransformRotation =>
            world.get::<Transform>(target.entity).map(|tf| AnimValue::Vec3(tf.rotation)),
        PropBinding::ActorTransformScale =>
            world.get::<Transform>(target.entity).map(|tf| AnimValue::Vec3(tf.scale)),
        PropBinding::CanvasTransformPosition =>
            world.get::<CanvasTransform>(target.entity).map(|ct| AnimValue::Vec2(ct.position)),
        PropBinding::CanvasTransformRotation =>
            world.get::<CanvasTransform>(target.entity).map(|ct| AnimValue::Float(ct.rotation)),
        PropBinding::CanvasTransformScale =>
            world.get::<CanvasTransform>(target.entity).map(|ct| AnimValue::Vec2(ct.scale)),
        // apply_write と同じスロット選択規則（最初の Sprite スロット）で読む。
        // 退避と復元の対象を一致させるため必須。
        PropBinding::SpriteColor => {
            target.slots().iter()
                .find(|s| s.kind == ComponentKind::Sprite)
                .and_then(|s| world.get::<SpriteComponent>(s.entity))
                .map(|sp| AnimValue::Color(sp.color))
        }
    }
}

/// Transform 書き換え後に、対象アクターの全 Model スロットの
/// instance_mats[0] を新しい TRS 行列へ同期し、バッチを dirty 化する。
///
/// レンダラーは ModelComponent.instance_mats を描画ソースとするため、
/// Transform を変えただけでは描画へ反映されない（apply_physics_transform と同方針）。
fn sync_model_instance_mats(world: &mut World, target: &Actor) {
    // 新しい TRS 行列を Transform から計算する
    let new_mat = match world.get::<Transform>(target.entity) {
        Some(tf) => tf.to_mat4(),
        None => return,
    };
    // 対象アクターの全 Model スロットに反映する
    let model_slots: Vec<_> = target.slots().iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .map(|s| s.entity)
        .collect();
    for slot_entity in model_slots {
        if let Some(mc) = world.get_mut::<ModelComponent>(slot_entity) {
            if let Some(m) = mc.instance_mats.first_mut() {
                *m = new_mat;
            }
            if let Some(batch) = mc.instanced_batch.as_mut() {
                batch.mark_dirty();
            }
        }
    }
}
