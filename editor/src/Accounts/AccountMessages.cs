// ============================================================
//  AccountMessages.cs — アカウント機能の利用者向け文言の唯一の置き場
//
//  【役割】
//  画面・ログ・エラーに出る日本語を 1 か所へ集める。
//  XAML にもコードにも日本語を直書きしない（同じ言い回しが増殖し、
//  用語を直すときに取りこぼすため）。
//
//  【語彙の約束】
//  アーティストも使う画面なので、JWT / チャレンジ / 署名 / PKCS#8 のような
//  実装語を出さない。出すのは「アカウント」「ログイン」「招待コード」「参加者」だけ。
//
//  【秘密を書かない】
//  秘密鍵・アクセストークン・招待コードそのものを含む文言をここへ置かない。
//  書式に値を差し込む箇所（{0} など）にも、それらを渡してはいけない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.Accounts;

/// <summary>
/// アカウント機能で表示する文言（定数）。
/// </summary>
public static class AccountMessages
{
    // ── 名前の規則（契約 2 章）──────────────────────────────

    /// <summary>名前が空。</summary>
    public const string NAME_EMPTY = "名前を入力してください。";

    /// <summary>名前の前後に空白がある。</summary>
    public const string NAME_HAS_SURROUNDING_SPACE = "名前の前後に空白を入れないでください。";

    /// <summary>名前が長すぎる。</summary>
    public const string NAME_TOO_LONG_FORMAT = "名前は {0} 文字までです（今は {1} 文字）。";

    /// <summary>名前に使えない文字がある。</summary>
    public const string NAME_INVALID_CHARACTER_FORMAT =
        "名前に使えない文字が含まれています: 「{0}」。英数字・かな・漢字と _ - . が使えます。";

    /// <summary>名前は後から変えられないことの注記。</summary>
    public const string NAME_IMMUTABLE_NOTE =
        "名前はロックの持ち主や送信者として表示されます。あとから変更できません。";

    // ── アカウントの作成・保管 ──────────────────────────────

    /// <summary>アカウントが未作成。</summary>
    public const string ACCOUNT_NOT_CREATED = "アカウントがありません。";

    /// <summary>アカウント作成に成功。</summary>
    public const string ACCOUNT_CREATED_FORMAT = "アカウント「{0}」を作成しました。";

    /// <summary>アカウントの保存に失敗。</summary>
    public const string ACCOUNT_SAVE_FAILED_FORMAT = "アカウントを保存できませんでした: {0}";

    /// <summary>アカウントの読み込みに失敗。</summary>
    public const string ACCOUNT_LOAD_FAILED_FORMAT = "アカウントを読み込めませんでした: {0}";

    /// <summary>保存されているアカウントの形式が不正。</summary>
    public const string ACCOUNT_FILE_BROKEN =
        "アカウントの保存ファイルが壊れています。書き出したファイルから読み込み直してください。";

    /// <summary>別の PC で保存されたなどの理由で復号できない。</summary>
    public const string ACCOUNT_DECRYPT_FAILED =
        "この PC・この Windows ユーザーではアカウントの鍵を復号できません。"
        + "別の PC で作ったものなら「読み込み」から取り込んでください。";

    /// <summary>すでにアカウントがある。</summary>
    public const string ACCOUNT_ALREADY_EXISTS =
        "すでにアカウントがあります。作り直すと参加中のプロジェクトへ入れなくなります。";

    // ── 書き出し・読み込み ──────────────────────────────────

    /// <summary>パスフレーズが短い。</summary>
    public const string PASSPHRASE_TOO_SHORT_FORMAT =
        "パスフレーズは {0} 文字以上にしてください。";

    /// <summary>確認用のパスフレーズが一致しない。</summary>
    public const string PASSPHRASE_MISMATCH = "確認用のパスフレーズが一致しません。";

    /// <summary>書き出しに成功。</summary>
    public const string EXPORT_OK_FORMAT = "アカウントを書き出しました: {0}";

