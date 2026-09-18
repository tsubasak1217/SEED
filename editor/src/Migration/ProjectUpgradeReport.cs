// ============================================================
//  ProjectUpgradeReport.cs — 一括アップグレードの出力（JSON Lines）の解釈
//
//  【役割】
//  <c>SEED.exe --upgrade-project</c> が 1 行 1 件で出す JSON を、
//  画面に並べられる形へ写し取る。**行の解釈はここだけ**に置き、
//  ウィンドウ側は出来上がったモデルを並べるだけにする。
//
//  【行の種別は kind で見分ける】（docs/asset_migration.md 6.5 (3)）
//    "summary"      … 集計（最後の 1 行）
//    "prefab_hash"  … プレハブの版の貼り直し
//    "error"        … 致命的な失敗（引数が不正・対象が見つからない）
//    それ以外       … 形式名＝ファイル 1 件の結果
//
//  【壊れた行で落ちないこと】
//  ランタイムが将来 1 行増やしても、エディタが例外で止まってはいけない。
//  解釈できない行は <see cref="ProjectUpgradeReport.UnparsedLines"/> へ寄せて先へ進む。
//
//  【依存】
//  WPF に依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SEEDEditor.Migration;

// ── 1 ファイルの結果 ─────────────────────────────────────────────

/// <summary>ファイル 1 件の処理結果（<c>status</c> の値と 1 対 1）。</summary>
public enum UpgradeFileStatus
{
    /// <summary>変換して書き込んだ（dry-run なら「書き込む予定」）。</summary>
    Upgraded,

    /// <summary>既に現行版なので何もしなかった。</summary>
    UpToDate,

    /// <summary>このエンジンより新しい版なので触れなかった。</summary>
    FutureVersion,

    /// <summary>読み取り・変換・書き込みのいずれかに失敗した（書き換えていない）。</summary>
    Failed,

    /// <summary>ランタイムが知らない状態名を返した（将来の拡張）。</summary>
    Unknown,
}

/// <summary>
/// ファイル 1 件のレポート行。
/// </summary>
/// <param name="Kind">形式名（<c>scene</c> / <c>anim</c> …）。</param>
/// <param name="Path">アセットルートの親から見た相対パス。</param>
/// <param name="From">読み込んだときの版（判定できなければ 0）。</param>
/// <param name="To">変換後の版。</param>
/// <param name="Status">結果。</param>
/// <param name="Message">補足（失敗理由・警告。無ければ空）。</param>
public sealed record UpgradeFileLine(
    string Kind, string Path, int From, int To, UpgradeFileStatus Status, string Message);

/// <summary>
/// プレハブの版（<c>prefab_hash</c>）の貼り直し 1 件のレポート行。
/// </summary>
/// <param name="Path">対象シーンの相対パス。</param>
/// <param name="Updated">貼り直したインスタンス数。</param>
/// <param name="Message">補足（無ければ空）。</param>
public sealed record UpgradePrefabHashLine(string Path, int Updated, string Message);

/// <summary>
/// 集計行（最後の 1 行）。
/// </summary>
/// <param name="Total">対象ファイル総数。</param>
/// <param name="Upgraded">変換した（する予定の）件数。</param>
/// <param name="UpToDate">既に現行版だった件数。</param>
/// <param name="FutureVersion">未来版で触れなかった件数。</param>
/// <param name="Failed">失敗した件数。</param>
/// <param name="PrefabHashScenes"><c>prefab_hash</c> を貼り直したシーン数。</param>
/// <param name="PrefabHashUpdated"><c>prefab_hash</c> を貼り直したインスタンス総数。</param>
/// <param name="DryRun">書き込みを行わない実行だったか。</param>
public sealed record UpgradeSummaryLine(
    int Total, int Upgraded, int UpToDate, int FutureVersion, int Failed,
    int PrefabHashScenes, int PrefabHashUpdated, bool DryRun)
{
    /// <summary>対処が必要な結果があったか（終了コードが非 0 になる条件）。</summary>
    public bool HasProblem => Failed > 0 || FutureVersion > 0;
}

// ── レポート全体 ─────────────────────────────────────────────────

/// <summary>
/// 一括アップグレードの出力を解釈した結果。
/// </summary>
public sealed class ProjectUpgradeReport
{
    // ── 行の種別を見分ける印（ランタイムの report.rs と綴りを一致させること）──

