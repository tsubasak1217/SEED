// ============================================================
//  AndroidRunState.cs — プロジェクトごとの Android の実行状態（前回の実行先・端末へ入れた APK・前回の実行の結果）
//
//  【置き場】
//  &lt;プロジェクト&gt;/cache/android/run_state.json（docs/project_system.md の cache/ の規約。実行時に作られる生成物で、
//  失っても「前回の実行先が選ばれていない」「APK を入れ直す」だけ。バージョン管理から除外してよい）。
//  プロジェクトが無いとき（アセットも指定しない開発用のビルド）は runtime/android/app/build/seed/run_state.json。
//
//  【中身】
//    last_target … 最後に使った実行先（エディタの実行先セレクタの既定値に使う。段階C-2）
//    editor_target … エディタの実行先セレクタで最後に選んだもの（"pc"・"auto"（Android（自動）。段階C-3）・端末のシリアル。
//                   段階C-2。PC を選んだことも覚えるため last_target とは別に持つ。SeedAndroid は読まない・書き戻すときは保つ）
//    installs    … 端末（シリアル）ごとに、最後に自分が入れた APK の SHA-256 と、入れた直後の pm path
//                   （インストールを飛ばしてよいかの判断。Plan/AndroidBuildPlan.cs）
//    last_run    … 最後の実行の結果（工程ごとの判断・所要時間・その時の入力の指紋）
//    ipc_launches … 端末（シリアル）ごとに、最後に起動したアプリの IPC のポートと接続トークン（段階D-1。起動の工程が
//                   起動の直後に書く。SeedAndroid の pause / resume / screenshot がつなぐときに使う。起動のたびに変わる）
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Android.State;

/// <summary>最後に使った実行先。</summary>
public sealed record AndroidTargetRecord
{
    /// <summary>シリアル。</summary>
    [JsonPropertyName("serial")] public required string Serial { get; init; }

    /// <summary>種類（physical / emulator）。</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    /// <summary>機種。</summary>
    [JsonPropertyName("model")] public string? Model { get; init; }

    /// <summary>使った ABI。</summary>
    [JsonPropertyName("abi")] public string? Abi { get; init; }

    /// <summary>アプリ ID。</summary>
    [JsonPropertyName("application_id")] public required string ApplicationId { get; init; }

    /// <summary>使った日時。</summary>
    [JsonPropertyName("used_at")] public DateTimeOffset UsedAt { get; init; }
}

/// <summary>端末へ入れた APK の記録。</summary>
public sealed record AndroidInstallStateRecord
{
    /// <summary>アプリ ID。</summary>
    [JsonPropertyName("application_id")] public required string ApplicationId { get; init; }

    /// <summary>入れた APK の SHA-256。</summary>
    [JsonPropertyName("apk_sha256")] public required string ApkSha256 { get; init; }

    /// <summary>入れた直後の pm path（base.apk の場所。インストールのたびに変わる）。</summary>
    [JsonPropertyName("installed_path")] public required string InstalledPath { get; init; }

    /// <summary>入れた日時。</summary>
    [JsonPropertyName("installed_at")] public DateTimeOffset InstalledAt { get; init; }
}

/// <summary>端末で最後に起動したアプリの IPC の記録（段階D-1。起動のたびに書き換わる）。</summary>
public sealed record AndroidIpcLaunchRecord
{
    /// <summary>起動したアプリ ID。</summary>
    [JsonPropertyName("application_id")] public required string ApplicationId { get; init; }

    /// <summary>端末のランタイムが待ち受けるポート。</summary>
    [JsonPropertyName("ipc_port")] public required int IpcPort { get; init; }

    /// <summary>接続トークン（起動ごとの使い捨て。接続の最初の行 HELLO:&lt;トークン&gt; で照合される）。</summary>
    [JsonPropertyName("ipc_token")] public required string IpcToken { get; init; }

    /// <summary>起動した日時。</summary>
    [JsonPropertyName("launched_at")] public DateTimeOffset LaunchedAt { get; init; }
}

/// <summary>最後の実行の 1 工程。</summary>
public sealed record AndroidRunStepRecord
{
    /// <summary>工程。</summary>
    [JsonPropertyName("phase")] public required string Phase { get; init; }

    /// <summary>結果（succeeded / skipped / failed / canceled）。</summary>
    [JsonPropertyName("outcome")] public required string Outcome { get; init; }

    /// <summary>行った・飛ばした理由。</summary>
    [JsonPropertyName("reason")] public string? Reason { get; init; }

    /// <summary>所要時間（秒）。</summary>
    [JsonPropertyName("seconds")] public double Seconds { get; init; }
}

/// <summary>最後の実行の結果。</summary>
public sealed record AndroidLastRunRecord
{
    /// <summary>目的（build / install / run / push）。</summary>
    [JsonPropertyName("goal")] public required string Goal { get; init; }

    /// <summary>始めた日時。</summary>
    [JsonPropertyName("started_at")] public DateTimeOffset StartedAt { get; init; }

    /// <summary>終えた日時。</summary>
    [JsonPropertyName("finished_at")] public DateTimeOffset FinishedAt { get; init; }

    /// <summary>結果（succeeded / failed / canceled）。</summary>
    [JsonPropertyName("result")] public required string Result { get; init; }