    /// <summary>書き出しに失敗。</summary>
    public const string EXPORT_FAILED_FORMAT = "書き出せませんでした: {0}";

    /// <summary>読み込みに成功。</summary>
    public const string IMPORT_OK_FORMAT = "アカウント「{0}」を読み込みました。";

    /// <summary>読み込みに失敗（パスフレーズ違い・ファイル破損の両方をここへ畳む）。</summary>
    public const string IMPORT_FAILED =
        "読み込めませんでした。パスフレーズが違うか、ファイルが壊れています。";

    /// <summary>読み込むファイルの形式が違う。</summary>
    public const string IMPORT_FORMAT_MISMATCH =
        "SEED アカウントの書き出しファイルではありません。";

    /// <summary>書き出しファイルの版が新しすぎる。</summary>
    public const string IMPORT_VERSION_UNSUPPORTED_FORMAT =
        "この書き出しファイル（形式 v{0}）は、このエディタでは読み込めません。";

    // ── 発行窓口（サーバ）との通信 ──────────────────────────

    /// <summary>窓口に繋がらない。</summary>
    public const string GATEWAY_UNREACHABLE =
        "アカウントのサーバに繋がりません。アドレスとサーバの起動を確認してください。";

    /// <summary>窓口のアドレスが不正。</summary>
    public const string GATEWAY_ADDRESS_INVALID = "サーバのアドレスの書き方が正しくありません。";

    /// <summary>窓口が想定外の応答を返した。</summary>
    public const string GATEWAY_UNEXPECTED_RESPONSE = "サーバの応答を解釈できませんでした。";

    /// <summary>窓口が返したエラー（コード付き）。</summary>
    public const string GATEWAY_ERROR_FORMAT = "{0}（{1}）";

    // ── ログイン ────────────────────────────────────────────

    /// <summary>ログインしていない（匿名のまま動く）。</summary>
    public const string AUTH_NOT_SIGNED_IN = "ログインしていません";

    /// <summary>ログイン処理中。</summary>
    public const string AUTH_SIGNING_IN = "ログインしています…";

    /// <summary>ログイン済み。</summary>
    public const string AUTH_SIGNED_IN_FORMAT = "{0} としてログイン中";

    /// <summary>ログインに失敗した（理由を添える）。</summary>
    public const string AUTH_FAILED_FORMAT = "ログインできませんでした: {0}";

    /// <summary>窓口が無いので匿名のまま動く。</summary>
    public const string AUTH_GATEWAY_ABSENT = "アカウントのサーバがないため、匿名で動作します。";

    /// <summary>このアカウントはこのプロジェクトに参加していない。</summary>
    public const string AUTH_NOT_A_MEMBER =
        "このプロジェクトの参加者ではありません。オーナーから招待コードをもらってください。";

    /// <summary>ログインは通ったが権限が無い（サーバが 401 を返す等）。</summary>
    public const string AUTH_REJECTED =
        "サーバに拒否されました。参加が取り消されている可能性があります。";

    /// <summary>トークンの自動更新に失敗。</summary>
    public const string AUTH_REFRESH_FAILED_FORMAT = "ログインの更新に失敗しました: {0}";

    // ── 参加（join）──────────────────────────────────────────

    /// <summary>サーバのアドレスが未入力。</summary>
    public const string JOIN_HOST_REQUIRED = "サーバのアドレスを入力してください。";

    /// <summary>招待コードが未入力。</summary>
    public const string JOIN_INVITE_REQUIRED = "招待コードを入力してください。";

    /// <summary>保存先が未入力。</summary>
    public const string JOIN_DESTINATION_REQUIRED = "保存先フォルダを選んでください。";

    /// <summary>保存先が空でない。</summary>
    public const string JOIN_DESTINATION_NOT_EMPTY_FORMAT =
        "保存先フォルダが空ではありません: {0}";

