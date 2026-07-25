// ============================================================
//  InputMapData.cs — InputMap アセット（.inputmap）のデータモデル（v2）
//
//  役割:
//    - .inputmap（JSON, v2）の読み書き。
//    - version 欠落（v1）を読み込んだ場合は内部で v2 へ移行する（後方互換）。
//    - 保存は常に version=2。
//
//  スキーマ v2:
//    - value_type: 0=Bool / 1=Axis1D / 2=Axis2D。
//    - Bool: condition（Trigger/Press/Release）＋ bindings（フラット）。
//    - Axis1D: positive / negative。
//    - Axis2D: x{positive,negative} / y{positive,negative} ＋ normalize。
//    - Binding: { platform, input_type(Key/GamepadButton/GamepadAxis), value, dead_zone }。
//
//  未知フィールド保持:
//    - 各データ型に [JsonExtensionData] を持たせ、往復（ロード→保存）で
//      未知フィールドを失わないようにする（前方互換）。
// ============================================================

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SEEDEditor.InputMap;

/// <summary>アクション値の型（数値は Rust 側 value_type と一致）。</summary>
public enum ActionValueType
{
    /// <summary>ボタン押下（true/false）。</summary>
    Bool = 0,
    /// <summary>1 次元軸（-1.0 〜 1.0）。</summary>
    Axis1D = 1,
    /// <summary>2 次元ベクトル（移動など）。旧称 Vector2。</summary>
    Axis2D = 2,
}

/// <summary>Bool アクションの条件（Rust 側 Condition と文字列一致）。</summary>
public enum ActionCondition
{
    /// <summary>成立した瞬間（生値の立ち上がり）。</summary>
    Trigger,
    /// <summary>押下中（生値そのまま）。既定。</summary>
    Press,
    /// <summary>離した瞬間（生値の立ち下がり）。</summary>
    Release,
}

/// <summary>InputMap アセット全体のルートデータ。</summary>
public class InputMapData
{
    /// <summary>スキーマバージョン（保存時は常に 2）。</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    /// <summary>定義済みアクションのリスト。</summary>
    [JsonPropertyName("actions")]
    public List<InputAction> Actions { get; set; } = new();

    /// <summary>未知フィールド（往復で失わないよう保持）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    // ── 永続化 ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// JSON ファイルからロードする。ファイルが存在しない場合は空データ（v2）を返す。
    /// version 欠落（v1）は v2 へ移行してから返す。
    /// </summary>
    public static InputMapData LoadFrom(string path)
    {
        if (!File.Exists(path)) return new InputMapData();
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return new InputMapData();

        // version を判定（欠落＝v1）。
        bool isV1;
        try
        {
            var node = JsonNode.Parse(json);
            isV1 = node?["version"] is null;
        }
        catch
        {
            // パース不能なら空データ。
            return new InputMapData();
        }

        var data = JsonSerializer.Deserialize<InputMapData>(json) ?? new InputMapData();

        if (isV1)
        {
            // v1 → v2 移行。
            foreach (var action in data.Actions) action.MigrateFromV1();
        }
        data.Version = 2;
        return data;
    }

