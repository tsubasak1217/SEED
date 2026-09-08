using System;

namespace SEEDEditor.Scripting;

/// <summary>
/// このメンバを**バインド元（値の供給元）**として公開する属性。
///
/// バインドの消費者は 2 つある。
/// <list type="number">
///   <item><description><b>水面シェーディングアセット（<c>.wgsl</c>）の <c>@ref</c> パラメータ</b>（Phase W8.3）。
///     インスペクタで「アクタ → コンポーネント → 変数」を選ぶだけで、実行中の値が毎フレーム
///     シェーダへ流し込まれる。</description></item>
///   <item><description><b>Text のプレースホルダ記法</b>（<c>{num}</c> / <c>{string}</c>）。
///     本文の差し込み口に、実行中の値が毎フレーム差し込まれる。</description></item>
/// </list>
///
/// ## 付けられる場所と条件
/// <list type="bullet">
///   <item><description><b>フィールド</b> … <c>[SerializeField]</c> との<b>併用が必須</b>
///     （インスペクタに出ない値をバインド候補に出すと、何がどこから流れているのか追跡できなくなるため）。</description></item>
///   <item><description><b>プロパティ</b> … get アクセサを持つこと。<c>[SerializeField]</c> は不要。</description></item>
///   <item><description><b>メソッド</b> … <b>引数なし</b>で戻り値を返すこと。<c>[SerializeField]</c> は不要。</description></item>
/// </list>
///
/// ## 対応する型
/// <list type="bullet">
///   <item><description><c>float</c> … 数値バインド（Text の <c>{num}</c>、WGSL の <c>f32</c>）</description></item>
///   <item><description><c>int</c> … 数値バインド（<c>float</c> へ変換して渡す。フィールド／プロパティ／メソッド共通）</description></item>
///   <item><description><c>string</c> … 文字列バインド（Text の <c>{string}</c>）</description></item>
///   <item><description><c>Vector3</c> … WGSL の <c>vec3&lt;f32&gt;</c>（色）パラメータ専用。
///     <b>フィールドのみ</b>（<c>[SerializeField]</c> 併用必須）。</description></item>
/// </list>
/// **成分の部分取り出しは行わない。** <c>Vector3</c> を <c>f32</c> のパラメータへ
/// 繋ぐことはできない（X 成分だけ欲しいなら <c>[Bindable] public float PosX => transform.Position.x;</c>
/// のようにプロパティを 1 本生やすこと）。上記以外の型に付けても候補には現れない。
///
/// ## 値が読まれるタイミング（**副作用を書かないこと**）
/// 毎フレーム、描画の直前に**実行中のインスタンスから直接**読まれる（Edit / Play の両方）。
/// プロパティ／メソッドは<b>毎フレーム呼び出される</b>ため、
/// カウンタを進める・生成する・ログを出すといった<b>副作用を持たせてはならない</b>
/// （読むだけの純粋な計算にすること）。
///
/// ## バインドが切れたとき
/// このメンバを消した／属性を外した／型を変えた場合、バインドは静かに切れる。
/// 消費側は保存値（フォールバック値・アセットの既定値）へフォールバックし、
/// インスペクタのその行に ⚠ が出る。
/// </summary>
/// <example>[SerializeField, Bindable] private float glowPower = 1.0f;</example>
[AttributeUsage(
    AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method,
    AllowMultiple = false)]
public sealed class BindableAttribute : Attribute
{
}