    /// <summary>アカウントが無いので参加できない。</summary>
    public const string JOIN_ACCOUNT_REQUIRED =
        "先にアカウントを作成してください（参加には自分の鍵が要ります）。";

    /// <summary>招待コードが使えない。</summary>
    public const string JOIN_INVITE_INVALID =
        "招待コードが使えません（期限切れ・使用済み・入力違いのいずれか）。";

    /// <summary>
    /// 名前がサーバ内で重複している。
    /// 一意判定は ASCII の大文字小文字を無視するので、そのことも伝える
    /// （`alice` が居るときに `Alice` で参加しようとしても弾かれる）。
    /// </summary>
    public const string JOIN_NAME_TAKEN_FORMAT =
        "名前「{0}」はこのサーバで既に使われています（大文字小文字の違いは区別されません）。"
        + "別の名前でアカウントを作り直してください。";

    /// <summary>招待コードが長すぎる（貼り付けで余計な文字が混ざった等）。</summary>
    public const string JOIN_INVITE_TOO_LONG_FORMAT =
        "招待コードが長すぎます（{0} 文字まで）。余計な文字が混ざっていないか確認してください。";

    /// <summary>参加に成功。</summary>
    public const string JOIN_OK_FORMAT = "プロジェクト「{0}」に参加しました。";

    /// <summary>クローン中。</summary>
    public const string JOIN_CLONING = "プロジェクトを取得しています…";

    /// <summary>クローンに失敗。</summary>
    public const string JOIN_CLONE_FAILED_FORMAT = "プロジェクトを取得できませんでした: {0}";

    /// <summary>参加の途中で中断された。</summary>
    public const string JOIN_CANCELED = "参加を中断しました。";

    /// <summary>取得できたがプロジェクトファイルが無い。</summary>
    public const string JOIN_PROJECT_FILE_MISSING =
        "取得できましたが、フォルダに .seedproj が見つかりません。オーナーに確認してください。";

    // ── オーナー向け ────────────────────────────────────────

    /// <summary>リポジトリ ID を読めない。</summary>
    public const string OWNER_REPOSITORY_ID_MISSING =
        "このプロジェクトのリポジトリ ID（.lore/id）を読めませんでした。";

    /// <summary>
    /// bootstrap の前提条件の案内。
    /// ループバック限定であることと、**認証を有効にする前に行う手順**であることを伝える
    /// （契約 5 章「有効化の順番」。認証を有効にしたサーバでは新しいリポジトリを作れない）。
    /// </summary>
    public const string OWNER_BOOTSTRAP_LOOPBACK_NOTE =
        "この操作はサーバと同じ PC からしか行えません（安全のため）。"
        + "サーバでリポジトリを作ったあと、Lore の認証を有効にする前に行ってください。";

    /// <summary>ループバック以外から bootstrap を呼んだ。</summary>
    public const string OWNER_BOOTSTRAP_LOOPBACK_REQUIRED =
        "この操作はサーバと同じ PC からしか行えません。サーバの PC で試してください。";

    /// <summary>owner の権限は失効できない。</summary>
    public const string OWNER_CANNOT_REVOKE_OWNER =
        "オーナーの権限は取り消せません（オーナーの交代・追加は未対応）。";

    /// <summary>失効させようとした参加者が居ない。</summary>
    public const string OWNER_MEMBER_NOT_FOUND =
        "その参加者は見つかりませんでした。一覧を更新してください。";

    /// <summary>Bearer が無い・期限切れ。</summary>
    public const string OWNER_SESSION_EXPIRED =
        "ログインの有効期限が切れています。プロジェクトを開き直してください。";

    /// <summary>チャレンジが混み合っている。</summary>
    public const string GATEWAY_TOO_MANY_REQUESTS =
        "サーバが混み合っています。しばらく待ってからもう一度お試しください。";

