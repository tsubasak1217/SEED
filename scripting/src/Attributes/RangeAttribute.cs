using System;

namespace SEEDEditor.Scripting;

/// <summary>
/// 数値フィールドの入力範囲を制限し、インスペクタにスライダーを表示する属性。
/// float / double / int / long / short に適用できる。
/// </summary>
/// <example>[Range(0, 100)] private float hp = 50;</example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class RangeAttribute : Attribute
{
    /// <summary>許容する最小値。</summary>
    public float Min { get; }

    /// <summary>許容する最大値。</summary>
    public float Max { get; }

    public RangeAttribute(float min, float max)
    {
        Min = min;
        Max = max;
    }
}
