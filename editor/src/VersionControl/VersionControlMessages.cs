// ============================================================
//  VersionControlMessages.cs — 利用者に見せる文言の唯一の置き場
//
//  【役割】
//  「マジック文字列禁止」の徹底と、言い回しの統一のため、UI とログに出る
//  日本語をすべてここへ集める。プロバイダ実装の中に日本語を直書きしない。
//
//  【語彙の約束（docs/vcs_lore.md 1 章）】
//  commit / push は「送信」、sync は「最新を取得」、
//  resolve mine / theirs は「リモートを採用」「自分の変更を残す」と呼ぶ。
//  stage / rebase といった語はここに存在しない（利用者に出さないため）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.VersionControl;

/// <summary>
/// バージョン管理の利用者向け文言。
/// </summary>
public static class VersionControlMessages
{
    // ── プロバイダの素性 ────────────────────────────────────

    /// <summary>Lore プロバイダの表示名。</summary>
    public const string PROVIDER_NAME_LORE = "Lore";

    /// <summary>バージョン管理が無いときの表示名。</summary>
    public const string PROVIDER_NAME_NONE = "バージョン管理なし";

    // ── 利用不可 ────────────────────────────────────────────

    /// <summary>このプロジェクトはバージョン管理下に無い。</summary>
    public const string UNAVAILABLE =
        "このプロジェクトはバージョン管理下にありません。";

    /// <summary>サーバに繋がっていないためできない操作。</summary>
    public const string REQUIRES_CONNECTION =
        "サーバに接続できないため実行できません（履歴の取得には接続が必要です）。";

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>状態を取得できた。</summary>
    public const string STATUS_OK = "状態を取得しました。";

    /// <summary>状態を取得できなかった。</summary>
    public const string STATUS_FAILED = "状態を取得できませんでした。";

    // ── 通知 ────────────────────────────────────────────────

    /// <summary>変更の通知に成功した。</summary>
    public const string NOTIFY_CHANGED_OK = "変更を記録しました。";

    /// <summary>変更の通知に失敗した。</summary>
    public const string NOTIFY_CHANGED_FAILED = "変更を記録できませんでした。";

    /// <summary>移動・改名の通知に成功した。</summary>
    public const string NOTIFY_MOVED_OK = "移動を記録しました。";

    /// <summary>移動・改名の通知に失敗した。</summary>
    public const string NOTIFY_MOVED_FAILED = "移動を記録できませんでした。";

    /// <summary>通知対象のパスが 1 件も無かった（何もしない）。</summary>
    public const string NOTIFY_NO_PATHS = "対象のファイルがありません。";

    /// <summary>作業コピーの外のパスを渡された。</summary>
    public const string PATH_OUTSIDE_WORKING_COPY =
        "プロジェクトフォルダの外のファイルはバージョン管理できません。";

    // ── 送信 ────────────────────────────────────────────────

    /// <summary>送信するものが無い（空コミット防止）。</summary>
    public const string SUBMIT_NOTHING = "送信する変更がありません。";

    /// <summary>コミットメッセージが空。</summary>
    public const string SUBMIT_MESSAGE_REQUIRED = "メッセージを入力してください。";

    /// <summary>競合が残っているので送信できない。</summary>
    public const string SUBMIT_BLOCKED_BY_CONFLICTS =
        "未解決の競合があるため送信できません。先に競合を解決してください。";

    /// <summary>送信に成功した（書式: 件数）。</summary>
    public const string SUBMIT_OK_FORMAT = "{0} 件の変更を送信しました。";

    /// <summary>
    /// 新しい変更は無かったが、手元に残っていたコミット（競合解決のマージなど）を送った。
    /// </summary>
    public const string SUBMIT_PUSH_ONLY_OK = "手元に残っていた変更を送信しました。";

    /// <summary>リモートが進んでいて送信できなかった。</summary>
    public const string SUBMIT_NEEDS_SYNC =
        "サーバ側が更新されているため送信できませんでした。"
        + "「最新を取得」してから、もう一度送信してください（手元の変更は残っています）。";

    /// <summary>送信に失敗した。</summary>
    public const string SUBMIT_FAILED = "送信できませんでした。";

    // ── 最新を取得 ──────────────────────────────────────────

    /// <summary>取得に成功した（書式: 更新件数）。</summary>
    public const string FETCH_OK_FORMAT = "最新を取得しました（{0} 件更新）。";

    /// <summary>取得したが競合が残った（書式: 競合件数）。</summary>
    public const string FETCH_CONFLICTED_FORMAT =
        "最新を取得しましたが、{0} 件のファイルが競合しています。どちらを残すか選んでください。";

    /// <summary>取得に失敗した。</summary>
    public const string FETCH_FAILED = "最新を取得できませんでした。";

    // ── 競合の解決 ──────────────────────────────────────────

    /// <summary>解決に成功した（書式: 件数）。</summary>
    public const string RESOLVE_OK_FORMAT = "{0} 件の競合を解決しました。";

