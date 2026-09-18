// ============================================================
//  InputMapData.cs — InputMap アセット（.inputmap）のデータモデル（v2）
//
//  役割:
//    - .inputmap（JSON, v2）の読み書き。
//    - 保存は常に現行版（AssetFormats.InputMap.CurrentVersion）を version へ刻む。
//
//  版（version）とマイグレーション:
//    この形式は版の仕組み導入前から `version` 欄を持っていたため、欄名は
//    `format_version` ではなく `version` のまま（綴りを変えないこと）。
//    v1 → v2 の変換は **ランタイム（Rust）に一本化**してある
//    （runtime/.../migration/steps/inputmap/v1_to_v2.rs）。
//    以前はこのファイルにも同じ変換（InputAction.MigrateFromV1）があり、
//    「ランタイムとエディタで移行結果が食い違う」負債になっていたので削除した。
//    読み込みは AssetMigrationGateway を通すだけでよい。
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
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.Migration;

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
    /// <summary>
    /// スキーマバージョン。保存時は常に現行版（<see cref="AssetFormats.InputMap"/> の表）。
    /// **クラスの先頭に宣言してあるので、直列化でもトップレベルの先頭に出る**
    /// （版を先頭に置くのは差分を読みやすくするため。順序を変えないこと）。
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = AssetFormats.InputMap.CurrentVersion;

    /// <summary>定義済みアクションのリスト。</summary>
    [JsonPropertyName("actions")]
    public List<InputAction> Actions { get; set; } = new();

    /// <summary>未知フィールド（往復で失わないよう保持）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// 読み込めなかったファイルの代わりに作られた空データか。
    ///
    /// <para>
    /// 未来版（新しいエンジンで保存された）・変換失敗のときに真になる。
    /// **真のまま保存してはいけない**（中身が空なので、利用者のアクション定義が全部消える）。
    /// 呼び出し側は編集させずに閉じること。
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool IsUnreadable { get; private set; }

    // ── 永続化 ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// JSON ファイルからロードする。
    ///
    /// <para>
    /// 古い版（v1）はランタイムの変換段を通してから解釈する（メモリ上だけ。ファイルは書き換えない）。
    /// ファイルが存在しない場合は空データを返す。未来版・変換失敗のときは
    /// 空データに <see cref="IsUnreadable"/> を立てて返す（呼び出し側は保存させないこと）。
    /// </para>
    /// </summary>
    /// <param name="path">読み込む .inputmap の絶対パス。</param>
    public static InputMapData LoadFrom(string path)
    {
        var read = AssetMigrationGateway.ReadFile(path, AssetFormats.InputMap);
        if (read.Status == AssetReadStatus.Missing) return new InputMapData();
        if (read.IsBlocked) return new InputMapData { IsUnreadable = true };
        if (string.IsNullOrWhiteSpace(read.Text)) return new InputMapData();

        try
        {
            var data = JsonSerializer.Deserialize<InputMapData>(read.Text) ?? new InputMapData();
            // 変換済みのテキストを読んだので、ここでは現行版を名乗ってよい。
            data.Version = AssetFormats.InputMap.CurrentVersion;
            return data;
        }
        catch (JsonException)
        {
            // 壊れた JSON（従来どおり空データで開く。門は壊れたテキストを素通しする）。
            return new InputMapData();
        }
    }

    /// <summary>
    /// JSON ファイルへ保存する（常に現行版を刻む）。
    ///
    /// <para>
    /// 書き込みは <see cref="SEEDEditor.Assets.SafeFileWriter"/> 経由の原子的置換
    /// （旧版を .backup/ へ退避 → .tmp へ書き切って rename）で行う。
    /// </para>
    /// </summary>
    /// <param name="path">保存先の絶対パス。</param>
    /// <param name="assetsRoot">
    /// アセットルート（バックアップの置き場を &lt;assets&gt;/.backup/ にそろえるために使う）。
    /// null ならファイルの隣に .backup フォルダができる。
    /// </param>
    public void SaveTo(string path, string? assetsRoot = null)
    {
        Version = AssetFormats.InputMap.CurrentVersion;
        // 型に無関係なグループを null 化して出力をクリーンに保つ。
        foreach (var action in Actions) action.PrepareForSave();
        var json = JsonSerializer.Serialize(this, SerializeOptions);
        SEEDEditor.Assets.SafeFileWriter.WriteAllTextAtomic(path, json, assetsRoot);
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

    // ── 保存整形 ─────────────────────────────────────────────
    //
    //  【v1 → v2 の移行はここには無い】
    //  以前は MigrateFromV1 / ExpandWasd がここで同じ変換を行っていたが、
    //  ランタイム（migration/steps/inputmap/v1_to_v2.rs）と二重実装になっており、
    //  片方だけ直すと移行結果が食い違う負債だった。変換は Rust 一本に集約したので、
    //  **ここへ移行処理を書き戻さないこと**（docs/asset_migration.md 1 章「変換の場所」）。

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
