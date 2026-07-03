using System;

namespace SEEDEditor.Scripting;

/// <summary>
/// 同一アクターに同じスクリプトを複数アタッチすることを禁止する属性。
/// エディタは既に同じスクリプトが付いている場合、重複アタッチを警告して防ぐ。
/// </summary>
/// <example>[DisallowMultipleComponent] public class PlayerController : SEEDScript { }</example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class DisallowMultipleComponentAttribute : Attribute
{
}