    /// <summary>サーバの内部エラー。</summary>
    public const string GATEWAY_INTERNAL_ERROR =
        "サーバで問題が起きました。サーバのログを確認してください。";

    /// <summary>bootstrap に成功。</summary>
    public const string OWNER_BOOTSTRAP_OK_FORMAT =
        "「{0}」をこのプロジェクトのオーナーにしました。";

    /// <summary>すでにオーナーが居る。</summary>
    public const string OWNER_ALREADY_EXISTS =
        "このプロジェクトには既にオーナーが居ます。";

    /// <summary>招待コードを発行した。</summary>
    public const string OWNER_INVITE_CREATED =
        "招待コードを発行しました。この画面を閉じると二度と表示できません。";

    /// <summary>招待コードをコピーした。</summary>
    public const string OWNER_INVITE_COPIED = "招待コードをコピーしました。";

    /// <summary>参加者を失効させた。</summary>
    public const string OWNER_MEMBER_REVOKED_FORMAT = "「{0}」の参加を取り消しました。";

    /// <summary>自分自身は失効させられない。</summary>
    public const string OWNER_CANNOT_REVOKE_SELF = "自分自身の参加は取り消せません。";

    /// <summary>オーナーでないので操作できない。</summary>
    public const string OWNER_PERMISSION_REQUIRED =
        "この操作はプロジェクトのオーナーだけが行えます。";

    /// <summary>参加者が居ない。</summary>
    public const string OWNER_NO_MEMBERS = "参加者はまだ居ません。";

    // ── 画面の見出し・ボタン（Hub とダイアログ）──────────────

    /// <summary>Hub のアカウント欄の見出し。</summary>
    public const string HUB_SECTION_TITLE = "アカウント";

    /// <summary>Hub「アカウントを作成」ボタンの見出し。</summary>
    public const string HUB_CREATE_TITLE = "アカウントを作成...";

    /// <summary>Hub「アカウントを作成」ボタンの注記。</summary>
    public const string HUB_CREATE_NOTE = "名前を決めると、この PC に鍵が作られます";

    /// <summary>Hub「書き出し」ボタンの見出し。</summary>
    public const string HUB_EXPORT_TITLE = "書き出し...";

    /// <summary>Hub「読み込み」ボタンの見出し。</summary>
    public const string HUB_IMPORT_TITLE = "読み込み...";

    /// <summary>Hub「プロジェクトに参加」ボタンの見出し。</summary>
    public const string HUB_JOIN_TITLE = "プロジェクトに参加...";

    /// <summary>Hub「プロジェクトに参加」ボタンの注記。</summary>
    public const string HUB_JOIN_NOTE = "招待コードで参加し、手元に取得します";

    /// <summary>作成ダイアログのタイトル。</summary>
    public const string CREATE_DIALOG_TITLE = "アカウントの作成";

    /// <summary>作成ダイアログの説明。</summary>
    public const string CREATE_DIALOG_PROMPT = "名前を入力してください。";

    /// <summary>名前の規則の案内。</summary>
    public const string CREATE_DIALOG_RULE_NOTE =
        "1〜32 文字。英数字・かな・漢字と _ - . が使えます（空白は不可）。";

    /// <summary>書き出しダイアログのタイトル。</summary>
    public const string EXPORT_DIALOG_TITLE = "アカウントの書き出し";

    /// <summary>書き出しダイアログの説明。</summary>
    public const string EXPORT_DIALOG_PROMPT =
        "別の PC へ移すためのパスフレーズを決めてください。"
        + "このパスフレーズが無いと読み込めません。";

    /// <summary>読み込みダイアログのタイトル。</summary>
    public const string IMPORT_DIALOG_TITLE = "アカウントの読み込み";

    /// <summary>読み込みダイアログの説明。</summary>
    public const string IMPORT_DIALOG_PROMPT = "書き出したときのパスフレーズを入力してください。";

