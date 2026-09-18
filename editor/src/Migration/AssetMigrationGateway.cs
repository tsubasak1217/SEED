// ============================================================
//  AssetMigrationGateway.cs — C# の読み手が通る唯一の門
//
//  【役割】
//  「ファイルを読む → 解釈する」のあいだに 1 枚だけ挟まる層。
//    1. 版の欄だけを安く覗く（AssetVersionPeek）
//    2. 現行版なら**何もしない**（ここが 99% の経路。exe を起動しない）
//    3. 古ければ SEED.exe --migrate-json を通し、持ち上がったテキストを返す
//    4. 未来版なら開かずに利用者へ知らせる
//
//  【呼び出し側の規約】
//  <c>var read = AssetMigrationGateway.ReadFile(path, AssetFormats.Anim);</c>
//  <c>if (!read.HasText) return &lt;既定値&gt;;</c> の 2 行で使う。
//  **Blocked（未来版・変換失敗）のときに既定値で上書き保存させないこと**。
//  そのファイルを開いたまま保存すると利用者のデータが消える。
//
//  【読み込みでファイルを書き換えない】
//  変換結果はメモリ上だけで使う。開いただけで全員の作業コピーが書き換わると
//  VCS で衝突するため（docs/asset_migration.md 1 章）。
//  ディスクを書き換えてよいのは「普通に保存したとき」と「一括アップグレード」だけ。
//
//  【依存】
//  WPF に依存しない（単体テストへそのままリンクできる）。
//  画面への提示は <see cref="IMigrationNotifier"/>、ランタイム exe の解決は
//  <see cref="RuntimeExePathProvider"/> を通して外から差し込む。
// ============================================================

using System;
using System.Globalization;
using System.IO;

namespace SEEDEditor.Migration;

// ── 結果 ─────────────────────────────────────────────────────────

/// <summary>読み込みの結末。</summary>
public enum AssetReadStatus
{
    /// <summary>そのまま解釈してよいテキストが得られた（現行版）。</summary>
    UpToDate,

    /// <summary>古い形式を変換して持ち上げたテキストが得られた。</summary>
    Migrated,

    /// <summary>ファイルが存在しない（呼び出し側の既定値でよい）。</summary>
    Missing,

    /// <summary>
    /// 開いてはいけない（未来版・変換失敗）。
    /// 呼び出し側は**既定値で続けず**、編集させないこと。
    /// </summary>
    Blocked,
}

/// <summary>
/// 読み込みの結果（不変）。
/// </summary>
/// <param name="Status">結末。</param>
/// <param name="Text">解釈してよい JSON テキスト（得られなかったときは空）。</param>
/// <param name="Message">止めた理由（利用者向け。止めていないときは空）。</param>
public sealed record AssetReadResult(AssetReadStatus Status, string Text, string Message)
{
    /// <summary>解釈してよいテキストが得られたか。</summary>
    public bool HasText => Status is AssetReadStatus.UpToDate or AssetReadStatus.Migrated;

    /// <summary>開いてはいけない状態か（利用者へ知らせ済み）。</summary>
    public bool IsBlocked => Status == AssetReadStatus.Blocked;
}

// ── 門 ───────────────────────────────────────────────────────────

/// <summary>
/// アセットを読むときのマイグレーションの門（プロセスに 1 つ）。
/// </summary>
public static class AssetMigrationGateway
{
    /// <summary>
    /// ランタイム exe のパスを返す関数（アプリ起動時に差し込む）。
    ///
    /// <para>
    /// 差し込まれていなければ変換は行われず、古い形式のファイルは
    /// <see cref="AssetReadStatus.Blocked"/> になる（黙って旧形式として解釈しない）。
    /// 単体テストとヘッドレスの一部はこの状態で動く。
    /// </para>
    /// </summary>
    public static Func<string?>? RuntimeExePathProvider { get; set; }

    /// <summary>利用者への提示（アプリ起動時に WPF 実装を差し込む。無ければログだけ）。</summary>
    public static IMigrationNotifier? Notifier { get; set; }

    /// <summary>診断ログの出力先（無ければ捨てる）。</summary>
    public static Action<string>? Log { get; set; }

    // ── 公開 API ─────────────────────────────────────────────

    /// <summary>
    /// ファイルを読み、必要なら現行版へ持ち上げたテキストを返す。
    /// </summary>
    /// <param name="path">読み込むファイルの絶対パス。</param>
    /// <param name="format">対象の形式。</param>
    /// <param name="notify">
    /// 止めたときに利用者へ知らせるか。
    /// <para>
    /// **既定は true**。読み込みの失敗を握りつぶして既定値を返す読み手
    /// （InputMapData / ProjectSettingsData / 地形ドキュメント）は、ここで知らせないと
    /// 「空の設定で開いたまま保存して全部消す」事故になるため必ず true で呼ぶ。
    /// </para>
    /// <para>
    /// 逆に、失敗を例外で呼び出し元へ返す読み手（AnimClipIO / SpriteMeshFile）は
    /// 呼び出し元が自前でダイアログを出すので false にする（同じ文言が 2 回出るのを避ける）。
    /// </para>
    /// </param>
    /// <returns>読み込み結果。</returns>
    public static AssetReadResult ReadFile(string? path, AssetFormat format, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new AssetReadResult(AssetReadStatus.Missing, string.Empty, string.Empty);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            // 読めない（権限・排他）。既定値で開くと保存で上書きしてしまうので止める。
            return Block(
                MigrationMessages.MIGRATE_FAILED_TITLE,
                DisplayName(path),
                string.Format(CultureInfo.InvariantCulture,
                    MigrationMessages.MIGRATE_FAILED_FORMAT, DisplayName(path), e.Message),
                notify);
        }

