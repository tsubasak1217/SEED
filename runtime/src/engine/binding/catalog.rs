// ============================================================
//  binding/catalog.rs — エンジン組込コンポーネントが供給する値の正典表
//
//  ## 役割（単一責任）
//  「どのコンポーネントの、どの変数が、どの型で読めるか」を **1 つの静的表**にする。
//  この表が正典であり、エディタは表の内容を IPC 経由で受け取るだけである
//  （C# 側にミラー表を置かないことで、二重管理の同期ずれを構造的に無くす）。
//
//  ## 供給値を 1 つ足すときに触る場所は 2 か所だけ
//    ① `BUILTIN_BINDABLES` に 1 行足す
//    ② `read_builtin` の該当コンポーネントの腕へ 1 行足す
//  それ以外（エディタの UI・保存形式・毎フレーム解決）は一切変更が要らない。
//
//  ## 型は厳密一致（成分の部分取り出しは禁止）
//  `Vec3` の供給値を `F32` の要求へ繋ぐことはできない。逆も同じ。
//  「X 成分だけ欲しい」は供給側（コンポーネント）が明示的にその変数を生やすべきで、
//  バインド機構が勝手に切り出すと、繋いだ本人にも何が流れているか分からなくなる。
//
//  ## 依存
//  ECS（World / Entity）と各コンポーネント定義のみ。描画にも IPC にも依存しない。
// ============================================================

use crate::engine::components::light_component::LightComponent;
use crate::engine::components::water_volume_component::WaterVolumeComponent;
use crate::engine::components::{ComponentKind, Transform};
use crate::engine::ecs::{Entity, World};

/// バインドで運べる値の型。**WGSL 側の型と 1 対 1** に対応する。
///
/// 4 成分（`vec4`）を足すときはここへ変種を足し、`components()` と
/// `pack()` の両方を更新すること（この 2 つが型の意味の全てである）。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum BindableValueType {
    /// スカラー（WGSL の `f32`）。
    F32,
    /// 3 成分ベクタ（WGSL の `vec3<f32>`。色・位置・スケール）。
    Vec3,
}

/// 供給値・要求値を運ぶ内部表現の成分数（`vec4` 固定）。
///
/// 水面パラメータの GPU 表現（`WaterShadeParamBlock`）と揃えてあるので、
/// 解決結果をそのまま保存値マップへ差し込める。
pub const BINDING_VALUE_COMPONENTS: usize = 4;

impl BindableValueType {
    /// この型が実際に使う成分数（`F32` = 1、`Vec3` = 3）。
    ///
    /// スクリプト側（C#）の FFI が返す成分数と突き合わせて厳密一致を判定するため、
    /// 「型 → 成分数」の対応はこの 1 か所だけが持つ。
    pub fn components(self) -> usize {
        match self {
            BindableValueType::F32  => 1,
            BindableValueType::Vec3 => 3,
        }
    }

    /// ワイヤ表現（IPC・保存で使う型名の文字列）。
    pub fn as_str(self) -> &'static str {
        match self {
            BindableValueType::F32  => "f32",
            BindableValueType::Vec3 => "vec3",
        }
    }

    /// ワイヤ表現から型を復元する（未知の綴りは `None`）。
    pub fn from_str(text: &str) -> Option<Self> {
        match text {
            "f32"  => Some(BindableValueType::F32),
            "vec3" => Some(BindableValueType::Vec3),
            _      => None,
        }
    }

    /// スカラーを `vec4` へ詰める（未使用成分は 0）。
    pub fn pack_scalar(v: f32) -> [f32; BINDING_VALUE_COMPONENTS] { [v, 0.0, 0.0, 0.0] }

    /// 3 成分を `vec4` へ詰める（第 4 成分は 0）。
    pub fn pack_vec3(v: [f32; 3]) -> [f32; BINDING_VALUE_COMPONENTS] { [v[0], v[1], v[2], 0.0] }
}

/// 供給値を持つコンポーネントがアクタのどこに居るか。
///
/// Transform だけはスロットではなく**アクタのルート実体**に直付けされているため、
/// スロット走査では見つけられない。この違いを表で表現しておくと、解決側に
/// 「Transform だけ特別扱い」の分岐が散らばらない。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum BindableHost {
    /// アクタのルート実体に直付け（Transform）。
    ActorRoot,
    /// コンポーネントスロットに載る（Light / WaterVolume など）。
    Slot(ComponentKind),
}

/// 「1 コンポーネントの 1 変数」を表す表の 1 行。
#[derive(Clone, Copy, Debug)]
pub struct BindableVariable {
    /// コンポーネントの居場所（ルート直付け or スロット種別）。
    pub host: BindableHost,
    /// エディタのスロット選択（1 ページ目）に出す表示名。
    pub component_label: &'static str,
    /// 変数の識別子。**保存文字列の 3 つ目の要素**であり、改名すると
    /// 既存シーンのバインドが切れる（＝安易に変えてはならない）。
    pub variable: &'static str,
    /// エディタの変数選択（2 ページ目）に出す表示名。
    pub variable_label: &'static str,
    /// 供給値の型。要求型と**厳密一致**したものだけが候補に出る。
    pub value_type: BindableValueType,
}

