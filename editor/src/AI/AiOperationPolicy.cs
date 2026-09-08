using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace SEEDEditor.AI;

// ============================================================
//  AiOperationPolicy.cs — 外部エージェントからのエディタ操作の許可判定
//
//  【なぜ必要か】
//   AI ブリッジ（HTTP）はローカルポートを開くだけで、誰が繋いできたのかを
//   区別していなかった。そのため MCP サーバーが起動したはずのヘッドレスエディタ宛の
//   コマンドが、たまたま同じポートを掴んでいた「利用者が手で開いている
//   エディタ」に届き、別シーンの内容で .scene を上書きし、最後にその
//   エディタを終了させる事故が起きた（docs/editor_mcp.md のポストモーテム節）。
//
//  【方針】
//   1. インスタンス識別: 起動時に与えられたトークンを持つリクエストだけを受け付ける。
//   2. 既定は読み取り専用: 利用者が手で起動したエディタは、明示的に許可するまで
//      「見る」コマンドしか通さない。
//   3. パネル内蔵 AI（利用者自身の操作）は同じ判定を通しつつ常に許可する。
//
//  WPF に依存しない純粋なロジックだけを置く（単体テストからリンクして使うため）。
// ============================================================

/// <summary>
/// エディタ操作コマンドの発信元。許可判定の基準になる。
/// </summary>
public enum AiCommandOrigin
{
    /// <summary>
    /// 利用者がエディタ内で起こした操作（AI アシスタントパネルのチャットなど）。
    /// 画面の前に人が居て、その人自身が依頼している以上、常に許可する。
    /// </summary>
    UserInitiated,

    /// <summary>
    /// 外部プロセス（MCP サーバー / CLI エージェント）からの HTTP リクエスト。
    /// トークン検証と読み取り専用ポリシーの対象になる。
    /// </summary>
    Remote,
}