        return MigrateText(text, format, DisplayName(path), notify);
    }

    /// <summary>
    /// すでに手元にある JSON テキストを、必要なら現行版へ持ち上げる。
    /// </summary>
    /// <param name="json">対象の JSON テキスト。</param>
    /// <param name="format">対象の形式。</param>
    /// <param name="displayName">利用者へ見せる名前（ファイル名など）。</param>
    /// <param name="notify">止めたときに利用者へ知らせるか（<see cref="ReadFile"/> の説明を参照）。</param>
    /// <returns>読み込み結果。</returns>
    public static AssetReadResult MigrateText(
        string json, AssetFormat format, string displayName, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(format);

        var peek = AssetVersionPeek.Read(json, format);

        // ── 版を読めなかった ──
        // 壊れた JSON は変換しても直らない。従来どおり呼び出し側の
        // 「壊れていたら既定値」に委ねる（＝テキストはそのまま渡す）。
        // ここで止めると、これまで開けていた壊れかけのファイルが突然開かなくなる。
        if (!peek.IsOk)
        {
            Log?.Invoke($"{MigrationMessages.LOG_PREFIX} {displayName}: "
                        + $"版を読めませんでした（{peek.Status}: {peek.Detail}）。そのまま解釈します。");
            return new AssetReadResult(AssetReadStatus.UpToDate, json, string.Empty);
        }

        // ── 未来版: 開かない ──
        if (peek.Version > format.CurrentVersion)
        {
            return Block(
                MigrationMessages.FUTURE_VERSION_TITLE,
                displayName,
                string.Format(CultureInfo.InvariantCulture,
                    MigrationMessages.FUTURE_VERSION_FORMAT,
                    displayName, format.Label, peek.Version, format.CurrentVersion),
                notify);
        }

        // ── 現行版: 何もしない（最も多い経路。exe を起動しない）──
        if (peek.Version == format.CurrentVersion)
            return new AssetReadResult(AssetReadStatus.UpToDate, json, string.Empty);

        // ── 古い版: ランタイムへ通す ──
        var result = MigrateJsonRunner.Run(ResolveRuntimeExePath(), format, json);
        if (result.IsSuccess)
        {
            Log?.Invoke($"{MigrationMessages.LOG_PREFIX} {displayName}: "
                        + $"{format.Label} v{peek.Version} → v{format.CurrentVersion} へ変換して読み込みました。");
            return new AssetReadResult(AssetReadStatus.Migrated, result.Json, string.Empty);
        }

        // 形式名の綴り違いは呼び出し側の不具合。利用者には整えた文言を出し、
        // 原因が分かる生のメッセージはログへ残す。
        if (result.Outcome == MigrateJsonOutcome.BadUsage)
        {
            Log?.Invoke($"{MigrationMessages.LOG_PREFIX} "
                        + string.Format(CultureInfo.InvariantCulture,
                                        MigrationMessages.MIGRATE_BAD_USAGE_FORMAT, result.Message));
        }

        // 未来版はランタイム側の判定でも起こり得る（先読みが読めない綴りだった場合など）。
        var title = result.Outcome == MigrateJsonOutcome.FutureVersion
            ? MigrationMessages.FUTURE_VERSION_TITLE
            : MigrationMessages.MIGRATE_FAILED_TITLE;

        return Block(title, displayName, string.Format(CultureInfo.InvariantCulture,
            MigrationMessages.MIGRATE_FAILED_FORMAT, displayName, result.Message), notify);
    }

    /// <summary>
    /// ランタイム exe のパスを解決する（差し込まれていなければ null）。
    /// </summary>
    /// <returns>絶対パス。解決できなければ null。</returns>
    public static string? ResolveRuntimeExePath()
    {
        try
        {
            return RuntimeExePathProvider?.Invoke();
        }
        catch (Exception e)
        {
            // 解決の失敗で読み込み経路を落とさない（exe 無しとして扱う）。
            Log?.Invoke($"{MigrationMessages.LOG_PREFIX} ランタイム exe の解決に失敗しました: {e.Message}");
            return null;
        }
    }

    // ── 内部 ─────────────────────────────────────────────────

    /// <summary>
    /// 「開いてはいけない」結果を作り、利用者へ知らせる。
    /// </summary>
    /// <param name="title">ダイアログの見出し。</param>
    /// <param name="displayName">対象の表示名。</param>
    /// <param name="message">利用者へ見せる本文。</param>
    /// <param name="notify">利用者へ知らせるか（呼び出し元が自前で出す場合は false）。</param>
    private static AssetReadResult Block(
        string title, string displayName, string message, bool notify)
    {
        // ログは必ず残す（知らせたかどうかに関わらず、後から原因を追えるように）。
        Log?.Invoke($"{MigrationMessages.LOG_PREFIX} {displayName} を開きませんでした: "
                    + message.Replace("\r\n", " / ").Replace("\n", " / "));
        if (notify) Notifier?.NotifyBlocked(title, message);
        return new AssetReadResult(AssetReadStatus.Blocked, string.Empty, message);
    }

    /// <summary>利用者へ見せる名前（フルパスは長いのでファイル名だけにする）。</summary>
    /// <param name="path">対象のパス。</param>
    private static string DisplayName(string path)
    {
        try { return Path.GetFileName(path); }
        catch (ArgumentException) { return path; }
    }
}
