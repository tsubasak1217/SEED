// ============================================================
//  component_reset_ops.rs — インスペクタの「⟲ デフォルトに戻す」の汎用実装
//
//  【解決したい問題】
//  インスペクタの各パラメータ行に「既定値へ戻す」ボタンを付けたいが、
//  コンポーネントは 20 種類以上あり、フィールドは数百ある。
//  「コンポーネント別・フィールド別のリセット IPC」を並べる方式では、
//  コンポーネントを 1 つ足すたびにリセット実装も足す必要があり、必ず漏れる。
//
//  【方式】
//  リセットを **「JSON 上の 1 パスだけを既定値で差し替える」** という
//  コンポーネント非依存の操作へ還元する。
//    1. 現在のコンポーネント値を `serde_json` で JSON 化する
//       （`ComponentData` は `#[serde(tag = "type", content = "data")]` なので
//         `{"type": "...", "data": {...}}` が得られる）
//    2. **同じ variant の既定値**を作って同じく JSON 化する
//    3. `data` 以下を `field_path`（`/` 区切り）で辿り、その 1 箇所だけを
//       既定値側の値で置き換える
//    4. `ComponentData` へ戻す
//
//  これにより、新しいコンポーネント・新しいフィールドが増えても
//  このファイルは一切変更不要になる（既定値の定義＝Rust の `Default` が正典）。
//  唯一の追従点は `default_component_data` の網羅 match で、
//  `ComponentData` に variant を足すとビルドが落ちて必ず気付く。
//
//  【Undo について】
//  Undo は `field_edit.rs` の共通機構が担当する
//  （`RESET_COMPONENT_FIELD` は `field_edit_target` で `Slot` に分類済み）。
//  **このファイルに Undo 記録を書いてはならない**（二重記録になる）。
// ============================================================

use serde_json::Value;

use crate::engine::components::ComponentData;

use super::{App, find_actor_by_dfs};

// ============================================================
//  パス解決（`/` 区切り）
// ============================================================

/// フィールドパスの区切り文字。
/// 例: `"intensity"` / `"material_overrides/0/kind/roughness"`。
const FIELD_PATH_SEPARATOR: char = '/';

/// JSON 値を `field_path` で辿って参照を返す（読み取り用）。
///
/// - オブジェクトならセグメントをキーとして引く
/// - 配列ならセグメントを 10 進の添字として引く
///
/// 途中で解決できなければ `None`。
fn resolve_path<'a>(root: &'a Value, field_path: &str) -> Option<&'a Value> {
    let mut cur = root;
    for seg in field_path.split(FIELD_PATH_SEPARATOR) {
        cur = match cur {
            Value::Object(map) => map.get(seg)?,
            Value::Array(arr)  => arr.get(seg.parse::<usize>().ok()?)?,
            // スカラーの先へは潜れない（パスが深すぎる）。
            _ => return None,
        };
    }
    Some(cur)
}

/// JSON 値を `field_path` で辿って可変参照を返す（書き換え用）。
/// 解決規則は `resolve_path` と同一。
fn resolve_path_mut<'a>(root: &'a mut Value, field_path: &str) -> Option<&'a mut Value> {
    let mut cur = root;
    for seg in field_path.split(FIELD_PATH_SEPARATOR) {
        cur = match cur {
            Value::Object(map) => map.get_mut(seg)?,
            Value::Array(arr)  => arr.get_mut(seg.parse::<usize>().ok()?)?,
            _ => return None,
        };
    }
    Some(cur)
}

// ============================================================
//  既定値の生成
// ============================================================

