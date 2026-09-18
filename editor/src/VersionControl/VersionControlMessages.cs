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

    // ── ブランチのマージ ────────────────────────────────────

    /// <summary>ブランチを取り込めた（書式: 取り込み元の名前）。</summary>
    public const string BRANCH_MERGE_OK_FORMAT =
        "ブランチ「{0}」を取り込みました。「送信」で共有されます。";

    /// <summary>取り込みで競合が出た（書式: 取り込み元の名前, 競合件数）。</summary>
    public const string BRANCH_MERGE_CONFLICTED_FORMAT =
        "ブランチ「{0}」の取り込みで {1} 件が競合しました。競合の節で選んでください。";

    /// <summary>ブランチを取り込めなかった。</summary>
    public const string BRANCH_MERGE_FAILED = "ブランチを取り込めませんでした。";

    /// <summary>先に最新を取得しないと取り込めない。</summary>
    public const string BRANCH_MERGE_NEEDS_SYNC =
        "ブランチが分岐しているため取り込めません。先に「最新を取得」してください。";

    /// <summary>取り込み元に現在のブランチが指定された。</summary>
    public const string BRANCH_MERGE_SELF =
        "現在のブランチ自身は取り込めません。取り込み元には別のブランチを選んでください。";

    // ── ブランチの削除（Lore ではアーカイブ）────────────────
    //  ★Lore v0.9.0 にブランチの「削除」は無く、`branch archive`（一覧から隠す）が相当。
    //    利用者向けの語彙は「削除（アーカイブ）」で統一する。

    /// <summary>ブランチを削除（アーカイブ）できた（書式: 名前）。</summary>
    public const string BRANCH_ARCHIVE_OK_FORMAT =
        "ブランチ「{0}」を削除（アーカイブ）しました。一覧には出なくなります。";

    /// <summary>ブランチを削除（アーカイブ）できなかった。</summary>
    public const string BRANCH_ARCHIVE_FAILED = "ブランチを削除（アーカイブ）できませんでした。";

    /// <summary>現在のブランチは削除できない。</summary>
    public const string BRANCH_ARCHIVE_CURRENT =
        "いま開いているブランチは削除できません。先に別のブランチへ切り替えてください。";

    /// <summary>既定ブランチは削除できない（書式: 既定ブランチ名）。</summary>
    public const string BRANCH_ARCHIVE_DEFAULT_FORMAT =
        "既定のブランチ「{0}」は削除できません。";

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

    // ── ロックのゲート（保存・送信を止める／注意する）──────────
    //
    //  【文言の方針】
    //  ・止めるときは「誰が」「どのファイルを」を必ず書く。
    //    「保存できません」だけでは、利用者は次に何をすればよいか分からない。
    //  ・止めないとき（注意）は「そのまま保存しました／送信しました」まで書く。
    //    注意だけ出して結果を書かないと「止まったのか？」と迷わせる。

    /// <summary>保存を止めたときのダイアログのタイトル。</summary>
    public const string LOCK_GATE_BLOCKED_TITLE = "保存できません";

    /// <summary>他の人がロック中で保存を止めた（書式: 所有者名, 相対パス）。</summary>
    public const string LOCK_GATE_BLOCKED_BY_OTHER_FORMAT =
        "{0} さんがロック中のため保存できません。\n"
        + "対象: {1}\n"
        + "その人が編集を終える（ロックを解除する）まで待つか、直接相談してください。";

    /// <summary>編集権を取れなかったので保存を止めた（書式: 相対パス）。</summary>
    public const string LOCK_GATE_ACQUIRE_FAILED_FORMAT =
        "編集権（ロック）を取得できなかったため保存できません。\n"
        + "対象: {0}\n"
        + "サーバの状態を確かめて、もう一度保存してください。";

    /// <summary>サーバへ問い合わせられなかったが保存は通した（書式: 相対パス）。</summary>
    public const string LOCK_GATE_WARN_UNREACHABLE_FORMAT =
        "サーバに接続できないため、ほかの人が編集中かどうか確認できませんでした。"
        + "そのまま保存します（{0}）。";

    /// <summary>ログインしていないので判定できないが保存は通した（書式: 相対パス）。</summary>
    public const string LOCK_GATE_WARN_ANONYMOUS_FORMAT =
        "ログインしていないため、ロックの持ち主を判定できません。"
        + "そのまま保存します（{0}）。";

    /// <summary>所有者不明のロックが残っていたが保存は通した（書式: 相対パス）。</summary>
    public const string LOCK_GATE_WARN_UNKNOWN_OWNER_FORMAT =
        "このファイルには利用者不明のロックが残っています（{0}）。"
        + "ほかの人が編集中かもしれません。そのまま保存します。";

    /// <summary>方針が「注意のみ」なので他の人のロックでも通した（書式: 所有者名, 相対パス）。</summary>
    public const string LOCK_GATE_WARN_ONLY_FORMAT =
        "{0} さんがロック中です（{1}）。設定が「注意のみ」のため保存は止めません。";

    /// <summary>ロックを自動で取得したときのログ（書式: 相対パス）。</summary>
    public const string LOCK_GATE_AUTO_ACQUIRED_FORMAT = "編集中として記録しました（{0}）。";

    /// <summary>自動で取得したロックを解放したときのログ（書式: 相対パス）。</summary>
    public const string LOCK_GATE_AUTO_RELEASED_FORMAT = "編集中の記録を解除しました（{0}）。";

    /// <summary>ロックを自動取得できなかったときのログ（書式: 相対パス, 理由）。</summary>
    public const string LOCK_GATE_AUTO_ACQUIRE_FAILED_FORMAT =
        "編集中として記録できませんでした（{0}）: {1}";

    // ── 送信ゲート ──────────────────────────────────────────

    /// <summary>送信を止めたときのダイアログのタイトル。</summary>
    public const string SUBMIT_BLOCKED_BY_LOCKS_TITLE = "送信できません";

    /// <summary>他の人のロックがあるので送信を止めた（書式: 件数, 内訳）。</summary>
    public const string SUBMIT_BLOCKED_BY_LOCKS_FORMAT =
        "ほかの人がロック中のファイルが {0} 件あるため送信できません。\n{1}";

    /// <summary>送信を止めた内訳の 1 行（書式: 相対パス, 所有者名）。</summary>
    public const string SUBMIT_LOCK_LINE_FORMAT = "・{0}（{1} さん）";

    /// <summary>方針が「注意のみ」なので、他の人のロックがあっても送信した（書式: 件数）。</summary>
    public const string SUBMIT_LOCK_WARN_ONLY_FORMAT =
        "ほかの人がロック中のファイルが {0} 件あります。"
        + "設定が「注意のみ」のため送信は止めません。";

    /// <summary>サーバへ問い合わせられず、ロックを確認しないまま送信した。</summary>
    public const string SUBMIT_LOCK_WARN_UNREACHABLE =
        "サーバに接続できないため、ほかの人のロックを確認できませんでした。そのまま送信します。";

    /// <summary>ログインしていないので、ロックの持ち主を判定しないまま送信した。</summary>
    public const string SUBMIT_LOCK_WARN_ANONYMOUS =
        "ログインしていないため、ロックの持ち主を判定できません。そのまま送信します。";

    // ── 一括書き込みゲート ──────────────────────────────────
    //
    //  「多数のファイルをまとめて書き換える操作」（プロジェクトの形式アップグレードなど）
    //  の前に、他の人のロックを 1 回の照会で確かめるためのもの。
    //  判定表は送信ゲートと同じ（取りに行かず、他の人のロックがあれば止める）で、
    //  文言だけが「送信」ではなく「実行」になる。

    /// <summary>一括書き込みを止めたときのダイアログのタイトル。</summary>
    public const string BULK_WRITE_BLOCKED_BY_LOCKS_TITLE = "実行できません";

    /// <summary>他の人のロックがあるので一括書き込みを止めた（書式: 件数, 内訳）。</summary>
    public const string BULK_WRITE_BLOCKED_BY_LOCKS_FORMAT =
        "ほかの人がロック中のファイルが {0} 件あるため実行できません。\n{1}";

    /// <summary>方針が「注意のみ」なので、他の人のロックがあっても実行した（書式: 件数）。</summary>
    public const string BULK_WRITE_LOCK_WARN_ONLY_FORMAT =
        "ほかの人がロック中のファイルが {0} 件あります。"
        + "設定が「注意のみ」のため実行は止めません。";

    /// <summary>サーバへ問い合わせられず、ロックを確認しないまま実行した。</summary>
    public const string BULK_WRITE_LOCK_WARN_UNREACHABLE =
        "サーバに接続できないため、ほかの人のロックを確認できませんでした。そのまま実行します。";

    /// <summary>ログインしていないので、ロックの持ち主を判定しないまま実行した。</summary>
    public const string BULK_WRITE_LOCK_WARN_ANONYMOUS =
        "ログインしていないため、ロックの持ち主を判定できません。そのまま実行します。";

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

    /// <summary>
    /// ブランチのマージで競合が無かったときに Lore が自動で打つコミットのメッセージ
    /// （書式: 取り込み元のブランチ名, 取り込み先のブランチ名）。
    /// これも履歴に残るので、どちらをどちらへ取り込んだのか分かる形にする。
    /// </summary>
    public const string BRANCH_MERGE_COMMIT_MESSAGE_FORMAT = "マージ: 「{0}」を「{1}」へ取り込み";

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

    // ── ブランチのマージ（パネル）────────────────────────────

    /// <summary>「その他 …」メニューの「ブランチをマージ…」。</summary>
    public const string PANEL_MENU_MERGE_BRANCH = "ブランチをマージ…";

    /// <summary>マージ元を選ぶダイアログのタイトル。</summary>
    public const string PANEL_BRANCH_MERGE_DIALOG_TITLE = "ブランチのマージ";

    /// <summary>マージ元を選ぶダイアログの本文（書式: 現在のブランチ名）。</summary>
    public const string PANEL_BRANCH_MERGE_DIALOG_PROMPT =
        "現在のブランチ「{0}」へ取り込むブランチを選んでください。";

    /// <summary>マージ元を選ぶダイアログの補足。</summary>
    public const string PANEL_BRANCH_MERGE_DIALOG_NOTE =
        "取り込みは手元だけで行われます。ほかの人へ渡すには、そのあと「送信」してください。";

    /// <summary>未送信の変更があるので取り込みを始めない（書式: 件数）。</summary>
    public const string PANEL_BRANCH_MERGE_DIRTY_FORMAT =
        "送信していない変更が {0} 件あります。先に「送信」するか、変更を元に戻してから取り込んでください。";

    /// <summary>取り込める（現在のブランチ以外の）ブランチが無い。</summary>
    public const string PANEL_BRANCH_MERGE_NO_CANDIDATES =
        "取り込めるブランチがありません（現在のブランチ以外が見つかりませんでした）。";

    // ── ブランチの削除（アーカイブ。パネル）──────────────────

    /// <summary>「その他 …」メニューの「ブランチを削除（アーカイブ）…」。</summary>
    public const string PANEL_MENU_ARCHIVE_BRANCH = "ブランチを削除（アーカイブ）…";

    /// <summary>削除するブランチを選ぶダイアログのタイトル。</summary>
    public const string PANEL_BRANCH_ARCHIVE_DIALOG_TITLE = "ブランチの削除（アーカイブ）";

    /// <summary>削除するブランチを選ぶダイアログの本文。</summary>
    public const string PANEL_BRANCH_ARCHIVE_DIALOG_PROMPT =
        "削除（アーカイブ）するブランチを選んでください。";

    /// <summary>
    /// 削除するブランチを選ぶダイアログの補足。
    /// Lore の archive が「隠す」であり、元に戻せないことをここで明示する。
    /// </summary>
    public const string PANEL_BRANCH_ARCHIVE_DIALOG_NOTE =
        "削除したブランチは一覧に出なくなります。コミットそのものは残りますが、"
        + "このエディタからは元に戻せません。";

    /// <summary>削除できる（現在のブランチ・既定ブランチ以外の）ブランチが無い。</summary>
    public const string PANEL_BRANCH_ARCHIVE_NO_CANDIDATES =
        "削除できるブランチがありません（現在のブランチと既定のブランチは削除できません）。";

    /// <summary>削除の確認ダイアログの本文（書式: ブランチ名）。</summary>
    public const string PANEL_BRANCH_ARCHIVE_CONFIRM_FORMAT =
        "ブランチ「{0}」を削除（アーカイブ）します。\n"
        + "削除すると一覧に出なくなり、このエディタからは元に戻せません。\n"
        + "削除しますか？";

    /// <summary>削除の確認ダイアログのタイトル。</summary>
    public const string PANEL_BRANCH_ARCHIVE_CONFIRM_TITLE = "ブランチの削除（アーカイブ）";

    // ── ブランチを選ぶダイアログの共通部品 ──────────────────

    /// <summary>ブランチ選択ダイアログの決定ボタン。</summary>
    public const string PANEL_BRANCH_PICKER_OK = "OK";

    /// <summary>ブランチ選択ダイアログの取り消しボタン。</summary>
    public const string PANEL_BRANCH_PICKER_CANCEL = "キャンセル";

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

    /// <summary>
    /// メッセージ欄のプレースホルダ。
    /// 「&lt;必須&gt;」を明記するのは、空だと送信ボタンが押せないことを
    /// 押す前に知らせるため（無効なボタンだけでは理由が伝わらない）。
    /// </summary>
    public const string PANEL_MESSAGE_PLACEHOLDER = "メッセージを入力してください <必須>";

    // ── ヘッダーのアイコンボタン ────────────────────────────
    //  文字ラベルを持たないので、ツールチップが唯一の説明になる。

    /// <summary>ヘッダーの「取得」アイコンボタンのツールチップ。</summary>
    public const string PANEL_HEADER_FETCH_TOOLTIP = "最新を取得（サーバの変更を手元へ）";

    /// <summary>ヘッダーの「送信」アイコンボタンのツールチップ。</summary>
    public const string PANEL_HEADER_SUBMIT_TOOLTIP = "送信（手元の変更をサーバへ）";

    /// <summary>ヘッダーの「その他」アイコンボタンのツールチップ。</summary>
    public const string PANEL_HEADER_MORE_TOOLTIP = "その他の操作";

    // ── 「その他」メニュー ──────────────────────────────────

    /// <summary>作業コピーのフォルダーをエクスプローラーで開く。</summary>
    public const string PANEL_MENU_SHOW_WORKING_COPY = "フォルダーで表示";

    /// <summary>自分が持っているロックをまとめて解除する。</summary>
    public const string PANEL_MENU_RELEASE_ALL_LOCKS = "ロックをすべて解除";

    /// <summary>アカウントと参加者のダイアログを開く。</summary>
    public const string PANEL_MENU_ACCOUNTS = "アカウント…";

    /// <summary>解除できる（自分の）ロックが 1 件も無いとき。</summary>
    public const string PANEL_NO_RELEASABLE_LOCKS = "解除できるロックはありません。";

    // ── 未送信 / 未取得の行 ────────────────────────────────
    //
    //  【なぜ件数ではなく「あり／なし」なのか】
    //  Lore が返すのは is_local_ahead / is_remote_ahead という **真偽値だけ** で、
    //  「何コミット進んでいるか」は返らない（LoreStatusTranslator.ToRemoteComparison）。
    //  数を書けない以上、書けるふりをせず「あり／なし」で出す。
    //  さらにこの真偽値はサーバへ問い合わせたときしか得られないため、
    //  オフライン取得の直後は「未確認」になる。

    /// <summary>未送信の見出し（件数が分かる将来のために書式も持つ）。</summary>
    public const string PANEL_SYNC_UNPUSHED_COUNT_FORMAT = "未送信 {0}";

    /// <summary>未取得の見出し（件数が分かる将来のために書式も持つ）。</summary>
    public const string PANEL_SYNC_UNPULLED_COUNT_FORMAT = "未取得 {0}";

    /// <summary>未送信がある（件数は分からない）。</summary>
    public const string PANEL_SYNC_UNPUSHED_PRESENT = "未送信あり";

    /// <summary>未送信が無い。</summary>
    public const string PANEL_SYNC_UNPUSHED_NONE = "未送信なし";

    /// <summary>未取得がある（件数は分からない）。</summary>
    public const string PANEL_SYNC_UNPULLED_PRESENT = "未取得あり";

    /// <summary>未取得が無い。</summary>
    public const string PANEL_SYNC_UNPULLED_NONE = "未取得なし";

    /// <summary>サーバへ問い合わせていないので未送信が分からない。</summary>
    public const string PANEL_SYNC_UNPUSHED_UNCHECKED = "未送信 未確認";

    /// <summary>サーバへ問い合わせていないので未取得が分からない。</summary>
    public const string PANEL_SYNC_UNPULLED_UNCHECKED = "未取得 未確認";

    /// <summary>未確認のときのツールチップ（どうすれば分かるかを書く）。</summary>
    public const string PANEL_SYNC_UNCHECKED_TOOLTIP =
        "サーバへ問い合わせていないため、未送信・未取得の有無は分かりません。"
        + "更新（円形の矢印）を押すとサーバに問い合わせて確かめます。";

    /// <summary>サーバに繋がらず未送信・未取得を確かめられなかったときのツールチップ。</summary>
    public const string PANEL_SYNC_UNAVAILABLE_TOOLTIP =
        "サーバに接続できないため、未送信・未取得の有無を確かめられませんでした。";

    /// <summary>リモートにこのブランチがまだ無い（初回の送信前）ときのツールチップ。</summary>
    public const string PANEL_SYNC_REMOTE_MISSING_TOOLTIP =
        "このブランチはまだサーバにありません（初回の送信でサーバ側に作られます）。";

    /// <summary>履歴節へ飛ぶリンクの文言。</summary>
    public const string PANEL_SYNC_SHOW_HISTORY_LINK = "すべての履歴を表示する";

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

    // ── 折りたたみ節の見出し ────────────────────────────────
    //  Visual Studio の「Git 変更」に倣い、タブではなく縦に並ぶ折りたたみ節にする。
    //  見出しには必ず件数を添える（開かずに規模が分かるようにするため）。

    /// <summary>「競合」節の見出し（書式: 件数）。</summary>
    public const string PANEL_SECTION_CONFLICTS_FORMAT = "競合 ({0})";

    /// <summary>「変更」節の見出し（書式: 件数）。</summary>
    public const string PANEL_SECTION_CHANGES_FORMAT = "変更 ({0})";

    /// <summary>「ロック」節の見出し（書式: 件数）。</summary>
    public const string PANEL_SECTION_LOCKS_FORMAT = "ロック ({0})";

    /// <summary>「履歴」節の見出し（件数は「さらに読み込む」で増えるので付けない）。</summary>
    public const string PANEL_SECTION_HISTORY = "履歴";

    /// <summary>節の中身をまだ取りに行っていないときの件数表示。</summary>
    public const string PANEL_SECTION_COUNT_UNKNOWN = "…";

    // ── 節の見出しの右端に置くアイコンボタン ────────────────

    /// <summary>ツリーをすべて展開する。</summary>
    public const string PANEL_TREE_EXPAND_ALL_TOOLTIP = "すべて展開";

    /// <summary>ツリーをすべて折りたたむ。</summary>
    public const string PANEL_TREE_COLLAPSE_ALL_TOOLTIP = "すべて折りたたむ";

    /// <summary>節の「その他」メニューのツールチップ。</summary>
    public const string PANEL_SECTION_MORE_TOOLTIP = "この一覧の操作";

    // ── 変更ツリー ──────────────────────────────────────────

    /// <summary>作業コピーのパスが分からないときの根の表示。</summary>
    public const string PANEL_TREE_ROOT_UNKNOWN = "（作業コピー）";

    /// <summary>
    /// 変更の状態を表す 1 文字（行の右端）。
    /// Visual Studio の「Git 変更」と同じ位置・同じ 1 文字表記にそろえる。
    /// </summary>
    public const string PANEL_CHANGE_LETTER_ADDED = "A";

    /// <summary>変更（Modified）。</summary>
    public const string PANEL_CHANGE_LETTER_MODIFIED = "M";

    /// <summary>削除（Deleted）。</summary>
    public const string PANEL_CHANGE_LETTER_DELETED = "D";

    /// <summary>移動・改名（Renamed）。</summary>
    public const string PANEL_CHANGE_LETTER_MOVED = "R";

    /// <summary>複製（Copied）。</summary>
    public const string PANEL_CHANGE_LETTER_COPIED = "C";

    /// <summary>未解決の競合。種類より優先して出す。</summary>
    public const string PANEL_CHANGE_LETTER_CONFLICT = "!";

    /// <summary>種類が分からない。</summary>
    public const string PANEL_CHANGE_LETTER_UNKNOWN = "?";

    // ── 履歴節 ──────────────────────────────────────────────

    /// <summary>履歴をさらに読み込むボタンの文言。</summary>
    public const string PANEL_HISTORY_LOAD_MORE = "さらに読み込む";

    /// <summary>
    /// 履歴の初回取得件数。全件をいきなり引くとサーバ往復が長くなるため、
    /// まず直近の N 件だけを出し、足りなければ「さらに読み込む」で伸ばす。
    /// </summary>
    public const int PANEL_HISTORY_PAGE_SIZE = 30;


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
    ///
    /// <para>
    /// ★ロックは「通知のみ」ではなくなった（保存ゲート）。ただし止まるのは
    /// **ログイン中にサーバから他の人のロックが確認できたときだけ**なので、
    /// そこまで書かないと「不明」のロックでも止まると誤解される。
    /// </para>
    /// </summary>
    public const string PANEL_LOCK_UNKNOWN_NOTE =
        "サーバに利用者認証を設定していない構成では、保持者が「不明」になります"
        + "（異常ではありません）。ほかの人のロックが確認できたファイルは保存・送信を止めますが、"
        + "保持者が不明のとき・サーバに繋がらないとき・ログインしていないときは止めません。";

    /// <summary>
    /// 自動で取得したロック（開いているあいだ保持しているもの）に添える印。
    /// 手で掛けたロックと区別できるようにするため。
    /// </summary>
    public const string PANEL_LOCK_AUTO_HELD = "編集中";

    /// <summary>「編集中」の印のツールチップ（なぜ自分で掛けた覚えが無いのか説明する）。</summary>
    public const string PANEL_LOCK_AUTO_HELD_TOOLTIP =
        "エディタで開いているあいだ自動で取得しているロックです。"
        + "閉じるかプロジェクトを終了すると自動で解除されます。"
        + "「解除」を押すと手動で外せます。";

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

    // ============================================================
    //  マージエディタ（競合の中身を並べて解決する専用ウィンドウ）
    //
    //  【向きの語彙（ここだけは絶対にぶらさない）】
    //  ・「取り込み元」… 印の >>>>>>> 側。sync ならリモート、
    //                    ブランチのマージなら取り込み元のブランチ。
    //  ・「現在」      … 印の <<<<<<< 側。sync なら自分の作業コピー、
    //                    ブランチのマージなら取り込み先＝現在のブランチ。
    //  Lore CLI の mine / theirs とは別の軸なので、この 2 語だけを画面に出す。
    // ============================================================

    // ── 印の解析に失敗したとき ──────────────────────────────

    /// <summary>印が閉じていない（書式: 始まりの行番号）。</summary>
    public const string MERGE_PARSE_UNTERMINATED_FORMAT =
        "{0} 行目から始まる競合の印が閉じていません。ファイルを直してから解決してください。";

    /// <summary>競合ブロックの中に別の印がある（書式: 行番号, 始まりの行番号, 行の中身）。</summary>
    public const string MERGE_PARSE_NESTED_FORMAT =
        "{0} 行目に、{1} 行目から始まる競合ブロックの中では現れないはずの印があります: {2}";

    /// <summary>ブロックの外に印が転がっている（書式: 行番号, 行の中身）。</summary>
    public const string MERGE_PARSE_ORPHAN_FORMAT =
        "{0} 行目の印は、対応する競合ブロックの始まりがありません: {1}";

    // ── ファイルを読めないとき ──────────────────────────────

    /// <summary>ファイルが見つからない。</summary>
    public const string MERGE_FILE_NOT_FOUND = "ファイルが見つかりません。";

    /// <summary>大きすぎて開けない（書式: 上限 [MB]）。</summary>
    public const string MERGE_FILE_TOO_LARGE_FORMAT =
        "ファイルが大きすぎるため中身を並べて表示できません（上限 {0} MB）。";

    /// <summary>バイナリなので中身を並べられない。</summary>
    public const string MERGE_FILE_BINARY =
        "行単位の内容を表示できません（バイナリ等）。2 択で解決してください。";

    // ── 書き戻す前の検査 ────────────────────────────────────

    /// <summary>印が残っている（書式: 行番号）。</summary>
    public const string MERGE_VALIDATE_MARKERS_REMAIN_FORMAT =
        "競合の印が {0} 行目に残っています。印を消さないと解決できません。";

    /// <summary>JSON として読めない（書式: 解析エラーの内容）。</summary>
    public const string MERGE_VALIDATE_JSON_BROKEN_FORMAT =
        "結果が JSON として読めません: {0}";

    /// <summary>同じオブジェクトの中でキーが重複している（書式: キー名）。</summary>
    public const string MERGE_VALIDATE_JSON_DUPLICATE_KEY_FORMAT =
        "同じオブジェクトの中でキー「{0}」が 2 回出てきます。"
        + "このまま保存するとゲーム側で読み込めません。";

    // ── 「両方を取り込む」の可否 ────────────────────────────

    /// <summary>印が無い（バイナリ等）ので両方を取り込めない。</summary>
    public const string MERGE_TAKE_BOTH_NO_MARKERS =
        "このファイルには競合の印がないため「両方を取り込む」ことができません"
        + "（バイナリなど、行単位で合成できない形式です）。";

    /// <summary>同じ箇所を両方が変更しているので両方を取り込めない。</summary>
    public const string MERGE_TAKE_BOTH_NOT_ADD_ONLY =
        "同じ箇所を両方が変更しているため「両方を取り込む」ことができません"
        + "（並べると内容が二重になります）。「比較…」で 1 つずつ選んでください。";

    // ── マージエディタの画面 ────────────────────────────────

    /// <summary>ウィンドウのタイトル（書式: ファイル名）。</summary>
    public const string MERGE_EDITOR_TITLE_FORMAT = "マージ — {0}";

    /// <summary>左上の見出し。</summary>
    public const string MERGE_EDITOR_PANE_INCOMING = "取り込み元";

    /// <summary>右上の見出し。</summary>
    public const string MERGE_EDITOR_PANE_CURRENT = "現在";

    /// <summary>下段の見出し。</summary>
    public const string MERGE_EDITOR_PANE_RESULT = "結果";

    /// <summary>sync のときの「取り込み元」の呼び名。</summary>
    public const string MERGE_EDITOR_SOURCE_REMOTE = "リモート";

    /// <summary>sync のときの「現在」の呼び名。</summary>
    public const string MERGE_EDITOR_SOURCE_LOCAL = "自分の変更";

    /// <summary>ブランチのマージのときの呼び名（書式: ブランチ名）。</summary>
    public const string MERGE_EDITOR_SOURCE_BRANCH_FORMAT = "ブランチ {0}";

    /// <summary>見出しの出どころ表示（書式: 取り込み元の名前, 現在の名前）。</summary>
    public const string MERGE_EDITOR_ORIGIN_FORMAT = "取り込み元: {0}　／　現在: {1}";

    /// <summary>残りの競合ブロック数（書式: 未選択数, 全体数）。</summary>
    public const string MERGE_EDITOR_REMAINING_FORMAT = "未解決のブロック {0} / {1}";

    /// <summary>すべて解決済みのときの表示。</summary>
    public const string MERGE_EDITOR_ALL_RESOLVED = "すべてのブロックを選びました。";

    /// <summary>ツールバー: すべて取り込み元。</summary>
    public const string MERGE_EDITOR_ALL_INCOMING = "すべて取り込み元";

    /// <summary>ツールバー: すべて現在。</summary>
    public const string MERGE_EDITOR_ALL_CURRENT = "すべて現在";

    /// <summary>ツールバー: すべて両方。</summary>
    public const string MERGE_EDITOR_ALL_BOTH = "すべて両方";

    /// <summary>ツールバー: 前の競合。</summary>
    public const string MERGE_EDITOR_PREV_CONFLICT = "前の競合";

    /// <summary>ツールバー: 次の競合。</summary>
    public const string MERGE_EDITOR_NEXT_CONFLICT = "次の競合";

    /// <summary>ツールバー: マージを確定。</summary>
    public const string MERGE_EDITOR_APPLY = "マージを確定";

    /// <summary>ツールバー: キャンセル。</summary>
    public const string MERGE_EDITOR_CANCEL = "キャンセル";

    /// <summary>ブロックのチェックボックスのツールチップ（書式: 側の名前）。</summary>
    public const string MERGE_EDITOR_TAKE_SIDE_TOOLTIP_FORMAT = "このブロックで「{0}」を採用する";

    /// <summary>結果の手編集がチェック操作で作り直される旨の注意。</summary>
    public const string MERGE_EDITOR_RESULT_REGENERATED_NOTE =
        "結果はチェックの操作で作り直されます（手で直した内容は失われます）。";

    /// <summary>詰め物の行（片側にしか無い行の場所）のツールチップ。</summary>
    public const string MERGE_EDITOR_PADDING_TOOLTIP = "この側には対応する行がありません。";

    /// <summary>マージエディタを開けなかったときのダイアログのタイトル。</summary>
    public const string MERGE_EDITOR_UNAVAILABLE_TITLE = "マージエディタを開けません";

    /// <summary>印が 1 つも無いので開けない。</summary>
    public const string MERGE_EDITOR_NO_CONFLICT =
        "このファイルに競合の印がありません。すでに解決されている可能性があります。";

    /// <summary>確定に失敗したときの表示（書式: 理由）。</summary>
    public const string MERGE_EDITOR_APPLY_FAILED_FORMAT = "確定できませんでした: {0}";

    /// <summary>未選択のブロックが残ったまま確定しようとしたときの確認（書式: 未選択数）。</summary>
    public const string MERGE_EDITOR_APPLY_UNSELECTED_CONFIRM_FORMAT =
        "まだ選んでいないブロックが {0} 個あります。\n"
        + "そのブロックは「元の内容」（どちらの変更も入らない状態）になります。\n"
        + "このまま確定しますか？";

    /// <summary>未選択のまま確定する確認のタイトル。</summary>
    public const string MERGE_EDITOR_APPLY_UNSELECTED_CONFIRM_TITLE = "マージの確定";

    // ── 競合行の追加操作（パネル）────────────────────────────

    /// <summary>行の「比較…」ボタン。</summary>
    public const string PANEL_CONFLICT_COMPARE = "比較…";

    /// <summary>行の「比較…」ボタンのツールチップ。</summary>
    public const string PANEL_CONFLICT_COMPARE_TOOLTIP =
        "マージエディタを開いて、ブロックごとにどちらを残すか選びます（ダブルクリックでも開きます）。";

    /// <summary>行の「両方を取り込む」ボタン。</summary>
    public const string PANEL_CONFLICT_TAKE_BOTH = "両方を取り込む";

    /// <summary>「すべて両方を取り込む」ボタン。</summary>
    public const string PANEL_CONFLICT_TAKE_BOTH_ALL = "すべて両方を取り込む";

    /// <summary>「両方を取り込む」が使えるときのツールチップ。</summary>
    public const string PANEL_CONFLICT_TAKE_BOTH_TOOLTIP =
        "両方の追加を並べて残します（同じ場所に追加し合ったときだけ使えます）。";

    /// <summary>「すべて両方を取り込む」が使えないときのツールチップ。</summary>
    public const string PANEL_CONFLICT_TAKE_BOTH_ALL_BLOCKED =
        "「両方を取り込む」ことができないファイルが含まれているため、まとめては実行できません。";

    /// <summary>行の「編集した内容で解決」ボタン。</summary>
    public const string PANEL_CONFLICT_RESOLVE_AS_IS = "編集した内容で解決";

    /// <summary>「編集した内容で解決」が使えるときのツールチップ。</summary>
    public const string PANEL_CONFLICT_RESOLVE_AS_IS_TOOLTIP =
        "いまのファイルの中身のまま解決済みにします（外部のエディタで直した場合に使います）。";

    /// <summary>「編集した内容で解決」が使えないときのツールチップ（書式: 理由）。</summary>
    public const string PANEL_CONFLICT_RESOLVE_AS_IS_BLOCKED_FORMAT =
        "このまま解決することはできません: {0}";

    // ── 中身を指定した解決（プロバイダ）──────────────────────

    /// <summary>「両方を取り込む」で解決できた（書式: 件数）。</summary>
    public const string RESOLVE_TAKE_BOTH_OK_FORMAT = "{0} 件の競合を「両方を取り込む」で解決しました。";

    /// <summary>「両方を取り込む」ができないファイルがあった（書式: パス, 理由）。</summary>
    public const string RESOLVE_TAKE_BOTH_BLOCKED_FORMAT = "{0}: {1}";

    /// <summary>結果をファイルへ書けなかった（書式: パス, 理由）。</summary>
    public const string RESOLVE_WRITE_FAILED_FORMAT = "{0} を書き込めませんでした: {1}";

    /// <summary>
    /// 書き込もうとしたファイルが、もう競合していなかった（書式: パス）。
    /// マージエディタを開いたまま別の手段で解決したときに起きる。
    /// </summary>
    public const string RESOLVE_NOT_CONFLICTED_FORMAT =
        "{0} はもう競合していません（ほかの操作で解決された可能性があります）。"
        + "上書きを避けるため中止しました。画面を更新してからやり直してください。";
}
