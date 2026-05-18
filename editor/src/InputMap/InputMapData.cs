// ============================================================
//  InputMapData.cs — InputMap アセットのデータモデル
//
//  .inputmap ファイル（JSON）の読み書きを担う。
//  アクション名・値の型・プラットフォームごとのバインディングを定義する。
// ============================================================

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.InputMap;

/// <summary>InputMap アセット全体のルートデータ。</summary>
public class InputMapData
{
    /// <summary>定義済みアクションのリスト。</summary>
    [JsonPropertyName("actions")]
    public List<InputAction> Actions { get; set; } = new();

    // ── 永続化 ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>JSON ファイルからロードする。ファイルが存在しない場合は空データを返す。</summary>
    public static InputMapData LoadFrom(string path)
    {
        if (!File.Exists(path)) return new InputMapData();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<InputMapData>(json) ?? new InputMapData();
    }

    /// <summary>JSON ファイルへ保存する。</summary>
    public void SaveTo(string path)
    {
        var json = JsonSerializer.Serialize(this, SerializeOptions);
        File.WriteAllText(path, json);
    }
}

/// <summary>1 つの入力アクション定義。</summary>
public class InputAction
{
    /// <summary>アクション名（スクリプトから参照する識別子）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "NewAction";

    /// <summary>アクション値の型。</summary>
    [JsonPropertyName("value_type")]
    public ActionValueType ValueType { get; set; } = ActionValueType.Bool;

    /// <summary>プラットフォームごとのバインディング一覧。</summary>
    [JsonPropertyName("bindings")]
    public List<InputBinding> Bindings { get; set; } = new();
}

/// <summary>アクション値の型。</summary>
public enum ActionValueType
{
    /// <summary>ボタン押下（true/false）。</summary>
    Bool,
    /// <summary>1 次元軸（-1.0 〜 1.0）。</summary>
    Axis1D,
    /// <summary>2 次元ベクトル（移動など）。</summary>
    Vector2,
}

/// <summary>プラットフォームごとの物理入力バインディング。</summary>
public class InputBinding
{
    /// <summary>対象プラットフォーム（PC / Mobile / PS5 / Switch）。</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "PC";

    /// <summary>入力種別（Key / GamepadButton / GamepadAxis / VirtualButton / VirtualStick など）。</summary>
    [JsonPropertyName("input_type")]
    public string InputType { get; set; } = "Key";

    /// <summary>具体的な入力値（"Space", "South", "btn_jump" など）。</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}
