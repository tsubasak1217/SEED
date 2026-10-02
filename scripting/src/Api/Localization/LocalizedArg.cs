using SEEDEditor.Scripting;

namespace SEED.Localization;

// ============================================================
//  LocalizedArg.cs — インスペクタで書く差し込みの 1 組（名前と値。LocalizedText・LocalizedLabel の「差し込み」の要素）
//
//  [System.Serializable] の構造体のリスト（docs/scripting_api.md §1「構造体のリスト」）としてインスペクタで増やす。
//  値は文字列のまま差し込む（数の書式を効かせたいときは、スクリプトから SetArg で数として渡す）。
// ============================================================

/// <summary>差し込みの 1 組（名前と値）。</summary>
[System.Serializable]
public struct LocalizedArg
{
    /// <summary>差し込みの名前（文の {name} の name）。</summary>
    [SerializeField(Label = "名前")]
    public string Name;

    /// <summary>差し込む値（文字列のまま）。</summary>
    [SerializeField(Label = "値")]
    public string Value;

    /// <summary>1 組を作る。</summary>
    /// <param name="name">名前。</param>
    /// <param name="value">値。</param>
    public LocalizedArg(string name, string value)
    {
        Name = name;
        Value = value;
    }
}