/// `current` と**同じ variant** の既定値 `ComponentData` を作る。
///
/// 既定値の正典は Rust の `Default`（＝コンポーネントを新規追加したときに
/// エディタが並べる初期値と同一）。`Default` を持たないコンポーネントは
/// シリアライズ用データ型の `Default` を使い、それも無い（＝Rust 側に
/// 既定値の定義が存在しない）ものは `None`＝リセット非対応とする。
///
/// **`_ =>` を書かないこと。** `ComponentData` に variant を足すと
/// ここがコンパイルエラーになり、「リセットできる種別か」を必ず決めさせる。
pub fn default_component_data(current: &ComponentData) -> Option<ComponentData> {
    use crate::engine::components::*;

    let d = match current {
        // ── モデル ──
        // `Default` は無いが `ModelComponent::empty()` が「何も持たない新規モデル」
        // ＝実質の既定値なのでこれを正典にする（GPU 資源は触らない純データ）。
        ComponentData::ModelComponent(_) =>
            ComponentData::ModelComponent(ModelComponent::empty().to_data()),

        // ── スクリプト（リセット非対応） ──
        // [SerializeField] の既定値は **C# の型定義側**にあり、Rust には存在しない。
        // 型名まで空へ戻してしまうとスロットが壊れるため、丸ごと非対応にする。
        ComponentData::ScriptComponent(_) => return None,

        // ── プラグイン（リセット非対応） ──
        // フィールドの既定値は **プラグイン DLL のフィールド定義（default_value）** が持つ。
        // Rust 側の `Default` は plugin_name まで空にしてしまい、スロットが壊れる。
        ComponentData::PluginComponent(_) => return None,

        // ── 旧フォーマット互換（リセット非対応） ──
        // `to_data` が生成しない読み込み専用 variant。インスペクタにも現れない。
        ComponentData::LegacyRigidbodyComponent(_) => return None,

        // ── 以下は `Default` を持つコンポーネント（正典＝Rust の Default） ──
        ComponentData::CanvasComponent(_) =>
            ComponentData::CanvasComponent(CanvasComponent::default().to_data()),
        ComponentData::SpriteComponent(_) =>
            ComponentData::SpriteComponent(SpriteComponent::default().to_data()),
        ComponentData::SkinnedSpriteComponent(_) =>
            ComponentData::SkinnedSpriteComponent(SkinnedSpriteComponent::default().to_data()),
        ComponentData::InputMapComponent(_) =>
            ComponentData::InputMapComponent(InputMapComponent::default().to_data()),
        ComponentData::CameraComponent(_) =>
            ComponentData::CameraComponent(CameraComponent::default().to_data()),
        // コライダーは `to_data` を持たず `From` 変換で相互変換する（他と流儀が違う）。
        ComponentData::ColliderComponent(_) =>
            ComponentData::ColliderComponent(
                ColliderComponentData::from(&ColliderComponent::default())),
        ComponentData::Collider2dComponent(_) =>
            ComponentData::Collider2dComponent(
                Collider2dComponentData::from(&Collider2dComponent::default())),
        ComponentData::AudioComponent(_) =>
            ComponentData::AudioComponent(AudioComponent::default().to_data()),
        ComponentData::LineRendererComponent(_) =>
            ComponentData::LineRendererComponent(LineRendererComponent::default().to_data()),
        ComponentData::TextComponent(_) =>
            ComponentData::TextComponent(TextComponent::default().to_data()),
        ComponentData::AnimatorComponent(_) =>
            ComponentData::AnimatorComponent(AnimatorComponent::default().to_data()),
        ComponentData::LightComponent(_) =>
            ComponentData::LightComponent(LightComponent::default().to_data()),
        ComponentData::JointAttachComponent(_) =>
            ComponentData::JointAttachComponent(JointAttachComponent::default().to_data()),
        ComponentData::ParticleEmitterComponent(_) =>
            ComponentData::ParticleEmitterComponent(
                ParticleEmitterComponent::default().to_data()),
        ComponentData::SkyboxComponent(_) =>
            ComponentData::SkyboxComponent(SkyboxComponent::default().to_data()),
        ComponentData::TerrainChunkComponent(_) =>
            ComponentData::TerrainChunkComponent(TerrainChunkComponent::default().to_data()),
        ComponentData::WaterVolumeComponent(_) =>
            ComponentData::WaterVolumeComponent(WaterVolumeComponent::default().to_data()),
        ComponentData::WaterLinkComponent(_) =>
            ComponentData::WaterLinkComponent(WaterLinkComponent::default().to_data()),
        ComponentData::InteractionSourceComponent(_) =>
            ComponentData::InteractionSourceComponent(
                InteractionSourceComponent::default().to_data()),
        ComponentData::CoverEmitterComponent(_) =>
            ComponentData::CoverEmitterComponent(
                CoverEmitterComponent::default().to_data()),
        ComponentData::ControlPointComponent(_) =>
            ComponentData::ControlPointComponent(ControlPointComponent::default().to_data()),
    };
    Some(d)
}