/// <summary>
/// AI ブリッジの実行許可ポリシー。プロセス内で 1 つの状態として扱う静的クラス。
///
/// <para>
/// 状態は「起動時の引数（ポート・トークン・ヘッドレスか）」と
/// 「このセッション限りの操作許可フラグ」だけ。永続化しない
/// （再起動のたびに読み取り専用へ戻るのが安全側の既定であるため）。
/// </para>
/// </summary>
public static class AiOperationPolicy
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>ブリッジの既定ポート（インスタンス指定が無い＝利用者が手で起動した場合）。</summary>
    public const int DEFAULT_PORT = 7234;

    /// <summary>ヘッドレス起動時にポートを探す範囲の下限。</summary>
    public const int INSTANCE_PORT_RANGE_MIN = 7300;

    /// <summary>ヘッドレス起動時にポートを探す範囲の上限。</summary>
    public const int INSTANCE_PORT_RANGE_MAX = 7399;

    /// <summary>リクエストにトークンを載せる HTTP ヘッダー名。</summary>
    public const string TOKEN_HEADER = "X-Seed-Token";

    /// <summary>生成するインスタンストークンのバイト数（16 バイト = 32 桁の 16 進文字列）。</summary>
    public const int TOKEN_BYTES = 16;

    /// <summary>読み取り専用インスタンスが変更系コマンドを拒否したときのメッセージ。</summary>
    public const string DENY_READ_ONLY =
        "このエディタは読み取り専用です（AI からの変更操作は既定で禁止）。"
      + "変更したい場合はエディタの「編集 → 環境設定」で "
      + "「AI 操作を許可（このインスタンス）」をオンにしてください。"
      + "ヘッドレス運用では seed_launch で起動したインスタンスを操作してください。";

    /// <summary>トークンが違う／付いていないときのメッセージ。</summary>
    public const string DENY_BAD_TOKEN =
        "インスタンストークンが一致しません。"
      + "seed_launch で起動したインスタンス以外は操作できません。";

    /// <summary>
    /// ゲーム入力注入コマンドの接頭辞（<c>game_input_key</c> / <c>game_input_mouse</c> /
    /// <c>game_input_sequence</c> / <c>game_input_release_all</c>）。
    ///
    /// これらは「ランタイムの入力状態を書き換えて実際にゲームを操作する」ため、
    /// 観測系ではなく**変更系**として扱う（ReadOnlyCommands には決して入れないこと）。
    /// 利用者が手で開いているエディタで遊んでいる最中に、外部エージェントが
    /// 勝手にキーを押し込めるようになってはいけない。
    /// </summary>
    public const string GAME_INPUT_COMMAND_PREFIX = "game_input_";

    /// <summary>読み取り専用インスタンスへゲーム入力注入が来たときのメッセージ。</summary>
    public const string DENY_GAME_INPUT =
        "このエディタは読み取り専用のため、ゲームへの入力注入（game_input_*）はできません"
      + "（利用者が操作中のゲームを AI が横から動かさないための制限）。"
      + "seed_launch で起動したヘッドレスインスタンスで実行するか、"
      + "エディタの「編集 → 環境設定」で「AI 操作を許可（このインスタンス）」を"
      + "オンにしてください。";

    /// <summary>ヘッドレスでないインスタンスへ shutdown が来たときのメッセージ。</summary>
    public const string DENY_SHUTDOWN =
        "shutdown は seed_launch で起動したヘッドレスインスタンスにのみ実行できます"
      + "（利用者が開いているエディタを AI から閉じることはできません）。";

    /// <summary>
    /// 読み取り専用でも許可するコマンド名。
    ///
    /// 「エディタとランタイムの状態を観測するだけで、ファイルにもシーンにも
    /// 触れない」ものだけを列挙する。ここに足すときは
    /// 「これを許すと最悪どうなるか」を必ず考えること。
    /// </summary>
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal)
    {
        "get_scene_info",
        "list_asset_files",
        "get_hierarchy",
        "get_editor_state",
        "get_log",
        "screenshot",
        "screenshot_gpu",
        // アクタを名前で引くだけ（選択も変えない）ので観測系。
        "find_actor",
        // 注意: save_data はここに入れない（セーブデータを書き換える変更系）。
        // 注意: game_input_* はここに入れない（変更系。GAME_INPUT_COMMAND_PREFIX 参照）。
    };

    // ── 状態 ─────────────────────────────────────────────────────

    /// <summary>このインスタンスのブリッジが待ち受けるポート。</summary>
    public static int Port { get; private set; } = DEFAULT_PORT;

    /// <summary>
    /// このインスタンスのトークン。空文字なら検証しない（利用者が手で起動した既定エディタ）。
    /// </summary>
    public static string Token { get; private set; } = "";

    /// <summary>起動時に <c>--ai-port</c> が指定されたか（＝MCP が起動したインスタンス）。</summary>
    public static bool HasExplicitPort { get; private set; }

    /// <summary>ヘッドレス起動か。shutdown の可否判定に使う。</summary>
    public static bool IsHeadless { get; private set; }

    /// <summary>
    /// このセッションで AI からの変更操作を許可するか。
    ///
    /// ヘッドレス（MCP が起動した使い捨てインスタンス）は既定で true。
    /// 利用者が手で起動した通常のエディタは既定で false で、環境設定の
    /// チェックボックスからのみ true にできる。**永続化しない**ので、
    /// エディタを再起動すると必ず読み取り専用へ戻る。
    /// </summary>
    public static bool MutationsEnabled { get; set; }

    // ── 初期化 ───────────────────────────────────────────────────

    /// <summary>
    /// 起動オプションからポリシーを確定させる。アプリ起動時に 1 回だけ呼ぶ。
    /// </summary>
    /// <param name="port">指定ポート。null なら既定ポート。</param>
    /// <param name="token">インスタンストークン。null / 空なら検証しない。</param>
    /// <param name="isHeadless">ヘッドレス起動か。</param>
    public static void Configure(int? port, string? token, bool isHeadless)
    {
        HasExplicitPort  = port.HasValue;
        Port             = port ?? DEFAULT_PORT;
        Token            = token ?? "";
        IsHeadless       = isHeadless;
        // ヘッドレスは AI に操作させるために起動されたインスタンスなので既定で許可する。
        MutationsEnabled = isHeadless;
    }

    /// <summary>ランダムなインスタンストークン（16 進小文字）を生成する。</summary>
    public static string GenerateToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(TOKEN_BYTES)).ToLowerInvariant();

    // ── 判定 ─────────────────────────────────────────────────────

    /// <summary>指定コマンドが「観測のみ」かどうか。</summary>
    public static bool IsReadOnlyCommand(string command) => ReadOnlyCommands.Contains(command);

    /// <summary>
    /// リクエストのトークンが妥当かどうかを判定する。
    ///
    /// トークンが設定されていないインスタンス（利用者が手で起動した既定エディタ）は
    /// 検証しない。設定されている場合は完全一致を要求する。
    /// </summary>
    /// <param name="presented">リクエストヘッダーに載っていた値（無ければ null）。</param>
    public static bool IsTokenValid(string? presented)
    {
        if (string.IsNullOrEmpty(Token)) return true;
        if (string.IsNullOrEmpty(presented)) return false;
        // タイミング差で総当たりされないよう固定時間比較を使う。
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(Token),
            System.Text.Encoding.UTF8.GetBytes(presented));
    }

    /// <summary>
    /// コマンドの実行可否を判定する。
    /// </summary>
    /// <param name="command">コマンド名（<c>save_scene</c> など）。</param>
    /// <param name="origin">発信元。</param>
    /// <returns>許可なら null、拒否ならその理由（AI へそのまま返すメッセージ）。</returns>
    public static string? CheckAllowed(string command, AiCommandOrigin origin)
    {
        // 利用者自身の操作（パネル内蔵 AI）は判定を通すが常に許可する。
        // ここを素通しにせず 1 本の関数へ通しておくことで、将来ポリシーを
        // 増やしたときに「パネルだけ抜けていた」が起きないようにする。
        if (origin == AiCommandOrigin.UserInitiated) return null;

        // 観測系はいつでも許可する。
        if (IsReadOnlyCommand(command)) return null;

        // shutdown は「AI が起動したヘッドレス」か「利用者が明示的に許可した」ときだけ通す。
        // 既定の対話エディタを AI が勝手に閉じることは絶対に無い（事故の再発防止）。
        if (command == "shutdown" && !IsHeadless && !MutationsEnabled) return DENY_SHUTDOWN;

        // ゲーム入力の注入は変更系。拒否理由を専用メッセージにして、
        // 「なぜ遊べないのか」がエージェント側の応答だけで分かるようにする。
        if (command.StartsWith(GAME_INPUT_COMMAND_PREFIX, StringComparison.Ordinal)
            && !MutationsEnabled)
            return DENY_GAME_INPUT;

        // その他の変更系は「このインスタンスで AI 操作が許可されている」ことが条件。
        if (!MutationsEnabled) return DENY_READ_ONLY;

        return null;
    }
}