/// エンジン組込コンポーネントが供給する値の一覧（**正典**）。
///
/// 並び順がそのままエディタの候補一覧の並びになる（コンポーネント単位でまとまるよう、
/// 同じコンポーネントの行は連続させること）。
///
/// 初期セットは「見た目の駆動に実際に使う値」に絞ってある。
/// 全フィールドを機械的に並べると候補一覧が読めなくなるため、
/// **主要値だけを人手で選ぶ**方針を採っている。
pub const BUILTIN_BINDABLES: &[BindableVariable] = &[
    // ── Transform（アクタのルート直付け）────────────────────
    BindableVariable {
        host: BindableHost::ActorRoot, component_label: "Transform",
        variable: "position", variable_label: "位置", value_type: BindableValueType::Vec3,
    },
    BindableVariable {
        host: BindableHost::ActorRoot, component_label: "Transform",
        variable: "scale", variable_label: "スケール", value_type: BindableValueType::Vec3,
    },
    // ── Light ───────────────────────────────────────────────
    BindableVariable {
        host: BindableHost::Slot(ComponentKind::Light), component_label: "Light",
        variable: "intensity", variable_label: "強度", value_type: BindableValueType::F32,
    },
    BindableVariable {
        host: BindableHost::Slot(ComponentKind::Light), component_label: "Light",
        variable: "color", variable_label: "色", value_type: BindableValueType::Vec3,
    },
    // ── WaterVolume ─────────────────────────────────────────
    BindableVariable {
        host: BindableHost::Slot(ComponentKind::WaterVolume), component_label: "WaterVolume",
        variable: "wave_amplitude", variable_label: "波の振幅", value_type: BindableValueType::F32,
    },
    BindableVariable {
        host: BindableHost::Slot(ComponentKind::WaterVolume), component_label: "WaterVolume",
        variable: "surface_height", variable_label: "水位", value_type: BindableValueType::F32,
    },
    BindableVariable {
        host: BindableHost::Slot(ComponentKind::WaterVolume), component_label: "WaterVolume",
        variable: "shallow_color", variable_label: "浅場色", value_type: BindableValueType::Vec3,
    },
    BindableVariable {
        host: BindableHost::Slot(ComponentKind::WaterVolume), component_label: "WaterVolume",
        variable: "deep_color", variable_label: "深場色", value_type: BindableValueType::Vec3,
    },
];

/// 指定した居場所の供給値だけを列挙する（要求型で絞り込む）。
///
/// エディタの候補一覧（1 ページ目・2 ページ目）と、解決時の照合の
/// **両方**がこの 1 関数を通るので、「一覧に出たのに繋がらない」が構造的に起きない。
pub fn variables_for(
    host: BindableHost,
    want: BindableValueType,
) -> impl Iterator<Item = &'static BindableVariable> {
    BUILTIN_BINDABLES.iter().filter(move |v| v.host == host && v.value_type == want)
}