    /// <summary>行の種別が入るキー。</summary>
    private const string KEY_KIND = "kind";

    /// <summary>集計行の印。</summary>
    public const string KIND_SUMMARY = "summary";

    /// <summary>プレハブの版の貼り直し行の印。</summary>
    public const string KIND_PREFAB_HASH = "prefab_hash";

    /// <summary>致命的な失敗の行の印。</summary>
    public const string KIND_ERROR = "error";

    // ── 状態の綴り ───────────────────────────────────────────

    /// <summary><see cref="UpgradeFileStatus.Upgraded"/> の綴り。</summary>
    public const string STATUS_UPGRADED = "upgraded";

    /// <summary><see cref="UpgradeFileStatus.UpToDate"/> の綴り。</summary>
    public const string STATUS_UP_TO_DATE = "up_to_date";

    /// <summary><see cref="UpgradeFileStatus.FutureVersion"/> の綴り。</summary>
    public const string STATUS_FUTURE_VERSION = "future_version";

    /// <summary><see cref="UpgradeFileStatus.Failed"/> の綴り。</summary>
    public const string STATUS_FAILED = "failed";

    // ── 欄名 ─────────────────────────────────────────────────

    private const string KEY_PATH                = "path";
    private const string KEY_FROM                = "from";
    private const string KEY_TO                  = "to";
    private const string KEY_STATUS              = "status";
    private const string KEY_MESSAGE             = "message";
    private const string KEY_UPDATED             = "updated";
    private const string KEY_TOTAL               = "total";
    private const string KEY_UPGRADED            = "upgraded";
    private const string KEY_UP_TO_DATE          = "up_to_date";
    private const string KEY_FUTURE_VERSION      = "future_version";
    private const string KEY_FAILED              = "failed";
    private const string KEY_PREFAB_HASH_SCENES  = "prefab_hash_scenes";
    private const string KEY_PREFAB_HASH_UPDATED = "prefab_hash_updated";
    private const string KEY_DRY_RUN             = "dry_run";

    // ── 中身 ─────────────────────────────────────────────────

    /// <summary>ファイル 1 件ずつの結果（出力された順）。</summary>
    public List<UpgradeFileLine> Files { get; } = new();

    /// <summary>プレハブの版の貼り直し（出力された順）。</summary>
    public List<UpgradePrefabHashLine> PrefabHashes { get; } = new();

    /// <summary>集計（最後の 1 行。出ていなければ null）。</summary>
    public UpgradeSummaryLine? Summary { get; private set; }

    /// <summary>致命的な失敗のメッセージ（引数が不正・対象が見つからない）。</summary>
    public List<string> Errors { get; } = new();

    /// <summary>解釈できなかった行（将来の拡張・壊れた出力）。</summary>
    public List<string> UnparsedLines { get; } = new();

    // ── 集計の便利メソッド ───────────────────────────────────

    /// <summary>更新対象（<see cref="UpgradeFileStatus.Upgraded"/>）のファイルだけを返す。</summary>
    public IEnumerable<UpgradeFileLine> UpgradedFiles => Where(UpgradeFileStatus.Upgraded);

    /// <summary>未来版のファイルだけを返す。</summary>
    public IEnumerable<UpgradeFileLine> FutureVersionFiles => Where(UpgradeFileStatus.FutureVersion);

    /// <summary>失敗したファイルだけを返す。</summary>
    public IEnumerable<UpgradeFileLine> FailedFiles => Where(UpgradeFileStatus.Failed);

    /// <summary>指定した状態のファイルを列挙する。</summary>
    /// <param name="status">絞り込む状態。</param>
    private IEnumerable<UpgradeFileLine> Where(UpgradeFileStatus status)
    {
        foreach (var file in Files)
        {
            if (file.Status == status) yield return file;
        }
    }

    /// <summary>
    /// 形式名ごとの更新件数（画面の内訳表示に使う）。ランタイムが出した順序を保つ。
    /// </summary>
    /// <param name="status">数える状態。</param>
    /// <returns>形式名 → 件数（1 件以上あるものだけ）。</returns>
    public IReadOnlyList<KeyValuePair<string, int>> CountByKind(UpgradeFileStatus status)
    {
        var order  = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            if (file.Status != status) continue;
            if (!counts.ContainsKey(file.Kind))
            {
                counts[file.Kind] = 0;
                order.Add(file.Kind);
            }
            counts[file.Kind]++;
        }

