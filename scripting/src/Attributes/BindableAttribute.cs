using System;

namespace SEEDEditor.Scripting;

/// <summary>
/// このフィールドを**シェーダパラメータのバインド元**として公開する属性（Phase W8.3）。
///
/// 水面シェーディングアセット（.wgsl）が <c>@ref</c> を付けて宣言したパラメータは、
/// インスペクタで「アクタ → コンポーネント → 変数」を選ぶだけで、
/// このフィールドの**実行中の値**が毎フレームシェーダへ流し込まれる。
///
/// ## 使い方
/// <c>[SerializeField]</c> との**併用が必須**である（インスペクタに出ない値を
/// バインド候補に出すと、何がどこから流れているのか追跡できなくなるため）。
/// <c>[SerializeField]</c> の無いフィールドに付けても候補には現れない。
///
/// ## 対応する型（WGSL 側と厳密一致）
/// <list type="bullet">
///   <item><description><c>float</c> … WGSL の <c>f32</c> パラメータへ繋げる</description></item>
///   <item><description><c>Vector3</c> … WGSL の <c>vec3&lt;f32&gt;</c>（色）パラメータへ繋げる</description></item>
/// </list>
/// **成分の部分取り出しは行わない。** <c>Vector3</c> を <c>f32</c> のパラメータへ
/// 繋ぐことはできない（X 成分だけ欲しいなら <c>float</c> のフィールドを別に用意すること）。
/// 上記以外の型に付けても候補には現れない。
///
/// ## 値が読まれるタイミング
/// 毎フレーム、描画の直前に**実行中のインスタンスから直接**読まれる（Edit / Play の両方）。
/// したがって Update などで書き換えた値がそのままシェーダへ届く。
///
/// ## バインドが切れたとき
/// このフィールドを消した／属性を外した／型を変えた場合、バインドは静かに切れる。
/// シェーダのパラメータは保存値（無ければアセットの既定値）へフォールバックし、
/// インスペクタのその行に ⚠ が出る。
/// </summary>
/// <example>[SerializeField, Bindable] private float glowPower = 1.0f;</example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class BindableAttribute : Attribute
{
}