    /// <summary>失敗の説明。</summary>
    [JsonPropertyName("message")] public string? Message { get; init; }

    /// <summary>工程ごとの結果。</summary>
    [JsonPropertyName("steps")] public IReadOnlyList<AndroidRunStepRecord> Steps { get; init; } = Array.Empty<AndroidRunStepRecord>();

    /// <summary>その時の各工程の入力の指紋と出力の同一性（キーは AndroidStepKeys）。</summary>
    [JsonPropertyName("fingerprints")]
    public IReadOnlyDictionary<string, AndroidStepFingerprint> Fingerprints { get; init; } = new Dictionary<string, AndroidStepFingerprint>();
}

/// <summary>プロジェクトごとの Android の実行状態。</summary>
public sealed class AndroidRunState
{
    /// <summary>記録の書式の版（変えたら上げる。違う版は読まない）。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary><see cref="EditorTarget"/> で「PC（この PC で実行）」を表す値。</summary>
    public const string EditorPcTarget = "pc";

    /// <summary>
    /// <see cref="EditorTarget"/> で「Android（自動）」を表す値（段階C-3。中核へ渡すシリアルの "auto" と同じ語）。
    /// </summary>
    public const string EditorAutoTarget = Adb.AndroidDeviceTarget.AutoSerial;

    /// <summary>プロジェクトの cache/ の中の置き場（cache/android/run_state.json）。</summary>
    public static readonly string ProjectRelativePath = Path.Combine("cache", "android", "run_state.json");

    /// <summary>書き出しの設定（人が読める整形。日本語の理由・機種名をエスケープしない）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>書式の版。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>最後に使った実行先。</summary>
    [JsonPropertyName("last_target")]
    public AndroidTargetRecord? LastTarget { get; set; }

    /// <summary>
    /// エディタの実行先セレクタで最後に選んだもの（<see cref="EditorPcTarget"/> か端末のシリアル。未選択なら null）。
    /// 項目を足しただけなので書式の版は変えない（古い記録は null として読む）。
    /// </summary>
    [JsonPropertyName("editor_target")]
    public string? EditorTarget { get; set; }

    /// <summary>シリアル → 最後に自分が入れた APK。</summary>
    [JsonPropertyName("installs")]
    public Dictionary<string, AndroidInstallStateRecord> Installs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>最後の実行の結果。</summary>
    [JsonPropertyName("last_run")]
    public AndroidLastRunRecord? LastRun { get; set; }

    /// <summary>
    /// シリアル → 最後に起動したアプリの IPC のポートと接続トークン（段階D-1）。項目を足しただけなので書式の版は変えない
    /// （古い記録は空として読む）。
    /// </summary>
    [JsonPropertyName("ipc_launches")]
    public Dictionary<string, AndroidIpcLaunchRecord> IpcLaunches { get; set; } = new(StringComparer.Ordinal);

    /// <summary>プロジェクトのルートから記録のファイルの場所を決める。</summary>
    /// <param name="projectRoot">プロジェクトのルート。</param>
    /// <returns>記録のファイル。</returns>
    public static string PathForProject(string projectRoot) => Path.Combine(projectRoot, ProjectRelativePath);

    /// <summary>
    /// 記録のファイルの場所を決める（プロジェクトがあればその cache/、無ければエンジン側の置き場）。
    /// 中核の実行（AndroidRunPipeline）と SeedAndroid の pause / resume / screenshot が同じ場所を使う。
    /// </summary>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <returns>記録のファイル。</returns>
    public static string PathFor(AndroidProjectInfo? project, AndroidEnginePaths engine) =>
        project is not null ? PathForProject(project.Folder.ProjectRoot) : engine.FallbackRunStatePath;

    /// <summary>
    /// その端末で最後に起動したアプリの IPC の記録（アプリ ID が違えば無いものとする）。
    /// </summary>
    /// <param name="serial">シリアル。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <returns>記録（無ければ null）。</returns>
    public AndroidIpcLaunchRecord? IpcLaunchFor(string serial, string applicationId) =>
        IpcLaunches.TryGetValue(serial, out var record) && string.Equals(record.ApplicationId, applicationId, StringComparison.Ordinal)
            ? record
            : null;

    /// <summary>
    /// 記録を読む（無い・壊れている・版が違うときは空の記録）。
    /// </summary>
    /// <param name="path">記録のファイル。</param>
    /// <returns>記録。</returns>
    public static AndroidRunState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AndroidRunState();
            var loaded = JsonSerializer.Deserialize<AndroidRunState>(File.ReadAllText(path), JsonOptions);
            return loaded is { FormatVersion: CurrentFormatVersion } ? loaded : new AndroidRunState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AndroidRunState();
        }
    }

    /// <summary>記録を書く（一時ファイルへ書き切ってから置き換える）。</summary>
    /// <param name="path">記録のファイル。</param>
    public void Save(string path) => AtomicJsonFile.Write(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>その端末へ最後に自分が入れた APK の記録（計画の材料の形）。</summary>
    /// <param name="serial">シリアル。</param>
    /// <returns>記録（無ければ null）。</returns>
    public AndroidInstallRecord? InstallRecordFor(string serial) =>
        Installs.TryGetValue(serial, out var record)
            ? new AndroidInstallRecord(record.ApplicationId, record.ApkSha256, record.InstalledPath)
            : null;
}
