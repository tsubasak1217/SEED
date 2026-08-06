using System.Collections.Generic;

namespace SEEDEditor.Controls;

/// <summary>
/// コンポーネントの数値フィールドのうち、**値域が確定しているもの**の [min..max] 表。
///
/// 【この表の役割】
/// インスペクタが「その行をスライダーにしてよいか」を判断する唯一の場所。
/// 表に載っていれば範囲スライダー行、載っていなければ素の数値入力行になる
/// （<see cref="SEEDEditor.Panels.InspectorPanel"/> の共通入口が参照する）。
///
/// 【値域の正典はランタイム側の clamp】
/// 表の値は Rust ランタイムが `SET_*_FIELD` 受信時に掛けている clamp と一致させる。
/// UI 側で勝手に狭めるとランタイムが受け付ける値に手が届かなくなり、
/// 広げるとスライダーの端が「動かせるのに反映されない死に領域」になる。
/// そのため各エントリには **どのファイルのどの clamp が根拠か** をコメントで書く。
/// 水（WaterVolumeComponent）の 0..1 エントリについては Rust 側の契約テスト
/// （water_ops.rs の tests::table_and_clamp_are_in_sync_*）が双方向に固定している。
///
/// 【載せない基準】
/// ・`v.max(0.0)` のように **下限しか無い**もの（上限が無いのでスライダーにできない）
/// ・下限/上限はあるが実用域が端に張り付くもの
///   （例: WaterVolumeComponent.ripple_damping は 0.01..100 だが実用域は 1 付近で、
///     線形スライダーにすると使い物にならないため数値行のままにする）
/// ・整数の個数・容量（例: ParticleEmitterComponent.max_particles は 1..65536 だが
///   スライダーで刻む値ではないため載せない）
/// ・キー名は **Rust の `*ComponentData` の serde フィールド名**。
///   `SET_*_FIELD` のキー名とは別体系なので混同しないこと
///   （例: ライトの角度は SET キー `inner_angle` / serde 名 `inner_angle_deg`）。
/// </summary>
internal static class ComponentFieldRanges
{
    /// <summary>キーの区切り（"{コンポーネント型名}.{serde フィールド名}"）。</summary>
    private const char KeySeparator = '.';

    /// <summary>
    /// 値域表。キーは "{コンポーネント型名}.{serde フィールド名}"、値は (最小, 最大)。
    /// コンポーネント型名は ACTOR_COMPONENTS の `type`（例 "WaterVolumeComponent"）と同じ綴り。
    /// </summary>
    private static readonly Dictionary<string, (float Min, float Max)> Ranges = new()
    {
        // ── WaterVolumeComponent ────────────────────────────────
        // 根拠: runtime/src/engine/core/app_base/app/water_ops.rs
        //       `v.clamp(NORMALIZED_MIN, NORMALIZED_MAX)`（NORMALIZED_MIN=0.0 / NORMALIZED_MAX=1.0）
        ["WaterVolumeComponent.surface_opacity"]      = (0f, 1f),
        ["WaterVolumeComponent.foam_intensity"]       = (0f, 1f),
        ["WaterVolumeComponent.fresnel_strength"]     = (0f, 1f),
        ["WaterVolumeComponent.reflection_roughness"] = (0f, 1f),
        ["WaterVolumeComponent.shore_wave_foam"]      = (0f, 1f),
        // 粘度だけは別の定数対で clamp される。
        // 根拠: water_ops.rs `v.clamp(VISCOSITY_MIN, VISCOSITY_MAX)`
        //       （VISCOSITY_MIN=0.0 / VISCOSITY_MAX=1.0。定義は
        //         runtime/src/engine/interaction/water_physics.rs）
        ["WaterVolumeComponent.viscosity"]            = (0f, 1f),

        // ── WaterLinkComponent ──────────────────────────────────
        // 根拠: runtime/src/engine/core/app_base/app/water_link_ops.rs
        //       `v.clamp(OPENNESS_MIN, OPENNESS_MAX)`（OPENNESS_MIN=0.0 / OPENNESS_MAX=1.0）
        ["WaterLinkComponent.openness"] = (0f, 1f),

        // ── InteractionSourceComponent ──────────────────────────
        // 根拠: runtime/src/engine/core/app_base/app/interaction_ops.rs
        //       `v.clamp(NORMALIZED_MIN, NORMALIZED_MAX)`（0.0 / 1.0）
        ["InteractionSourceComponent.strength"] = (0f, 1f),

        // ── CoverEmitterComponent（I3.1）─────────────────────────
        // 根拠: runtime/src/engine/core/app_base/app/cover_emitter_ops.rs
        //       extents_* / mask_size_* は `v.max(EXTENT_MIN)`、fade は `v.max(FADE_MIN)`、
        //       strength は `v.max(STRENGTH_MIN)`。いずれも上限は無いが、
        //       スライダーで扱える実用域をここで決める（手入力は範囲外も通る）。
        ["CoverEmitterComponent.fade"] = (0f, 16f),
        // 強度は「量/秒」。1.0 = 1 秒で満量。0.05〜2 が実用域なので上限 2 とする。
        ["CoverEmitterComponent.strength"] = (0f, 2f),

        // ── ParticleEmitterComponent ────────────────────────────
        // 根拠: runtime/src/engine/core/app_base/app/particle_ops.rs
        //       "direction_randomness" => `v.clamp(0.0, 1.0)`
        ["ParticleEmitterComponent.direction_randomness"] = (0f, 1f),

        // ── AudioComponent ──────────────────────────────────────
        // 根拠: runtime/src/engine/core/app_base/app/audio_ops.rs
        //       "pan" => `v.clamp(-1.0, 1.0)`（-1=左 / 0=中央 / +1=右）
        ["AudioComponent.pan"] = (-1f, 1f),

        // ── LightComponent ──────────────────────────────────────
        // 根拠: runtime/src/engine/core/app_base/app/light_ops.rs
        //       "inner_angle" / "outer_angle" => `v.clamp(0.0, 89.0)`（半頂角の度数）
        //       ※ SET キーは inner_angle / outer_angle だが serde 名は *_deg。
        ["LightComponent.inner_angle_deg"] = (0f, 89f),
        ["LightComponent.outer_angle_deg"] = (0f, 89f),

        // ── CameraComponent ─────────────────────────────────────
        // 根拠: runtime/src/engine/core/app_base/app/camera_component_ops.rs
        //       `value.clamp(1.0, 179.0)`（垂直画角の度数）
        // ※ カメラ行の ⟲ 対応は第2バッチだが、値域の正典はこの表に一本化するため先に載せる。
        ["CameraComponent.fov_y_deg"] = (1f, 179f),
    };

    /// <summary>
    /// 指定コンポーネント型・serde フィールド名の値域を引く。
    /// 表に無ければ false（＝スライダーにせず素の数値入力行にする）。
    /// </summary>
    /// <param name="componentType">コンポーネント型名（例 "WaterVolumeComponent"）。</param>
    /// <param name="field">Rust の serde フィールド名（例 "surface_opacity"）。</param>
    /// <param name="range">見つかった値域（見つからない場合は既定値）。</param>
    public static bool TryGet(string componentType, string field, out (float Min, float Max) range)
        => Ranges.TryGetValue(componentType + KeySeparator + field, out range);
}
