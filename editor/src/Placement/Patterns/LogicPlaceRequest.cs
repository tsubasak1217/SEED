using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Placement.Patterns;

/// <summary>
/// <c>LOGIC_PLACE:{json}</c> で送るリクエスト本体。
///
/// <para>
/// Rust 側 <c>LogicPlaceRequest</c>
/// （runtime/src/engine/core/app_base/app/logic_placement_ops.rs）と
/// 1 対 1 に対応する。プロパティ名は serde のフィールド名に合わせてある。
/// </para>
///
/// <para>
/// <b>1 コマンドで送る理由</b>: 点列生成・地形接地・アクタ生成はランタイムに
/// しか材料が無く、点ごとに往復させると Undo が多数件に割れる。
/// 「1 コマンド = 1 Undo」を守るため一括で渡す。
/// </para>
///
/// <para>WPF に依存しない（テストプロジェクトが直接リンクするため）。</para>
/// </summary>
public sealed class LogicPlaceRequest
{
    // ── 配置対象の識別子（Rust 側の定数と一致させること）──────

    /// <summary>配置対象: 新規アクタ群を生成する。</summary>
    public const string TargetActors = "actors";

    /// <summary>配置対象: ControlPoint の点列へ追記する。</summary>
    public const string TargetControlPoints = "control_points";

    // ── フィールド ────────────────────────────────────────────

    /// <summary>配置対象（<see cref="TargetActors"/> / <see cref="TargetControlPoints"/>）。</summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = TargetActors;

    /// <summary>2D 配置かどうか。</summary>
    [JsonPropertyName("is_2d")]
    public bool Is2D { get; set; }

    /// <summary>右クリック対象アクタの DFS id（ルート配置なら null）。</summary>
    [JsonPropertyName("parent_dfs")]
    public uint? ParentDfs { get; set; }

    /// <summary>生成するグループフォルダ名。</summary>
    [JsonPropertyName("group_name")]
    public string GroupName { get; set; } = "";

    /// <summary>生成アクタ名の接頭辞（<c>{prefix}_01</c> のように連番が付く）。</summary>
    [JsonPropertyName("name_prefix")]
    public string NamePrefix { get; set; } = "";

    /// <summary>配置元アクタファイル（assets 相対の仮想パス）。空アクタなら null。</summary>
    [JsonPropertyName("source_path")]
    public string? SourcePath { get; set; }

    /// <summary>地形へ接地させるか（3D のみ有効）。</summary>
    [JsonPropertyName("ground")]
    public bool Ground { get; set; }

    /// <summary>ControlPoint 追記時の対象アクタ DFS id。</summary>
    [JsonPropertyName("actor_dfs_id")]
    public uint ActorDfsId { get; set; }

    /// <summary>ControlPoint 追記時の対象スロット添字。</summary>
    [JsonPropertyName("slot_idx")]
    public uint SlotIdx { get; set; }

    /// <summary>パターン指定。</summary>
    [JsonPropertyName("spec")]
    public PlacementSpec Spec { get; set; } = new();

    /// <summary>
    /// IPC のシリアライズ設定。
    ///
    /// <b>インデント無し（1 行）が必須。</b>IPC は行区切りで届くため、
    /// 改行が混ざるとコマンドが途中で切れる。
    /// </summary>
    private static readonly JsonSerializerOptions IpcJsonOptions = new()
    {
        WriteIndented = false,
        // 日本語（グループ名・接頭辞）が \uXXXX へ膨らむのを避ける。
        // ランタイム側は serde_json が UTF-8 をそのまま読むので問題ない。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// ランタイムへ送る 1 行のコマンド文字列（<c>LOGIC_PLACE:{json}</c>）を組み立てる。
    ///
    /// <para>
    /// こちらは<b>即時生成</b>（基準点を伴わない＝アクタ原点／ワールド原点基準）の経路。
    /// エディタの通常操作はアクタ配置・制御点追記のどちらも
    /// <see cref="ToBeginIpcCommand"/>（配置モード）を通るため、
    /// 現在この経路を使うのは自動化・外部ツールからの一括投入だけである。
    /// </para>
    /// </summary>
    public string ToIpcCommand() => "LOGIC_PLACE:" + JsonSerializer.Serialize(this, IpcJsonOptions);

    /// <summary>
    /// ランタイムを<b>配置モード</b>へ入れるコマンド（<c>LOGIC_PLACE_BEGIN:{json}</c>）を組み立てる。
    ///
    /// <para>
    /// ペイロードは <see cref="ToIpcCommand"/> とまったく同じ（基準点は含まない）。
    /// 受け取ったランタイムはカーソル追従のプレビューを出し、左クリックで確定・
    /// 右クリック / Esc で取消する。<b>基準点はカーソルの着弾位置</b>で決まる。
    /// </para>
    /// </summary>
    public string ToBeginIpcCommand()
        => "LOGIC_PLACE_BEGIN:" + JsonSerializer.Serialize(this, IpcJsonOptions);
}
