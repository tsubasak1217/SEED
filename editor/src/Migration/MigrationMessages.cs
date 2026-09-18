// ============================================================
//  MigrationMessages.cs — マイグレーション機能の文言（1 か所へ集約）
//
//  【役割】
//  ダイアログ・トースト・ログに出す日本語をここだけに置く。
//  画面側へ文字列リテラルを散らすと、言い回しを直すたびに取りこぼしが出る。
//
//  【書き方の方針】（docs/editor_ui_style.md と同じ考え方）
//  ・止めるときは「なぜ止めたか」と「どうすれば進めるか」まで書く。
//  ・自動で進めたときは「何をしたか」を書く（黙って処理しない）。
//  ・ログ行は接頭辞 [マイグレーション] を付ける（grep しやすくするため）。
//
//  【依存】
//  WPF に依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.Migration;

/// <summary>
/// マイグレーション機能の利用者向け文言。
/// </summary>
public static class MigrationMessages
{
    // ── ログ ────────────────────────────────────────────────

    /// <summary>ログ行の接頭辞。</summary>
    public const string LOG_PREFIX = "[マイグレーション]";

    // ── 未来版（新しいエンジンで保存されたファイル）────────────

    /// <summary>未来版のファイルを開けなかったときのダイアログのタイトル。</summary>
    public const string FUTURE_VERSION_TITLE = "このエンジンでは開けません";

    /// <summary>
    /// 未来版のファイルを開けなかった（書式: ファイル名, 形式名, ファイルの版, 対応している版）。
    /// </summary>
    public const string FUTURE_VERSION_FORMAT =
        "{0} は、より新しいバージョンのエンジンで保存されたファイルです"
        + "（{1} 形式 v{2}。このエディタが読めるのは v{3} まで）。\n"
        + "内容を壊さないよう開きませんでした。エディタを更新してから開いてください。";

    // ── 変換の失敗 ──────────────────────────────────────────

    /// <summary>古い形式の変換に失敗したときのダイアログのタイトル。</summary>
    public const string MIGRATE_FAILED_TITLE = "古い形式を変換できません";

    /// <summary>古い形式の変換に失敗した（書式: ファイル名, 理由）。</summary>
    public const string MIGRATE_FAILED_FORMAT =
        "{0} は古い形式のため変換が必要ですが、変換に失敗しました。\n{1}";

    /// <summary>ランタイム exe が見つからず変換できない（書式: 想定していたパス）。</summary>
    public const string RUNTIME_EXE_MISSING_FORMAT =
        "ランタイム（SEED.exe）が見つからないため、古い形式のファイルを変換できません。\n"
        + "想定していた場所: {0}\n"
        + "ランタイムをビルドしてから開き直してください。";

    /// <summary>ランタイム exe のパスをまだ解決できない（起動直後など）。</summary>
    public const string RUNTIME_EXE_UNRESOLVED =
        "ランタイム（SEED.exe）の場所が分からないため、古い形式のファイルを変換できません。";

    /// <summary>変換プロセスが期限内に終わらなかった（書式: 期限 [秒]）。</summary>
    public const string MIGRATE_TIMEOUT_FORMAT =
        "ランタイムによる変換が {0} 秒以内に終わりませんでした。";

    /// <summary>変換プロセスを起動できなかった（書式: 例外メッセージ）。</summary>
    public const string MIGRATE_LAUNCH_FAILED_FORMAT =
        "ランタイムを起動できませんでした: {0}";

    /// <summary>形式名の綴り違い（呼び出し側の不具合。利用者には出さずログへ残す）。</summary>
    public const string MIGRATE_BAD_USAGE_FORMAT =
        "形式名がランタイムに受け付けられませんでした（呼び出し側の不具合）: {0}";

    // ── 一括アップグレード（メニュー） ───────────────────────

    /// <summary>「ツール」メニューの項目名。</summary>
    public const string UPGRADE_MENU_HEADER = "プロジェクトの形式をアップグレード...";

    /// <summary>一括アップグレードのウィンドウのタイトル。</summary>
    public const string UPGRADE_WINDOW_TITLE = "プロジェクトの形式をアップグレード";

