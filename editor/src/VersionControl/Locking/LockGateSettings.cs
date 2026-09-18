// ============================================================
//  LockGateSettings.cs — ロックのゲートの調整値と、利用者が変えられる設定
//
//  【役割】
//  ・強制方針（止める／注意のみ）を利用者が切り替えられるようにする
//  ・ゲートが使う時間（問い合わせの期限・照会結果の寿命）を 1 か所へ集める
//
//  【置き場】
//  `editor/settings/locking.json`。アカウント設定
//  （`editor/settings/accounts.json`）と同じ流儀にしてある。
//  プロジェクトを跨いで共有される値なのでプロジェクトフォルダには置かない。
//
//  【なぜ期限が短いのか（重要）】
//  保存のたびにサーバへ 1 往復する（実測 0.4 秒前後）。サーバが無応答のとき、
//  ロックの既定の期限（<see cref="VersionControlSettings.DEFAULT_REMOTE_OPERATION_TIMEOUT_MS"/>
//  = 2 分）をそのまま使うと、Ctrl+S でエディタが 2 分固まる。
//  ゲートは「分からなければ通す」ので、短い期限で「サーバに繋がらない」へ
//  倒すほうが正しい。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.VersionControl.Locking;

/// <summary>
/// ロックのゲートの設定（`editor/settings/locking.json`）。
/// </summary>
public sealed class LockGateSettings
{
    // ── 調整値（定数）──────────────────────────────────────
    //  使用箇所で数値を直書きしないこと。

    /// <summary>設定ファイル名。</summary>
    public const string FILE_NAME = "locking.json";

    /// <summary>
    /// ゲートがサーバへ問い合わせるときの期限 [ms]。
    /// これを超えたら「サーバに繋がらない」とみなして通す。
    /// </summary>
    public const int DEFAULT_GATE_TIMEOUT_MS = 5_000;

    /// <summary>
    /// 照会結果を使い回してよい時間 [ms]。
    /// 連続保存（例: シーンとシーン設定が続けて保存される）で
    /// 同じパスを何度も問い合わせないための短い寿命。
    /// </summary>
    public const int DEFAULT_STATUS_CACHE_TTL_MS = 3_000;

    /// <summary>
    /// 同じ理由・同じパスの注意を出し直すまでの間隔 [ms]。
    /// 匿名やオフラインのときに保存のたび注意を出すと、
    /// すぐに読まれなくなる（＝本当に必要な注意も埋もれる）。
    /// </summary>
    public const int DEFAULT_WARNING_REPEAT_MS = 60_000;

    /// <summary>
    /// 自動ロックの解放をエディタ終了時に待つ上限 [ms]。
    /// 解放できなくても終了は止めない（残っても他の人が困るだけで、データは失われない）。
    /// </summary>
    public const int DEFAULT_RELEASE_WAIT_MS = 3_000;

    /// <summary>方針の設定値: 止める。</summary>
    public const string POLICY_VALUE_ENFORCE = "enforce";

    /// <summary>方針の設定値: 注意のみ。</summary>
    public const string POLICY_VALUE_WARN_ONLY = "warn_only";

    /// <summary>JSON の書式（人が直接編集するので整形する）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    // ── 設定項目 ────────────────────────────────────────────

    /// <summary>
    /// 強制方針。<c>"enforce"</c>（既定）か <c>"warn_only"</c>。
    ///
    /// <para>
    /// 文字列で持つのは、設定ファイルを人が読んで意味が分かるようにするため
    /// （数値だと 0 / 1 の意味が分からない）。未知の値は既定の
    /// <see cref="LockEnforcementPolicy.Enforce"/> として読む。
    /// </para>
    /// </summary>
    [JsonPropertyName("enforcement")]
    public string Enforcement { get; set; } = POLICY_VALUE_ENFORCE;

    /// <summary>
    /// シーン・アクターを開いたときに自動でロックを取るか。既定は真。
    /// 切ると「編集中」の表示も出なくなる（手動ロックは従来どおり使える）。
    /// </summary>
    [JsonPropertyName("auto_lock_opened_documents")]
    public bool AutoLockOpenedDocuments { get; set; } = true;

    /// <summary>設定された強制方針（未知の値は既定へ倒す）。</summary>
    [JsonIgnore]
    public LockEnforcementPolicy Policy
        => string.Equals(Enforcement?.Trim(), POLICY_VALUE_WARN_ONLY,
                         StringComparison.OrdinalIgnoreCase)
            ? LockEnforcementPolicy.WarnOnly
            : LockEnforcementPolicy.Enforce;

    /// <summary>ゲートの問い合わせ期限。</summary>
    [JsonIgnore]
    public static TimeSpan GateTimeout { get; } =
        TimeSpan.FromMilliseconds(DEFAULT_GATE_TIMEOUT_MS);

    /// <summary>照会結果の寿命。</summary>
    [JsonIgnore]
    public static TimeSpan StatusCacheTtl { get; } =
        TimeSpan.FromMilliseconds(DEFAULT_STATUS_CACHE_TTL_MS);

    /// <summary>同じ注意を出し直すまでの間隔。</summary>
    [JsonIgnore]
    public static TimeSpan WarningRepeatInterval { get; } =
        TimeSpan.FromMilliseconds(DEFAULT_WARNING_REPEAT_MS);

    /// <summary>自動ロックの解放を待つ上限。</summary>
    [JsonIgnore]
    public static TimeSpan ReleaseWait { get; } =
        TimeSpan.FromMilliseconds(DEFAULT_RELEASE_WAIT_MS);

    /// <summary>既定値だけで構成した設定。</summary>
    public static LockGateSettings Default { get; } = new();

    /// <summary>設定ファイルの絶対パスを返す。</summary>
    /// <param name="settingsDir">エディタの設定フォルダ。</param>
    public static string FilePath(string settingsDir) => Path.Combine(settingsDir, FILE_NAME);

    /// <summary>
    /// 設定を読む。無い・壊れている場合は既定値を返す
    /// （設定ファイルが 1 つ壊れただけでエディタが起動できなくなってはいけない）。
    /// </summary>
    /// <param name="settingsDir">エディタの設定フォルダ。</param>
    public static LockGateSettings Load(string? settingsDir)
    {
        if (string.IsNullOrWhiteSpace(settingsDir)) return new LockGateSettings();

        try
        {
            var path = FilePath(settingsDir);
            if (!File.Exists(path)) return new LockGateSettings();

            return JsonSerializer.Deserialize<LockGateSettings>(
                       File.ReadAllText(path), JsonOptions)
                   ?? new LockGateSettings();
        }
        catch (Exception)
        {
            return new LockGateSettings();
        }
    }

    /// <summary>
    /// 設定を書く。失敗しても例外を投げない（保存できないのは致命的ではない）。
    /// </summary>
    /// <param name="settingsDir">エディタの設定フォルダ。</param>
    /// <returns>書けたら真。</returns>
    public bool Save(string settingsDir)
    {
        try
        {
            Directory.CreateDirectory(settingsDir);
            File.WriteAllText(FilePath(settingsDir), JsonSerializer.Serialize(this, JsonOptions));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
