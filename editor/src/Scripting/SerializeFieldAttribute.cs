using System;

namespace SEEDEditor.Scripting;

[AttributeUsage(AttributeTargets.Field)]
public sealed class SerializeFieldAttribute : Attribute
{
    public string? Label   { get; init; }
    public string? Tooltip { get; init; }
}