    /// <summary>ウィンドウを開いた直後の説明。</summary>
    public const string UPGRADE_INTRO =
        "アセットを現在のエンジンの形式へそろえます。まず変更内容だけを調べます"
        + "（この時点ではファイルを書き換えません）。";

    /// <summary>下調べ（dry-run）の実行中に出す文言。</summary>
    public const string UPGRADE_SCANNING = "調べています...";

    /// <summary>アップグレードの実行中に出す文言。</summary>
    public const string UPGRADE_RUNNING = "アップグレードしています...";

    /// <summary>ロックの確認中に出す文言。</summary>
    public const string UPGRADE_CHECKING_LOCKS = "ほかの人のロックを確認しています...";

    /// <summary>実行ボタンの表示。</summary>
    public const string UPGRADE_RUN_BUTTON = "アップグレードを実行";

    /// <summary>閉じるボタンの表示。</summary>
    public const string UPGRADE_CLOSE_BUTTON = "閉じる";

    /// <summary>下調べの結果、更新が要らなかった。</summary>
    public const string UPGRADE_NOTHING_TO_DO =
        "すべてのファイルが現在の形式です。アップグレードの必要はありません。";

    /// <summary>下調べの結果（書式: 更新する件数, 現行版のままの件数）。</summary>
    public const string UPGRADE_DRY_RUN_SUMMARY_FORMAT =
        "更新するファイル: {0} 件 / すでに現在の形式: {1} 件";

    /// <summary>形式ごとの内訳の 1 行（書式: 形式名, 件数）。</summary>
    public const string UPGRADE_KIND_LINE_FORMAT = "{0}: {1} 件";

    /// <summary>ファイル 1 件の行（書式: 相対パス, 変換前の版, 変換後の版）。</summary>
    public const string UPGRADE_FILE_LINE_FORMAT = "{0}  (v{1} → v{2})";

    /// <summary>未来版のファイルがある（書式: 件数）。</summary>
    public const string UPGRADE_FUTURE_VERSION_HEADER_FORMAT =
        "新しいエンジンで保存されたファイル（アップグレードできません）: {0} 件";

    /// <summary>読めないファイルがある（書式: 件数）。</summary>
    public const string UPGRADE_FAILED_HEADER_FORMAT =
        "読めなかったファイル（書き換えません）: {0} 件";

    /// <summary>問題のあるファイル 1 件の行（書式: 相対パス, 理由）。</summary>
    public const string UPGRADE_PROBLEM_LINE_FORMAT = "{0}: {1}";

    /// <summary>実行後の結果（書式: 更新した件数, prefab_hash を貼り直したインスタンス数）。</summary>
    public const string UPGRADE_RESULT_SUMMARY_FORMAT =
        "{0} 件のファイルを更新しました。プレハブの版（prefab_hash）を {1} 件貼り直しました。";

    /// <summary>実行後に VCS パネルへの反映を促す一言。</summary>
    public const string UPGRADE_RESULT_VCS_HINT =
        "変更はバージョン管理パネルに並びます。内容を確かめてから送信してください。";

    /// <summary>ランタイムを起動できず、下調べも実行もできない（書式: 理由）。</summary>
    public const string UPGRADE_RUNTIME_UNAVAILABLE_FORMAT =
        "ランタイム（SEED.exe）を実行できないため、アップグレードできません。\n{0}";

    /// <summary>プロジェクトが開かれていない。</summary>
    public const string UPGRADE_NO_PROJECT =
        "プロジェクトが開かれていないため、アップグレードできません。";

    /// <summary>ロックゲートに止められた（書式: ゲートの文言）。</summary>
    public const string UPGRADE_BLOCKED_BY_LOCKS_FORMAT =
        "ほかの人が編集中のファイルがあるため、アップグレードを中止しました。\n{0}";

    // ── プロジェクトを開いたときの案内 ───────────────────────

    /// <summary>
    /// 古い形式のファイルが見つかったときのトースト（書式: 件数, メニュー項目名）。
    /// </summary>
    public const string STARTUP_NOTICE_FORMAT =
        "古い形式のファイルが {0} 件あります。ツール → {1} で更新できます";

    /// <summary>ランタイム exe が無いので案内を出さなかった（ログのみ。書式: 理由）。</summary>
    public const string STARTUP_NOTICE_SKIPPED_FORMAT =
        "古い形式の確認を省略しました: {0}";
}