    /// <summary>
    /// 頼んだ分は解決したが、別のファイルの競合が残っている（書式: 解決件数, 残り件数）。
    /// </summary>
    public const string RESOLVE_PARTIAL_FORMAT =
        "{0} 件の競合を解決しました（残り {1} 件）。";

    /// <summary>解決に失敗した。</summary>
    public const string RESOLVE_FAILED = "競合を解決できませんでした。";

    /// <summary>解決対象が指定されていない。</summary>
    public const string RESOLVE_NO_PATHS = "解決するファイルが指定されていません。";

    /// <summary>「自分の変更を残す」の表示名（パネルのボタン文言）。</summary>
    public const string CHOICE_KEEP_MINE = "自分の変更を残す";

    /// <summary>「リモートを採用」の表示名（パネルのボタン文言）。</summary>
    public const string CHOICE_TAKE_REMOTE = "リモートを採用";

    // ── ブランチ ────────────────────────────────────────────

    /// <summary>ブランチ一覧を取得できた。</summary>
    public const string BRANCH_LIST_OK = "ブランチ一覧を取得しました。";

    /// <summary>ブランチ一覧を取得できなかった。</summary>
    public const string BRANCH_LIST_FAILED = "ブランチ一覧を取得できませんでした。";

    /// <summary>ブランチ名が空。</summary>
    public const string BRANCH_NAME_REQUIRED = "ブランチ名を入力してください。";

    /// <summary>ブランチを作成できた（書式: 名前）。</summary>
    public const string BRANCH_CREATE_OK_FORMAT = "ブランチ「{0}」を作成しました。";

    /// <summary>ブランチを作成できなかった。</summary>
    public const string BRANCH_CREATE_FAILED = "ブランチを作成できませんでした。";

    /// <summary>ブランチを切り替えられた（書式: 名前）。</summary>
    public const string BRANCH_SWITCH_OK_FORMAT = "ブランチ「{0}」へ切り替えました。";

    /// <summary>ブランチを切り替えられなかった。</summary>
    public const string BRANCH_SWITCH_FAILED = "ブランチを切り替えられませんでした。";

    // ── 履歴 ────────────────────────────────────────────────

    /// <summary>履歴を取得できた（書式: 件数）。</summary>
    public const string HISTORY_OK_FORMAT = "履歴を {0} 件取得しました。";

    /// <summary>履歴を取得できなかった。</summary>
    public const string HISTORY_FAILED = "履歴を取得できませんでした。";

    // ── ロック ──────────────────────────────────────────────

    /// <summary>ロック一覧を取得できた。</summary>
    public const string LOCK_LIST_OK = "ロック一覧を取得しました。";

    /// <summary>ロック一覧を取得できなかった。</summary>
    public const string LOCK_LIST_FAILED = "ロック一覧を取得できませんでした。";

    /// <summary>ロックを取得できた（書式: 件数）。</summary>
    public const string LOCK_ACQUIRE_OK_FORMAT = "{0} 件のロックを取得しました。";

    /// <summary>ロックを取得できなかった。</summary>
    public const string LOCK_ACQUIRE_FAILED = "ロックを取得できませんでした。";

    /// <summary>ロックを解放できた（書式: 件数）。</summary>
    public const string LOCK_RELEASE_OK_FORMAT = "{0} 件のロックを解放しました。";

    /// <summary>ロックを解放できなかった。</summary>
    public const string LOCK_RELEASE_FAILED = "ロックを解放できませんでした。";

    /// <summary>ロック状態を取得できた。</summary>
    public const string LOCK_STATUS_OK = "ロック状態を取得しました。";

    /// <summary>ロック状態を取得できなかった。</summary>
    public const string LOCK_STATUS_FAILED = "ロック状態を取得できませんでした。";

    /// <summary>対象パスが指定されていない。</summary>
    public const string LOCK_NO_PATHS = "対象のファイルが指定されていません。";

    /// <summary>所有者不明のロックを見つけたときの表示（サーバ認証なしの構成）。</summary>
    public const string LOCK_OWNER_UNKNOWN = "編集中（利用者不明）";

    // ── 共通 ────────────────────────────────────────────────

    /// <summary>操作が中断された。</summary>
    public const string CANCELED = "操作を中断しました。";

    /// <summary>想定外の例外で終わった。</summary>
    public const string UNEXPECTED_FAILURE = "バージョン管理の操作でエラーが発生しました。";

    /// <summary>
    /// 競合を解決したあとに作るマージコミットのメッセージ（書式: 解決件数）。
    /// これは履歴に残る文言なので、利用者が後から読んで意味が分かる形にする。
    /// </summary>
    public const string MERGE_COMMIT_MESSAGE_FORMAT = "マージ: 競合 {0} 件を解決";

    /// <summary>タイムアウトした。</summary>
    public const string TIMED_OUT = "時間内に完了しなかったため中断しました。";
}