        var result = new List<KeyValuePair<string, int>>(order.Count);
        foreach (var kind in order) result.Add(new KeyValuePair<string, int>(kind, counts[kind]));
        return result;
    }

    // ── 解釈 ─────────────────────────────────────────────────

    /// <summary>
    /// 標準出力の行を 1 行ずつ解釈してレポートを組み立てる。
    /// </summary>
    /// <param name="lines">標準出力の行（空行は無視する）。</param>
    /// <returns>組み立てたレポート。</returns>
    public static ProjectUpgradeReport Parse(IEnumerable<string>? lines)
    {
        var report = new ProjectUpgradeReport();
        if (lines is null) return report;

        foreach (var raw in lines)
        {
            var line = raw?.Trim() ?? string.Empty;
            if (line.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!report.Accept(doc.RootElement)) report.UnparsedLines.Add(line);
            }
            catch (JsonException)
            {
                report.UnparsedLines.Add(line);
            }
        }
        return report;
    }

    /// <summary>1 行ぶんの JSON を取り込む。取り込めなければ false。</summary>
    /// <param name="root">1 行ぶんの JSON。</param>
    private bool Accept(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty(KEY_KIND, out var kindEl)) return false;

        var kind = kindEl.GetString() ?? string.Empty;
        switch (kind)
        {
            case KIND_SUMMARY:
                Summary = new UpgradeSummaryLine(
                    ReadInt(root, KEY_TOTAL),
                    ReadInt(root, KEY_UPGRADED),
                    ReadInt(root, KEY_UP_TO_DATE),
                    ReadInt(root, KEY_FUTURE_VERSION),
                    ReadInt(root, KEY_FAILED),
                    ReadInt(root, KEY_PREFAB_HASH_SCENES),
                    ReadInt(root, KEY_PREFAB_HASH_UPDATED),
                    ReadBool(root, KEY_DRY_RUN));
                return true;

            case KIND_PREFAB_HASH:
                PrefabHashes.Add(new UpgradePrefabHashLine(
                    ReadString(root, KEY_PATH),
                    ReadInt(root, KEY_UPDATED),
                    ReadString(root, KEY_MESSAGE)));
                return true;

            case KIND_ERROR:
                Errors.Add(ReadString(root, KEY_MESSAGE));
                return true;

            default:
                // 形式名＝ファイル 1 件の行。status が無ければ解釈できない。
                if (!root.TryGetProperty(KEY_STATUS, out _)) return false;
                Files.Add(new UpgradeFileLine(
                    kind,
                    ReadString(root, KEY_PATH),
                    ReadInt(root, KEY_FROM),
                    ReadInt(root, KEY_TO),
                    ParseStatus(ReadString(root, KEY_STATUS)),
                    ReadString(root, KEY_MESSAGE)));
                return true;
        }
    }

    /// <summary>状態の綴りを列挙へ直す（知らない綴りは <see cref="UpgradeFileStatus.Unknown"/>）。</summary>
    /// <param name="status">ランタイムが出した綴り。</param>
    public static UpgradeFileStatus ParseStatus(string? status) => status switch
    {
        STATUS_UPGRADED       => UpgradeFileStatus.Upgraded,
        STATUS_UP_TO_DATE     => UpgradeFileStatus.UpToDate,
        STATUS_FUTURE_VERSION => UpgradeFileStatus.FutureVersion,
        STATUS_FAILED         => UpgradeFileStatus.Failed,
        _                     => UpgradeFileStatus.Unknown,
    };

    /// <summary>整数の欄を読む（無ければ 0）。</summary>
    /// <param name="root">対象の JSON。</param>
    /// <param name="key">欄名。</param>
    private static int ReadInt(JsonElement root, string key)
        => root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number
           && el.TryGetInt32(out var v) ? v : 0;

    /// <summary>真偽の欄を読む（無ければ false）。</summary>
    /// <param name="root">対象の JSON。</param>
    /// <param name="key">欄名。</param>
    private static bool ReadBool(JsonElement root, string key)
        => root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.True;

    /// <summary>文字列の欄を読む（無ければ空文字）。</summary>
    /// <param name="root">対象の JSON。</param>
    /// <param name="key">欄名。</param>
    private static string ReadString(JsonElement root, string key)
        => root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
           ? el.GetString() ?? string.Empty
           : string.Empty;
}
