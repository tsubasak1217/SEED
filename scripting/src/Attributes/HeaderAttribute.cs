using System;

namespace SEEDEditor.Scripting;

/// <summary>
/// フィールドの直前にセクション見出しを表示する属性。
/// 関連するフィールド群をグループ化して見やすくする。
/// </summary>
/// <example>[Header("移動設定")] [SerializeField] private float speed = 1f;</example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class HeaderAttribute : Attribute
{
    /// <summary>見出しに表示する文言。</summary>
    public string Text { get; }

    public HeaderAttribute(string text) => Text = text;
}