/// 組込コンポーネントから 1 変数の実値を読む。
///
/// `entity` は `host` に対応する実体（`ActorRoot` ならアクタ実体、
/// `Slot` ならスロット実体）。見つからない・型が違う場合は `None`。
///
/// **供給値を足すときはここへ 1 行足す**（表への追加と対になる）。
pub fn read_builtin(
    world:  &World,
    entity: Entity,
    var:    &BindableVariable,
) -> Option<[f32; BINDING_VALUE_COMPONENTS]> {
    match var.host {
        // ── Transform（アクタのルート実体）────────────────────
        BindableHost::ActorRoot => {
            let t = world.get::<Transform>(entity)?;
            match var.variable {
                "position" => Some(BindableValueType::pack_vec3(t.position)),
                "scale"    => Some(BindableValueType::pack_vec3(t.scale)),
                _          => None,
            }
        }
        // ── Light ───────────────────────────────────────────
        BindableHost::Slot(ComponentKind::Light) => {
            let l = world.get::<LightComponent>(entity)?;
            match var.variable {
                "intensity" => Some(BindableValueType::pack_scalar(l.intensity)),
                "color"     => Some(BindableValueType::pack_vec3(l.color)),
                _           => None,
            }
        }
        // ── WaterVolume ─────────────────────────────────────
        BindableHost::Slot(ComponentKind::WaterVolume) => {
            let w = world.get::<WaterVolumeComponent>(entity)?;
            match var.variable {
                "wave_amplitude" => Some(BindableValueType::pack_scalar(w.wave_amplitude)),
                // 水位は「水位グラフが動いていればその現在値」を優先する
                //（インスペクタの設定値ではなく、実際に見えている水位を流す）。
                "surface_height" => Some(BindableValueType::pack_scalar(
                    w.sim_level_y.unwrap_or(w.surface_height))),
                "shallow_color"  => Some(BindableValueType::pack_vec3(w.shallow_color)),
                "deep_color"     => Some(BindableValueType::pack_vec3(w.deep_color)),
                _                => None,
            }
        }
        // 表に載っていない種別（＝供給値を持たないコンポーネント）。
        BindableHost::Slot(_) => None,
    }
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::light_component::LightComponent;

    /// 表の全行が `read_builtin` に対応する腕を持つこと。
    ///
    /// 「表に足したが読み取りを書き忘れた」を機械的に検出する
    /// （＝候補一覧に出るのに絶対に解決できない行を作らせない）。
    #[test]
    fn every_table_row_is_readable() {
        let mut world = World::new();
        for var in BUILTIN_BINDABLES {
            let e = world.spawn();
            // その行の腕が読めるよう、対応するコンポーネントを既定値で置く。
            match var.host {
                BindableHost::ActorRoot => world.insert(e, Transform::default()),
                BindableHost::Slot(ComponentKind::Light) =>
                    world.insert(e, LightComponent::default()),
                BindableHost::Slot(ComponentKind::WaterVolume) =>
                    world.insert(e, WaterVolumeComponent::default()),
                other => panic!("表に未知の居場所 {other:?} が増えている（テストを更新すること）"),
            }
            assert!(read_builtin(&world, e, var).is_some(),
                "表の行 `{}::{}` に read_builtin の腕がない", var.component_label, var.variable);
        }
    }

    /// 変数名が同一コンポーネント内で重複しないこと（保存文字列が一意に解けるため）。
    #[test]
    fn variable_names_are_unique_per_component() {
        for (i, a) in BUILTIN_BINDABLES.iter().enumerate() {
            for b in &BUILTIN_BINDABLES[i + 1..] {
                assert!(!(a.host == b.host && a.variable == b.variable),
                    "`{}::{}` が重複している", a.component_label, a.variable);
            }
        }
    }

    /// 型フィルタが厳密一致で効くこと（vec3 の要求に f32 の行が混ざらない）。
    #[test]
    fn variables_for_filters_by_exact_type() {
        let vec3: Vec<_> = variables_for(
            BindableHost::Slot(ComponentKind::Light), BindableValueType::Vec3).collect();
        assert_eq!(vec3.len(), 1, "Light の vec3 供給値は色だけ");
        assert_eq!(vec3[0].variable, "color");

        let f32s: Vec<_> = variables_for(
            BindableHost::Slot(ComponentKind::Light), BindableValueType::F32).collect();
        assert_eq!(f32s.len(), 1);
        assert_eq!(f32s[0].variable, "intensity");
    }

    /// 型のワイヤ表現が往復すること（IPC の型名が壊れない土台）。
    #[test]
    fn value_type_wire_roundtrip() {
        for t in [BindableValueType::F32, BindableValueType::Vec3] {
            assert_eq!(BindableValueType::from_str(t.as_str()), Some(t));
        }
        assert_eq!(BindableValueType::from_str("vec4"), None);
        assert_eq!(BindableValueType::F32.components(), 1);
        assert_eq!(BindableValueType::Vec3.components(), 3);
    }

    /// 実値が正しく読めること（成分の詰め方も含む）。
    #[test]
    fn reads_actual_values() {
        let mut world = World::new();
        let e = world.spawn();
        let mut l = LightComponent::default();
        l.intensity = 7.5;
        l.color     = [0.25, 0.5, 0.75];
        world.insert(e, l);

        let intensity = BUILTIN_BINDABLES.iter()
            .find(|v| v.host == BindableHost::Slot(ComponentKind::Light) && v.variable == "intensity")
            .expect("表にある");
        assert_eq!(read_builtin(&world, e, intensity), Some([7.5, 0.0, 0.0, 0.0]));

        let color = BUILTIN_BINDABLES.iter()
            .find(|v| v.host == BindableHost::Slot(ComponentKind::Light) && v.variable == "color")
            .expect("表にある");
        assert_eq!(read_builtin(&world, e, color), Some([0.25, 0.5, 0.75, 0.0]));
    }

    /// コンポーネントが載っていない実体からは読めないこと（バインド切れの基本形）。
    #[test]
    fn missing_component_yields_none() {
        let mut world = World::new();
        let e = world.spawn();
        let var = BUILTIN_BINDABLES.iter()
            .find(|v| v.host == BindableHost::Slot(ComponentKind::Light))
            .expect("表にある");
        assert_eq!(read_builtin(&world, e, var), None);
    }
}