// ============================================================
//  中核の純関数
// ============================================================

/// 現在値のうち `field_path` が指すフィールドだけを既定値へ戻した `ComponentData` を返す。
///
/// `field_path` は `/` 区切りのパス。JSON 表現上のパスであり、
/// **serde の実際の形に一致させること**。例:
///   - `"intensity"`（LightComponent の強度）
///   - `"wave_amplitude"`（WaterVolumeComponent の波の振幅）
///   - `"material_overrides/0/kind/roughness"`
///     （ModelComponent のマテリアル上書き。`MaterialOverride` は
///       `{ slot, kind: { kind: "inline", ... } }` という入れ子なので
///       `kind` を 1 段挟む点に注意）
///
/// ## 戻り値が `None` になる条件（＝「何もしない」）
///   - この種別に Rust 側の既定値が無い（`default_component_data` が `None`）
///   - `field_path` が空、または**現在値側で解決できない**（存在しないフィールド名）
///   - 既に既定値と同一（無意味な `SCENE_MODIFIED` で「未保存」印を付けないため。
///     `RESET_WATER_SHADER_PARAM` と同じ方針）
///   - 差し替え後の JSON が `ComponentData` へ戻せない（不正なリセットでシーンを壊さない）
///
/// ## 既定値側でパスが解決できない場合に JSON `null` を既定値とみなす理由
/// マテリアルのインライン上書きは `Option<T>` であり、`None` は
/// 「**モデル埋め込みの値を使う**」を意味する。つまり「既定へ戻す」の正解は
/// 何らかの数値ではなく `null`（＝上書きを外す）である。
/// また、既定値側の配列は空（`material_overrides: []`）なので
/// `material_overrides/0/...` はそもそも解決できない。
/// 「解決できなければ `null`」という規則のおかげで、
/// **既定の配列が空でも配列要素ごとのリセットが成立する**。
pub fn component_data_with_field_reset(
    current: &ComponentData,
    field_path: &str,
) -> Option<ComponentData> {
    // 空パスはコンポーネント全体を指してしまう（＝全フィールドの一括リセット）。
    // ボタン 1 個の意味を超えるので受け付けない。
    if field_path.is_empty() {
        return None;
    }

    // 同 variant の既定値。無い種別はリセット非対応。
    let default = default_component_data(current)?;

    // `{"type": "...", "data": {...}}` の `data` 部だけを扱う。
    let mut cur_json = serde_json::to_value(current).ok()?;
    let def_json     = serde_json::to_value(&default).ok()?;
    let def_data     = def_json.get("data")?;

    // 既定値の取り出しは**先に**行う（`cur_json` の可変借用と重ならないようにするため）。
    // 解決できない場合は `null`（上記の理由により、これが正しい「既定へ戻す」）。
    let def_value = resolve_path(def_data, field_path).cloned().unwrap_or(Value::Null);

    let cur_data = cur_json.get_mut("data")?;
    // 現在値側で解決できないパスは「存在しないフィールド」なので何もしない。
    let target = resolve_path_mut(cur_data, field_path)?;

    // 既に既定値なら差分ゼロ。呼び出し側が SCENE_MODIFIED を送らずに済むよう None を返す。
    if *target == def_value {
        return None;
    }
    *target = def_value;

    // 戻せなければ諦める（型が合わない差し替えでシーンを壊さない）。
    serde_json::from_value::<ComponentData>(cur_json).ok()
}

// ============================================================
//  App 側のハンドラ
// ============================================================