    /// <summary>JSON ファイルへ保存する（常に version=2）。</summary>
    public void SaveTo(string path)
    {
        Version = 2;
        // 型に無関係なグループを null 化して出力をクリーンに保つ。
        foreach (var action in Actions) action.PrepareForSave();
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

    /// <summary>Bool のみ有効な条件（Trigger/Press/Release）。既定 Press。</summary>
    [JsonPropertyName("condition")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActionCondition? Condition { get; set; }

    /// <summary>Axis2D のみ有効: 長さ>1 のとき正規化。</summary>
    [JsonPropertyName("normalize")]
    public bool? Normalize { get; set; }

    /// <summary>Bool: フラットなバインドリスト。</summary>
    [JsonPropertyName("bindings")]
    public List<InputBinding>? Bindings { get; set; }

    /// <summary>Axis1D: 正バインド。</summary>
    [JsonPropertyName("positive")]
    public List<InputBinding>? Positive { get; set; }

    /// <summary>Axis1D: 負バインド。</summary>
    [JsonPropertyName("negative")]
    public List<InputBinding>? Negative { get; set; }

    /// <summary>Axis2D: X 軸バインド。</summary>
    [JsonPropertyName("x")]
    public AxisBindingGroup? X { get; set; }

    /// <summary>Axis2D: Y 軸バインド。</summary>
    [JsonPropertyName("y")]
    public AxisBindingGroup? Y { get; set; }

    /// <summary>未知フィールド（往復で失わないよう保持）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    // ── 編集用の非 null アクセサ（UI が要求時に生成）───────────

    /// <summary>Bool バインドリストを取得（無ければ生成）。</summary>
    public List<InputBinding> EnsureBindings() => Bindings ??= new();
    /// <summary>Axis1D 正バインドを取得（無ければ生成）。</summary>
    public List<InputBinding> EnsurePositive() => Positive ??= new();
    /// <summary>Axis1D 負バインドを取得（無ければ生成）。</summary>
    public List<InputBinding> EnsureNegative() => Negative ??= new();
    /// <summary>Axis2D X 軸を取得（無ければ生成）。</summary>
    public AxisBindingGroup EnsureX() => X ??= new();
    /// <summary>Axis2D Y 軸を取得（無ければ生成）。</summary>
    public AxisBindingGroup EnsureY() => Y ??= new();

    // ── 移行・保存整形 ───────────────────────────────────────

    /// <summary>
    /// v1（bindings に全型が入る形式）から v2 へ移行する。
    /// Bool は bindings をそのまま使う。Axis1D/Axis2D は WASD / Key を正負グループへ展開する。
    /// </summary>
    public void MigrateFromV1()
    {
        switch (ValueType)
        {
            case ActionValueType.Bool:
                // bindings のうち PC の Key/GamepadButton/GamepadAxis のみ残す（WASD は無意味）。
                if (Bindings is not null)
                    Bindings.RemoveAll(b => b.InputType == "WASD");
                Condition ??= ActionCondition.Press;
                break;

            case ActionValueType.Axis1D:
                {
                    var pos = EnsurePositive();
                    var neg = EnsureNegative();
                    if (Bindings is not null)
                    {
                        foreach (var b in Bindings)
                        {
                            if (b.Platform != "PC") continue;
                            if (b.InputType == "WASD") ExpandWasd(b.Value, pos, neg);
                            else if (b.InputType == "Key") pos.Add(new InputBinding { InputType = "Key", Value = b.Value });
                        }
                    }
                    Bindings = null;
                    break;
                }

            case ActionValueType.Axis2D:
                {
                    var x = EnsureX();
                    var y = EnsureY();
                    if (Bindings is not null)
                    {
                        foreach (var b in Bindings)
                        {
                            if (b.Platform != "PC" || b.InputType != "WASD") continue;
                            if (b.Value == "Horizontal") ExpandWasd("Horizontal", x.EnsurePositive(), x.EnsureNegative());
                            else if (b.Value == "Vertical") ExpandWasd("Vertical", y.EnsurePositive(), y.EnsureNegative());
                        }
                    }
                    Bindings = null;
                    break;
                }
        }
    }

    /// <summary>WASD 合成軸を正負キーバインドへ展開する（矢印キーも同時に有効）。</summary>
    private static void ExpandWasd(string value, List<InputBinding> positive, List<InputBinding> negative)
    {
        if (value == "Horizontal")
        {
            positive.Add(new InputBinding { InputType = "Key", Value = "D" });
            positive.Add(new InputBinding { InputType = "Key", Value = "RightArrow" });
            negative.Add(new InputBinding { InputType = "Key", Value = "A" });
            negative.Add(new InputBinding { InputType = "Key", Value = "LeftArrow" });
        }
        else if (value == "Vertical")
        {
            positive.Add(new InputBinding { InputType = "Key", Value = "W" });
            positive.Add(new InputBinding { InputType = "Key", Value = "UpArrow" });
            negative.Add(new InputBinding { InputType = "Key", Value = "S" });
            negative.Add(new InputBinding { InputType = "Key", Value = "DownArrow" });
        }
    }

    /// <summary>保存前に、値の型に無関係なグループを null 化する（出力をクリーンに保つ）。</summary>
    public void PrepareForSave()
    {
        switch (ValueType)
        {
            case ActionValueType.Bool:
                Bindings ??= new();
                Condition ??= ActionCondition.Press;
                Positive = Negative = null;
                X = Y = null;
                Normalize = null;
                break;
            case ActionValueType.Axis1D:
                Positive ??= new();
                Negative ??= new();
                Bindings = null;
                Condition = null;
                X = Y = null;
                Normalize = null;
                break;
            case ActionValueType.Axis2D:
                X ??= new();
                Y ??= new();
                Normalize ??= false;
                Bindings = null;
                Condition = null;
                Positive = Negative = null;
                break;
        }
    }
}

/// <summary>Axis2D の 1 軸分（正/負バインド）。</summary>
public class AxisBindingGroup
{
    /// <summary>正バインド。</summary>
    [JsonPropertyName("positive")]
    public List<InputBinding>? Positive { get; set; }

    /// <summary>負バインド。</summary>
    [JsonPropertyName("negative")]
    public List<InputBinding>? Negative { get; set; }

    /// <summary>未知フィールド保持。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>正バインドを取得（無ければ生成）。</summary>
    public List<InputBinding> EnsurePositive() => Positive ??= new();
    /// <summary>負バインドを取得（無ければ生成）。</summary>
    public List<InputBinding> EnsureNegative() => Negative ??= new();
}

/// <summary>プラットフォームごとの物理入力バインディング。</summary>
public class InputBinding
{
    /// <summary>対象プラットフォーム（現状 PC のみランタイム評価）。</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "PC";

    /// <summary>入力種別（Key / GamepadButton / GamepadAxis）。</summary>
    [JsonPropertyName("input_type")]
    public string InputType { get; set; } = "Key";

    /// <summary>具体的な入力値（"Space", "South", "LeftStickX" など）。</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    /// <summary>デッドゾーン（GamepadAxis のみ意味を持つ。省略時はランタイム既定 0.2）。</summary>
    [JsonPropertyName("dead_zone")]
    public float? DeadZone { get; set; }

    /// <summary>未知フィールド保持。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
