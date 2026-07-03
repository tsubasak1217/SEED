using System;

namespace SEED.Scripting;

/// <summary>
/// スクリプトのフィールドをエディタのインスペクタに公開するための属性。
/// エディタで設定した値はシーンに保存され、実行時にインスタンスへ復元される。
/// 対応型: float / double / int / long / short / bool / string
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SerializeFieldAttribute : Attribute
{
    /// <summary>インスペクタに表示するラベル（省略時はフィールド名から自動生成）。</summary>
    public string? Label   { get; init; }

    /// <summary>ラベルにマウスオーバーしたとき表示されるツールチップ。</summary>
    public string? Tooltip { get; init; }
}
