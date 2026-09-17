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

    // ============================================================
    //  パネル（Version Control）の表示文言
    //  ここも「唯一の置き場」の原則どおり同じファイルへ置く。
    //  XAML にもコードビハインドにも日本語を直書きしないこと。
    // ============================================================

    // ── パネルの素性 ────────────────────────────────────────

    /// <summary>パネルのタイトル（AvalonDock のタブに出る）。</summary>
    public const string PANEL_TITLE = "バージョン管理";

    // ── 利用不可のときの案内 ────────────────────────────────

    /// <summary>バージョン管理下に無いときの見出し。</summary>
    public const string PANEL_UNAVAILABLE_TITLE = "このプロジェクトはバージョン管理されていません";

    /// <summary>バージョン管理下に置く手順の案内（利用不可のときだけ出す）。</summary>
    public const string PANEL_UNAVAILABLE_GUIDE =
        "バージョン管理下に置くには、Lore のリポジトリを作成するか、"
        + "既にあるリポジトリをこのプロジェクトフォルダへ取得してください。"
        + "作業コピーになると（プロジェクトフォルダに .lore フォルダができると）"
        + "このパネルが使えるようになります。";

    // ── ヘッダー ────────────────────────────────────────────

    /// <summary>ブランチのラベル。</summary>
    public const string PANEL_BRANCH_LABEL = "ブランチ";

    /// <summary>ブランチのコンボの末尾に出す「新しいブランチを作る」項目。</summary>
    public const string PANEL_BRANCH_NEW_ITEM = "新しいブランチ…";

    /// <summary>新しいブランチ名を尋ねるダイアログのタイトル。</summary>
    public const string PANEL_BRANCH_NEW_DIALOG_TITLE = "新しいブランチ";

    /// <summary>新しいブランチ名を尋ねるダイアログの本文。</summary>
    public const string PANEL_BRANCH_NEW_DIALOG_PROMPT = "新しいブランチの名前を入力してください。";

    /// <summary>ブランチ切替の確認（未送信の変更があるとき。書式: 件数）。</summary>
    public const string PANEL_BRANCH_SWITCH_CONFIRM_FORMAT =
        "送信していない変更が {0} 件あります。\n"
        + "ブランチを切り替えると、これらの変更が失われることがあります。\n"
        + "切り替えますか？";

    /// <summary>ブランチ切替の確認ダイアログのタイトル。</summary>
    public const string PANEL_BRANCH_SWITCH_CONFIRM_TITLE = "ブランチの切り替え";

    /// <summary>接続先と identity をまとめて出すときの書式（identity — リモート URL）。</summary>
    public const string PANEL_CONNECTION_FORMAT = "{0} — {1}";

    /// <summary>identity が分からないときの表示。</summary>
    public const string PANEL_IDENTITY_UNKNOWN = "利用者不明";

    /// <summary>
    /// SEED アカウントでログイン中のときの identity 表示（例: "tsubasa（ログイン中）"）。
    /// 匿名との違いをここで見せる。
    /// </summary>
    public const string PANEL_IDENTITY_SIGNED_IN_FORMAT = "{0}（ログイン中）";

    /// <summary>リモート URL が分からないときの表示。</summary>
    public const string PANEL_REMOTE_UNKNOWN = "接続先未設定";

    /// <summary>パネルからオーナー向けの操作を開くボタンの文言。</summary>
    public const string PANEL_ACCOUNTS_BUTTON = "アカウント";

    /// <summary>パネルからオーナー向けの操作を開くボタンのツールチップ。</summary>
    public const string PANEL_ACCOUNTS_TOOLTIP =
        "このプロジェクトのアカウントと参加者を管理します";

    /// <summary>更新ボタンのツールチップ。</summary>
    public const string PANEL_REFRESH_TOOLTIP = "状態を取り直す";

    // ── 主操作 ──────────────────────────────────────────────

    /// <summary>「最新を取得」ボタンの文言。</summary>
    public const string PANEL_FETCH_BUTTON = "最新を取得";

    /// <summary>「送信」ボタンの文言。</summary>
    public const string PANEL_SUBMIT_BUTTON = "送信";

    /// <summary>メッセージ欄のプレースホルダ。</summary>
    public const string PANEL_MESSAGE_PLACEHOLDER = "変更内容をひとことで";

    // ── 結果の 1 行メッセージ（パネル専用の短い言い回し）────

    /// <summary>先に最新を取得すべきとき。該当ボタンを強調する。</summary>
    public const string PANEL_NOTICE_NEEDS_SYNC = "先に「最新を取得」してください。";

    /// <summary>サーバへ繋がらないとき。</summary>
    public const string PANEL_NOTICE_REQUIRES_CONNECTION = "サーバに接続できません。";

    // ── 変更一覧 ────────────────────────────────────────────

    /// <summary>競合グループの見出し（書式: 件数）。</summary>
    public const string PANEL_GROUP_CONFLICTS_FORMAT = "競合（{0} 件）";

    /// <summary>変更グループの見出し（書式: 件数）。</summary>
    public const string PANEL_GROUP_CHANGES_FORMAT = "変更（{0} 件）";

    /// <summary>変更が 1 件も無いときの表示。</summary>
    public const string PANEL_NO_CHANGES = "変更はありません。";

    /// <summary>「すべて」に対して競合解決を行うボタンの接頭辞（書式: 選択肢名）。</summary>
    public const string PANEL_RESOLVE_ALL_FORMAT = "すべて{0}";

    /// <summary>「リモートを採用」の確認本文（書式: 件数）。自分の変更が消えるため必ず確認する。</summary>
    public const string PANEL_RESOLVE_TAKE_REMOTE_CONFIRM_FORMAT =
        "{0} 件のファイルについて、自分の変更を捨ててリモートの内容にします。\n"
        + "この操作は取り消せません。続けますか？";

    /// <summary>「リモートを採用」の確認ダイアログのタイトル。</summary>
    public const string PANEL_RESOLVE_TAKE_REMOTE_CONFIRM_TITLE = "リモートを採用";

    // ── 変更の種類の表示名 ──────────────────────────────────

    /// <summary>追加。</summary>
    public const string PANEL_CHANGE_ADDED = "追加";

    /// <summary>変更。</summary>
    public const string PANEL_CHANGE_MODIFIED = "変更";

    /// <summary>削除。</summary>
    public const string PANEL_CHANGE_DELETED = "削除";

    /// <summary>移動。</summary>
    public const string PANEL_CHANGE_MOVED = "移動";

    /// <summary>複製。</summary>
    public const string PANEL_CHANGE_COPIED = "複製";

    /// <summary>競合。</summary>
    public const string PANEL_CHANGE_CONFLICT = "競合";

    /// <summary>種類が分からない。</summary>
    public const string PANEL_CHANGE_UNKNOWN = "不明";

    // ── 右クリックメニュー ──────────────────────────────────

    /// <summary>プロジェクトパネルで表示する。</summary>
    public const string PANEL_MENU_SHOW_IN_PROJECT = "プロジェクトパネルで表示";

    /// <summary>エクスプローラーでフォルダーの場所を開く。</summary>
    public const string PANEL_MENU_OPEN_FOLDER = "フォルダーの場所を開く";

    /// <summary>パスをクリップボードへコピーする。</summary>
    public const string PANEL_MENU_COPY_PATH = "パスをコピー";

    /// <summary>ロックする。</summary>
    public const string PANEL_MENU_LOCK = "ロックする";

    /// <summary>ロックを解除する。</summary>
    public const string PANEL_MENU_UNLOCK = "ロックを解除";

    // ── タブ ────────────────────────────────────────────────

    /// <summary>「変更」タブ。</summary>
    public const string PANEL_TAB_CHANGES = "変更";

    /// <summary>「履歴」タブ。</summary>
    public const string PANEL_TAB_HISTORY = "履歴";

    /// <summary>「ロック」タブ。</summary>
    public const string PANEL_TAB_LOCKS = "ロック";

    // ── 履歴タブ ────────────────────────────────────────────

    /// <summary>サーバに繋がっていないため履歴を出せないとき。</summary>
    public const string PANEL_HISTORY_REQUIRES_CONNECTION =
        "履歴はサーバ接続時のみ表示できます。";

    /// <summary>履歴が 1 件も無いとき。</summary>
    public const string PANEL_HISTORY_EMPTY = "履歴がありません。";

    /// <summary>リビジョン番号の表示書式（書式: 番号）。</summary>
    public const string PANEL_REVISION_NUMBER_FORMAT = "#{0}";

    /// <summary>メッセージが空のリビジョンの表示。</summary>
    public const string PANEL_REVISION_NO_MESSAGE = "（メッセージなし）";

    /// <summary>日時の表示書式（履歴・ロック共通）。</summary>
    public const string PANEL_TIMESTAMP_FORMAT = "yyyy/MM/dd HH:mm";

    /// <summary>日時が取得できなかったときの表示。</summary>
    public const string PANEL_TIMESTAMP_UNKNOWN = "日時不明";

    // ── ロックタブ ──────────────────────────────────────────

    /// <summary>ロックが 1 件も無いとき。</summary>
    public const string PANEL_LOCKS_EMPTY = "ロックされているファイルはありません。";

    /// <summary>保持者が自分。</summary>
    public const string PANEL_LOCK_HOLDER_SELF = "自分";

    /// <summary>保持者が他の人（書式: 所有者名）。</summary>
    public const string PANEL_LOCK_HOLDER_OTHER_FORMAT = "他の人（{0}）";

    /// <summary>保持者が不明（サーバ認証なしの構成では普通に起こる）。</summary>
    public const string PANEL_LOCK_HOLDER_UNKNOWN = "不明";

    /// <summary>ロックされていない。</summary>
    public const string PANEL_LOCK_HOLDER_NONE = "ロックなし";

    /// <summary>
    /// 所有者が不明になり得ることの説明（ロックタブに常時出す）。
    /// 「壊れている」と誤解されないよう、普通の状態だと明示する。
    /// </summary>
    public const string PANEL_LOCK_UNKNOWN_NOTE =
        "サーバに利用者認証を設定していない構成では、保持者が「不明」になります"
        + "（異常ではありません）。ロックは通知のみで、保存は止まりません。";

    /// <summary>ロック解除ボタンの文言。</summary>
    public const string PANEL_LOCK_RELEASE_BUTTON = "解除";

    // ── 実行中の表示 ────────────────────────────────────────

    /// <summary>実行中であることの表示（書式: 操作名）。</summary>
    public const string PANEL_BUSY_FORMAT = "{0}…";

    /// <summary>操作名: 状態の取り直し。</summary>
    public const string PANEL_OPERATION_REFRESH = "更新中";

    /// <summary>操作名: 最新を取得。</summary>
    public const string PANEL_OPERATION_FETCH = "最新を取得中";

    /// <summary>操作名: 送信。</summary>
    public const string PANEL_OPERATION_SUBMIT = "送信中";

    /// <summary>操作名: 競合の解決。</summary>
    public const string PANEL_OPERATION_RESOLVE = "競合を解決中";

    /// <summary>操作名: ブランチ操作。</summary>
    public const string PANEL_OPERATION_BRANCH = "ブランチを操作中";

    /// <summary>操作名: 履歴の取得。</summary>
    public const string PANEL_OPERATION_HISTORY = "履歴を取得中";

    /// <summary>操作名: ロックの操作。</summary>
    public const string PANEL_OPERATION_LOCK = "ロックを操作中";
}