    /// <summary>パスフレーズ欄のラベル。</summary>
    public const string LABEL_PASSPHRASE = "パスフレーズ";

    /// <summary>確認用パスフレーズ欄のラベル。</summary>
    public const string LABEL_PASSPHRASE_CONFIRM = "パスフレーズ（確認）";

    /// <summary>書き出しファイルの選択ダイアログのフィルタ。</summary>
    public const string EXPORT_FILE_FILTER = "SEED アカウント (*.seedaccount)|*.seedaccount";

    /// <summary>参加ダイアログのタイトル。</summary>
    public const string JOIN_DIALOG_TITLE = "プロジェクトに参加";

    /// <summary>参加ダイアログのサーバ欄のラベル。</summary>
    public const string LABEL_SERVER_HOST = "サーバのアドレス（ホスト名か IP）";

    /// <summary>参加ダイアログの招待コード欄のラベル。</summary>
    public const string LABEL_INVITE_CODE = "招待コード";

    /// <summary>参加ダイアログの保存先欄のラベル。</summary>
    public const string LABEL_DESTINATION = "保存先フォルダ（空のフォルダ）";

    /// <summary>オーナー向けダイアログのタイトル。</summary>
    public const string OWNER_DIALOG_TITLE = "アカウントと参加者";

    /// <summary>オーナー登録ボタンの見出し。</summary>
    public const string OWNER_BOOTSTRAP_BUTTON = "このプロジェクトでアカウントを有効にする";

    /// <summary>招待コード発行ボタンの見出し。</summary>
    public const string OWNER_INVITE_BUTTON = "招待コードを発行";

    /// <summary>参加者一覧の更新ボタンの見出し。</summary>
    public const string OWNER_REFRESH_BUTTON = "一覧を更新";

    /// <summary>参加取り消しボタンの見出し。</summary>
    public const string OWNER_REVOKE_BUTTON = "参加を取り消す";

    /// <summary>参加者一覧の見出し。</summary>
    public const string OWNER_MEMBERS_TITLE = "参加者";

    /// <summary>参加者 1 行の書式（名前・役割・状態）。</summary>
    public const string OWNER_MEMBER_ROW_FORMAT = "{0}　［{1}］　{2}";

    /// <summary>コピーボタンの見出し。</summary>
    public const string BUTTON_COPY = "コピー";

    /// <summary>参照ボタンの見出し。</summary>
    public const string BUTTON_BROWSE = "参照...";

    /// <summary>決定ボタンの見出し。</summary>
    public const string BUTTON_OK = "OK";

    /// <summary>閉じるボタンの見出し。</summary>
    public const string BUTTON_CLOSE = "閉じる";

    /// <summary>取り消しボタンの見出し。</summary>
    public const string BUTTON_CANCEL = "キャンセル";

    /// <summary>参加ボタンの見出し。</summary>
    public const string BUTTON_JOIN = "参加する";

    /// <summary>フォルダ選択ダイアログのタイトル。</summary>
    public const string FOLDER_PICKER_TITLE = "保存先フォルダを選ぶ";

    // ── ログ（利用者には出さないが、原因追跡に要る）──────────

    /// <summary>窓口が見つかったときのログ。</summary>
    public const string LOG_GATEWAY_FOUND_FORMAT = "[アカウント] 発行窓口を検出しました: {0}";

    /// <summary>窓口が見つからなかったときのログ。</summary>
    public const string LOG_GATEWAY_ABSENT_FORMAT =
        "[アカウント] 発行窓口がありません（{0}）。匿名のまま動作します。";

    /// <summary>ログインに成功したときのログ。</summary>
    public const string LOG_SIGNED_IN_FORMAT =
        "[アカウント] {0} としてログインしました（参加プロジェクト {1} 件）。";

    /// <summary>ログインに失敗したときのログ。</summary>
    public const string LOG_SIGN_IN_FAILED_FORMAT = "[アカウント] ログインに失敗しました: {0}";
}
