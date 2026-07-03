using System;

namespace SEEDEditor.Scripting;

/// <summary>
/// このスクリプトが動作するために必要な他コンポーネントを宣言する属性。
/// エディタでスクリプトをアクターへアタッチした際、不足している要求
/// コンポーネントを自動的に追加する。
///
/// 2 通りの指定方法がある:
///   - 他スクリプトを型で要求:      [RequireComponent(typeof(Health))]
///   - ネイティブコンポーネントを名前で要求: [RequireComponent("CameraComponent")]
/// 複数付与して複数要求できる。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequireComponentAttribute : Attribute
{
    /// <summary>要求する他スクリプトの型（型指定コンストラクタ使用時）。</summary>
    public Type? ScriptType { get; }

    /// <summary>要求するネイティブコンポーネント名（文字列指定時、例 "CameraComponent"）。</summary>
    public string? ComponentName { get; }

    /// <summary>他スクリプト型を要求する。</summary>
    public RequireComponentAttribute(Type scriptType) => ScriptType = scriptType;

    /// <summary>ネイティブコンポーネントを名前で要求する。</summary>
    public RequireComponentAttribute(string componentName) => ComponentName = componentName;
}
