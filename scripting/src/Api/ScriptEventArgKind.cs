namespace SEED;

/// <summary>
/// <see cref="ScriptEvent"/> のバインディングが呼び出し先メソッドへ渡す
/// 「固定引数」の種別。
///
/// Unity の UnityEvent が持つ「静的引数（インスペクタで入力した値）」に相当する。
/// 引数の実値はバインディング側に文字列（<see cref="ScriptEventBinding.Arg"/>）で保存し、
/// この種別に従って実行時に型変換してから渡す。
///
/// 【拡張時の注意】
/// - JSON への書き出し表記は <see cref="ScriptEvent.ArgKindToJson"/> /
///   <see cref="ScriptEvent.ArgKindFromJson"/> の 1 か所だけで定義している。
///   値を足すときは必ずその 2 つの表と
///   <see cref="ScriptEvent.IsSupportedArgType"/>（C# 型 → 種別）を同時に更新すること。
/// - 種別と C# 型は 1 対 1 に対応させる（多対 1 にすると
///   「どの型へ変換して渡すか」が一意に決まらなくなる）。
/// </summary>
public enum ScriptEventArgKind
{
    /// <summary>引数なし（呼び出し先は 0 引数メソッド）。</summary>
    None = 0,

    /// <summary><c>string</c> 引数。<see cref="ScriptEventBinding.Arg"/> をそのまま渡す。</summary>
    String,

    /// <summary><c>float</c> 引数。<see cref="ScriptEventBinding.Arg"/> を不変カルチャで解釈する。</summary>
    Float,

    /// <summary><c>int</c> 引数。<see cref="ScriptEventBinding.Arg"/> を不変カルチャで解釈する。</summary>
    Int,

    /// <summary><c>bool</c> 引数。<see cref="ScriptEventBinding.Arg"/> が "true" のときだけ true。</summary>
    Bool,

    /// <summary>
    /// <see cref="SEED.GameObject"/> 引数。<see cref="ScriptEventBinding.Arg"/> をアクター名として
    /// <see cref="SEED.GameObject.Find"/> で解決してから渡す（見つからなければ IsValid=false の値）。
    /// </summary>
    GameObject,
}
