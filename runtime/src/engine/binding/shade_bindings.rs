// ============================================================
//  binding/shade_bindings.rs — シェーディングアセットの `@ref` バインド解決（共通）
//
//  ## 役割（単一責任）
//  「パラメータ名 → バインド先文字列（`"アクタ名|スロット名|変数名"`）」のマップを
//  シーンから解決し、**解決できたものだけ**を「パラメータ名 → 実値」のマップに落とす。
//  アセットの種別（水面 / L3）に一切依存しない。種別ごとの違いは
//  「どのキャッシュから宣言を引くか」だけであり、それは呼び出し側
//  （`water/bindings.rs` / `app/shading_param_ops.rs`）の責務である。
//
//  ## `@ref` の付いていないパラメータは対象外
//  バインドを設定した後にアセットから `@ref` を外した場合、そのバインドは
//  **孤児として無視**される（保存値の孤児と同じ扱い）。
//  アセットに `@ref` を書き戻せばバインドも復活する。
//
//  ## 解決失敗は「何もしない」
//  アクタ／スロット／変数が消えた、型が変わった、スクリプトが `[Bindable]` を外した ──
//  いずれの場合も差し込まないだけで、値は保存値／アセット既定値のまま描かれる。
//  ユーザーへの通知はインスペクタの ⚠ が担当する（毎フレーム走る経路なのでログは出さない）。
// ============================================================

use std::collections::BTreeMap;

use crate::engine::core::renderer::shade_params::{ShadeParamDecl, ShadeParamKind};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::catalog::{BindableValueType, BINDING_VALUE_COMPONENTS};
use super::resolve::{parse_binding, resolve_binding};

/// パラメータの UI 種別から、バインドが要求する値の型を決める。
///
/// **色（`vec3<f32>`）は `Vec3`、それ以外（`f32`）は `F32`** という 1 対 1 対応。
/// WGSL 側の型がこの 2 種類しかないので分岐もここだけで閉じる。
pub fn required_value_type(kind: ShadeParamKind) -> BindableValueType {
    match kind {
        ShadeParamKind::Color => BindableValueType::Vec3,
        _                     => BindableValueType::F32,
    }
}

/// バインドを解決して「パラメータ名 → 実値」のマップを作る。
///
/// - `decls`    : アセットの宣言（`@ref` の有無と型の正典）
/// - `bindings` : 保存されているバインド（パラメータ名 → バインド先文字列）
///
/// 戻り値に入るのは**解決できたバインドだけ**である。
/// 空マップ＝「1 本も解決できなかった」＝すべて保存値／既定値へフォールバック。
pub fn resolve_bindings(
    actors:     &[Actor],
    world:      &World,
    world_line: u32,
    decls:      &[ShadeParamDecl],
    bindings:   &BTreeMap<String, String>,
) -> BTreeMap<String, [f32; BINDING_VALUE_COMPONENTS]> {
    let mut out = BTreeMap::new();
    for (param_name, binding_text) in bindings {
        // ① アセット側が `@ref` を宣言しているパラメータだけを対象にする。
        let Some(decl) = decls.iter().find(|d| &d.name == param_name) else { continue };
        if !decl.bindable { continue; }
        // ② バインド先文字列を割る（壊れていれば解決失敗）。
        let Some(target) = parse_binding(binding_text) else { continue };
        // ③ シーンから実値を読む（型は厳密一致）。
        let want = required_value_type(decl.kind);
        if let Some(v) = resolve_binding(actors, world, world_line, &target, want) {
            out.insert(param_name.clone(), v);
        }
    }
    out
}

/// 保存値へ解決済みバインドを重ねた「実際に使う値」のマップを作る。
///
/// 優先順位は **解決済みバインド > シーン保存値 > アセット既定値**。
/// 既定値の適用は `shade_params::build_block` / `params_json` が行うので、
/// ここでは前 2 者だけを重ねる。
pub fn overlay_bindings(
    saved: &BTreeMap<String, [f32; BINDING_VALUE_COMPONENTS]>,
    live:  &BTreeMap<String, [f32; BINDING_VALUE_COMPONENTS]>,
) -> BTreeMap<String, [f32; BINDING_VALUE_COMPONENTS]> {
    let mut out = saved.clone();
    for (k, v) in live { out.insert(k.clone(), *v); }
    out
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 型の対応が 1 対 1 であること（色 = Vec3、それ以外 = F32）。
    #[test]
    fn value_type_matches_param_kind() {
        assert_eq!(required_value_type(ShadeParamKind::Color), BindableValueType::Vec3);
        assert_eq!(required_value_type(ShadeParamKind::Float), BindableValueType::F32);
        assert_eq!(
            required_value_type(ShadeParamKind::Range { min: 0.0, max: 1.0 }),
            BindableValueType::F32,
        );
    }

    /// 重ね合わせ: バインドの実値が保存値より優先されること。
    #[test]
    fn overlay_prefers_live_values() {
        let saved = BTreeMap::from([
            ("a".to_string(), [1.0, 0.0, 0.0, 0.0]),
            ("b".to_string(), [2.0, 0.0, 0.0, 0.0]),
        ]);
        let live = BTreeMap::from([("b".to_string(), [9.0, 0.0, 0.0, 0.0])]);
        let out = overlay_bindings(&saved, &live);
        assert_eq!(out["a"], [1.0, 0.0, 0.0, 0.0], "バインドの無い値は保存値のまま");
        assert_eq!(out["b"], [9.0, 0.0, 0.0, 0.0], "バインドが解決できた値は上書き");
    }
}
