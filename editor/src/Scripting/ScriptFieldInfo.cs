using System.Reflection;

namespace SEEDEditor.Scripting;

public record ScriptFieldInfo(
    FieldInfo Field,
    string    Label,
    string?   Tooltip,
    object?   DefaultValue
);
