// ============================================================
//  VersionControlSettings.cs — バージョン管理まわりの設定値の唯一の置き場
//
//  【役割】
//  タイムアウト・デバウンス時間・再試行回数といった「調整しうる数値」を
//  1 か所へ集める。プロバイダ実装やサービスの中に数値を直接書かない
//  （データドリブン: 挙動の調整はこのファイルの値の差し替えだけで済む）。
//
//  【なぜインスタンスなのか】
//  静的定数にすると単体テストで短いタイムアウトへ差し替えられない。
//  既定値を持つ不変オブジェクトにしておき、テストや将来のプロジェクト設定から
//  別の値を渡せるようにしてある。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;

namespace SEEDEditor.VersionControl;

/// <summary>
/// バージョン管理の動作パラメータ（不変）。
/// </summary>
public sealed class VersionControlSettings
{
    // ── 既定値（定数）────────────────────────────────────────
    //  すべて「この 1 か所だけ」に書く。使用箇所で数値を直書きしないこと。

    /// <summary>ローカルだけで完結する操作（status / stage / dirty）の既定タイムアウト [ms]。</summary>
    public const int DEFAULT_LOCAL_OPERATION_TIMEOUT_MS = 30_000;

    /// <summary>サーバ往復を伴う操作（push / sync / lock / 履歴）の既定タイムアウト [ms]。</summary>
    public const int DEFAULT_REMOTE_OPERATION_TIMEOUT_MS = 120_000;

    /// <summary>
    /// 状態の再取得をまとめるデバウンス時間 [ms]。
    /// 保存が連続したときに status を毎回走らせないための待ち。
    /// </summary>
    public const int DEFAULT_STATUS_DEBOUNCE_MS = 400;

    /// <summary>履歴の既定取得件数（0 は「無制限」を意味するので必ず正の値にする）。</summary>
    public const int DEFAULT_HISTORY_LENGTH = 100;

    /// <summary>
    /// 履歴のうち、メッセージ・作者・日時（リビジョンのメタデータ）まで補う件数の上限。
    ///
    /// <para>
    /// ★Lore の <c>revision history</c> は番号とハッシュしか返さないため、
    /// メッセージ等は <c>revision metadata list</c> を **1 リビジョンにつき 1 回**
    /// 呼んで補う。往復が件数分かかるので、パネルが最初に見せる範囲だけに絞る。
    /// これを超えた分は番号だけが並ぶ（表示はできる）。
    /// </para>
    /// </summary>
    public const int DEFAULT_HISTORY_METADATA_LIMIT = 30;

    /// <summary>
    /// ワーカーを停止するときに実行中の操作を待つ上限 [ms]。
    /// これを超えたら待たずに諦める（エディタ終了を止めないため）。
    /// </summary>
    public const int DEFAULT_SHUTDOWN_WAIT_MS = 5_000;

    /// <summary>Lore の作業コピーを示すフォルダ名（この有無でプロバイダを決める）。</summary>
    public const string LORE_METADATA_DIR_NAME = ".lore";

    /// <summary>Lore の作業コピー設定ファイル名（`.lore/` 直下）。</summary>
    public const string LORE_CONFIG_FILE_NAME = "config.toml";

    /// <summary>
    /// リポジトリ ID が入ったファイル名（`.lore/` 直下）。
    /// SEED アカウントの権限はこの ID 単位で持つ（docs/seed_accounts.md 3 章）。
    /// </summary>
    public const string LORE_ID_FILE_NAME = "id";

    /// <summary>
    /// `.lore/id` のバイト数。
    /// ★中身は **生のバイト列**であってテキストではない。
    /// API へ渡す `repository_id` は、これを 16 進小文字にした 32 文字。
    /// </summary>
    public const int REPOSITORY_ID_BYTE_LENGTH = 16;

    // ── プロパティ ──────────────────────────────────────────

    /// <summary>ローカル操作のタイムアウト。</summary>
    public TimeSpan LocalOperationTimeout { get; }

    /// <summary>サーバ往復を伴う操作のタイムアウト。</summary>
    public TimeSpan RemoteOperationTimeout { get; }

    /// <summary>状態再取得のデバウンス時間。</summary>
    public TimeSpan StatusDebounce { get; }

    /// <summary>履歴の既定取得件数。</summary>
    public int HistoryLength { get; }

    /// <summary>履歴のうちメタデータまで補う件数の上限。</summary>
    public int HistoryMetadataLimit { get; }

    /// <summary>ワーカー停止時に実行中の操作を待つ上限。</summary>
    public TimeSpan ShutdownWait { get; }

    // ── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// 設定値を指定して生成する。省略した項目は既定値になる。
    /// </summary>
    /// <param name="localOperationTimeout">ローカル操作のタイムアウト。</param>
    /// <param name="remoteOperationTimeout">サーバ往復を伴う操作のタイムアウト。</param>
    /// <param name="statusDebounce">状態再取得のデバウンス時間。</param>
    /// <param name="historyLength">履歴の既定取得件数。</param>
    /// <param name="shutdownWait">ワーカー停止時の待ち上限。</param>
    /// <param name="historyMetadataLimit">履歴のうちメタデータまで補う件数の上限。</param>
    public VersionControlSettings(
        TimeSpan? localOperationTimeout  = null,
        TimeSpan? remoteOperationTimeout = null,
        TimeSpan? statusDebounce         = null,
        int?      historyLength          = null,
        TimeSpan? shutdownWait           = null,
        int?      historyMetadataLimit   = null)
    {
        LocalOperationTimeout  = localOperationTimeout
            ?? TimeSpan.FromMilliseconds(DEFAULT_LOCAL_OPERATION_TIMEOUT_MS);
        RemoteOperationTimeout = remoteOperationTimeout
            ?? TimeSpan.FromMilliseconds(DEFAULT_REMOTE_OPERATION_TIMEOUT_MS);
        StatusDebounce         = statusDebounce
            ?? TimeSpan.FromMilliseconds(DEFAULT_STATUS_DEBOUNCE_MS);
        HistoryLength          = historyLength  ?? DEFAULT_HISTORY_LENGTH;
        ShutdownWait           = shutdownWait
            ?? TimeSpan.FromMilliseconds(DEFAULT_SHUTDOWN_WAIT_MS);
        HistoryMetadataLimit   = historyMetadataLimit ?? DEFAULT_HISTORY_METADATA_LIMIT;
    }

    /// <summary>既定値だけで構成した設定。</summary>
    public static VersionControlSettings Default { get; } = new();
}
