using System;

namespace SEEDEditor.Scripting;

/// <summary>
/// フィールドのラベルにマウスオーバーしたとき表示する説明文を指定する属性。
/// [SerializeField(Tooltip=...)] の代わりに独立して付与できる。
/// </summary>
/// <example>[Tooltip("移動速度（m/s）")] private float speed = 1f;</example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class TooltipAttribute : Attribute
{
    /// <summary>表示するツールチップ文言。</summary>
    public string Text { get; }

    public TooltipAttribute(string text) => Text = text;
}