impl App {
    /// コンポーネントのフィールド 1 個を既定値へ戻す
    /// （`RESET_COMPONENT_FIELD` IPC。インスペクタ各行の「⟲ デフォルトに戻す」ボタン）。
    ///
    /// **種別によるフィルタを掛けない**のがこのハンドラの要点で、
    /// 全コンポーネントを 1 本の経路で賄う（`handle_reset_water_shader_param` が
    /// 水専用なのに対し、こちらはコンポーネント非依存）。
    ///
    /// Undo は `field_edit.rs` の共通機構が担当する。
    /// **ここに Undo 記録を書いてはならない**（二重記録になる）。
    ///
    /// 何も変わらなかった場合（既に既定値・不明なフィールド名・リセット非対応の種別）は
    /// 再送信も `SCENE_MODIFIED` も行わない（無意味な「未保存」印を付けないため）。
    pub(super) fn handle_reset_component_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        field_path:   &str,
    ) {
        let wl = self.active_world_line;

        // 対象スロットの現在値を取り出す（種別フィルタなし＝全コンポーネント共通）。
        let current_slot = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                else { return };
            let Some(slot) = actor.slots().get(slot_idx as usize) else { return };
            crate::engine::structs::objects::actor::slot_to_data(&scene.world, slot)
        };
        let Some(mut slot_data) = current_slot else { return };

        // 指定フィールドだけを既定値へ戻した値を作る。None＝変化なし。
        let Some(reset) = component_data_with_field_reset(&slot_data.component, field_path)
            else { return };
        slot_data.component = reset;

        // 適用は Undo の復元と同じ経路（`apply_slot_data`）を再利用する。
        // その場適用に失敗する重い変更（モデル差し替え等）は内部で
        // `snapshot_actor_slots` → 差し替え → `rebuild_actor_slots` へフォールバックする。
        // なお `apply_slot_data` は `restore_stripped_fields` で ModelComponent の
        // instances を World の現在値から埋め戻すため、インスタンス配置は壊れない。
        self.apply_slot_data(wl, actor_dfs_id, slot_idx, &slot_data);

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::{
        LightComponent, MaterialOverride, MaterialOverrideKind,
        ModelComponent, ModelComponentData, WaterVolumeComponent,
    };

    /// テスト用: 既定値から作った LightComponent の ComponentData。
    fn light_data() -> ComponentData {
        ComponentData::LightComponent(LightComponent::default().to_data())
    }

    /// ライト: `intensity` を書き換えてからリセットすると既定値へ戻り、
    /// **同じスロットの他フィールド（color 等）は巻き添えにならない**こと。
    #[test]
    fn light_intensity_resets_without_touching_other_fields() {
        let default_intensity = LightComponent::default().to_data().intensity;

        let mut l = LightComponent::default();
        l.intensity = 42.0;
        l.color     = [0.25, 0.5, 0.75];
        let edited  = ComponentData::LightComponent(l.to_data());

        let reset = component_data_with_field_reset(&edited, "intensity")
            .expect("既定値と異なるのでリセットが成立すること");
        match reset {
            ComponentData::LightComponent(d) => {
                assert_eq!(d.intensity, default_intensity, "intensity が既定値へ戻ること");
                assert_eq!(d.color, [0.25, 0.5, 0.75], "他フィールドは変わらないこと");
            }
            _ => panic!("variant が変わらないこと"),
        }
    }

    /// 水ボリューム: `wave_amplitude` のリセットで既定値 0.01 に戻ること。
    #[test]
    fn water_wave_amplitude_resets_to_default() {
        let mut w = WaterVolumeComponent::default();
        w.wave_amplitude = 9.0;
        let edited = ComponentData::WaterVolumeComponent(w.to_data());

        let reset = component_data_with_field_reset(&edited, "wave_amplitude")
            .expect("リセットが成立すること");
        match reset {
            ComponentData::WaterVolumeComponent(d) => {
                assert_eq!(d.wave_amplitude, 0.01, "既定値 0.01 へ戻ること");
            }
            _ => panic!("variant が変わらないこと"),
        }
    }

    /// 既に既定値のフィールドへのリセットは「何もしない」（None）こと。
    /// 無意味な SCENE_MODIFIED で「未保存」印を付けないための規則。
    #[test]
    fn reset_of_already_default_field_is_noop() {
        assert!(component_data_with_field_reset(&light_data(), "intensity").is_none());
    }

    /// 存在しないフィールド名は「何もしない」（None）こと。
    #[test]
    fn reset_of_unknown_field_is_noop() {
        assert!(component_data_with_field_reset(&light_data(), "no_such_field").is_none());
        // 空パス（＝コンポーネント全体）も受け付けないこと。
        assert!(component_data_with_field_reset(&light_data(), "").is_none());
        // スカラーの先へ潜るパスも解決できないこと。
        assert!(component_data_with_field_reset(&light_data(), "intensity/0").is_none());
    }

    /// モデル: マテリアル上書きの `roughness` をリセットすると
    /// **null（＝モデル埋め込みの値を使う）**になり、
    /// 同じ上書きの他フィールド（metallic）と `slot` は保持されること。
    ///
    /// 既定値側の `material_overrides` は空配列なのでパスは解決できないが、
    /// 「解決できなければ null」の規則により正しく上書きが外れる。
    ///
    /// 【パスの形について】`MaterialOverride` は `{ slot, kind: {...} }`、
    /// かつ `MaterialOverrideKind` が内部タグ（`#[serde(tag = "kind")]`）なので、
    /// JSON 上のパスは `material_overrides/0/kind/roughness` と `kind` を 1 段挟む。
    #[test]
    fn model_material_override_field_resets_to_null() {
        let edited = ComponentData::ModelComponent(ModelComponentData {
            model_path:    "a.gltf".into(),
            instances:     Vec::new(),
            meta:          Vec::new(),
            groups:        Vec::new(),
            next_group_id: 0,
            cast_shadows:  true,
            material_overrides: vec![MaterialOverride {
                slot: 3,
                kind: MaterialOverrideKind::Inline {
                    base_color: None,
                    metallic:   Some(0.8),
                    roughness:  Some(0.2),
                    emissive:   None,
                    alpha_mode: None,
                    alpha_cutoff: None,
                    ior: None,
                    transmission: None,
                    diffuse_transmission: None,
                    mr_tex_ignore: None,
                    cull_face: None,
                    shading_model: None,
                },
            }],
            render_tag: 0,
            // 描画オフセットは既定（恒等）。テストの関心外だが構造体の全フィールドは必須。
            offset_position: [0.0, 0.0, 0.0],
            offset_rotation: [0.0, 0.0, 0.0],
            offset_scale:    [1.0, 1.0, 1.0],
        });

        let reset = component_data_with_field_reset(&edited, "material_overrides/0/kind/roughness")
            .expect("上書きが外れる＝差分があるのでリセットが成立すること");
        match reset {
            ComponentData::ModelComponent(d) => {
                assert_eq!(d.material_overrides.len(), 1, "上書き自体は消えないこと");
                let ovr = &d.material_overrides[0];
                assert_eq!(ovr.slot, 3, "slot は保持されること");
                match &ovr.kind {
                    MaterialOverrideKind::Inline { roughness, metallic, .. } => {
                        assert_eq!(*roughness, None, "roughness は null（埋め込み値を使う）へ戻ること");
                        assert_eq!(*metallic, Some(0.8), "同じ上書きの他フィールドは保持されること");
                    }
                    _ => panic!("Inline のままであること"),
                }
            }
            _ => panic!("variant が変わらないこと"),
        }

        // 2 回目（既に None）は差分ゼロなので何もしないこと。
        let already = ComponentData::ModelComponent(
            match component_data_with_field_reset(&edited, "material_overrides/0/kind/roughness")
                .unwrap()
            {
                ComponentData::ModelComponent(d) => d,
                _ => unreachable!(),
            },
        );
        assert!(
            component_data_with_field_reset(&already, "material_overrides/0/kind/roughness")
                .is_none(),
            "既に既定値なら何もしないこと"
        );
    }

    /// モデル: **配列添字がマテリアルスロット番号と一致しない**場合でも、
    /// 添字で指したとおりの要素だけがリセットされること。
    ///
    /// `handle_set_material_override` は同一 slot を `retain` で除いてから `push` するため、
    /// `material_overrides` の並びは編集順であってスロット順ではない。
    /// エディタは ACTOR_COMPONENTS の `override_index`（＝ Vec 内の位置）でパスを組むので、
    /// 「slot 番号を添字に使ってしまう」取り違えが起きると別スロットのマテリアルが壊れる。
    /// このテストはその取り違えを検出するために、
    /// **slot 番号（7, 1）と添字（0, 1）が食い違う**配置で添字 1 をリセットする。
    #[test]
    fn model_material_override_uses_vec_index_not_slot_number() {
        /// 上書きを 1 件組み立てるヘルパ（metallic だけを持つインライン上書き）。
        fn inline_metallic(slot: usize, metallic: f32) -> MaterialOverride {
            MaterialOverride {
                slot,
                kind: MaterialOverrideKind::Inline {
                    base_color: None,
                    metallic: Some(metallic),
                    roughness: None,
                    emissive: None,
                    alpha_mode: None,
                    alpha_cutoff: None,
                    ior: None,
                    transmission: None,
                    diffuse_transmission: None,
                    mr_tex_ignore: None,
                    cull_face: None,
                    shading_model: None,
                },
            }
        }

        let edited = ComponentData::ModelComponent(ModelComponentData {
            model_path:    "a.gltf".into(),
            instances:     Vec::new(),
            meta:          Vec::new(),
            groups:        Vec::new(),
            next_group_id: 0,
            cast_shadows:  true,
            // 添字 0 → slot 7 / 添字 1 → slot 1（並びとスロット番号が一致しない配置）。
            material_overrides: vec![inline_metallic(7, 0.10), inline_metallic(1, 0.90)],
            render_tag: 0,
            // 描画オフセットは既定（恒等）。テストの関心外だが構造体の全フィールドは必須。
            offset_position: [0.0, 0.0, 0.0],
            offset_rotation: [0.0, 0.0, 0.0],
            offset_scale:    [1.0, 1.0, 1.0],
        });

        // 添字 1（＝ slot 1 の上書き）だけを既定へ戻す。
        let reset = component_data_with_field_reset(&edited, "material_overrides/1/kind/metallic")
            .expect("差分があるのでリセットが成立すること");
        let ComponentData::ModelComponent(d) = reset else { panic!("variant が変わらないこと") };

        assert_eq!(d.material_overrides.len(), 2, "要素数は変わらないこと");
        assert_eq!(d.material_overrides[0].slot, 7, "並びが入れ替わらないこと");
        assert_eq!(d.material_overrides[1].slot, 1, "並びが入れ替わらないこと");

        match &d.material_overrides[0].kind {
            MaterialOverrideKind::Inline { metallic, .. } =>
                assert_eq!(*metallic, Some(0.10), "添字 0（slot 7）は巻き添えにならないこと"),
            _ => panic!("Inline のままであること"),
        }
        match &d.material_overrides[1].kind {
            MaterialOverrideKind::Inline { metallic, .. } =>
                assert_eq!(*metallic, None, "添字 1 は null（埋め込み値を使う）へ戻ること"),
            _ => panic!("Inline のままであること"),
        }

        // 添字が範囲外のパスは「何もしない」こと（存在しない要素を指しても壊れない）。
        assert!(
            component_data_with_field_reset(&edited, "material_overrides/9/kind/metallic").is_none(),
            "範囲外の添字は無視されること"
        );
        // MatAsset 種別の上書きに対してインライン用のパスを送っても何も起きないこと
        //（エディタ側は .mat の行に ⟲ を出さないが、ワイヤ表現としても安全であることを固定する）。
        let mat_asset = ComponentData::ModelComponent(ModelComponentData {
            model_path:    "a.gltf".into(),
            instances:     Vec::new(),
            meta:          Vec::new(),
            groups:        Vec::new(),
            next_group_id: 0,
            cast_shadows:  true,
            material_overrides: vec![MaterialOverride {
                slot: 0,
                kind: MaterialOverrideKind::MatAsset { path: "assets://a.mat".into() },
            }],
            render_tag: 0,
            // 描画オフセットは既定（恒等）。テストの関心外だが構造体の全フィールドは必須。
            offset_position: [0.0, 0.0, 0.0],
            offset_rotation: [0.0, 0.0, 0.0],
            offset_scale:    [1.0, 1.0, 1.0],
        });
        assert!(
            component_data_with_field_reset(&mat_asset, "material_overrides/0/kind/metallic").is_none(),
            "MatAsset にインライン用フィールドは存在しないので何もしないこと"
        );
    }

    /// `default_component_data` が全 variant を網羅していること
    /// （代表 variant が non-None を返し、非対応と決めた 3 種だけが None）。
    ///
    /// 網羅性そのものは `_ =>` を書かない match がコンパイル時に保証する。
    /// このテストは「非対応（None）の範囲が意図せず広がっていないこと」を守る。
    #[test]
    fn default_component_data_covers_all_variants() {
        use crate::engine::components::*;

        // リセット対応（Rust 側に既定値が存在する種別）。
        let supported = [
            ComponentData::ModelComponent(ModelComponent::empty().to_data()),
            ComponentData::CanvasComponent(CanvasComponent::default().to_data()),
            ComponentData::SpriteComponent(SpriteComponent::default().to_data()),
            ComponentData::SkinnedSpriteComponent(SkinnedSpriteComponent::default().to_data()),
            ComponentData::InputMapComponent(InputMapComponent::default().to_data()),
            ComponentData::CameraComponent(CameraComponent::default().to_data()),
            ComponentData::ColliderComponent(
                ColliderComponentData::from(&ColliderComponent::default())),
            ComponentData::Collider2dComponent(
                Collider2dComponentData::from(&Collider2dComponent::default())),
            ComponentData::AudioComponent(AudioComponent::default().to_data()),
            ComponentData::LineRendererComponent(LineRendererComponent::default().to_data()),
            ComponentData::TextComponent(TextComponent::default().to_data()),
            ComponentData::AnimatorComponent(AnimatorComponent::default().to_data()),
            ComponentData::LightComponent(LightComponent::default().to_data()),
            ComponentData::JointAttachComponent(JointAttachComponent::default().to_data()),
            ComponentData::ParticleEmitterComponent(
                ParticleEmitterComponent::default().to_data()),
            ComponentData::SkyboxComponent(SkyboxComponent::default().to_data()),
            ComponentData::TerrainChunkComponent(TerrainChunkComponent::default().to_data()),
            ComponentData::WaterVolumeComponent(WaterVolumeComponent::default().to_data()),
            ComponentData::WaterLinkComponent(WaterLinkComponent::default().to_data()),
            ComponentData::InteractionSourceComponent(
                InteractionSourceComponent::default().to_data()),
            ComponentData::CoverEmitterComponent(CoverEmitterComponent::default().to_data()),
            ComponentData::ControlPointComponent(ControlPointComponent::default().to_data()),
        ];
        for d in &supported {
            assert!(
                default_component_data(d).is_some(),
                "既定値を作れるべき種別: {}",
                super::super::field_edit::component_kind_of(d).display_name(),
            );
        }

        // リセット非対応（既定値の定義が Rust 側に無い／読み込み専用）。
        let unsupported = [
            ComponentData::ScriptComponent(ScriptComponentData {
                type_name: "Player".into(), fields: Default::default(),
            }),
            ComponentData::PluginComponent(PluginComponent::new("sample")),
            // 旧フォーマット互換の型は `Default` を持たないので値を直接組み立てる
            // （値の中身はここでは意味を持たない。variant だけが判定対象）。
            ComponentData::LegacyRigidbodyComponent(RigidbodyComponentData {
                mass: 1.0,
                restitution: 0.0,
                friction: 0.5,
                linear_damping: 0.0,
                angular_damping: 0.0,
                gravity_scale: 1.0,
                is_kinematic: false,
                freeze_position: [false; 3],
                freeze_rotation: [false; 3],
                initial_linear_velocity: [0.0; 3],
                initial_angular_velocity: [0.0; 3],
            }),
        ];
        for d in &unsupported {
            assert!(default_component_data(d).is_none(), "リセット非対応であること");
        }
    }
}
