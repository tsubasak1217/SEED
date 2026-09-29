// ============================================================
//  PlayRequirements.cs — Google Play の要件の表（runtime/android/play_requirements.json）の読み込み（段階D。docs/android.md §24）
//
//  Google Play の方針（targetSdk の下限・16 KB ページ・予約されたアプリ ID・権限ごとの申告〈W1-2〉等）は毎年変わるので、コードに書かずに
//  データの表に置く。要件チェック（AndroidRequirementChecks・AndroidArtifactChecks・AndroidPlatformFeatureChecks）はこの表だけを見て判定する。
//  表が無い・壊れているときは判定できないので、要件チェックはその旨の「不合格」1 件だけを返す（ビルドは止めない）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Android.Release;

/// <summary>targetSdk の要件。</summary>
public sealed record PlayTargetSdkRequirement
{
    /// <summary>新規・更新に求められる最小の API レベル。</summary>
    [JsonPropertyName("min_api_level")] public int MinApiLevel { get; init; }

    /// <summary>その要件が始まった日（yyyy-MM-dd）。</summary>
    [JsonPropertyName("required_since")] public string? RequiredSince { get; init; }

    /// <summary>延長を申請したときの期限（yyyy-MM-dd）。</summary>
    [JsonPropertyName("extension_until")] public string? ExtensionUntil { get; init; }

    /// <summary>補足（表示用）。</summary>
    [JsonPropertyName("note")] public string? Note { get; init; }
}

/// <summary>
/// 権限ごとの Google Play の方針の注意（W1-2。例 USE_EXACT_ALARM は目覚まし・カレンダーのアプリとして Play Console で申告が要る）。
/// 配布物（とビルドの前はプラットフォーム機能から決まる権限）にこの権限があれば、要件チェックの一覧に注意として出す
/// （Release/AndroidPlatformFeatureChecks）。
/// </summary>
public sealed record PlayPermissionPolicy
{
    /// <summary>判定の表記「知らせ」（<see cref="Severity"/> の値）。</summary>
    public const string InfoSeverity = "info";

    /// <summary>判定の表記「注意」（<see cref="Severity"/> の既定値）。</summary>
    public const string WarningSeverity = "warning";

    /// <summary>権限の名前（例 android.permission.USE_EXACT_ALARM）。</summary>
    [JsonPropertyName("permission")] public string Permission { get; init; } = string.Empty;

    /// <summary>判定（info / warning。知らない値は warning）。</summary>
    [JsonPropertyName("severity")] public string Severity { get; init; } = WarningSeverity;

    /// <summary>見出し（無ければ権限の名前の最後の区切り）。</summary>
    [JsonPropertyName("title")] public string? Title { get; init; }

    /// <summary>説明（何を申告・対処するか）。</summary>
    [JsonPropertyName("note")] public string Note { get; init; } = string.Empty;

    /// <summary>出どころ（公式のページ）。</summary>
    [JsonPropertyName("source")] public string? Source { get; init; }
}

/// <summary>Google Play の要件の表。</summary>
public sealed record PlayRequirements
{
    /// <summary>targetSdk。</summary>
    [JsonPropertyName("target_sdk")] public PlayTargetSdkRequirement TargetSdk { get; init; } = new();

    /// <summary>必ず入れる ABI（実機向け）。</summary>
    [JsonPropertyName("required_abis")] public IReadOnlyList<string> RequiredAbis { get; init; } = Array.Empty<string>();

    /// <summary>64 bit の ABI だけか（32 bit の .so を入れるなら 64 bit 版も要る。SEED は 64 bit だけを作る）。</summary>
    [JsonPropertyName("only_64bit_abis")] public bool Only64BitAbis { get; init; } = true;

    /// <summary>.so の LOAD セグメントと、非圧縮の .so の zip 内の位置に求める整列（バイト。16 KB ページ）。</summary>
    [JsonPropertyName("page_size_bytes")] public long PageSizeBytes { get; init; }

    /// <summary>16 KB ページの補足（表示用）。</summary>
    [JsonPropertyName("page_size_note")] public string? PageSizeNote { get; init; }

    /// <summary>Google Play が受け付けないアプリ ID の前半（com.example. 等）。</summary>
    [JsonPropertyName("reserved_application_id_prefixes")] public IReadOnlyList<string> ReservedApplicationIdPrefixes { get; init; } = Array.Empty<string>();

    /// <summary>エンジンの既定のアプリ ID（配布するゲームは自分の ID にする）。</summary>
    [JsonPropertyName("engine_default_application_ids")] public IReadOnlyList<string> EngineDefaultApplicationIds { get; init; } = Array.Empty<string>();

    /// <summary>release に入っていてはおかしい権限（INTERNET はデバッグ版のエディタとの IPC 用だけ）。</summary>
    [JsonPropertyName("release_unexpected_permissions")] public IReadOnlyList<string> ReleaseUnexpectedPermissions { get; init; } = Array.Empty<string>();

    /// <summary>デバッグ用の鍵の証明書の名前の目印（これで署名された配布物は Google Play へ出せない）。</summary>
    [JsonPropertyName("debug_certificate_subject_marker")] public string DebugCertificateSubjectMarker { get; init; } = string.Empty;

    /// <summary>権限ごとの Google Play の方針の注意（W1-2。無ければ空）。</summary>
    [JsonPropertyName("permission_policies")] public IReadOnlyList<PlayPermissionPolicy> PermissionPolicies { get; init; } = Array.Empty<PlayPermissionPolicy>();

    /// <summary>出どころ（公式のページ）。</summary>
    [JsonPropertyName("sources")] public IReadOnlyDictionary<string, string> Sources { get; init; } = new Dictionary<string, string>();

    /// <summary>読み込みの設定（コメント・末尾のカンマを許す）。</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 表を読む（無い・壊れている・中身が足りないときは null と理由）。
    /// </summary>
    /// <param name="path">play_requirements.json のパス。</param>
    /// <param name="error">読めなかった理由（読めたら null）。</param>
    /// <returns>要件の表（読めなければ null）。</returns>
    public static PlayRequirements? Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = $"Google Play の要件の表がありません（{path}）。";
                return null;
            }
            var loaded = JsonSerializer.Deserialize<PlayRequirements>(File.ReadAllText(path), ReadOptions);
            if (loaded is null || loaded.TargetSdk.MinApiLevel <= 0 || loaded.PageSizeBytes <= 0)
            {
                error = $"Google Play の要件の表（{path}）に target_sdk.min_api_level・page_size_bytes がありません。";
                return null;
            }
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"Google Play の要件の表（{path}）を読めません: {ex.Message}";
            return null;
        }
    }
}
